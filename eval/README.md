# Evaluation fixtures

`fixtures/email.draft.jsonl` covers successful generation, exact artifact reuse, fail-closed quality validation, and model-call budget enforcement. The evaluation runner also exercises the W04 `email.send` gate, the W05 project-state lifecycle, and W06 no-model resolution: permission-checked decision retrieval, explicit artifact lookup, zero model calls, and accepted/rejected resolver reasons.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.
