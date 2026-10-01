# Project status

Updated for the `0.1.0-alpha.4` development line.

## Repository baseline

- .NET-only source tree with five production projects and one test project.
- One Runtime executable/composition root for API, worker, migration, and client
  modes.
- One public contract project (`iRoute.Common`), enforced by architecture tests.
- One OCI image and five coordinated NuGet artifacts.
- CI gates formatting, build, tests, tool installation, packages, Compose,
  container commands, migrations, health, OpenAPI, dashboard, and task completion
  against SQLite and PostgreSQL. CodeQL and Gitleaks run separately.

## Implemented product capabilities

- Durable task submission, tenant-scoped idempotency, ordered events, replayable
  SSE, cancellation, approvals, external-action reconciliation, and artifacts.
- SQLite and PostgreSQL persistence with explicit migrations, durable execution
  claims, fenced leases, heartbeats, checkpoints, and crash takeover.
- Exact-result, artifact, fact, decision, and deterministic-handler resolution
  before model use.
- Bounded evidence/context compilation, explainable direct routing, bounded
  planning, model-profile provenance, quality escalation, and validation.
- Generic buffered/streaming model gateway, normalized results/failures, multiple
  registered routes, deterministic fallback, persistent circuits, and fenced
  half-open probes.
- Native .NET OpenAI Responses and Anthropic Messages adapters; local Claude
  subscription execution through the unmodified official CLI. Native ChatGPT
  dynamic registration, sign-in, account selection, rotating refresh, revocation,
  and streaming plan transport are implemented for local macOS/Linux use.
- Focused execution services and thin cancellation/reconciliation endpoints;
  independent-actor approval enforcement and revalidation.
- Normalized capability execution and reference adapters for email, calendar,
  read-only database, registered OpenAPI, MCP, and agent results.
- Artifact/memory TTL, supersession, dependency invalidation, cold archival,
  tenant record bounds, and background lifecycle cleanup.
- JWT identity, development identity headers, permission scopes, OpenTelemetry,
  health checks, bounded/redacted observability APIs, and operator dashboard.
- Opt-in durable tenant provider-attempt concurrency/rate/token/cost admission,
  reservation/reconciliation, lease-expiry recovery, and fair queued dispatch.
  Native ASP.NET body-size and per-process HTTP rate limits are enabled by default.

## Verification currently in the repository

- Architecture dependency and contract-placement tests.
- Core orchestration behavior test coverage.
- SQLite-backed execution/idempotency/approval/cancellation/reconciliation tests,
  native provider protocol/error/budget tests, subscription isolation/billing
  fallback tests, and gateway schema/snapshot checks. These use fake providers,
  not paid or authenticated inference.
- Compiler/analyzer/style enforcement with warnings as errors.
- Package filename/count checks, local tool installation, SQLite/PostgreSQL
  migration checks, and API/worker execution smoke tests in CI.
- SSE completion replay, idempotent resubmission, artifact lookup, and
  cross-tenant denial checks in the shared runtime smoke action.
- Explicit CodeQL build and full-history secret scanning.
- Opt-in live product comparisons on four non-Astra ChatGPT models: 36 paired
  synthetic scenarios, provider token counts, factual assertions, warm reuse,
  and changed-source regeneration. See [results and limits](product-benchmark.md).
- Optimization regressions for compatible artifact policy, tenant/project
  isolation, TTL/dependency invalidation, deduplication, source lifecycle, source
  expiry, and zero-model state lookups.
- Source producer-version ordering across request context and persisted state,
  decoded Unicode relevance, and persisted cache confidence (unknown legacy
  confidence regenerates rather than becoming `1`).
- Six PostgreSQL contention/fencing/fairness/upgrade tests against isolated
  schemas, now enabled in CI; SQLite upgrade/rollback and loopback HTTP 413/429
  tests including chunked-body and forged-header cases.
- Repeated native correctness checks: 32 ChatGPT outputs and eight Claude Sonnet
  CLI outputs. [Hardening results](benchmarks/2026-10-01-hardening.md) distinguish
  deterministic factual rubrics from broad semantic or safety certification.

The verification set is intentionally small after the repository rewrite. It
must grow around routing, persistence, identity, migrations, gateway circuits,
approvals, concurrency, and public-contract compatibility before beta.

## Known gaps before beta

- Quota policy operations/telemetry, larger scheduling/tenant-load validation,
  distributed ingress limits, and real API-key billing validation. Current quotas
  are admission/accounting controls, not guaranteed provider consumption caps.
- Production connector implementations and connector-specific security review.
- Sustained load, soak, cancellation-race, database failover, backup/restore, and
  multi-replica chaos validation.
- Stronger distributed external-action reconciliation and running-action race
  validation. Self-approval is now denied by policy.
- Broader independent quality grading and load validation. Four ChatGPT models
  and the native Claude CLI adapter have exploratory end-to-end evidence, not
  production certification. Windows credential
  storage and additional credential-storage hardening (OS keychains) remain open.
- Complete public operation/fixture compatibility coverage and automated
  OpenAPI/schema drift detection.
- Production security hardening such as distributed ingress policy, dependency
  review enforcement, and an incident-response SLA.
- Measured provider/model quality, latency, safety, and cost evidence. Built-in
  deterministic/synthetic profiles are not production measurements.

## Next milestone

Tenant quota/fairness foundations are implemented and tested locally. Completing
their operational, load, and security validation is the next milestone. Ownership
remains inside the current boundaries:

- quota/fairness contracts and ports in Common;
- reservation, reconciliation, and scheduling policy in Services;
- PostgreSQL state and atomic enforcement in Data;
- configuration, endpoints, telemetry, and composition in Runtime;
- behavior, contention, and architecture coverage in Tests.

Beta is not declared until the quota milestone and the security/load validation
above have evidence, not only code paths.
