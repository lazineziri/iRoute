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
- Normalized capability execution and reference adapters for email, calendar,
  read-only database, registered OpenAPI, MCP, and agent results.
- Artifact/memory TTL, supersession, dependency invalidation, cold archival,
  tenant record bounds, and background lifecycle cleanup.
- JWT identity, development identity headers, permission scopes, OpenTelemetry,
  health checks, bounded/redacted observability APIs, and operator dashboard.

## Verification currently in the repository

- Architecture dependency and contract-placement tests.
- Core orchestration behavior test coverage.
- Compiler/analyzer/style enforcement with warnings as errors.
- Package filename/count checks, local tool installation, SQLite/PostgreSQL
  migration checks, and API/worker execution smoke tests in CI.
- SSE completion replay, idempotent resubmission, artifact lookup, and
  cross-tenant denial checks in the shared runtime smoke action.
- Explicit CodeQL build and full-history secret scanning.

The verification set is intentionally small after the repository rewrite. It
must grow around routing, persistence, identity, migrations, gateway circuits,
approvals, concurrency, and public-contract compatibility before beta.

## Known gaps before beta

- Per-tenant concurrency, rate, token/cost quota reservation/reconciliation, and
  demonstrable fair scheduling under contention.
- Production connector implementations and connector-specific security review.
- Sustained load, soak, cancellation-race, database failover, backup/restore, and
  multi-replica chaos validation.
- Stronger distributed external-action reconciliation and explicit protection
  against self-approval where policy forbids it.
- Complete public operation/fixture compatibility coverage and automated
  OpenAPI/schema drift detection.
- Production security hardening such as request/body/rate limits, final ingress
  policy, dependency review enforcement, and an incident-response SLA.
- Measured provider/model quality, latency, safety, and cost evidence. Built-in
  deterministic/synthetic profiles are not production measurements.

## Next milestone

The next product milestone is tenant quotas and fairness. The implementation
should remain inside the current boundaries:

- quota/fairness contracts and ports in Common;
- reservation, reconciliation, and scheduling policy in Services;
- PostgreSQL state and atomic enforcement in Data;
- configuration, endpoints, telemetry, and composition in Runtime;
- behavior, contention, and architecture coverage in Tests.

Beta is not declared until the quota milestone and the security/load validation
above have evidence, not only code paths.
