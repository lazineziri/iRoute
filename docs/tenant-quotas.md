# Tenant quotas and HTTP safeguards

Experimental admission/accounting controls are available inside the existing
six-project system. No Redis service, new host, or additional project is required.
SQLite remains the local single-process backend; PostgreSQL is required for
multi-replica enforcement. Apply migrations before starting updated workers.

## Enable quotas

Quotas default to **disabled**, preserving existing local usage. Enable them on
every API/worker process with identical configuration:

```text
TenantQuotas__Enabled=true
TenantQuotas__WindowSeconds=60
TenantQuotas__Default__MaxConcurrentExecutions=8
TenantQuotas__Default__MaxConcurrentModelCalls=4
TenantQuotas__Default__MaxModelAttemptsPerWindow=60
TenantQuotas__Default__MaxTokensPerWindow=120000
TenantQuotas__FramingTokenReserve=4096
```

An optional positive `TenantQuotas__Default__MaxCostPerWindow` enables monetary
admission checks. `TenantQuotas:Tenants:<tenant-id>` overrides the complete policy
for that tenant; unspecified fields receive policy defaults, not the customized
default policy's values. Tenant keys are exact/case-sensitive. Prefer an
`appsettings` JSON tenant map for IDs that do not fit environment-variable keys.
Invalid windows, limits, and tenant keys fail configuration validation.

## Reservation and accounting

Each configured native/generic provider attempt reserves usage before inference,
including resilience fallback attempts. Quota rejection returns the typed
`tenant_quota_exceeded` gateway failure and cannot fall back around the policy.
Exact cache/state reuse makes no provider reservation. Deterministic development
gateways are local fixtures and do not consume real provider quota.

Reservations use UTF-8 input/context bytes, a configurable framing allowance,
and the effective task output budget. This is deliberately conservative relative
to the compiler's bytes/4 estimate, but is **not** exact provider tokenization or
a guarantee about arbitrary gateway overhead. Actual complete input + output
usage replaces the reservation on success or a reported overrun. Cache/reasoning
subsets are not counted twice. Interrupted streams, cancellation, and failures
without complete usage retain the full reservation; lease expiry frees concurrency
without fabricating zero usage. Reconciliation is idempotent and runs even when
the caller cancels. Storage failure fails the operation instead of bypassing admission.

Rate/token/cost accounting uses UTC-aligned **fixed windows**, charged to the
attempt's admission window, not a rolling window or lifetime total. Boundary
bursts are possible. All replicas must share policy, database, window size, and
synchronized clocks. A policy change does not rewrite existing reservations.
Old expired reservations are cleaned on the tenant's next accepted admission.

Costs use explicitly configured normalized token prices, not a billing invoice;
cached/written token pricing, tool charges, and arbitrary gateway surcharges may
differ. Unknown prices or subscription cost fail closed when a monetary quota
is configured. Unknown cost never becomes free usage. Monetary values are stored
rounded upward to micro-units for portable SQLite/PostgreSQL aggregation.

These are admission and accounting controls, **not hard provider spend caps**.
Actual output can exceed a reservation and is then charged without a refund.
ChatGPT plan requests cannot send `max_output_tokens`; their output limit is a
post-completion acceptance check. Use provider/account usage controls as well.
See [OpenAI preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)
and [token counting](https://developers.openai.com/api/docs/guides/token-counting).

## Fair queued execution dispatch

`TenantQuotas__FairSchedulingEnabled=true` is the default. Durable claims choose
the least recently dispatched eligible tenant, then its oldest available work.
Persisted dispatch state survives restarts. When quotas are enabled, active
execution leases also obey the tenant execution-concurrency limit. Expired leases
can be reclaimed; stale workers cannot renew/complete or append fenced events.
Quota-enabled claims use the fair durable path even if the fairness flag is off.

Fair dispatch applies to the durable worker queue, not arbitrary embedded
synchronous callers, workflow-step ordering, or weighted tenant priorities.
Provider concurrency is enforced separately per attempt. Small contention and
starvation regression tests pass; high-scale fairness and tail latency remain
unvalidated. Do not interpret this alpha as a production scheduling SLA.

## HTTP limits

The native ASP.NET host defaults to:

```text
ApiLimits__MaxBodyBytes=1048576
ApiLimits__RequestsPerMinute=120
```

Kestrel caps known-length and chunked bodies; known oversize bodies are rejected
before JSON binding with HTTP 413. API/model-health requests use a no-queue fixed
window limiter and HTTP 429 with `Retry-After`. Verified JWT tenant claims create
separate partitions; all anonymous/development requests share a bucket, so
changing untrusted tenant headers cannot escape it. Basic health/static endpoints
remain available. Development identity headers still require operator-enforced
loopback binding; they are not authentication.

HTTP rate limits are **per process**, not distributed ingress protection. Configure
authenticated ingress/WAF limits for multi-replica production; durable provider
quotas do not bound queued storage, all memory use, or open SSE connections.

## Migrations, verification, and remaining work

`20261001010000_ArtifactConfidence` preserves existing artifact content and leaves
old confidence null. Cached legacy results regenerate and persist verified
reported confidence rather than being promoted to certainty.
`20261001020000_TenantQuotas` creates the reservation/account/dispatch tables.
Upgrade and rollback/re-upgrade are tested against SQLite and PostgreSQL. A
rollback discards confidence metadata and quota ledger/dispatch state: stop all
writers and take a backup first. Prefer application rollback with additive schema
left intact; dropping a ledger resets accounting and must not be used to evade limits.

Normal tests do not call live models. To enable the six PostgreSQL tests, point
`IROUTE_POSTGRES_TEST_CONNECTION` at a **disposable database** whose user may create
and drop schemas. Each test owns a uniquely generated schema and removes only
that schema. CI provides a fresh PostgreSQL service. See the
[hardening results](benchmarks/2026-10-01-hardening.md) for observed coverage.

Still required before beta: sustained multi-tenant load/soak, independent quality
grading, failover/restore drills, quota operations/telemetry, complete identity and
public-operation compatibility coverage, and live API-key billing validation.
