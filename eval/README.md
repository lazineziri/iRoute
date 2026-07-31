# Evaluation fixtures

`fixtures/email.draft.jsonl` covers successful small-profile generation, exact artifact reuse, fail-closed route eligibility, and model-call budget enforcement. The evaluation runner also exercises the W04 `email.send` gate, the W05 project-state lifecycle, W06 no-model resolution, W07 context compilation, and W08 routing. W08 checks direct-path planner avoidance, quality-floor escalation to the strong profile, measured candidate inputs, rejection reasons, and both routing audit events.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.
