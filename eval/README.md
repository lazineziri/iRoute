# Evaluation fixtures

`fixtures/email.draft.jsonl` is the first executable behavioral gate. It covers successful generation, exact artifact reuse, fail-closed quality validation, and model-call budget enforcement.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.
