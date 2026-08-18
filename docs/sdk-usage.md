# .NET SDK usage

iRoute ships one supported .NET client for the v1 HTTP and SSE API. It configures
identity headers or bearer authentication, serializes requests, exposes execution
operations, consumes events, and maps HTTP problem details. Routing, provider
selection, fallback, memory, validation, durable retries, and workflow execution
remain server-side.

## Client guide

| Language | Guide | Public representation |
|---|---|---|
| .NET | [iRoute.Sdk](../src/iRoute.Sdk.DotNet/README.md) | Typed contracts and asynchronous streams |

## Release status

The source tree and release archives contain version `0.1.0-alpha.3`. The
supported client is published as `iRoute.Sdk` on NuGet and can also be consumed
from a source checkout or a locally built package.

Breaking source and configuration changes are expected before `1.0`. The v1 wire
contract follows the repository's [compatibility promise](compatibility.md).

## Start a runtime

The smallest complete runtime includes both an API and an execution worker:

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
curl --fail http://localhost:8080/health/ready
```

To run from source, use two terminals from the repository root:

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/iRoute.Api -- --urls http://localhost:8080
```

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/iRoute.Worker
```

The deterministic development gateway requires no provider credential. The API
persists a submission before returning; the worker claims and processes queued
work. Starting only the API is sufficient for reads and no-model fast paths, but
ordinary queued model work will not finish without a worker.

## Shared client configuration

The reference example understands these environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `IROUTE_URL` | `http://localhost:8080` | API base URL |
| `IROUTE_TOKEN` | unset | JWT bearer token |
| `IROUTE_TENANT` | `demo` | Development tenant header |
| `IROUTE_ACTOR` | `sdk-example` | Development actor header |

`DevelopmentHeaders` is accepted only when the server environment is exactly
`Development`. Outside Development the server requires JWT mode and derives the
tenant, actor, and permission scopes from authenticated claims. Client-supplied
identity or scope headers cannot elevate a JWT identity.

## Execution lifecycle

`POST /v1/executions` normally returns HTTP `202` with an `ExecutionSnapshot` in
`Queued`. A trusted no-model resolution may finish synchronously. Treat these as
terminal statuses:

- `Succeeded`
- `Failed`
- `Cancelled`
- `TimedOut`

All other statuses are non-terminal. `WaitingForApproval` requires an explicit
approval decision before work can resume. Clients can either poll the execution
or consume its event stream. The SDK intentionally does not provide a hidden
`waitUntilDone` policy: applications choose their polling interval, deadline,
and reconnection behavior.

## Idempotency

Give every logical submission a stable `idempotencyKey`. The SDK propagates it
in both the JSON request and `Idempotency-Key` header where supported. Reusing
the same key and equivalent input returns the existing execution. Reusing it for
different input fails with `idempotency_key_conflict`.

External writes require an idempotency key. Do not generate a new key while
retrying the same logical operation.

## Task request reference

The SDK serializes the public `TaskRequest` fields:

| Field | Required | Meaning |
|---|---:|---|
| `taskType` | yes | Registered task identifier, such as `email.draft` |
| `input` | yes | Task-specific JSON value |
| `projectId` | no | Project scope for state, memory, and artifacts |
| `idempotencyKey` | strongly recommended | Stable identity of the logical request |
| `constraints` | no | Hard execution and routing limits |
| `metadata` | no | Bounded string-to-string application metadata |
| `tenantId` | Development only | Per-request tenant override subject to identity checks |
| `actorId` | Development only | Per-request actor override subject to identity checks |
| `permissionScopes` | Development only | Per-request scopes subject to identity checks |

Constraint fields are optional and fail closed when they cannot be satisfied:

| Constraint | Meaning |
|---|---|
| `maxInputTokens` | Maximum compiled task input and context tokens |
| `maxOutputTokens` | Maximum output tokens requested from a model |
| `maxCost` | Maximum normalized gateway-reported cost units |
| `deadlineMilliseconds` | End-to-end execution deadline |
| `minimumQuality` | Mandatory validated quality floor from `0` to `1` |
| `requireEvidence` | Require evidence-bearing output |
| `allowExternalWrites` | Permit policy evaluation of external writes; not an authorization grant |
| `maxModelCalls` | Model-call ceiling |
| `maxToolCalls` | Capability/tool-call ceiling |
| `maxParallelCalls` | Parallel-step ceiling |
| `maxTaskDepth` | Workflow dependency-depth ceiling |
| `allowedRegions` | Eligible provider deployment regions |
| `requiredResidency` | Mandatory deployment residency classification |

Unknown task types and unsatisfied constraints become classified failures; the
client does not relax them.

## Execution result reference

An `ExecutionSnapshot` contains `executionId`, `taskType`, `status`, creation and
update timestamps, tenant/actor/project scope, optional task-definition version,
an optional terminal `outcome`, an optional terminal `error`, and an optional
cancellation-request timestamp.

A successful `outcome` contains:

- `output`: task-specific JSON;
- `resolutionLevel`: exact artifact, structured state, semantic memory,
  deterministic capability, small model, strong model, or verified/human;
- `confidence` and task-specific `validation`;
- evidence references and materialized artifact references;
- normalized usage for input/output tokens, cost, duration, model calls, and
  tool calls;
- optional bounded context manifest;
- optional routing decision with candidate rejection, selected deployment,
  attempts, fallback, and circuit evidence.

A terminal `error` contains stable `code`, `title`, `detail`, `retryable`, and
optional metadata. Preserve unknown fields and error codes for forward
compatibility.

## Event replay

The event endpoint is ordered by the numeric `sequence` field. Store the last
processed sequence and reconnect with that value as `afterSequence`. Replay
returns only newer events. Consumers must ignore unknown event types and fields
because v1 may add them.

The .NET client consumes the response incrementally and supports long-lived SSE
streams with cancellation.

See the authoritative [SSE contract](../spec/events/sse-v1.md).

## Approvals and permission scopes

A write task must set `constraints.allowExternalWrites` and include the action's
required permission scope. When the execution reaches `WaitingForApproval`, read
the `actionId` from the `approval.required` event and submit:

```json
{
  "actionId": "the-recorded-action-id",
  "approved": true,
  "reason": "Reviewed by an authorized operator"
}
```

The deciding identity also needs `approval:grant`. In JWT mode both the action
scope and `approval:grant` must be present in token claims. Never manufacture an
action ID; use the durable ID emitted by the server.

## Artifacts

A successful outcome can contain artifact references under
`outcome.artifacts`. Fetch an artifact by its `artifactId`. A missing or
cross-tenant artifact is returned as not found. Artifact content is returned by
the artifact endpoint, not the event stream.

## Observability and cost

The summary endpoint accepts `from`, `to`, `taskType`, and `policyVersion`.
Queries are tenant scoped and bounded by server configuration. The execution
timeline is ordered and metadata-only by default.

The `cost` field is the normalized value reported by the configured gateway. The
v1 contract does not assign it a currency. Do not treat it as provider billing
or add currency symbols without an application-level convention.

## Errors, timeouts, and retries

Non-success HTTP responses become the language's typed SDK exception or error.
Preserve `status`, `code`, `title`, `detail`, and the raw response body. An
execution can also fail after submission; inspect `ExecutionSnapshot.error` in
terminal snapshots.

The SDK does not retry submissions, polls, cancellations, or approvals automatically.
Application retries must respect idempotency, deadlines, `retryable`, and any
HTTP `Retry-After` value. Provider retries and cross-deployment fallback belong
to the iRoute worker/gateway layer and must not be duplicated in the client.

See the authoritative [error taxonomy](../spec/errors/error-taxonomy.v1.md).

## Common operations

The .NET SDK exposes these operations with typed request and response contracts:

| Operation | Endpoint |
|---|---|
| Submit execution | `POST /v1/executions` |
| Read execution | `GET /v1/executions/{executionId}` |
| Cancel execution | `POST /v1/executions/{executionId}/cancel` |
| Submit approval | `POST /v1/executions/{executionId}/approvals` |
| Stream/replay events | `GET /v1/executions/{executionId}/events?after=` |
| Read artifact | `GET /v1/artifacts/{artifactId}` |
| Gateway health | `GET /health/model-gateway` |
| Observability summary | `GET /v1/observability/summary` |
| Execution timeline | `GET /v1/observability/executions/{executionId}` |

The [OpenAPI document](../spec/openapi/iroute.v1.yaml), JSON Schemas under
[`spec/schemas`](../spec/schemas), and language-neutral examples under
[`spec/examples`](../spec/examples) are authoritative for wire shapes.
