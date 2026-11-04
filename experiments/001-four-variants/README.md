# Experiment 001 — Four workflow variants

Does requiring a separate implementation plan add value when the requested code style stays the same? This experiment compares style-only with style-and-process, alongside task-only and process-only controls.

| Variant | Readability instructions | Process instructions |
| --- | --- | --- |
| A | No | No |
| B | Yes | No |
| C | No | Yes |
| D | Yes | Yes |

The [workflow instructions](../../workflows/) specify the two treatments. Style asks for short explanations of meaningful code chunks, descriptive identifiers, and concise C# XML documentation. Process asks for a separate plan before production edits and a final review of the implementation against the requirements.

## Design

Four synthetic C# libraries covered caching, deployment configuration migration, stock import, and dock scheduling. Each started from a common seed and progressed through three revisions. Each revision ran in a fresh GPT-6.1 Sol session at Extra High effort, using the previous submission from the same variant. All four variants received the same revision requirements and validation instructions. The run produced 48 submissions across 16 lineages, with one repetition per family and variant.

The candidate task included the current revision specification, its workflow treatment, and the following common preamble. Previous specifications were available in `.benchmark` from revision 2 onward.

> Implement the following revision in the current workspace. Use the supplied project, existing source, and task requirements. The .benchmark folder is reserved for previous requirements and temporary scratch files. It is omitted from the submission. Edit the implementation files in this workspace and perform the task validation. Finish with a brief summary of the implementation and checks.

The original run used separate Windows sandboxes. Sibling submissions, grader source, and previous planning files were outside the candidate workspace. Configuration A revision 1 ran alone as a setup check. The remaining launches used matched waves. One review copy was briefly outside the protected evaluator directory during cache revision 1. It was moved, and an audit of the 28 recorded shell commands from those sessions found no access to the copy.

External graders evaluated completed source against fixed checks. A regression was a previously passing case with the same ID that failed at the next revision. A case intentionally replaced by a later requirement did not count as a regression. The source review used anonymized copies and a 0–2 rubric for control flow and naming, inline explanation, method contracts, and stale comments. Style itself remained visible to reviewers.

## Results

Every variant passed all 185 frozen check executions across 12 submissions. The frozen suite detected zero regressions. The 740 check executions include repeated checks across revisions and are not independent observations.

| Variant | Cumulative minutes | Tokens | Estimated API equivalent | Changed declarations with XML docs |
| --- | ---: | ---: | ---: | ---: |
| A · Task only | 49.7 | 2,484,138 | $1.78 | 0/55 |
| B · Style only | 66.4 | 3,423,759 | $2.44 | 72/72 |
| C · Process only | 74.6 | 4,101,503 | $2.50 | 9/59 |
| D · Style and process | 86.7 | 4,599,429 | $2.85 | 69/69 |

Cumulative time sums the duration of candidate attempts. Parallel execution took 93 minutes of elapsed time. Tokens are input plus output. Reasoning is included in output, and cached input is included in input. The cost estimate applies a fixed schedule of $2.00 per million uncached input tokens, $0.10 per million cache reads, $2.50 per million cache writes, and $10.00 per million output tokens. The figures give an API equivalent for the measured work. [Model pricing reference](https://developers.openai.com/api/docs/models/gpt-6.1-sol)

B and D tied on the review's control flow, naming, and chunk explanations. D scored one point higher on method contracts across four challenges. The logs recorded a separate plan before the first production edit in all 24 C and D sessions. They recorded no separate plan writes in A or B. The process instruction changed the sequence of observable work. D still used 31% more cumulative time, 34% more tokens, and 17% more estimated cost than B.

The [follow-up diagnostics](diagnostics/) exercise exception provenance, stale-cache fallback, and numeric parsing. They were created after the frozen run and have separate results. Both style variants met all additional cache expectations. The import probe found the same trailing-NUL behavior in all four variants.

This pass supports style-only for these tasks when readability is worth the extra cost relative to A. One repetition per cell and the ceiling on the frozen checks limit what the experiment can say about correctness or other models and codebases.

## Data and reproduction

- [Results](results.json) contain case-level grading, seed baselines, per-run durations and token categories, the process observation, and anonymized-review-derived notes. `predecessor` is null for a seed-based run and holds the previous run ID otherwise.
- Each run's `sourceDirectory` is relative to this directory. The recorded SHA-256 values apply to the original bytes under `submissions/`. Git attributes preserve their line endings on checkout.
- Raw transcripts, account-wide usage, session identifiers, absolute paths, and timestamps are outside the public export. Relative durations, workflow instructions, evaluator outputs, and source snapshots are retained for replay.
- [Reproduction guide](../../docs/reproduction.md) describes the replay tools. Saved outputs can be checked without model access.
