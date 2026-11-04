# Workflow variants

All four variants receive the same common prompt and the same current revision requirements. B and C each introduce one bundle of instructions. D combines those exact bundles. A receives the common prompt and task requirements only. Its frozen file records that decision for the experiment.

| Variant | Additional instruction |
| --- | --- |
| [A · Task only](A.txt) | None |
| [B · Style only](B.txt) | Useful chunk explanations, meaningful names, and accurate XML documentation |
| [C · Process only](C.txt) | Record a brief plan before implementation, then review and validate the change |
| [D · Style and process](D.txt) | B and C together |

Run `node harness/prompt.mjs import 3 B` from the repository root to reconstruct a prompt from the frozen inputs. The prior task specifications were separate files in `.benchmark/` for revisions 2 and 3.
