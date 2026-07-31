# Evaluation and regression

W10 has two complementary suites:

- `fixtures/email.draft.jsonl` drives a live API and verifies the end-to-end W03-W09 runtime path.
- `datasets/builtin-tasks.v1.json` is the versioned offline golden suite. It covers every built-in task with normal, edge, adversarial, stale-memory, unauthorized-action, and dependency-change cases, checked reference observations for the reference and candidate policies, and cost/latency benchmark samples.

The task evaluator registry in `tools/evaluation-harness.mjs` applies task-specific output structure plus golden assertions, evidence precision/coverage, unsupported-claim, terminal-status, authorization, and side-effect checks. It measures quality, safety, tokens, model/tool calls, cost, and latency per completed task. The checked JSON and Markdown routing comparison reports live in `reports/`.

Run the deterministic merge gate with:

```bash
npm run test:regression
```

The gate fails when a built-in task or required scenario is missing, candidate quality falls below its task floor, safety fails, cost or latency rises without the configured quality gain, the candidate policy source fingerprint changes, or either checked report is stale. After recording fresh observations in the dataset, regenerate both reports with `npm run eval:write`; never refresh a report merely to bypass a failing result. Benchmark values are versioned regression inputs, not production SLOs.

## Live behavioral evaluation

`fixtures/email.draft.jsonl` covers successful small-profile generation, exact artifact reuse, fail-closed route eligibility, and model-call budget enforcement. The live runner also exercises the W04 `email.send` gate, the W05 project-state lifecycle, W06 no-model resolution, W07 context compilation, W08 routing, and W09 model gateway. W09 checks normalized health, deadline propagation, gateway lifecycle events, configured identity, transport, and observed latency. Streaming deployments must also emit the payload-free `gateway.streamed` aggregate.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.

To check external gateways independently, run `node tools/check-gateway-contract.mjs` with `IROUTE_GATEWAY_URL` and `IROUTE_GATEWAY_TRANSPORT=Buffered|Streaming`. The W09 conformance suite verifies two separately identified external implementations against the same request, result, health, usage, failure, and cancellation semantics.
