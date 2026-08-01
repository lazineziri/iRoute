# Changelog

All notable user-visible changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and release versions
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) with the
additional public-contract promises in `docs/compatibility.md`.

## [Unreleased]

### Added

- Durable asynchronous execution submission with HTTP `202`, PostgreSQL/SQLite
  work persistence, fenced leases, heartbeats, crash takeover, checkpoint
  recovery, and distributed cancellation.
- Approval requeueing, scalable execution-worker deployments, ordered
  queue/lease events, and retry policies with timeouts, bounded exponential
  backoff, jitter, and `Retry-After` support.

## [0.1.0-alpha.1] - 2026-08-01

### Added

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

- This is an alpha prerelease, not a production support commitment.
- In-flight execution and cancellation are process-local; API requests scale
  horizontally, but work is not transparently movable between replicas.
- Run exactly one lifecycle worker per database until distributed leasing is
  implemented.
- Production transport adapters, external-action reconciliation, and managed
  provider integrations remain operator responsibilities.
