# Experiment 002 · Workflow efficiency

The goal is to choose the shortest useful workflow for code a developer and an agent will take turns maintaining. Experiment 001 showed that Style Only improved the anonymous source review while using more tokens than Task Only. Its variants carried both their code and their instructions into later revisions, so it left the effects of each uncertain.

## Pilot design

The [manifest](manifest.json) fixes GPT-6.1 Sol at Extra High, the three prompts, the order of the cells, and per-attempt limits. All cells use the same tool definitions and service tier. The shorter prompt retains chunk explanations, the five-line cue, descriptive names, and `///` coverage for private helpers. It also removes details and examples, so this compares two instruction bundles rather than estimating the isolated effect of prompt length. The variants are Task Only, [Style Only](../../workflows/B.txt), and [Short Style](../../workflows/short-style.txt).

| Comparison | Sessions | Design |
| --- | ---: | --- |
| Inherited code | 9 planned | Each variant implements revision 1 from the same seed. A fresh agent applies revisions 2 and 3 with the same task-only instructions to its own variant's passing predecessor. |
| Instructions during the change | 3 planned | Each variant implements revision 3 from the same validated revision 2 snapshot. Only the instructions differ. |

The [incremental sync challenge](../../benchmarks/challenges/incremental-sync/) begins with a small .NET library. The revisions add durable checkpoints, independent progress per tenant, and idempotent retries when cancellation arrives after a durable write and before the acknowledgement is checkpointed. The fixed grader covers ordering, retry behavior, and tenant isolation. A deliberately modified reference verifies that the grader catches a missing cancellation check.

Each cell starts with a fresh Responses API history and a copy of the authorized source. The runner records any blocked successor when a predecessor fails. It uses one attempt per cell in the pilot. Any later recovery run should have a separate ID and include the original attempt's expense. This first pass uses one fixture and one run per cell. The original Codex CLI results provide historical context.

## Measures and decision

- **Correctness.** Require a buildable submission and passing frozen checks. Track a regression when a previously passing case ID fails. Report incomplete and blocked cells alongside passes.
- **Agent effort.** Compare cumulative candidate time, observed tokens, and estimated API cost after each revision. Include failed attempts and mark missing usage. Compare the initial style premium with any savings on the subsequent task-only handoffs. Report the observed cumulative difference before extrapolating a break-even point.
- **Human handoff.** For a follow-up with reviewers, mask variant labels and rotate equivalent tasks so a reviewer sees a behavior question only once. Ask the reviewer to identify the checkpoint condition, predict which writes a retry repeats, and explain which tenant can advance after a partial failure. Measure active time and accuracy before showing the expected answer. Record whether explanations or names supported the answer. Human time and API dollars are separate measures.

Prefer the instruction variant that preserves behavior and gives the clearest handoff at a cost you can justify. Use the pilot to check the tools, timing, and scoring. Freeze the preferred wording before evaluating fresh tasks across repeated runs. A later comparison can hold the implementation fixed and vary only comments or private identifiers to test what caused a difference in the inherited code.

See the [runner guide](../../harness/API.md) for the dry run, sandbox preflight, and report commands.
