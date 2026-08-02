# Changelog

All notable user-visible changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and release versions
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) with the
additional public-contract promises in `docs/compatibility.md`.

## [Unreleased]

No changes yet.

## [0.1.0-alpha.1] - 2026-08-01

### Added

- Durable asynchronous execution submission with HTTP `202`, PostgreSQL/SQLite
  work persistence, fenced leases, heartbeats, crash takeover, checkpoint
  recovery, distributed cancellation, and approval requeueing.
- Scalable execution-worker deployments, ordered queue/lease events, and retry
  policies with timeouts, bounded exponential backoff, jitter, and
  `Retry-After` support.
- Multiple provider-neutral gateway routes with deterministic quality, cost,
  deadline, region, residency, profile, and attempt-budget fallback policy.
- Durable per-deployment closed/open/half-open circuit breakers with fenced
  probes, Retry-After-aware open intervals, multi-replica coordination,
  classified exhaustion, trace events, and resilience metrics.
- Task-aware execution across the complete built-in task registry with
  measured routing, bounded planning, validation, and materialization.
- Tenant-scoped SQLite/PostgreSQL persistence, workflow checkpoints, approvals,
  artifact/memory lineage, dependency invalidation, and lifecycle cleanup.
- Provider-neutral model gateway and normalized email, calendar, database,
  OpenAPI, MCP, and agent-result connector boundaries.
- Privacy-safe OpenTelemetry, bounded observability APIs, and operator dashboard.
- Official .NET, Node.js, Python, Java, PHP, and Rust clients plus the `iroute`
  CLI and shared conformance fixtures.
- Non-root containers, explicit migrations, a one-container SQLite quick start,
  and horizontally scalable Kubernetes API reference manifests.
- Apache-2.0 community, security, compatibility, governance, and reproducible
  release policies.

### Known limitations

- This is an experimental alpha prerelease with expected breaking changes and
  no production or security-response SLA.
- Distributed tenant concurrency, request, queue, token, and cost quotas are not
  implemented.
- Run exactly one lifecycle worker per database until distributed leasing is
  implemented.
- Reference connectors are not production integrations, and provider
  performance/cost figures are not validated production measurements.
- Authentication, TLS, secrets, backups, network controls, production transport
  adapters, external-action reconciliation, and managed provider integrations
  remain operator responsibilities.

### Security

- `DevelopmentHeaders` is rejected automatically when the host environment is
  not Development; production-shaped deployment defaults use JWT.
