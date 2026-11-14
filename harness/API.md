# Run the workflow-efficiency pilot

Use Node.js 22 or later and a local Docker engine running Linux containers. The image supplies .NET SDK 10.0.403, Node.js, and the shell and tar utility used to stage files. The runner records the immutable local image ID with the result. [Experiment design](../experiments/002-workflow-efficiency/)

```sh
node harness/api-run.mjs --dry-run
docker build -f harness/Dockerfile -t workflow-benchmark:dotnet10 harness
node harness/api-run.mjs --preflight --image workflow-benchmark:dotnet10
```

Dry run prints the planned cells and input hashes. Preflight compiles the seed and grades the seed and common predecessor inside disposable offline containers. It uses no API credentials. The image build can retrieve base images.

Set `OPENAI_API_KEY` through your shell or secret manager to a project key from the API organization you intend to bill. A claimed Pro API grant applies to the linked organization. The runner calls the Responses API directly. [API keys and service accounts](https://developers.openai.com/api/docs/guides/api-keys) · [API credit coverage](https://developers.openai.com/api/docs/guides/chatgpt-api-credits#what-the-credits-cover)

```sh
node harness/api-run.mjs --run --image workflow-benchmark:dotnet10 --max-usd 5
node harness/api-report.mjs .api-runs/002-<run-id>/results.json
```

Use the results path printed by the runner in the report command. Add `--json` for the cumulative series. The `$5` argument is an example local model-cost budget. The runner reserves against the configured token limits before each generation and retains the reserve when usage is unavailable. The estimate uses standard global GPT-6.1 Sol rates and reported cache counters. Reconcile with Platform usage for billed costs and set the project spend limit if you need billing enforcement. The local budget governs admission and estimates, and service-side enforcement can lag. [Model rates](https://developers.openai.com/api/docs/models/gpt-6.1-sol) · [Spend limits](https://developers.openai.com/api/docs/guides/spend-limits)

The agent can list and read staged files, edit C# source, and request a fixed offline build. Docker runs builds and graders in separate credential-free containers with network access disabled, bounded resources, and container-owned scratch space. Grader files and other variants stay outside the candidate tool view. Only passing snapshots move to the next revision. Each run writes source snapshots and usage metadata under the ignored `.api-runs/` directory. The API loop keeps request history in memory for the duration of a cell, with `store: false`. API retention still follows the platform data policy. [API data controls](https://developers.openai.com/api/docs/guides/your-data)

Before publishing results, review generated source for third-party license notices, identify the generated material, and export the case outcomes and measurements needed for replay. [OpenAI Service Terms](https://openai.com/policies/service-terms/) · [Sharing and Publication Policy](https://openai.com/policies/sharing-publication-policy/)
