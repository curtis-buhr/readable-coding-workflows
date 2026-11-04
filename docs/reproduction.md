# Reproduce the saved results

Install Node.js 22 or later and the .NET 10 SDK. Run the commands from the repository root. The benchmark projects use the SDK libraries and offline NuGet configuration.

```sh
node harness/replay.mjs
node harness/replay.mjs --json
node benchmarks/replay.cjs cache-B-r3
node benchmarks/replay.cjs --family cache
node benchmarks/replay.cjs --reference
node benchmarks/replay.cjs --all
```

The harness verifies the archived source hashes and recomputes the summary from the published run data. The benchmark replay builds each submission from a copy and checks its results against the archive. The reference mode also checks that a deliberate behavior change is detected. The full replay compiles all 48 submissions and takes longer than replaying one family. Generated outputs are ignored by Git.

To design a fresh experiment, use the [challenge and workflow layout](../README.md#how-the-benchmark-was-built), keep evaluator files and other candidates outside the candidate’s readable workspace, and use fresh model sessions. Save the exact model configuration, prompts, source hashes, and token usage. The published candidate code and grader suites are available for reproducing the historical result, so use new tasks or private checks to measure generalization.
