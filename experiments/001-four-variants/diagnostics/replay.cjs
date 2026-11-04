const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const projects = new Map([['cache', 'CacheDiagnostic'], ['import', 'ImportDiagnostic']]);
const requested = process.argv.slice(2);
if (requested.length > 1 || requested.some(family => !projects.has(family))) {
  console.error('Usage: node replay.cjs [cache|import]');
  process.exit(2);
}

// Give each compilation its own artifacts directory so revision outputs never mix.
const scratch = fs.mkdtempSync(path.join(__dirname, '.replay-'));
try {
  let checks = 0;
  for (const family of requested.length ? requested : projects.keys()) {
    const directory = path.join(__dirname, family);
    const observations = JSON.parse(fs.readFileSync(path.join(directory, 'observed.json'), 'utf8'));
    for (const saved of observations) {
      assert.match(saved.variant, /^[ABCD]$/);
      assert.ok([2, 3].includes(saved.revision));
      const runId = `${family}-${saved.variant}-r${saved.revision}`;
      const source = path.resolve(__dirname, '..', 'submissions', runId);
      const output = execFileSync('dotnet', [
        'run', '--project', `${projects.get(family)}.csproj`, '--no-launch-profile',
        '--artifacts-path', path.join(scratch, runId), `-p:CandidateSourceDir=${source}`,
        '-p:NuGetAudit=false',
      ], { cwd: directory, encoding: 'utf8', timeout: 120_000, windowsHide: true });

      // Compare behavioral output. A replay may run on a different .NET patch release.
      const report = JSON.parse(output.trimEnd().split(/\r?\n/).at(-1));
      const cases = family === 'cache' ? report : report.cases;
      assert.deepEqual(cases, saved.cases, runId);
      checks += cases.length;
      console.log(`${runId}: reproduced ${cases.length} observations`);
    }
  }
  console.log(`Reproduced ${checks} diagnostic observations.`);
} finally {
  const resolved = fs.realpathSync(scratch);
  const parent = fs.realpathSync(__dirname);
  assert.equal(path.dirname(resolved), parent);
  assert.match(path.basename(resolved), /^\.replay-[\w-]+$/);
  fs.rmSync(resolved, { recursive: true, force: true });
}
