const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const root = __dirname;
const scratchRoot = path.resolve(root, '../../../.replay');
fs.mkdirSync(scratchRoot, { recursive: true });
const scratch = fs.mkdtempSync(path.join(scratchRoot, 'incremental-sync-'));
const reference = fs.readFileSync(path.join(root, 'reference/SyncEngine.cs'), 'utf8');

/** Materializes a reference revision using only the fixture's REV2 and REV3 directives. */
function forRevision(source, revision) {
    let enabled = true;
    const scopes = [];
    const lines = [];
    for (const line of source.split(/\r?\n/)) {
        const match = /^#if REV([23])$/.exec(line);
        if (match) {
            scopes.push(enabled);
            enabled = enabled && revision >= Number(match[1]);
        } else if (line === '#else') {
            assert.ok(scopes.length, 'Unexpected #else');
            enabled = scopes.at(-1) && !enabled;
        } else if (line === '#endif') {
            assert.ok(scopes.length, 'Unexpected #endif');
            enabled = scopes.pop();
        } else if (enabled) {
            assert.ok(!line.startsWith('#'), 'Unsupported reference directive');
            lines.push(line);
        }
    }
    assert.equal(scopes.length, 0);
    return lines.join('\n');
}

/** Compiles a snapshot and checks the grader produced stable cases and the expected failures. */
function validate(name, submission, revision, count, expectedFailures = []) {
    const output = path.join(scratch, name + '.json');
    const child = spawnSync(process.execPath, [path.join(root, 'grader/evaluate.cjs'), submission,
        String(revision), output], { encoding: 'utf8', timeout: 130_000 });
    assert.equal(child.status, 0, child.error?.message ?? child.stderr);
    const result = JSON.parse(fs.readFileSync(output, 'utf8'));
    assert.equal(result.status, 'evaluated', result.logPath ?? result.detail);
    assert.equal(result.cases.length, count);
    const failed = result.cases.filter(item => !item.passed).map(item => item.id).sort();
    assert.deepEqual(failed, expectedFailures.sort(), JSON.stringify(result.cases.filter(item => !item.passed)));
    console.log(`${name}: ${result.passed}/${result.total} passed${failed.length ? ' (expected: ' + failed.join(', ') + ')' : ''}`);
    return result.cases.map(item => item.id);
}

/** Stages plain production code so the grader builds the same files a candidate would receive. */
function stage(name, source) {
    const submission = path.join(scratch, name);
    fs.mkdirSync(submission);
    fs.writeFileSync(path.join(submission, 'SyncEngine.cs'), source);
    return submission;
}

const previous = fs.readFileSync(path.join(root, 'reference/r2/SyncEngine.cs'), 'utf8');
assert.equal(previous.replace(/\r\n/g, '\n'), forRevision(reference, 2),
    'The staged revision 2 predecessor does not match the reference.');

const baseline = validate('seed', path.join(root, 'seed'), 0, 5);
const first = validate('reference-r1', stage('r1', forRevision(reference, 1)), 1, 10);
const second = validate('reference-r2', stage('r2', previous), 2, 16);
const thirdSource = forRevision(reference, 3);
const third = validate('reference-r3', stage('r3', thirdSource), 3, 22);
for (const [earlier, later] of [[baseline, first], [first, second], [second, third]]) {
    assert.deepEqual(later.slice(0, earlier.length), earlier, 'A case ID changed across revisions.');
}

// The early version's acknowledgement ordering would save a late acknowledgement after cancellation.
const checkpointBoundary = 'cancellationToken.ThrowIfCancellationRequested();\n            if (!acknowledged)';
assert.equal(thirdSource.split(checkpointBoundary).length, 2, 'Expected one post-write cancellation check.');
const mutant = thirdSource.replace(checkpointBoundary, 'if (!acknowledged)');
validate('missing-post-write-cancellation', stage('mutant', mutant), 3, 22,
    ['cancel.late-acknowledgement-replays-without-duplicate-effects',
        'cancel.asynchronous-late-acknowledgement-stops-batch']);
console.log(`Validation artifacts: ${path.relative(process.cwd(), scratch)}`);
