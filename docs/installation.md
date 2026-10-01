# Installation

`0.1.0-alpha.4` is the .NET-only release line. Its verified-tag workflow publishes
the packages and image described below. A source checkout and local container
build remain available independently of registry publication.

## Requirements

- Git for a source checkout
- .NET SDK `10.0.100` or a compatible newer .NET 10 feature band
- Docker with Compose v2 for container profiles
- PostgreSQL only when running without the provided Compose database

## Source installation

```bash
git clone https://github.com/lazineziri/iRoute.git
cd iRoute
dotnet restore iRoute.slnx
dotnet build iRoute.slnx --configuration Release --no-restore
dotnet test --solution iRoute.slnx --configuration Release --no-build
```

Run the local SQLite profile:

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/iRoute.Runtime -- serve --urls http://localhost:8080
```

Readiness should return HTTP 200:

```bash
curl --fail http://localhost:8080/health/ready
```

The default model gateway is deterministic and does not call a provider. For
OpenAI/Anthropic API keys and supported subscription access, follow
[model-provider setup](model-providers.md). Local macOS/Linux users can run
`iroute auth chatgpt login`; registration happens during OpenAI's authorization
flow, not on the Platform agent-builder page. Account eligibility and plan limits
remain provider-controlled. Windows credential storage is not implemented yet.

## .NET tool

Install the published Runtime/CLI tool from NuGet:

```bash
dotnet tool install --global iRoute --version 0.1.0-alpha.4
iroute help
```

For a source-built local tool package:

```bash
dotnet pack src/iRoute.Runtime --configuration Release --output artifacts/packages
dotnet tool install --global iRoute \
  --version 0.1.0-alpha.4 \
  --add-source artifacts/packages
```

Remove it with `dotnet tool uninstall --global iRoute`.

## Container

Build and run the loopback-only SQLite profile:

```bash
docker compose -f deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/ready
```

The published image is
`ghcr.io/lazineziri/iroute:0.1.0-alpha.4`. It runs `serve` by default:

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Identity__Mode=DevelopmentHeaders \
  -e Storage__Provider=Sqlite \
  -e Storage__AutoInitialize=true \
  -e 'ConnectionStrings__iRoute=Data Source=/var/lib/iroute/iroute.db' \
  -v iroute-data:/var/lib/iroute \
  ghcr.io/lazineziri/iroute:0.1.0-alpha.4
```

Do not expose `DevelopmentHeaders` outside loopback. Production must use JWT.

## PostgreSQL profile

```bash
cp .env.example .env
# Set IROUTE_DB_PASSWORD, IROUTE_IDENTITY_AUTHORITY, and IROUTE_IDENTITY_AUDIENCE.
docker compose -f deploy/compose.yaml up --build --wait
```

Compose runs `migrate up` before starting API and worker processes. For an
external database, use the same connection string in the migration, API, and
worker processes.

## Verify an execution

```bash
curl --request POST http://localhost:8080/v1/executions \
  --header 'Content-Type: application/json' \
  --header 'X-Tenant-Id: demo' \
  --header 'X-Actor-Id: installer' \
  --header 'Idempotency-Key: install-check-001' \
  --data @examples/email-draft.json
```

Use the returned execution ID with `iroute get <id>` or follow the
[client guide](client-usage.md). Common installation failures and operational
checks are covered in [operations](operations.md).
