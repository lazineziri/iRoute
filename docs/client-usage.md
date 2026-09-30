# Client and CLI usage

The `iRoute` .NET tool contains the server and a thin HTTP client. There is no
separate SDK project or client release train. Applications can use the `v1`
OpenAPI/JSON Schema contract directly and reference `iRoute.Common` when they
want the canonical .NET DTOs.

## Connection and identity

Client commands accept these options:

| Option | Environment variable | Default |
|---|---|---|
| `--base-url` | `IROUTE_URL` | `http://localhost:8080` |
| `--token` | `IROUTE_TOKEN` | none |
| `--tenant` | `IROUTE_TENANT` | `local` |
| `--actor` | `IROUTE_ACTOR` | `cli` |
| `--scope` | none; repeat the option | none |

JWT mode uses `--token`; tenant, actor, and permissions come from claims and
cannot be overridden by request JSON. Development mode sends the local tenant,
actor, and scopes as headers. Development headers are safe only on loopback.

## Submit

Use a complete request file:

```bash
iroute execute \
  --request @examples/email-draft.json \
  --idempotency-key email-draft-001 \
  --tenant demo \
  --actor author
```

Or build a request from a task type and input:

```bash
iroute execute email.draft \
  --input '{"recipient":{"name":"Ada"},"objective":"Share status"}' \
  --project iroute-project \
  --idempotency-key email-draft-002
```

Every logical submission should have a stable idempotency key. Reusing a key
with different input returns a conflict; retrying the same request returns its
original execution.

## Read and stream

```bash
iroute get <execution-id>
iroute events <execution-id>
iroute events <execution-id> --after <last-sequence>
```

Events are replayable SSE records with monotonic sequence numbers. Store the
last processed sequence and reconnect with `--after`. A disconnected stream
does not cancel the execution.

## Cancel and approve

```bash
iroute cancel <execution-id>
iroute approve <execution-id> <action-id> --reason 'Reviewed'
iroute deny <execution-id> <action-id> --reason 'Not authorized'
```

Cancellation is cooperative and durable. Approval requires the relevant
permission scope and cannot bypass the task policy. External actions that lose
their acknowledgement can be inspected/reconciled through the HTTP endpoints;
that operation is intentionally not hidden behind automatic client retries.

## Artifacts and observability

```bash
iroute artifact <artifact-id>
iroute observe --from 2026-08-18T00:00:00Z --to 2026-08-19T00:00:00Z
iroute observe --task-type email.draft --policy-version v1
iroute timeline <execution-id>
iroute health
```

Artifact and observability reads are tenant-scoped. The dashboard uses the same
bounded observability endpoints and does not embed private execution data in its
static files.

## HTTP example

```bash
curl --request POST http://localhost:8080/v1/executions/ \
  --header 'Content-Type: application/json' \
  --header 'X-Tenant-Id: demo' \
  --header 'X-Actor-Id: author' \
  --header 'X-Permission-Scopes: project:read artifact:read' \
  --header 'Idempotency-Key: email-draft-003' \
  --data @examples/email-draft.json
```

In JWT mode replace development headers with `Authorization: Bearer <token>`.

## .NET contracts

Reference `iRoute.Common` when an application wants the same request, snapshot,
event, approval, artifact, and problem shapes used by the runtime:

```xml
<PackageReference Include="iRoute.Common" Version="0.1.0-alpha.4" />
```

The package contains contracts only. It does not inject services, select models,
retry requests, or hide HTTP behavior. Generate an HTTP client from
[`spec/openapi/iroute.v1.yaml`](../spec/openapi/iroute.v1.yaml) or use
`HttpClient` with those DTOs.

## Failure behavior

Non-success responses use Problem Details with a stable iRoute error code.
Clients must preserve unknown codes, respect `retryable` and `Retry-After`, and
avoid automatically retrying non-idempotent actions. Timeouts and caller
cancellation are not evidence that the server cancelled the durable execution;
query its snapshot before deciding what to do next.

See the [error taxonomy](../spec/errors/error-taxonomy.v1.md),
[SSE contract](../spec/events/sse-v1.md), and [compatibility promise](compatibility.md).
