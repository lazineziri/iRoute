# Compatibility promise

`0.1.0-alpha.4` is an experimental prerelease. The promise below prevents
casual drift, but it is not a production support or security-response SLA.

## HTTP and events

- The public API major is `v1`.
- Additive response fields and event types are allowed; consumers must ignore
  unknown fields and safely surface unknown error codes.
- SSE sequence numbers are monotonic and reconnect uses `after`; a terminal
  execution remains queryable after a stream disconnect.
- Tenant and actor identity come from authenticated claims in JWT mode. Body
  identity cannot override those claims.

## .NET packages

- All five production packages share one product version and are tested as one
  release set.
- `iRoute.Common` owns public contracts. Other packages may change internal
  implementations without creating alternate DTOs or ports.
- The `iRoute` tool, Runtime API, Common contracts, OpenAPI, and schemas move on
  the same release train.
- There is no separately supported multi-language SDK matrix in this repository.

## Storage and configuration

- SQLite is for local/single-process use. PostgreSQL is the supported shape for
  separate API and worker processes.
- Additive database migrations support application rollback over the expanded
  schema. Data-destructive rollback requires explicit `--confirm` and an operator
  backup.
- Existing configuration keys are not silently repurposed. Renames require a
  documented transition or a breaking prerelease note.

## Support windows

Only the latest prerelease receives best-effort fixes. When `1.0` is reached,
the project will publish a stable support window before claiming production
compatibility. See [SECURITY.md](../SECURITY.md) for vulnerability reporting.
