# Follow-up diagnostics

The frozen graders tied. Source review found two places where an additional executable probe could tell us more. These diagnostics were defined after the experiment and scored separately.

With Node.js 22 or later and the .NET 10 SDK installed, run both diagnostics from the repository root.

```sh
node experiments/001-four-variants/diagnostics/replay.cjs
```

Pass `cache` or `import` to run just that diagnostic. The script compiles against saved submissions, compares behavior with the original observations, and removes its temporary build output. It does not call a model.

## Stale data after a refresh fails

The revision 2 requirement allows fallback when a loader reports a transient failure and the prior entry still qualifies. Each probe seeds a value, advances a scripted clock to 45 seconds, and reads again. The retained-entry control returned the stale seed in all eight submissions.

| Variant | Loader invalidates or clears the entry, then throws | Loader succeeds, then the completion timestamp read throws |
| --- | --- | --- |
| A | Propagates the loader exception | Returns the stale seed |
| B | Propagates the loader exception | Propagates the clock exception |
| C | Returns the stale seed | Propagates the clock exception |
| D | Propagates the loader exception | Propagates the clock exception |

Revisions 2 and 3 agreed for every variant. The invalidation and clearing probes use a sequential callback through the injected loader. The requirement excludes concurrency and does not explicitly define reentrancy, so these results depend on interpreting eligibility at the point of fallback. The clock probe distinguishes an exception from the loader from the same exception type raised by a separate dependency. [Recorded observations](cache/observed.json)

## Trailing NUL in an import quantity

All four revision 3 versions accepted `1\0` as the row's numeric delta. They applied one unit and reserved the operation ID, so a later valid row with that ID was rejected. All four rejected `1\0` followed by `2` with `InvalidQuantity`, left the store empty, and accepted the valid retry. A `+1` control passed in all four versions.

Under a strict reading of the signed decimal grammar, both NUL cases should be rejected. These observations were recorded on .NET 10.0.14. [Recorded observations](import/observed.json)
