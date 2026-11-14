import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { loadExperiment, runExperiment, stageGrade } from './api-experiment.mjs';
import { summarize } from './api-report.mjs';
import { createWorkspace, projectHashes } from './api-workspace.mjs';

test('plans three handoffs and a matched same-source change', async () => {
  const { plan, cells } = await loadExperiment();
  assert.equal(cells.length, 12);
  const shared = cells.filter(cell => cell.track === 'shared');
  assert.equal(new Set(shared.map(cell => cell.input)).size, 1);
  assert.equal(new Set(shared.map(cell => cell.instructions)).size, 3);
  for (const revision of [2, 3]) {
    const handoffs = cells.filter(cell => cell.track === 'handoff' && cell.revision === revision);
    assert.equal(new Set(handoffs.map(cell => cell.instructions)).size, 1);
    assert.ok(handoffs.every(cell => cell.parent === `handoff-${cell.variant}-r${revision - 1}`));
  }
  assert.ok(Object.keys(plan.graderHashes).length >= 2);
  assert.ok(Object.values(plan.sharedSeed).every(hash => /^[a-f0-9]{64}$/.test(hash)));
});

test('workspace edits preserve project files and identify the built snapshot', async () => {
  const { seed } = await loadExperiment();
  const workspace = createWorkspace(seed, async () => ({ exitCode: 0 }));
  const { executeTool } = workspace;
  assert.deepEqual(await executeTool('read_file', { file: '../grader/Program.cs', offset: 0 }), { error: 'Unknown file' });
  assert.ok((await executeTool('create_file', { file: 'NuGet.Config', content: '' })).error);
  assert.ok((await executeTool('replace_text', { file: 'SyncEngine.cs', old_text: '', new_text: '' })).error);
  await executeTool('build_project', {});
  assert.ok(workspace.builtCurrent());
  await executeTool('create_file', { file: 'Changes.cs', content: '// new source\n' });
  assert.equal(workspace.builtCurrent(), false);
  assert.equal(workspace.snapshot().get('Challenge.csproj'), seed.get('Challenge.csproj'));
});

test('grader staging includes the fixed project and excludes reference code', async context => {
  const output = await mkdtemp(join(tmpdir(), 'workflow-grade-'));
  context.after(() => rm(output, { recursive: true, force: true }));
  const { common } = await loadExperiment();
  await stageGrade(output, common, 3);
  assert.deepEqual((await readdir(output)).sort(), ['grade.cjs', 'grader', 'seed', 'submission']);
  assert.equal(await readFile(join(output, 'seed/Challenge.csproj'), 'utf8'), common.get('Challenge.csproj'));
  assert.ok((await readFile(join(output, 'grade.cjs'), 'utf8')).includes("'/workspace/submission', '3'"));
});

test('mocked pilot keeps failed attempts and blocks only the affected handoff', async context => {
  const output = await mkdtemp(join(tmpdir(), 'workflow-api-'));
  context.after(() => rm(output, { recursive: true, force: true }));
  const experiment = await loadExperiment();
  const seedHashes = projectHashes(experiment.seed);
  let pending;
  let responseNumber = 0;
  const transport = {
    countInput: async () => 120,
    async create(body) {
      const first = body.input.length === 1;
      const usage = { input_tokens: 120, input_tokens_details: { cached_tokens: 0, cache_write_tokens: 0 },
        output_tokens: 40, output_tokens_details: { reasoning_tokens: 20 } };
      if (first) {
        pending = `revision-${++responseNumber}`;
        return { status: 'completed', model: body.model, service_tier: 'default', usage, output: [{ type: 'function_call',
          call_id: pending, name: 'create_file', arguments: JSON.stringify({ file: `Edit${responseNumber}.cs`, content: '// mocked edit\n' }) }] };
      }
      assert.equal(body.input.at(-1).call_id, pending);
      return { status: 'completed', model: body.model, service_tier: 'default', usage,
        output: [{ type: 'message', content: [{ type: 'output_text', text: 'Done' }] }] };
    },
  };
  const grade = async (_files, revision, id) => ({ status: 'evaluated', cases: [{ id: 'behavior', introduced: 0,
    passed: id !== 'handoff-short-style-r1' || revision !== 1 }] });
  const result = await runExperiment({ experiment, output, transport, build: async () => ({ exitCode: 0 }), grade,
    maxUsd: 5, imageId: 'mock' });
  assert.equal(result.runs.length, 12);
  assert.equal(result.runs.filter(row => row.accepted).length, 9);
  assert.equal(result.runs.filter(row => row.status === 'blocked-by-predecessor').length, 2);
  for (const row of result.runs.filter(row => row.startHashes)) {
    const previous = result.runs.find(candidate => candidate.id === row.parent);
    assert.deepEqual(row.startHashes, row.track === 'shared' ? experiment.plan.sharedSeed : previous?.submissionHashes ?? seedHashes);
    assert.equal(row.requests.length, 2);
  }
  const failed = result.runs.find(row => row.id === 'handoff-short-style-r1');
  assert.deepEqual(failed.regressions, ['behavior']);
  assert.equal(summarize(result.runs).find(group => group.track === 'handoff' && group.variant === 'short-style')
    .revisions.at(-1).cumulativeObservedTokens, 320);
});
