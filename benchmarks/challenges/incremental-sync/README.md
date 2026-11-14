# Incremental sync

This .NET 10 challenge starts with an ordered writer. Revision 1 adds durable checkpoints, revision 2 adds isolated progress for independent tenants, and revision 3 handles cancellation between a durable write and a checkpoint. It tests whether a fresh maintainer can preserve the acknowledgement boundary while changing the behavior around it.

Candidate inputs are the seed or the assigned predecessor, the task for the current revision, and any prior requirements. For a matched revision 3 comparison, use `reference/r2/SyncEngine.cs` with the project and NuGet configuration from `seed/`. It is a plain revision 2 snapshot. The grader and complete reference belong in the evaluator.

Validate the fixture from the repository root with Node.js 22+ and the .NET 10 SDK.

```sh
node benchmarks/challenges/incremental-sync/validate.cjs
```

The command grades the seed and all three reference revisions. It also removes the post-write cancellation check and verifies that the late-acknowledgement cases fail. Cases retain their IDs across revisions so the evaluator can report regressions.

To grade a particular snapshot, supply its source directory, the target revision (0–3), and a writable result path.

```sh
node benchmarks/challenges/incremental-sync/grader/evaluate.cjs path/to/submission 3 .replay/incremental-sync.json
```

The grader builds flat candidate C# files with the fixture's fixed project and runs deterministic checks. Run candidate code and grading in disposable workspaces when executing unreviewed submissions.
