# Changelog

All notable user-visible changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and release versions
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) with the
additional public-contract promises in `docs/compatibility.md`.

## [Unreleased]

No changes yet.

## [0.1.0-alpha.3] - 2026-08-18

### Added

- Model profiles now declare `Synthetic`, `Unverified`, or `Measured`
  provenance. Measured profiles carry provider, model, timestamp, sample-count,
  and quality-calibration metadata through routing decisions and every official
  SDK contract.
- Execution stores expose an atomic status-claim operation used to serialize
  resumable inline workflows across concurrent callers and runtime instances.

### Fixed

- A failed `created` event append or client disconnect after the execution row
  was inserted no longer strands the execution in `Accepted` and poisons its
  idempotency key. The inserted execution is terminalized with a durable error.
- Multi-step plans that require approval now select the plan-wide action step
  instead of throwing through `Steps.Single()` before creating an approval.
- Policy evaluation considers the complete plan's effective side effect, so a
  model-first read-only workflow is not rejected because its first step is
  side-effect free.
- Workflow usage and evidence are aggregated exactly once across all completed
  steps. A final tool step can no longer hide earlier model cost or calls from
  budgets, events, or outcomes.
- Concurrent identical inline approvals atomically claim execution once. A
  replay no longer collides in the cancellation registry, reruns a completed
  plan, overwrites terminal state, or creates duplicate artifacts and events.

### Changed

- **Breaking source change:** `ModelProfile.MeasurementSource` is now the typed
  `ModelProfileSource` enum and an optional `ModelProfileMeasurement` record is
  available for verified measurements.
- **Breaking extension change:** custom `IExecutionStore` implementations must
  implement `TryTransitionAsync` with compare-and-set status semantics.

### Upgrading

- Update every official SDK to `0.1.0-alpha.3` (`0.1.0a3` on PyPI).
- Recompile custom routing/profile integrations for `ModelProfileSource` and
  provide a measurement record only when the source is `Measured`.
- Add an atomic conditional status update to custom execution stores before
  running inline approval resumptions. No database migration is required by the
  built-in SQLite or PostgreSQL providers.

## [0.1.0-alpha.2] - 2026-08-03

### Fixed

- The documented two-terminal quick start never completed. The API and the worker
  both defaulted to the relative connection string `Data Source=iroute.db`, and
  `dotnet run --project` sets the working directory per project, so each host
  opened a different database and submissions stayed `Queued` forever. A relative
  SQLite data source now resolves against one shared per-user directory.
- A cancellation and a worker transition could discard each other. Cancelling an
  execution that had just finished reverted its status and erased the recorded
  outcome, and a cancellation arriving mid-execution was overwritten by the
  worker's next write, so the request was silently ignored.
- An unknown `taskType` returned HTTP 500 and stranded the execution in a
  non-terminal state, because no `Accepted -> Failed` transition existed.
- Retrying a submission that raced the original returned HTTP 500 instead of the
  execution that won, which is the situation an idempotency key exists for.
- A repeatedly failing execution was redelivered about once per second forever,
  growing the event log without bound.
- Event streaming timed out after 30 seconds in the Python SDK and could block
  forever in the Java SDK.
- Durable writes made while processing a leased execution were not fenced by the
  lease, so a worker whose lease had been taken over could interleave writes with
  the new owner.

### Added

- Operators can list external actions whose outcome is unknown and record what
  actually happened, releasing a reservation that previously wedged an execution
  permanently. Adds the `external_action.reconciled` event.
- Reusing an idempotency key with a different payload now returns `409` with
  `idempotency_key_conflict`, as the OpenAPI document has always declared.
- `ExecutionWorker:MaxDeliveryAttempts` and `ExecutionWorker:MaxAbandonDelay`.

### Changed

- **Breaking:** `Storage:Provider=Memory` is removed. It kept no durable record,
  so executions, approvals and leases were lost on restart. Use `Sqlite` for
  single-node development or `Postgres` to deploy.
- **Breaking:** the Node.js SDK is published as `@iroute-dev/sdk`. The `@iroute`
  npm scope belongs to an unrelated account.
- Node.js `24.18.1` is the single declared floor across the repository.
- An unsupported `Storage:Provider` is now rejected at startup rather than on the
  first database call.

### Upgrading

- A relative SQLite database is no longer read from the host's working directory.
  Move an existing `iroute.db` to the per-user data directory reported in the
  README, or set `ConnectionStrings__iRoute` to its absolute path.
- Replace `Storage:Provider=Memory` with `Sqlite`.
- Replace the `@iroute/sdk` dependency with `@iroute-dev/sdk`.

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
