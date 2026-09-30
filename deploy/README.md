# Deployment

iRoute publishes one non-root image:

```text
ghcr.io/lazineziri/iroute:<version>
```

Its entry point is `dotnet iroute.dll`; the default command is `serve`. The same
artifact runs every operational mode:

| Command | Use |
|---|---|
| `serve` | API, health, OpenAPI, dashboard, and optional local workers |
| `worker` | execution and lifecycle background services |
| `migrate status` | schema inspection |
| `migrate up` | schema upgrade |

## SQLite Compose

The loopback-only development profile builds the image, persists SQLite under
`/var/lib/iroute`, embeds workers in the API process, and uses deterministic
model execution:

```bash
docker compose -f deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/ready
```

Stop it with:

```bash
docker compose -f deploy/compose.sqlite.yaml down
```

Add `--volumes` only when you intentionally want to delete local SQLite data.

## PostgreSQL Compose

The production-shaped profile runs one image as migration, API, and worker
processes, plus PostgreSQL:

```bash
cp .env.example .env
# Replace the database password and configure a real JWT authority/audience.
docker compose -f deploy/compose.yaml up --build --wait
```

For a released image, omit `--build` and set `IROUTE_VERSION` to the immutable
version. Do not rely on the checked development password or expose the profile
without TLS ingress and external secret management.

## Kubernetes

The reference manifests under `deploy/kubernetes` contain API replicas, an HPA,
execution workers, one lifecycle worker, service accounts, configuration, and a
separate migration Job. They expect external PostgreSQL and JWT identity.

1. Copy `secret.example.yaml` to a secret managed by your platform; do not commit it.
2. Replace `example.invalid` gateway/identity values.
3. Pin `ghcr.io/lazineziri/iroute` by version or digest.
4. Apply namespace, configuration, service accounts, and the migration Job.
5. Wait for migration success.
6. Apply the Kustomize workload and verify readiness/canary execution.

```bash
kubectl apply -f deploy/kubernetes/namespace.yaml
kubectl apply -f <your-secret-manifest-or-external-secret>
kubectl apply -f deploy/kubernetes/configmap.yaml
kubectl apply -f deploy/kubernetes/serviceaccounts.yaml
kubectl create -f deploy/kubernetes/migrate-job.yaml
kubectl -n iroute wait --for=condition=complete job/<generated-job-name> --timeout=10m
kubectl apply -k deploy/kubernetes
```

The manifests are a hardened reference, not a complete platform: supply ingress,
TLS, network policy, external secrets, database backup/PITR, image admission,
telemetry export, and measured resource limits for your environment.

See [operations](../docs/operations.md) for configuration, upgrade, rollback,
backup, identity, gateway, and worker guidance.
