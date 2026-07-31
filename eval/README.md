# Evaluation fixtures

`fixtures/email.draft.jsonl` covers successful small-profile generation, exact artifact reuse, fail-closed route eligibility, and model-call budget enforcement. The evaluation runner also exercises the W04 `email.send` gate, the W05 project-state lifecycle, W06 no-model resolution, W07 context compilation, W08 routing, and W09 model gateway. W09 checks normalized health, deadline propagation, gateway lifecycle events, configured identity, transport, and observed latency. Streaming deployments must also emit the payload-free `gateway.streamed` aggregate.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.

To check external gateways independently, run `node tools/check-gateway-contract.mjs` with `IROUTE_GATEWAY_URL` and `IROUTE_GATEWAY_TRANSPORT=Buffered|Streaming`. The W09 conformance suite verifies two separately identified external implementations against the same request, result, health, usage, failure, and cancellation semantics.
