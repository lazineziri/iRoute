# Clean installation

This is the supported installation path for `0.1.0-alpha.1`. Start from a clean
clone or source archive; do not copy `bin`, `obj`, `node_modules`, databases, or
local secret files from another environment.

## Prerequisites

- .NET SDK `10.0.100` or newer on the .NET 10 line
- Node.js `24.18.1` for the complete contract/SDK toolchain
- Docker with Compose v2 for the local container path
- Git for a source checkout

Python, Java, PHP, and Rust are required only when developing their respective
SDKs. Their exact CI baselines are in [version-baseline.md](version-baseline.md).

## Source installation

From the repository root:

```bash
dotnet --version
node --version
docker version
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --configuration Release --no-restore
npm ci
npm ci --prefix sdks/node
dotnet test --solution iRoute.slnx --configuration Release --no-build --no-restore
npm run test:contracts
npm run test:deployment
npm run test:regression
npm run test:sdks
npm run test:release
```

The SDK runner skips a native language locally when its toolchain is absent;
the official CI jobs cover every supported language before release.

## Smallest runnable deployment

Start durable SQLite API and worker containers with no provider credentials:

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/live
curl --fail http://localhost:8080/health/ready
```

Submit the checked example from another terminal:

```bash
curl --request POST http://localhost:8080/v1/executions \
  --header 'Content-Type: application/json' \
  --header 'X-Tenant-Id: clean-install' \
  --header 'X-Actor-Id: adopter' \
  --header 'Idempotency-Key: clean-install-email-001' \
  --data @examples/email-draft.json
```

Submission returns HTTP `202`; poll the execution URL or reconnect to its SSE stream until it reaches `Succeeded`. Shut down without deleting the named
SQLite volume:

```bash
docker compose --file deploy/compose.sqlite.yaml down
```

Use `down --volumes` only when deliberately discarding local state.

## Production-shaped deployment

The PostgreSQL Compose and Kubernetes paths require operator configuration. Read
[deploy/README.md](../deploy/README.md) and [operations.md](operations.md) before
using them. In particular:

- replace development identity headers with JWT;
- replace all example secret, host, registry, and image values;
- use an external backed-up PostgreSQL service;
- run the release-matched migration job before workloads;
- keep exactly one lifecycle worker per database;
- run at least two execution workers for takeover capacity;
- configure TLS ingress, secret management, and at least one generic gateway deployment;
- keep the registered gateway route list identical across execution workers and use PostgreSQL for shared circuit state;
- run sustained load, soak, and production failover validation before declaring the installation production-ready.

## Installation failures

- A failing `/health/ready` with a reachable database usually means migrations
  are pending; run the migration image `status` command.
- A .NET SDK outside the `global.json` policy fails before compilation; install
  a compatible .NET 10 SDK.
- Node dependency drift is resolved with `npm ci`, never by editing a lockfile
  manually.
- Do not work around a contract or regression failure by regenerating a golden
  baseline without reviewing the behavioral change.

Report reproducible non-security failures through the bug template. Report
vulnerabilities privately according to [SECURITY.md](../SECURITY.md).
