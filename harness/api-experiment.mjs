import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { appendFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { performance } from 'node:perf_hooks';
import { createBudget, reasoningEffort, responsesModel, runResponses } from './api-responses.mjs';
import { createWorkspace, projectHashes, readProject, sha256, workspaceTools, writeProject } from './api-workspace.mjs';

export const root = fileURLToPath(new URL('../', import.meta.url));
export const fixture = join(root, 'benchmarks/challenges/incremental-sync');
const manifestPath = join(root, 'experiments/002-workflow-efficiency/manifest.json');
const sharedPrompt = 'Implement the requested revision in the supplied C# project. Inspect the files, edit source, and build the final snapshot. The project and dependency settings are fixed. Keep behavior from prior revisions except where the current revision changes it.';

/** Hash the actual staged inputs and order the matched cells before spending credits. */
export async function loadExperiment() {
  const manifestText = await readFile(manifestPath, 'utf8');
  const manifest = JSON.parse(manifestText);
  if (manifest.model !== responsesModel || manifest.reasoningEffort !== reasoningEffort) throw new Error('Unexpected model settings');
  const variants = await Promise.all(manifest.variants.map(async variant => ({
    id: variant.id,
    text: variant.instructions ? (await readFile(join(root, variant.instructions), 'utf8')).trim() : '',
  })));
  const tasks = await Promise.all([1, 2, 3].map(async revision =>
    (await readFile(join(fixture, `tasks/r${revision}.txt`), 'utf8')).trim()));
  const seed = await readProject(join(fixture, 'seed'));
  const common = new Map(seed).set('SyncEngine.cs', (await readFile(join(fixture, 'reference/r2/SyncEngine.cs'), 'utf8')).replaceAll('\r\n', '\n'));

  // Hash the grader source, including its project files, without packaging it into candidate inputs.
  const graderHashes = {};
  async function hashGrader(directory, prefix = '') {
    const entries = await readdir(directory, { withFileTypes: true });
    for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
      if (entry.isDirectory() && ['bin', 'obj'].includes(entry.name)) continue;
      const relative = `${prefix}${entry.name}`;
      const file = join(directory, entry.name);
      if (entry.isDirectory()) await hashGrader(file, `${relative}/`);
      else if (entry.isFile()) graderHashes[relative] = sha256(await readFile(file));
      else throw new Error(`Unexpected grader entry: ${relative}`);
    }
  }
  await hashGrader(join(fixture, 'grader'));
  const cells = [];
  function block(track, revision, order) {
    for (const [index, variant] of order.entries()) {
      const instructions = track === 'shared' || revision === 1 ? variant.text : '';
      const input = tasks.slice(0, revision).map((task, i) => `Revision ${i + 1}\n${task}`).join('\n\n');
      cells.push({ id: `${track}-${variant.id}-r${revision}`, track, variant: variant.id, revision,
        position: index + 1, parent: track === 'handoff' && revision > 1 ? `handoff-${variant.id}-r${revision - 1}` : null,
        instructions: [sharedPrompt, instructions].filter(Boolean).join('\n\n'), input });
    }
  }
  const order = [...variants].sort((a, b) => sha256(`${manifest.orderSeed}/${a.id}`).localeCompare(sha256(`${manifest.orderSeed}/${b.id}`)));
  const rotate = offset => [...order.slice(offset % order.length), ...order.slice(0, offset % order.length)];
  for (const revision of [1, 2, 3]) block('handoff', revision, rotate(revision - 1));
  block('shared', 3, rotate(1));
  const plan = { ...manifest, manifestHash: sha256(manifestText), seed: projectHashes(seed), sharedSeed: projectHashes(common), graderHashes,
    toolHash: sha256(JSON.stringify(workspaceTools)), cells: cells.map(({ instructions, input, ...cell }) => ({ ...cell, promptHash: sha256(JSON.stringify([instructions, input])) })) };
  return { plan, cells, seed, common };
}

/** Stage the grader and snapshot in an independent container input directory. */
export async function stageGrade(directory, files, revision) {
  const submission = join(directory, 'submission');
  await mkdir(submission);
  await writeProject(submission, files);
  await cp(join(fixture, 'grader'), join(directory, 'grader'), { recursive: true,
    filter: file => !['bin', 'obj'].includes(file.split(/[\\/]/).at(-1)) });
  // The evaluator uses the fixture's offline package configuration.
  await mkdir(join(directory, 'seed'));
  await writeFile(join(directory, 'seed/Challenge.csproj'), files.get('Challenge.csproj'));
  await writeFile(join(directory, 'seed/NuGet.Config'), files.get('NuGet.Config'));
  await writeFile(join(directory, 'grade.cjs'), `const { spawnSync } = require('node:child_process');\n` +
    `const result = spawnSync(process.execPath, ['grader/evaluate.cjs', '/workspace/submission', '${revision}', '/workspace/result.json'], {encoding:'utf8', timeout:160000});\n` +
    `if (result.status !== 0) { process.stderr.write(result.stderr ?? 'Grader failed'); process.exit(1); }\n` +
    `process.stdout.write(require('node:fs').readFileSync('/workspace/result.json', 'utf8'));\n`);
}

function passes(grade) {
  return grade.status === 'evaluated' && grade.cases.length > 0 && grade.cases.every(item => item.passed);
}

/** Run all planned cells. Only a passing snapshot can seed its own next handoff. */
export async function runExperiment({ experiment, output, transport, build, grade, maxUsd, imageId }) {
  const { plan, cells, seed, common } = experiment;
  const budget = createBudget(maxUsd);
  const runs = [];
  const snapshots = new Map();
  let stopped;
  const started = performance.now();
  const startedAt = new Date().toISOString();
  const baseline = await grade(common, 2, 'shared-seed');
  if (!passes(baseline)) throw new Error('Shared R2 snapshot failed the frozen baseline checks');
  const baselineSeed = await grade(seed, 0, 'initial-seed');
  if (!passes(baselineSeed)) throw new Error('Initial seed failed the frozen baseline checks');
  await writeFile(join(output, 'plan.json'), JSON.stringify({ ...plan, imageId, maxUsd, startedAt, baselines: [baselineSeed, baseline] }, null, 2) + '\n', { flag: 'wx' });
  const byId = new Map();

  async function record(row) {
    runs.push(row);
    byId.set(row.id, row);
    await writeFile(join(output, 'runs.jsonl'), JSON.stringify(row) + '\n', { flag: 'a' });
  }

  for (const [index, cell] of cells.entries()) {
    const spec = plan.cells[index];
    if (stopped) {
      await record({ ...spec, status: stopped });
      continue;
    }
    const source = cell.track === 'shared' ? common : cell.parent ? snapshots.get(cell.parent) : seed;
    if (!source) {
      await record({ ...spec, status: 'blocked-by-predecessor' });
      continue;
    }
    if (budget.remainingUsd === 0) {
      await record({ ...spec, status: 'blocked-by-budget' });
      continue;
    }

    const directory = join(output, cell.id);
    await mkdir(directory);
    const toolEvents = [];
    let pendingBuild;
    const workspace = createWorkspace(source, (files, signal) => {
      pendingBuild = build(files, cell.id, signal);
      return pendingBuild;
    });
    const result = await runResponses({ transport, instructions: cell.instructions, input: cell.input,
      tools: workspaceTools, executeTool: workspace.executeTool, budget, limits: plan.limits,
      onRequest: request => appendFileSync(join(directory, 'requests.jsonl'), JSON.stringify(request) + '\n'),
      onTool: event => toolEvents.push(event) });
    // A deadline can finish the API loop before the container has finished shutting down.
    if (pendingBuild) await Promise.allSettled([pendingBuild]);
    const snapshot = workspace.snapshot();
    const snapshotPath = join(directory, 'submission');
    await mkdir(snapshotPath);
    await writeProject(snapshotPath, snapshot);
    const evaluationStarted = performance.now();
    let evaluation = null;
    let evaluationError = null;
    if (result.status === 'completed') {
      try { evaluation = await grade(snapshot, cell.revision, cell.id); }
      catch (error) { evaluationError = error; }
    }
    const prior = cell.parent ? byId.get(cell.parent).evaluation : cell.track === 'shared' ? baseline : baselineSeed;
    const passedPreviously = new Set(prior.cases.filter(item => item.passed).map(item => item.id));
    const regressions = evaluation?.cases.filter(item => !item.passed && passedPreviously.has(item.id)).map(item => item.id) ?? [];
    const accepted = evaluation ? passes(evaluation) : false;
    if (accepted) snapshots.set(cell.id, snapshot);
    await record({ ...spec, ...result, accepted, evaluation, evaluationMs: evaluation ? performance.now() - evaluationStarted : null,
      evaluationError: evaluationError ? 'grader-infrastructure' : null, regressions, candidateBuildPassed: workspace.builtCurrent(),
      startHashes: projectHashes(source), submissionHashes: projectHashes(snapshot), toolEvents });
    if (evaluationError) throw evaluationError;
    if ([401, 403].includes(result.httpStatus) || result.errorCode === 'credit_balance_exhausted') stopped = 'blocked-by-api-access';
    if (result.reason === 'budget') stopped = 'blocked-by-budget';
    console.log(`${cell.id} ${result.status}${evaluation ? `, ${evaluation.cases.filter(item => item.passed).length}/${evaluation.cases.length} checks` : ''}`);
  }

  const results = { experiment: plan.experiment, backend: plan.backend, imageId, startedAt, elapsedMs: performance.now() - started,
    budgetUsd: maxUsd, reservedOrEstimatedUsd: budget.spentUsd, runs };
  await writeFile(join(output, 'results.json'), JSON.stringify(results, null, 2) + '\n', { flag: 'wx' });
  return results;
}
