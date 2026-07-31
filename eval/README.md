# Evaluation fixtures

`fixtures/email.draft.jsonl` covers successful generation, exact artifact reuse, fail-closed quality validation, and model-call budget enforcement. The evaluation runner also exercises the W04 `email.send` gate: authenticated action scope, durable approval, authorized resumption, idempotent replay, external-action audit events, and payload-free event data.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.
