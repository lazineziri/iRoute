# iRoute Node.js and TypeScript SDK

`@iroute-dev/sdk` is the typed Node.js client for the iRoute v1 HTTP and SSE API.
It supports execution, polling, incremental event streaming, cancellation,
approvals, artifacts, gateway health, and observability without owning routing
or provider policy.

## Status and requirements

- SDK version: `0.1.0-alpha.1`
- Runtime minimum: Node.js `24.18.1`
- Verified baseline: Node.js `24.18.1` and TypeScript `7.0.2`
- Module format: ESM
- Package name: `@iroute-dev/sdk`

Once published, the public alpha is installed from npm as a prerelease package.
The source installation below remains available.

## Start iRoute

```bash
docker compose --file deploy/compose.sqlite.yaml up --build --wait
```

The default API URL is `http://localhost:8080`. See the shared
[SDK usage guide](../../docs/sdk-usage.md) for source-run and authentication
options.

## Install from npm

```bash
npm install @iroute-dev/sdk@0.1.0-alpha.1
```

## Install from source

Build the SDK, then install its directory into an application:

```bash
npm ci --prefix ../iRoute/sdks/node
npm run build --prefix ../iRoute/sdks/node
npm install ../iRoute/sdks/node
```

The package exports `dist/index.js` and its TypeScript declarations.

## Create a client

```typescript
import { IRouteClient } from '@iroute-dev/sdk';

const client = new IRouteClient(
  new URL(process.env.IROUTE_URL ?? 'http://localhost:8080'),
  {
    token: process.env.IROUTE_TOKEN,
    tenantId: process.env.IROUTE_TENANT ?? 'demo',
    actorId: process.env.IROUTE_ACTOR ?? 'node-app',
    permissionScopes: []
  }
);
```

In Development the identity options become headers. In JWT mode the server
derives identity and scopes from token claims and will not allow headers to
elevate them. A custom Fetch implementation can be supplied as `fetch` for
testing or application-specific transport behavior.

## Submit an execution

```typescript
import { randomUUID } from 'node:crypto';
import type { TaskRequest } from '@iroute-dev/sdk';

const request = {
  taskType: 'email.draft',
  input: { purpose: 'Confirm the Node SDK integration.' },
  idempotencyKey: `node-email-${randomUUID()}`,
  constraints: {
    maxOutputTokens: 500,
    deadlineMilliseconds: 30_000,
    minimumQuality: 0.8
  }
} satisfies TaskRequest;

let execution = await client.execute(request);

console.log(execution.executionId, execution.status);
```

Use a stable business idempotency key in real applications. Generating a UUID is
appropriate only when the application is creating a new logical operation.

## Poll until terminal

```typescript
import type { ExecutionStatus } from '@iroute-dev/sdk';

const terminal = new Set<ExecutionStatus>([
  'Succeeded', 'Failed', 'Cancelled', 'TimedOut'
]);
const sleep = (milliseconds: number) =>
  new Promise(resolve => setTimeout(resolve, milliseconds));

while (!terminal.has(execution.status)) {
  await sleep(500);
  const current = await client.get(execution.executionId);
  if (!current) throw new Error('Execution is not visible to this tenant.');
  execution = current;
}

if (execution.status === 'Succeeded') {
  console.log(execution.outcome?.output);
} else {
  console.error(execution.error?.code, execution.error?.detail);
}
```

`WaitingForApproval` is non-terminal.

## Stream and reconnect to events

`streamEvents` consumes SSE incrementally and accepts an `AbortSignal`:

```typescript
const controller = new AbortController();
let lastSequence = 0;

for await (const event of client.streamEvents(
  execution.executionId,
  lastSequence,
  controller.signal
)) {
  console.log(event.sequence, event.type);
  lastSequence = event.sequence;
}
```

Persist `lastSequence` after processing each event. On reconnect, call
`streamEvents(executionId, lastSequence)` to receive only newer events. Ignore
unknown event types and fields.

## Time-bounded requests

Every operation accepts an optional `AbortSignal`:

```typescript
const signal = AbortSignal.timeout(30_000);
const snapshot = await client.get(execution.executionId, signal);
```

Aborting the HTTP request does not automatically cancel the durable execution.
Call `cancel` when the server-side execution should also be cancelled.

## Cancel an execution

```typescript
const accepted = await client.cancel(execution.executionId);
if (!accepted) console.log('Execution was not found.');
```

Cancellation is durable but cooperative. Poll or reconnect to events to observe
the terminal result.

## Approve or deny an action

Read `actionId` from an `approval.required` event:

```typescript
const result = await client.submitApproval(execution.executionId, {
  actionId,
  approved: true,
  reason: 'Reviewed by an authorized operator'
});

execution = result.execution;
```

The deciding token needs `approval:grant` and the action's required scope.

## Retrieve an artifact

```typescript
const reference = execution.outcome?.artifacts[0];
if (reference) {
  const artifact = await client.getArtifact(reference.artifactId);
  console.log(artifact?.content);
}
```

Not found returns `undefined`, including for cross-tenant access.

## Health and observability

```typescript
const health = await client.getModelGatewayHealth();
console.log(health.gatewayId, health.status);

const summary = await client.getObservabilitySummary({
  from: new Date(Date.now() - 24 * 60 * 60 * 1000),
  to: new Date(),
  taskType: 'email.draft'
});
console.log(summary.totals.executions, summary.gatewayGroups);

const timeline = await client.getExecutionTimeline(execution.executionId);
console.log(timeline?.traceId, timeline?.events);
```

Gateway health returns its typed body for both HTTP `200` and `503`.
Observability is tenant scoped. Reported cost has no currency in v1.

## Errors

```typescript
import { IRouteApiError } from '@iroute-dev/sdk';

try {
  execution = await client.execute(request, AbortSignal.timeout(30_000));
} catch (error) {
  if (error instanceof IRouteApiError) {
    console.error(error.status, error.code, error.detail);
    console.error(error.responseBody);
  } else {
    throw error;
  }
}
```

The SDK does not retry automatically. Retrying a submission requires the same
idempotency key. Provider fallback remains server-side.

## Method reference

| Method | Result |
|---|---|
| `execute(request, signal?)` | Typed `ExecutionSnapshot` |
| `get(executionId, signal?)` | Snapshot or `undefined` |
| `cancel(executionId, signal?)` | `false` when not found |
| `submitApproval(executionId, decision, signal?)` | Typed `ApprovalResult` |
| `streamEvents(executionId, afterSequence?, signal?)` | Incremental async generator |
| `getArtifact(artifactId, signal?)` | Artifact or `undefined` |
| `getModelGatewayHealth(signal?)` | Typed gateway health |
| `getObservabilitySummary(query?, signal?)` | Typed summary |
| `getExecutionTimeline(executionId, signal?)` | Timeline or `undefined` |

## Run the example and tests

From the repository root:

```bash
npm run build --prefix sdks/node
node examples/sdks/node/execute.mjs
npm test --prefix sdks/node
```

Public wire shapes are defined by the
[OpenAPI document](../../spec/openapi/iroute.v1.yaml) and
[JSON Schemas](../../spec/schemas).
