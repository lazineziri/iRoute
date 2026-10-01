# Correctness and hardening follow-up — 1 October 2026

Local `0.1.0-alpha.4` development tree; **uncommitted and unpublished**. This is
verification evidence, not beta certification or a new savings claim.

## Implemented fixes and safeguards

- Positive producer versions determine source ordering, including delayed writes
  on independent database connections and newer stored memory versus an older
  incoming request. Equal/older/unversioned replacements cannot overwrite a
  known newer version. Producer versions are distinct from store-local versions.
- Unicode relevance uses decoded JSON values and Unicode normalization/letter
  matching rather than raw escaped JSON and ASCII-only words. Japanese, Arabic,
  and Chinese older-history regressions pass; this is not full semantic retrieval.
- Artifact confidence is persisted and reused without promotion to `1`.
  Legacy-null confidence regenerates, then subsequent reuse retains its value.
- Context/provider budgets are clamped consistently to task definitions.
- Durable tenant reservation/reconciliation and fair dispatch stay inside the
  existing projects. Serializable enforcement, unknown-usage retention, crash
  lease expiry, policy denial, fallback suppression, streaming/disposal, and
  native HTTP body/rate protections have regression coverage.
- PostgreSQL serialization failures wrapped by EF are recognized and retried as
  complete transactions. SQLite confidence rollback uses supported native DDL.
- Claude CLI and Anthropic input counts include cache reads/writes. Breakdown
  fields remain subsets, not additional total usage. The accounting follows the
  [official Anthropic usage definition](https://platform.claude.com/docs/en/api/typescript/messages).

## Offline and storage verification

The full Release suite with an explicit disposable PostgreSQL connection passed:
**228 passed, 0 failed, 4 live-only tests skipped (232 total)**.

Six PostgreSQL tests cover 12 competing execution claimers, stale-worker fencing
after expiry/takeover, a 21-item busy tenant versus a one-item small tenant,
16 competing reservations capped at three, 12 competing producer versions,
and upgrade/rollback/re-upgrade preserving legacy artifact content. Each test
creates/removes only its own private schema. These tests are now enabled by the
CI job's disposable PostgreSQL service; hosted CI has not been run from this
uncommitted checkout.

SQLite additionally covers atomic reservation/reconciliation, legacy migration
upgrade/rollback, and confidence regeneration. Real loopback HTTP tests verify
413 for known-length and chunked bodies, 429 with `Retry-After`, resistance to
forged-header rate-limit evasion, and independent basic health availability.
Routing/two-step workflow tests use controlled providers and synthetic profiles;
they verify eligibility, escalation selection, call/depth budgets, usage aggregation,
and stop-on-failure, not measured quality of different real model tiers.

The Release build/analyzers and formatting check pass. All five coordinated NuGet
packages were built locally. A freshly installed tool's main, client, migration,
and ChatGPT-auth help commands also passed. The rebuilt non-root image passed the repository's
actual shared CI smoke action on SQLite and PostgreSQL with a separate worker:
migrations/current status, command modes, health, OpenAPI/dashboard, queued task
completion, artifacts, SSE replay, idempotency, tenant denial, approval separation,
cancellation, and reconciliation permissions. These smoke tests use deterministic
reference adapters, not external connector side effects.

## Repeated live correctness checks

Four synthetic scenarios, each repeated twice with independent cold project
state: active-version conflict, unpaid invoice status, an older Japanese approval
fact, and an archived instruction attempting unauthorized action claims/stale facts.
Each used the full execution pipeline and delivered JSON fields. The deterministic
grader has controls rejecting negation, wrong status, metadata-only facts, and
affirmative unauthorized action claims. It permits explicit rejection/quotation
of malicious text. There is no direct-model baseline in this follow-up.

| Native route | Outputs passed | Input tokens | Output tokens | Inference transport observation |
|---|---:|---:|---:|---|
| `gpt-5.6-sol` | 8/8 | 1,348 | 320 | 8 Responses HTTP calls |
| `gpt-5.6-terra` | 8/8 | 1,348 | 335 | 8 Responses HTTP calls |
| `gpt-5.6-luna` | 8/8 | 1,348 | 505 | 8 Responses HTTP calls |
| `gpt-5.5` | 8/8 | 1,348 | 400 | 8 Responses HTTP calls |
| Claude Code `sonnet` alias | 8/8 | 9,494 | 1,437 | 8 CLI inference invocations; HTTP count unknown |

Astra was excluded. ChatGPT used iRoute's own signed-in session and low reasoning.
Claude used the official subscription login with safe mode, no external tools/MCP,
no session persistence, and one allowed CLI turn. Its installed `sonnet` alias was
not pinned to a dated model snapshot. Dollar cost is unknown for both subscription
routes, and token counts/overhead are not a cross-provider price comparison.
Native provider execution does not turn synthetic routing profiles into measurements.

Raw reports are ignored local artifacts:

- ChatGPT: `artifacts/benchmarks/run-e5ab0ffe7a564cceba10a6c97b1ba8fa/results.json`.
- Final Claude: `artifacts/benchmarks/run-7979f659c5e54ea8bdada4861cabb6d8/results.json`.
- Initial Claude: `artifacts/benchmarks/run-93113a8803794d64965c3d8247e61f4f/results.json`.

The first Claude run completed all eight inferences, but the original rubric
incorrectly failed two safe quotations of rejected instructions. That report is
preserved. After adding rejection-versus-action-claim controls, a fresh eight-case
run passed. **Total usage for this follow-up was 32 ChatGPT HTTP calls plus 16
Claude CLI inference invocations**, including the initial grading attempt.

## Limits and remaining work

These small, repeated factual checks are not independent human semantic grading,
representative customer evaluation, calibrated confidence, adversarial safety
certification, or sustained-load evidence. The
[original paired benchmark](2026-10-01-initial.md) remains the exploratory savings
comparison; this follow-up does not revise or extrapolate its savings.

OpenAI and Anthropic **API-key inference was not run**: neither key was present.
Native protocol tests use controlled responses; subscription runs do not validate
API-dollar billing. Configure keys locally, never paste them into chat, for that
remaining check. Claude's HTTP request count is not inferred from logical model calls.

Quotas are opt-in admission/accounting safeguards, not guaranteed spend caps.
ChatGPT plan usage does not accept `max_output_tokens`, so actual overrun must be
accounted after completion; see [official preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).
HTTP limits are per-process. Consult [quota operations](../tenant-quotas.md) before
enabling them on multiple replicas or changing windows/policies.

Beta still needs larger/long-running tenant-load and fairness tests, quota
operations/telemetry, real API-key billing validation, distributed ingress limits,
failover/backup/restore drills, independent quality/safety grading, production
connector security review, and broader identity/public-operation compatibility
coverage. No credentials, registry packages, releases, or production deployment
were modified or published by this work.
