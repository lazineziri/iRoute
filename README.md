# iRoute

iRoute is an open-source .NET runtime for task-aware AI execution. It resolves
work from trusted state first, routes only unresolved work to registered
capabilities or model gateways, validates the result, and stores reusable
artifacts with evidence, quality, latency, and cost metadata.

> **Experimental alpha:** `0.1.0-alpha.4` is the current .NET release line.
> Breaking changes are expected before `1.0`; production
> connectors, sustained load testing, and a security SLA are not included.
> Tenant quotas and fair dispatch are experimental; quotas require explicit enablement.

## One runtime, six projects

The repository is intentionally .NET-only. There are five production projects
and one test project, with no wrapper layer folders:

| Project | Responsibility |
|---|---|
| `iRoute.Common` | The only home for contracts, DTOs, interfaces, ports, options, enums, and shared primitives |
| `iRoute.Services` | Routing, planning, policies, execution behavior, capabilities, gateways, and validation |
| `iRoute.Data` | Entity Framework Core, SQLite/PostgreSQL stores, entities, and migrations |
| `iRoute.Core` | Small, stable execution facade over Common contracts |
| `iRoute.Runtime` | The only executable and composition root: API, workers, migrations, client, and CLI |
| `iRoute.Tests` | Architecture and behavior verification |

Allowed production references are deliberately one-way:

```text
Services ─┐
Data ─────┼──> Common
Core ─────┘

Runtime ────> Core + Services + Data + Common
```

Architecture tests enforce that graph and ensure production contracts remain in
Common. See [architecture](docs/architecture.md) for the request flow and code
ownership rules.

## What works today

- Durable asynchronous executions, ordered events, cancellation, deadlines,
  approvals, external-action reconciliation, and restart-safe checkpoints.
- Database-backed tenant model-attempt reservations/reconciliation, fair queued
  execution dispatch, and native ASP.NET body/rate safeguards. See
  [quota configuration and limits](docs/tenant-quotas.md).
- Deterministic artifact, fact, decision, and handler resolution before model
  use, with dependency-aware invalidation and bounded context compilation.
- Explainable routing and planning with model-profile provenance, quality
  escalation, provider-neutral HTTP gateways, deterministic fallback, and
  durable circuit state.
- Direct .NET OpenAI Responses and Anthropic Messages adapters for structured
  email drafts and summaries. Optional local Claude subscription access uses the
  official CLI. Native ChatGPT sign-in, account selection, and token refresh are
  implemented for local macOS/Linux use. The corrected ChatGPT adapter has been
  exercised through the full pipeline on four non-Astra models; see the
  [exploratory product benchmark](docs/product-benchmark.md) for results and limits.
- Normalized capability execution for reference email, calendar, database,
  OpenAPI, MCP, and agent-result connectors. Write examples are simulated and
  approval-gated; they are not production integrations.
- SQLite for local single-process use and PostgreSQL for API/worker deployments,
  including explicit schema migration commands.
- JWT identity in deployed environments, development headers for locally bound use,
  tenant-scoped storage, permission scopes, OpenTelemetry, health endpoints,
  redacted observability views, and the `/dashboard/` operator UI.
- One installable `iroute` .NET tool and one OCI image. Both expose `serve`,
  `worker`, `migrate`, and client commands.

The previous multi-language clients are historical releases and are not part of
this repository or the `alpha.4` release line.

## Quick start from source

Install a .NET 10 SDK accepted by [`global.json`](global.json), then run:

```bash
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --configuration Release --no-restore
dotnet test --solution iRoute.slnx --configuration Release --no-build
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/iRoute.Runtime -- serve --urls http://localhost:8080
```

The default development profile uses SQLite, embeds the background workers, and
uses a deterministic gateway, so no provider key is required. In another
terminal:

```bash
curl --request POST http://localhost:8080/v1/executions \
  --header 'Content-Type: application/json' \
  --header 'X-Tenant-Id: demo' \
  --header 'X-Actor-Id: founder' \
  --header 'Idempotency-Key: email-draft-001' \
  --data @examples/email-draft.json
```

The response is `202 Accepted` with an execution ID. Read the snapshot at
`GET /v1/executions/{executionId}`, reconnect to the SSE stream at
`GET /v1/executions/{executionId}/events`, or open
`http://localhost:8080/dashboard/` using tenant `demo`.

The same operation through the built-in CLI is:

```bash
dotnet run --project src/iRoute.Runtime -- \
  execute --request @examples/email-draft.json \
  --idempotency-key email-draft-cli-001 \
  --tenant demo \
  --actor founder
```

See [installation](docs/installation.md) and [client/CLI usage](docs/client-usage.md)
for the complete lifecycle.

To use your subscription locally, use iRoute's own `auth chatgpt login` and
`ModelGateway__Mode=OpenAIChatGPT`, or sign into the official Claude Code CLI and
select `ModelGateway__Mode=ClaudeCode`. API keys are optional: the separate
`OpenAI`/`Anthropic` modes use `OPENAI_API_KEY`/`ANTHROPIC_API_KEY` and API billing.
Set a model available to your account and see
[provider setup and subscription limits](docs/model-providers.md). Unpriced
API/subscription usage is explicitly marked unknown, not reported as free.

## Container quick start

The repository builds one non-root runtime image. The image defaults to
`iroute serve`; pass `worker` or `migrate ...` to select another mode.

```bash
docker compose -f deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/ready
```

The PostgreSQL profile separates API, worker, and migration processes while
running the same image:

```bash
cp .env.example .env
# Set the JWT authority and audience in .env.
docker compose -f deploy/compose.yaml up --build --wait
```

Deployment, migration, scaling, and rollback guidance is in
[deploy/README.md](deploy/README.md) and [operations](docs/operations.md).

## Runtime commands

```text
iroute serve [ASP.NET options]       API plus optional embedded workers
iroute worker                        execution and lifecycle workers only
iroute migrate status                show schema state
iroute migrate up [target]           apply migrations
iroute migrate down <target> --confirm
iroute client <command>              call a running server
iroute auth chatgpt <command>        sign in, select accounts, or revoke a local session
iroute <client-command>              client-command shorthand
```

Run `iroute help`, `iroute client help`, or `iroute migrate help` for the exact
arguments.

## HTTP surface

- `POST /v1/executions/`
- `GET /v1/executions/{executionId}`
- `GET /v1/executions/{executionId}/events?after=0`
- `POST /v1/executions/{executionId}/cancel`
- `POST /v1/executions/{executionId}/approvals`
- `GET /v1/executions/{executionId}/external-actions`
- `POST /v1/executions/{executionId}/external-actions/{actionId}/reconcile`
- `GET /v1/artifacts/{artifactId}`
- `GET /v1/observability/summary`
- `GET /v1/observability/executions/{executionId}`
- `GET /health/live`, `/health/ready`, and `/health/model-gateway`
- `GET /openapi/v1.json` and `/dashboard/`

The versioned [OpenAPI document](spec/openapi/iroute.v1.yaml),
[JSON Schemas](spec/schemas), [SSE contract](spec/events/sse-v1.md), and
[error taxonomy](spec/errors/error-taxonomy.v1.md) are the public wire contract.

## Configuration

iRoute uses standard .NET configuration. Nested environment-variable keys use
double underscores, for example `Storage__Provider=Postgres`.

| Section | Important settings |
|---|---|
| `Storage` | `Provider=Sqlite|Postgres`, `AutoInitialize`; connection string key `ConnectionStrings__iRoute` |
| `Identity` | `Mode=DevelopmentHeaders|Jwt`, `Authority`, `Audience`, tenant/actor/permission claim names |
| `Runtime` | `RunBackgroundWorkers`; defaults on for SQLite and off for PostgreSQL |
| `ExecutionWorker` | poll interval, lease, heartbeat, abandonment, and enablement |
| `Workflow` | queue capacity, parallelism, retry delay, and deterministic jitter |
| `ModelGateway` | deterministic/HTTP mode, transport, deployments, attempt limit, and circuit policy |
| `Lifecycle` | sweep schedule, TTLs, archival, deletion, retention, and record bounds |
| `Observability` | payload mode, query windows, timeline bounds, and recent-execution limit |

Defaults are documented in
[`src/iRoute.Runtime/appsettings.json`](src/iRoute.Runtime/appsettings.json).
Configuration is validated at startup; production rejects development-header
identity and JWT mode rejects missing authority or audience.

## CI/CD and releases

- `ci` restores, verifies formatting, builds, tests, packs all five production
  packages, validates Compose, builds the unified image, and smoke-tests its
  commands, migrations, health, OpenAPI, dashboard, and asynchronous task
  completion on SQLite and PostgreSQL, including a separate worker process.
- `codeql` performs an explicit analyzed .NET build; `secret-scan` runs Gitleaks.
- `release` is a manual dry-run on branches. A verified annotated `v<version>`
  tag publishes five NuGet packages through OIDC, a multi-architecture GHCR
  image with provenance/SBOM, and then the immutable GitHub prerelease.
  Release verification includes the PostgreSQL contention/migration suite.
- Dependabot tracks NuGet, Docker, and GitHub Actions dependencies weekly.

The version in `release.json`, every production project, release notes, package
filenames, and tag must agree. See [releasing](docs/releasing.md) for the exact
operator procedure.

## Project documentation

Start with the [documentation map](docs/README.md). Contributions follow
[CONTRIBUTING.md](CONTRIBUTING.md), security reports follow
[SECURITY.md](SECURITY.md), and support expectations are in [SUPPORT.md](SUPPORT.md).
iRoute is Apache-2.0 licensed.
