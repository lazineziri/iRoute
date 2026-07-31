# Evaluation fixtures

`fixtures/email.draft.jsonl` covers successful generation, exact artifact reuse, fail-closed quality validation, and model-call budget enforcement. The evaluation runner also exercises the W04 `email.send` gate, the W05 project-state lifecycle, W06 no-model resolution, and W07 context compilation: ranked project state, explicit artifact sections, duplicate and full-history exclusion, serialized token bounds, complete provenance, and the context audit event.

Start the API on `http://localhost:8080`, then run:

```bash
node tools/run-evaluation.mjs
```

Set `IROUTE_BASE_URL` to evaluate another deployment. Fixtures contain no credentials or production data.
