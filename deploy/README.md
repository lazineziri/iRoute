# Deployment profiles

iRoute publishes three non-root process modes from one runtime image:

| Target | Process | Intended use |
|---|---|---|
| `api` | `iroute serve` | HTTP, SSE, health, OpenAPI, dashboard, and optional local workers |
| `worker` | `iroute worker` | Durable execution worker and optional lifecycle worker |
| `migrate` | `iroute migrate` | Explicit schema status, upgrade, and rollback |

## SQLite API and worker

This profile starts one runtime process with embedded workers, persists SQLite under `/var/lib/iroute`,
uses the deterministic gateway, runs explicitly in the Development environment,
and needs no provider credential:

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
# Set IROUTE_IDENTITY_AUTHORITY and IROUTE_IDENTITY_AUDIENCE before startup.
docker compose -f deploy/compose.yaml up --build --wait
```

This profile runs in Production and defaults to JWT. It fails startup until a
non-empty authority and audience are supplied. The checked database password is
still only a local placeholder; configure managed PostgreSQL, TLS ingress, and
external secret management before exposing iRoute publicly.

HTTP model execution can register multiple provider-neutral routes with
`ModelGateway__Deployments__{index}__...`. Supply the same ordered list to every
execution worker; each entry declares gateway/deployment identity, operational
provider/region/residency/model-version metadata, supported capabilities and
profiles, expected quality/cost/latency, priority, transport, URL, and a
secret-sourced API key. PostgreSQL persists shared circuit state so only one
replica receives a half-open probe. The checked Compose and Kubernetes defaults
show the resilience thresholds but intentionally do not guess real provider
routes or credentials.

## Kubernetes reference

The manifests under `deploy/kubernetes` use external PostgreSQL, a dedicated
migration Job, two API replicas with an HPA, two leased execution workers, and one lifecycle worker. Replace all
`example.invalid`, `your-org`, image tags, and secret values before deployment.
The complete ordering, upgrade, rollback, and scaling procedure is documented in
`docs/operations.md`.
