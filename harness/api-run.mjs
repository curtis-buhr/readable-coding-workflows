import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { createResponsesTransport } from './api-responses.mjs';
import { loadExperiment, root, runExperiment, stageGrade } from './api-experiment.mjs';
import { writeProject } from './api-workspace.mjs';
import { resolveImageId, runIsolated } from './isolated-dotnet.mjs';

const usage = 'Usage: node harness/api-run.mjs --dry-run | --preflight --image IMAGE | --run --image IMAGE --max-usd AMOUNT';

/** Validate the required execution mode before touching credentials or Docker. */
function parseArgs(args) {
  const [mode, ...values] = args;
  if (mode === '--dry-run' && values.length === 0) return { mode };
  const [imageFlag, image, budgetFlag, amount] = values;
  if (imageFlag !== '--image' || !image) throw new Error(usage);
  if (mode === '--preflight' && values.length === 2) return { mode, image };
  const maxUsd = Number(amount);
  if (mode === '--run' && values.length === 4 && budgetFlag === '--max-usd' &&
      Number.isFinite(maxUsd) && maxUsd > 0) return { mode, image, maxUsd };
  throw new Error(usage);
}

/** Run a fixed offline build or grader in a separate disposable container. */
function jobs(imageId, output) {
  async function runJob(stage, argv, options = {}) {
    const directory = await mkdtemp(join(output, 'staging-'));
    try {
      await stage(directory);
      return await runIsolated({ imageId, filesDirectory: directory, argv, ...options });
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  }

  const build = (files, _cell, signal) => runJob(directory => writeProject(directory, files),
    ['dotnet', 'build', 'Challenge.csproj', '--nologo', '--disable-build-servers', '--verbosity', 'quiet', '-p:NuGetAudit=false'],
    { signal, timeoutMs: 90_000, maxOutputBytes: 12_000 }).then(result => ({
    exitCode: result.exitCode, timedOut: result.timedOut, outputLimited: result.outputLimited,
    diagnostics: (result.stdout + result.stderr).slice(-2_000),
  }));

  async function grade(files, revision) {
    const result = await runJob(directory => stageGrade(directory, files, revision), ['node', 'grade.cjs'], { timeoutMs: 175_000 });
    if (result.timedOut || result.outputLimited) return { status: result.timedOut ? 'timeout' : 'output-limit', cases: [] };
    if (result.exitCode !== 0) throw new Error('The grader did not return a result');
    const { status, cases } = JSON.parse(result.stdout);
    if (typeof status !== 'string' || !Array.isArray(cases) || (status === 'evaluated' && !cases.length) ||
      cases.some(item => typeof item.id !== 'string' || typeof item.passed !== 'boolean' ||
        !Number.isInteger(item.introduced) || item.introduced < 0 || item.introduced > revision) ||
      new Set(cases.map(item => item.id)).size !== cases.length) throw new Error('Invalid grader result');
    return { status, cases: cases.map(({ id, passed, introduced }) => ({ id, passed, introduced })) };
  }
  return { build, grade };
}

async function main() {
  const { mode, image, maxUsd } = parseArgs(process.argv.slice(2));
  const experiment = await loadExperiment();
  if (mode === '--dry-run') {
    console.log(JSON.stringify(experiment.plan, null, 2));
    return;
  }
  const apiKey = process.env.OPENAI_API_KEY;
  if (mode === '--run' && !apiKey?.trim()) throw new Error('Set OPENAI_API_KEY to a project key in the linked API organization before starting a live run.');

  const imageId = await resolveImageId(image);
  const outputRoot = join(root, '.api-runs');
  await mkdir(outputRoot, { recursive: true });
  const output = await mkdtemp(join(outputRoot, '002-'));
  const { build, grade } = jobs(imageId, output);
  if (mode === '--preflight') {
    const compiled = await build(experiment.seed, 'preflight');
    if (compiled.exitCode !== 0 || compiled.timedOut || compiled.outputLimited) throw new Error(`Sandbox build failed: ${compiled.diagnostics}`);
    for (const [files, revision] of [[experiment.seed, 0], [experiment.common, 2]]) {
      const result = await grade(files, revision);
      if (result.status !== 'evaluated' || result.cases.some(item => !item.passed)) throw new Error(`Sandbox baseline R${revision} failed`);
      console.log(`Sandbox baseline R${revision}: ${result.cases.length}/${result.cases.length} checks passed`);
    }
    return;
  }

  const transport = createResponsesTransport({ apiKey, projectId: process.env.OPENAI_PROJECT_ID,
    organizationId: process.env.OPENAI_ORG_ID });
  console.log(`Run output: ${output}`);
  const result = await runExperiment({ experiment, output, transport, build, grade, maxUsd, imageId });
  const accepted = result.runs.filter(run => run.accepted).length;
  console.log(`${accepted}/${result.runs.length} revisions accepted. Results: ${join(output, 'results.json')}`);
  if (accepted !== result.runs.length) process.exitCode = 2;
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
