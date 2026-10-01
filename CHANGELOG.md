# Changelog

All notable user-visible changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and release versions
follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html) with the
additional public-contract promises in `docs/compatibility.md`.

## [Unreleased]

## [0.1.0-alpha.4] - 2026-10-01

### Added

- Experimental tenant provider-attempt quotas, serializable SQLite/PostgreSQL
  reservation/reconciliation, crash-expiry recovery, and fair durable dispatch.
- Native ASP.NET body limits and verified-identity HTTP rate partitions, plus
  PostgreSQL contention/fencing/upgrade coverage enabled in CI.
- Repeated opt-in ChatGPT and Claude subscription correctness checks with
  field-specific factual/status/action-claim rubrics and negative controls.

- Opt-in product benchmarks comparing native direct-model calls with the full
  iRoute pipeline, retaining synthetic outputs, provider token breakdowns,
  elapsed times, factual checks, and observed inference-call counts.
- Optional nullable cached-input and reasoning token breakdowns in normalized
  usage, JSON Schemas, OpenAPI, and the public contract snapshot.
- Native .NET OpenAI Responses and Anthropic Messages structured-output adapters,
  local official Claude CLI subscription access, and native ChatGPT dynamic
  registration/sign-in, account selection, rotating refresh, and revocation on
  macOS/Linux. ChatGPT plan requests use bounded Responses event streams.
- Explicit optional `usage.costKnown` and owner-tenant subscription routing, with
  no silent subscription-to-API billing fallback.
- Durable execution, cancellation, reconciliation, approval security, provider
  protocol, subscription, adapter composition, and gateway contract regression tests.
- Added `iRoute.Common`, `iRoute.Services`, `iRoute.Data`, `iRoute.Core`,
  `iRoute.Runtime`, and `iRoute.Tests` as the complete six-project solution.
- Added architecture tests for allowed project references and the single Common
  contract boundary.
- Added one unified non-root runtime image for `serve`, `worker`, `migrate`, and
  client commands.
- Added package, Compose, container-command, health, OpenAPI, and dashboard
  smoke gates to CI.
- Added package installation plus SQLite/PostgreSQL migration, API/worker task
  completion, artifact read, SSE replay, idempotency, and tenant-denial smoke
  gates shared by CI and release validation.
- Added verified-tag NuGet OIDC publishing, multi-architecture GHCR publication
  with provenance/SBOM, source archive, checksums, and GitHub release ordering.

### Changed

- Replaced the large partial execution class with focused services and a small
  dispatcher. Core remains a facade; Runtime endpoints no longer own cancellation
  or reconciliation business rules.
- Approval decisions now require a different actor from the requester, and
  approval/action exceptions live with the other Common contracts.
- The active repository and `alpha.4` release line are strictly .NET-only.
- All cross-project DTOs, interfaces, ports, options, enums, and primitives now
  live in `iRoute.Common`; Services owns behavior, Data owns persistence, Core is
  a small facade, and Runtime is the only executable/composition root.
- Code follows feature folders inside its owning project instead of wrapper
  `Application`, `Infrastructure`, `Hosts`, or `Clients` directories.
- CI/CD now targets NuGet, GHCR, and GitHub Releases only.
- Runtime time and delay handling now uses the BCL `TimeProvider`; host settings
  use startup-validated options; known client, API, and gateway JSON boundaries use
  source-generated metadata; and immutable registries use frozen collections.
- Central build policy now enforces recommended .NET analyzers, code-style
  analysis, warnings as errors, and repository-wide formatting conventions.
- Current README, architecture, installation, client, operations, publishing,
  release, compatibility, status, and product specification documentation now
  describe the six-project system and unified image.

### Fixed

- Gateway configuration records redact credentials and endpoint configuration
  in diagnostic string formatting. Release verification runs PostgreSQL
  contention/migration tests as well as the container smoke checks.
- Older/equal producer versions cannot overwrite newer project state or win
  context selection; relevance ranking uses decoded Unicode text.
- Artifact reuse preserves the original validated confidence. Nullable legacy
  confidence triggers regeneration instead of invented certainty.
- Context and native output budgets cannot exceed task-definition limits.
- PostgreSQL retries recognize wrapped serialization failures and rerun complete
  transactions; additive confidence migration rollback works on both providers.
- Claude CLI and Anthropic usage include cache reads/writes in input totals;
  cache reads and reasoning remain optional subsets, never extra totals.

- Artifact reuse now fingerprints output-affecting constraints and metadata;
  idempotent submissions cannot silently change their constraints. Previous
  persisted hashes are not silently reused; see the benchmark upgrade note.
- Sources excluded by lifecycle checks cannot be materialized as active project
  memory, and explicit source expiry is preserved for no-model lookups.
- Provider cached-input and reasoning detail counts are validated and retained
  without double-counting totals; partially known workflow details remain unknown.
- ChatGPT plan requests now use array-shaped input and omit the unsupported
  output-token parameter. Bounded Responses streams accept missing media headers,
  reconstruct indexed text when terminal output is empty, and reject conflicting
  snapshots, refusals, tools, malformed frames, and premature termination.
- Anchored the root build-artifact ignore rules so the legitimate
  `iRoute.Common/Contracts/Artifacts` source folder is included in Git and the
  Docker build context.
- Included the Alpine Kerberos/GSSAPI runtime libraries required by the
  PostgreSQL driver, eliminating native-library load errors in container logs.

### Removed

- Removed the Node.js, Python, Java, PHP, and Rust SDK implementations, package
  manifests, examples, native CI jobs, and registry publishing jobs.
- Removed the former project wrapper trees, redundant per-component container
  targets, old SDK documentation folder, JavaScript tooling, and Node dependency
  tree.

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
