const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');
const experiment = path.join(root, 'experiments', '001-four-variants');
const challenges = path.join(__dirname, 'challenges');
const families = ['cache', 'config', 'import', 'scheduler'];

/** Runs a challenge grader and retains its JSON and build logs for inspection. */
function grade(family, revision, submission, output) {
  const evaluator = path.join(challenges, family, 'grader', 'evaluate.cjs');
  const child = spawnSync(process.execPath, [evaluator, submission, String(revision), output], {
    cwd: root, encoding: 'utf8', timeout: 180_000, windowsHide: true,
    maxBuffer: 8 * 1024 * 1024,
  });
  if (child.status !== 0) {
    throw new Error(child.error?.message || child.stderr.trim() || `Grader exited with status ${child.status}`);
  }
  return JSON.parse(fs.readFileSync(output, 'utf8'));
}

/** Compares outcomes without including host-specific logs or timings. */
function comparableGrade(gradeResult) {
  return {
    status: gradeResult.status,
    cases: gradeResult.cases.map(({ id, introduced, passed }) => ({ id, introduced, passed }))
      .sort((left, right) => left.id.localeCompare(right.id)),
  };
}

/** Resolves a path from public metadata within its expected directory. */
function resolveWithin(directory, relative) {
  assert.ok(typeof relative === 'string' && !path.isAbsolute(relative), 'Expected a relative path');
  const resolved = path.resolve(directory, relative);
  const remaining = path.relative(directory, resolved);
  assert.ok(remaining && remaining !== '..' && !remaining.startsWith(`..${path.sep}`) &&
    !path.isAbsolute(remaining), `Path escapes its directory: ${relative}`);
  return resolved;
}

/** Checks the saved bytes before rebuilding and replaying a submission. */
function replay(run, scratch) {
  assert.ok(families.includes(run.family), `Unknown challenge: ${run.family}`);
  assert.ok(Number.isInteger(run.revision) && run.revision >= 1 && run.revision <= 3,
    `Invalid revision: ${run.revision}`);
  assert.match(run.id, /^(cache|config|import|scheduler)-[ABCD]-r[123]$/);
  assert.equal(run.id, `${run.family}-${run.variant}-r${run.revision}`);
  assert.equal(run.sourceDirectory, `submissions/${run.id}`);
  const submission = resolveWithin(experiment, run.sourceDirectory);
  for (const file of run.sourceFiles) {
    const actual = createHash('sha256').update(fs.readFileSync(resolveWithin(submission, file.path))).digest('hex');
    assert.equal(actual, file.sha256, `Source hash mismatch for ${run.id}/${file.path}`);
  }

  // Test output can include local paths and belongs in scratch rather than the public result set.
  const output = path.join(scratch, `${run.id}.json`);
  const actual = grade(run.family, run.revision, submission, output);
  assert.deepEqual(comparableGrade(actual), comparableGrade(run.grading), `Grade mismatch for ${run.id}`);
  const passed = actual.cases.filter(item => item.passed).length;
  console.log(`${run.id}: ${passed}/${actual.cases.length} cases match (${actual.status})`);
}

/** Demonstrates that the cache grader detects a one-second stale-age regression. */
function verifyReference(scratch) {
  const fixture = path.join(challenges, 'cache');
  const reference = path.join(scratch, 'reference');
  fs.cpSync(path.join(fixture, 'reference'), reference, { recursive: true });
  const project = fs.readFileSync(path.join(fixture, 'seed', 'Challenge.csproj'), 'utf8');
  const symbols = '    <DefineConstants>$(DefineConstants);REV1;REV2;REV3</DefineConstants>';
  fs.writeFileSync(path.join(reference, 'Challenge.csproj'), project.replace('  </PropertyGroup>',
    `${symbols}\n  </PropertyGroup>`));
  fs.copyFileSync(path.join(fixture, 'seed', 'NuGet.Config'), path.join(reference, 'NuGet.Config'));

  // Establish a clean baseline before applying the mutation to a separate copy.
  const baseline = grade('cache', 3, reference, path.join(scratch, 'reference.json'));
  assert.equal(baseline.status, 'evaluated', 'Cache reference failed to build or run');
  assert.equal(baseline.cases.length, 20, 'Unexpected cache revision 3 case count');
  assert.ok(baseline.cases.every(item => item.passed), 'Cache reference failed a behavioral case');
  console.log('Cache reference: all 20 cases passed');

  const mutant = path.join(scratch, 'mutant');
  fs.cpSync(reference, mutant, { recursive: true });
  const file = path.join(mutant, 'CatalogCache.cs');
  const source = fs.readFileSync(file, 'utf8');
  const boundary = 'TimeSpan.FromSeconds(90))';
  assert.equal(source.split(boundary).length, 2, 'The mutation must change exactly one boundary');
  fs.writeFileSync(file, source.replace(boundary, 'TimeSpan.FromSeconds(91))'));
  const changed = grade('cache', 3, mutant, path.join(scratch, 'mutant.json'));
  assert.equal(changed.status, 'evaluated', 'Mutant must build and execute');
  const failed = changed.cases.filter(item => !item.passed).map(item => item.id);
  assert.ok(failed.includes('freshness.stale-fallback-no-age-reset'),
    `Expected stale-age regression was missed. Observed failures: ${failed.join(', ')}`);
  console.log(`Cache mutant: caught ${failed.join(', ')}`);
}

const [selection, family, ...extra] = process.argv.slice(2);
const valid = extra.length === 0 && (selection === '--family' ? families.includes(family) :
  family === undefined && (['--all', '--reference'].includes(selection) ||
    /^(cache|config|import|scheduler)-[ABCD]-r[123]$/.test(selection ?? '')));
if (!valid) {
  console.error('Usage: node benchmarks/replay.cjs <run-id | --family cache|config|import|scheduler | --all | --reference>');
  process.exitCode = 2;
} else {
  const scratchRoot = path.join(root, '.replay');
  fs.mkdirSync(scratchRoot, { recursive: true });
  const scratch = fs.mkdtempSync(path.join(scratchRoot, 'run-'));
  console.log(`Replay output: ${path.relative(root, scratch)}`);
  if (selection === '--reference') {
    verifyReference(scratch);
  } else {
    const results = JSON.parse(fs.readFileSync(path.join(experiment, 'results.json'), 'utf8'));
    const runs = results.runs.filter(run => selection === '--all' ||
      (selection === '--family' ? run.family === family : run.id === selection));
    const expectedCount = selection === '--all' ? 48 : selection === '--family' ? 12 : 1;
    assert.equal(runs.length, expectedCount, 'Unexpected number of archived submissions');
    assert.equal(new Set(runs.map(run => run.id)).size, runs.length, 'Duplicate archived submission');
    for (const run of runs) replay(run, scratch);
    console.log(`Replayed ${runs.length} saved submission${runs.length === 1 ? '' : 's'}`);
  }
}
