import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const families = ['cache', 'config', 'import', 'scheduler'];
const variants = ['A', 'B', 'C', 'D'];
const revisions = [1, 2, 3];
const tokenFields = ['inputTokens', 'cachedInputTokens', 'cacheWriteInputTokens', 'outputTokens', 'reasoningOutputTokens'];
const standardUsdPerMillion = { input: 2, cachedInput: 0.1, cacheWriteInput: 2.5, output: 10 };
const defaultResults = fileURLToPath(new URL('../experiments/001-four-variants/results.json', import.meta.url));

/** Reject incomplete or inconsistent experiment records with the responsible field. */
function requireValid(condition, message) {
  if (!condition) throw new Error(message);
}

/** Index a scorecard by stable case ID. Cases retired in later revisions drop out of the comparison. */
function indexCases(cases, revision, context) {
  requireValid(Array.isArray(cases) && cases.length > 0, `${context}: missing cases`);
  const indexed = new Map();
  for (const item of cases) {
    requireValid(item && typeof item.id === 'string' && item.id.length > 0, `${context}: invalid case ID`);
    requireValid(!indexed.has(item.id), `${context}: duplicate case ${item.id}`);
    requireValid(typeof item.passed === 'boolean' && Number.isInteger(item.introduced) &&
      item.introduced >= 0 && item.introduced <= revision, `${context}: invalid result for ${item.id}`);
    indexed.set(item.id, item.passed);
  }
  return indexed;
}

/** Resolve an archived relative path without traversing a link or leaving its parent directory. */
function archivePath(directory, relativePath) {
  requireValid(typeof relativePath === 'string' && relativePath.length > 0 &&
    !/[\\:\u0000]/.test(relativePath), `Invalid archive path: ${relativePath}`);
  let current = directory;
  for (const part of relativePath.split('/')) {
    requireValid(part !== '' && part !== '.' && part !== '..', `Invalid archive path: ${relativePath}`);
    current = join(current, part);
    requireValid(!lstatSync(current).isSymbolicLink(), `Unexpected link in archive: ${relativePath}`);
  }
  return current;
}

/** Confirm the submission directory contains precisely the archived source files. */
function verifySource(directory, run) {
  requireValid(run.sourceDirectory === `submissions/${run.id}`, `${run.id}: unexpected source directory`);
  const submission = archivePath(directory, run.sourceDirectory);
  requireValid(Array.isArray(run.sourceFiles) && run.sourceFiles.length > 0, `${run.id}: missing source manifest`);
  const declared = new Set();
  for (const entry of run.sourceFiles) {
    requireValid(!declared.has(entry?.path), `${run.id}: duplicate source file ${entry?.path}`);
    declared.add(entry?.path);
    const file = archivePath(submission, entry?.path);
    requireValid(lstatSync(file).isFile(), `${run.id}: expected a source file at ${entry.path}`);
    const sha256 = createHash('sha256').update(readFileSync(file)).digest('hex');
    requireValid(sha256 === entry.sha256, `${run.id}: source hash mismatch at ${entry.path}`);
  }

  // Detect additions as well as edits and removals.
  const archived = readdirSync(submission, { recursive: true, withFileTypes: true });
  const files = archived.filter(entry => entry.isFile()).map(entry =>
    relative(submission, join(entry.parentPath, entry.name)).split(sep).join('/'));
  requireValid(archived.every(entry => entry.isFile() || entry.isDirectory()) && files.length === declared.size &&
    files.every(file => declared.has(file)), `${run.id}: unexpected entries in source archive`);
}

/** Recompute cumulative results through a revision from the recorded measurements. */
function totals(runs) {
  const usage = Object.fromEntries(tokenFields.map(field => [field, 0]));
  let seconds = 0;
  let checks = 0;
  let passed = 0;
  let regressions = 0;
  let changedDeclarations = 0;
  let documentedChanged = 0;
  for (const run of runs) {
    seconds += run.timing.seconds;
    checks += run.cases.size;
    passed += [...run.cases.values()].filter(Boolean).length;
    regressions += run.regressions;
    changedDeclarations += run.readability.changedDeclarations;
    documentedChanged += run.readability.documentedChanged;
    for (const field of tokenFields) usage[field] += run.usage[field];
  }

  // Cached input and cache writes are subsets of input. Reasoning is a subset of output.
  const uncachedInput = usage.inputTokens - usage.cachedInputTokens - usage.cacheWriteInputTokens;
  const rates = standardUsdPerMillion;
  const apiEquivalentUsd = (uncachedInput * rates.input + usage.cachedInputTokens * rates.cachedInput +
    usage.cacheWriteInputTokens * rates.cacheWriteInput + usage.outputTokens * rates.output) / 1e6;
  return {
    runs: runs.length, seconds, tokens: usage.inputTokens + usage.outputTokens, usage, apiEquivalentUsd,
    checks, passed, regressions, changedDeclarations, documentedChanged
  };
}

/** Verify the saved experiment and replay its scores without running submissions or requesting a model. */
function replay(resultsFile) {
  const data = JSON.parse(readFileSync(resultsFile, 'utf8'));
  requireValid(Array.isArray(data?.runs) && data.runs.length === families.length * variants.length * revisions.length,
    'Expected 48 runs for four challenges, four variants, and three revisions');
  requireValid(Array.isArray(data.baselines) && data.baselines.length === families.length, 'Expected four seed scorecards');

  const baselines = new Map();
  for (const baseline of data.baselines) {
    requireValid(families.includes(baseline?.family) && !baselines.has(baseline.family), 'Unknown or duplicate seed scorecard');
    baselines.set(baseline.family, indexCases(baseline.cases, 0, `${baseline.family} seed`));
  }

  const runs = new Map();
  for (const run of data.runs) {
    requireValid(run && families.includes(run.family) && variants.includes(run.variant) && revisions.includes(run.revision),
      'Invalid challenge, variant, or revision');
    const expectedId = `${run.family}-${run.variant}-r${run.revision}`;
    requireValid(run.id === expectedId && !runs.has(run.id), `Invalid or duplicate run ID: ${run.id}`);
    requireValid(Number.isFinite(run.timing?.seconds) && run.timing.seconds >= 0, `${run.id}: invalid elapsed time`);
    requireValid(tokenFields.every(field => Number.isSafeInteger(run.usage?.[field]) && run.usage[field] >= 0),
      `${run.id}: missing or invalid token usage`);
    const usage = run.usage;
    requireValid(usage.cachedInputTokens + usage.cacheWriteInputTokens <= usage.inputTokens &&
      usage.reasoningOutputTokens <= usage.outputTokens, `${run.id}: inconsistent token subsets`);
    const readability = run.readability;
    requireValid(Number.isSafeInteger(readability?.changedDeclarations) && Number.isSafeInteger(readability?.documentedChanged) &&
      readability.documentedChanged >= 0 && readability.documentedChanged <= readability.changedDeclarations,
      `${run.id}: invalid declaration counts`);
    requireValid(run.grading?.status === 'evaluated', `${run.id}: missing evaluated scorecard`);
    verifySource(dirname(resultsFile), run);
    runs.set(run.id, { ...run, cases: indexCases(run.grading.cases, run.revision, run.id) });
  }

  // Compare each run to its own immediate predecessor, starting from the common seed scorecard.
  for (const run of runs.values()) {
    const predecessor = run.revision === 1 ? null : `${run.family}-${run.variant}-r${run.revision - 1}`;
    requireValid(run.predecessor === predecessor, `${run.id}: unexpected predecessor`);
    const previousCases = predecessor === null ? baselines.get(run.family) : runs.get(predecessor)?.cases;
    requireValid(previousCases, `${run.id}: missing predecessor scorecard`);
    run.regressions = [...run.cases].filter(([id, passed]) => !passed && previousCases.get(id) === true).length;
  }

  const all = [...runs.values()];
  return {
    all: totals(all),
    variants: variants.map(variant => ({
      variant,
      cumulative: revisions.map(throughRevision => ({
        throughRevision,
        ...totals(all.filter(run => run.variant === variant && run.revision <= throughRevision))
      }))
    }))
  };
}

try {
  const args = process.argv.slice(2);
  const outputJson = args.includes('--json');
  const files = args.filter(arg => arg !== '--json');
  requireValid(files.length <= 1 && !files[0]?.startsWith('-'), 'Usage: node harness/replay.mjs [--json] [results.json]');
  const summary = replay(resolve(files[0] ?? defaultResults));
  if (outputJson) console.log(JSON.stringify(summary, null, 2));
  else {
    console.table(summary.variants.flatMap(variant => variant.cumulative.map(result => ({
      variant: variant.variant,
      through: result.throughRevision,
      runs: result.runs,
      minutes: (result.seconds / 60).toFixed(1),
      tokens: result.tokens,
      'API USD': result.apiEquivalentUsd.toFixed(4),
      checks: `${result.passed}/${result.checks}`,
      regressions: result.regressions,
      'changed XML docs': `${result.documentedChanged}/${result.changedDeclarations}`
    }))));
    const all = summary.all;
    console.log(`Verified ${all.runs} source snapshots and ${all.passed}/${all.checks} saved checks.`);
  }
} catch (error) {
  console.error(`Replay failed: ${error.message}`);
  process.exitCode = 1;
}
