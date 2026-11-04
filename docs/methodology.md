# Experiment design

The primary comparison is B (style) versus D (style and process). The style bundle specifies short explanations over meaningful chunks, XML method documentation, and descriptive identifiers. The process bundle requires a written outline before implementation and a review after editing. A and C provide the corresponding comparisons without the style bundle. The design measures the effects of the bundles together.

Each of the four synthetic C# libraries has an identical starting snapshot for its four variants. Revision 1 adds a component, revision 2 introduces a policy split, and revision 3 changes behavior that crosses multiple paths. A fresh GPT-6.1 Sol Extra High session implements each revision. The inherited code comes from that variant’s preceding snapshot. Prior revision requirements remain visible, and earlier conversation and plan files are left out of the next session.

External graders build copies of the saved submissions. A regression is a case that passed in the preceding revision and failed in the next revision under the same case ID. Cases that deliberately change with the requirements have separate IDs. The frozen suite measured 740 case executions across 48 sessions. The four challenge families are the units for comparing how the workflows behaved across related revisions.

Anonymous source review rated four dimensions on a 0–2 scale: control flow and names, inline explanations, XML method contracts, and drift in existing documentation. Reviewers saw the requirements and each sample’s revisions with the condition labels removed. Documentation style was visible in the source. XML coverage uses a separate syntax-based count of new or changed declarations with `///` comments.

The first configuration A session ran alone as a setup check. Later sessions ran in matched waves. One anonymous review copy was briefly outside the protected evaluation folder during cache revision 1. It was moved, and review of all 28 recorded shell commands from the affected sessions found no access to the copy. The session audit found 48 distinct IDs and verified the starting-source hashes against the expected seed or predecessor.

Token totals include input and output. Cached input is a subset of input, and reasoning is a subset of output. The analysis applies a fixed GPT-6.1 Sol API rate schedule to the recorded candidate tokens. It reports summed per-session time as well as elapsed time, which includes overlap between candidates. See the [pricing source](https://developers.openai.com/api/docs/models/gpt-6.1-sol).

The additional cache and import probes were designed after source review. Their results sit alongside the frozen results so readers can see which issues the original coverage missed. This experiment used one pass of the four-family matrix. The published tasks and graders support historical replay, and a future held-out evaluation will need fresh cases.
