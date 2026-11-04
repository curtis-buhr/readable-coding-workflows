# Improving Readability in Generated Code

*Developing and refining workflow instructions that make AI-generated code easier to read, review, and maintain.*

I want it to be easier for a developer and an AI agent to trade places in the driver’s seat. When I’m directing an agent, I want to be able to follow its work closely enough to catch mistakes, verify decisions, and take over when I need to. Short comments that explain how a chunk of code works, along with names that make the purpose of a value clear, can make that easier.

My goal is to develop a workflow I can use in my own projects. I’m refining what I ask agents to explain, how I ask them to name things, and when I ask them to plan before writing code. I try different versions of the instructions on C# tasks that evolve over several revisions, then use the results to decide what to keep, change, or drop.

The first tests showed that asking for more readable code increased token use and estimated cost. I want that extra work to pay off when I review a change or when an agent picks up the code again. I think of it the same way I think about asking a developer to go back and document a tricky change. It takes time now, and I want that investment to make later changes easier and less expensive.

## Experiment 001

| Workflow | Frozen checks | Candidate time | Tokens | API-equivalent cost |
| --- | ---: | ---: | ---: | ---: |
| A · Task only | 185/185 | 49.7 min | 2.48M | $1.78 |
| B · Style only | 185/185 | 66.4 min | 3.42M | $2.44 |
| C · Process only | 185/185 | 74.6 min | 4.10M | $2.50 |
| D · Style and process | 185/185 | 86.7 min | 4.60M | $2.85 |

The anonymous source review gave B and D the same ratings for control flow, naming, and chunk explanations. D earned a slightly higher method-contract score and used 34% more tokens than B. C and D wrote a plan before editing in all 24 sessions that requested one. For these tasks, I’d use style-only as the default and ask for a written plan when I want to review the approach before implementation.

## What this looks like in C#

In the streaming import, a canceled call must leave both stock and operation history intact. A useful explanation shows why the importer stages changes even in partial mode. That tells a reviewer which invariant to preserve when adding another mode. See the [style-only import submission](experiments/001-four-variants/submissions/import-B-r3/StockImporter.cs).

The cache gave us a different kind of evidence. All four variants passed the original suite. During source review, we noticed that a refresh could return stale data after its loader had already removed the entry. We ran the same additional probes against each submission and recorded those findings separately. This is a useful case for checking the boundary of a `catch` block and deciding which failures should trigger fallback. See [Experiment 001](experiments/001-four-variants/).

## Replay the experiment

With Node.js 22 or later and the .NET 10 SDK, run these commands from the repository root. The replay uses saved source and telemetry and requires no model account.

```sh
node harness/replay.mjs
node benchmarks/replay.cjs cache-B-r3
node benchmarks/replay.cjs --reference
```

The first command checks the saved results and reproduces the totals. The second rebuilds and grades a saved candidate. The third validates a reference solution and checks that a deliberate expiry regression is caught. See the [reproduction guide](docs/reproduction.md) for the full replay.

## How the benchmark was built

- **[Challenges](benchmarks/challenges/)** · Streaming stock imports, dock scheduling, configuration migration, and cache freshness. Each starts from an existing implementation and adds three sets of requirements.
- **[Workflows](workflows/)** · The four instruction variants. The style and process blocks are identical wherever they appear.
- **[Experiment 001](experiments/001-four-variants/)** · The 48 saved submissions, frozen checks, resource measurements, and anonymous readability review.
- **[Research and source licenses](docs/research-notes.md)** · What the papers tested, how those findings shaped the experiment, and the terms for each cited version.

Each revision used a fresh GPT-6.1 Sol Extra High session and inherited its own preceding source. The study has one run per variant and task family. The check totals describe behavior covered by the suites, and the readability ratings are qualitative. Time is the sum of candidate durations, and cost applies fixed API rates to recorded usage. See the [protocol](docs/methodology.md) for design details and limitations.

## My role

I defined the readability goals, proposed competing workflows, and asked for the tests to cover maintenance over several revisions. I used Codex to review the literature, develop the synthetic fixtures and harness, run isolated candidate sessions, and help inspect the resulting code. The repository records the prompts, evidence, and limits so readers can judge the conclusions themselves.

The project’s original materials are published under the [MIT License](LICENSE). The cited papers retain their own terms.
