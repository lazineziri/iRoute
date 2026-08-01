# Deployment profiles

iRoute publishes three non-root targets from one Dockerfile:

| Target | Process | Intended use |
|---|---|---|
| `api` | `iRoute.Api` | HTTP, SSE, health, OpenAPI, and dashboard |
| `worker` | `iRoute.Worker` | Durable execution worker and optional lifecycle worker |
| `migrate` | `iRoute.Migrations` | Explicit schema status, upgrade, and rollback |

## SQLite API and worker

This profile starts an API and worker, persists their shared SQLite database under `/var/lib/iroute`,
uses the deterministic gateway, and needs no provider credential:

```bash
docker compose -f deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/ready
```

Stop it with `docker compose -f deploy/compose.sqlite.yaml down`. Add `--volumes`
only when the local SQLite data should be deliberately discarded.

## PostgreSQL Compose

The production-shaped Compose profile starts PostgreSQL, runs migrations once,
then starts the API and a combined execution/lifecycle worker:

```bash
cp .env.example .env
docker compose -f deploy/compose.yaml up --build --wait
```

This remains a local/team profile because its defaults use development identity
headers and a local database password. Configure JWT identity, managed PostgreSQL,
TLS ingress, and external secret management before exposing iRoute publicly.

## Kubernetes reference

The manifests under `deploy/kubernetes` use external PostgreSQL, a dedicated
migration Job, two API replicas with an HPA, two leased execution workers, and one lifecycle worker. Replace all
`example.invalid`, `your-org`, image tags, and secret values before deployment.
The complete ordering, upgrade, rollback, and scaling procedure is documented in
`docs/operations.md`.
