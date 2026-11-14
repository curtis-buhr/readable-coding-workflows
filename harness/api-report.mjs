import { readFile } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

/** Summarize cumulative candidate expense with failed attempts and unknown usage retained. */
export function summarize(runs) {
  const groups = new Map();
  for (const row of runs) {
    const key = `${row.track}/${row.variant}`;
    const group = groups.get(key) ?? { track: row.track, variant: row.variant, revisions: [] };
    const previous = group.revisions.at(-1);
    const requests = row.requests ?? [];
    const known = requests.filter(request => request.usage);
    const rowTokens = known.reduce((sum, request) => sum + request.usage.totalTokens, 0);
    const knownCost = requests.every(request => Number.isFinite(request.estimatedCostUsd));
    const successful = row.accepted === true;
    group.revisions.push({
      revision: row.revision,
      status: row.evaluationError ?? row.evaluation?.status ?? row.status,
      accepted: successful,
      passed: row.evaluation?.cases.filter(item => item.passed).length ?? null,
      checks: row.evaluation?.cases.length ?? null,
      regressions: row.regressions?.length ?? null,
      cumulativeAccepted: (previous?.cumulativeAccepted ?? 0) + Number(successful),
      cumulativeCandidateSeconds: (previous?.cumulativeCandidateSeconds ?? 0) + (row.elapsedMs ?? 0) / 1_000,
      cumulativeObservedTokens: (previous?.cumulativeObservedTokens ?? 0) + rowTokens,
      cumulativeRequestsMissingUsage: (previous?.cumulativeRequestsMissingUsage ?? 0) + requests.length - known.length,
      cumulativeEstimatedUsd: previous?.cumulativeEstimatedUsd === null || !knownCost ? null :
        (previous?.cumulativeEstimatedUsd ?? 0) + requests.reduce((sum, request) => sum + request.estimatedCostUsd, 0),
      cumulativeBudgetUsd: (previous?.cumulativeBudgetUsd ?? 0) + (row.budgetUsd ?? 0),
    });
    groups.set(key, group);
  }
  return [...groups.values()];
}

async function main() {
  const [path, flag, ...extra] = process.argv.slice(2);
  if (!path || (flag && flag !== '--json') || extra.length) throw new Error('Usage: node harness/api-report.mjs <results.json> [--json]');
  const result = JSON.parse(await readFile(path, 'utf8'));
  if (result.experiment !== '002-workflow-efficiency' || !Array.isArray(result.runs)) throw new Error('Expected Experiment 002 results');
  const summary = summarize(result.runs);
  if (flag) {
    console.log(JSON.stringify(summary, null, 2));
    return;
  }
  console.table(summary.flatMap(group => group.revisions.map(revision => ({ track: group.track, variant: group.variant,
    revision: revision.revision, status: revision.status, accepted: revision.accepted,
    checks: revision.checks === null ? '—' : `${revision.passed}/${revision.checks}`,
    regressions: revision.regressions ?? '—', candidateSeconds: revision.cumulativeCandidateSeconds.toFixed(1),
    observedTokens: revision.cumulativeObservedTokens, missingUsage: revision.cumulativeRequestsMissingUsage,
    estimatedUsd: revision.cumulativeEstimatedUsd?.toFixed(4) ?? 'unknown',
    budgetUsd: revision.cumulativeBudgetUsd.toFixed(4),
  }))));
}

if (process.argv[1] && fileURLToPath(pathToFileURL(resolve(process.argv[1]))) === fileURLToPath(import.meta.url)) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
