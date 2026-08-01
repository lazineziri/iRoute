# `@iroute/sdk`

Thin Node.js client for the iRoute v1 HTTP and SSE protocol. Routing, planning,
provider choice, memory, validation, and retry policy remain server-side.

```javascript
import { IRouteClient } from '@iroute/sdk';

const client = new IRouteClient(new URL('http://localhost:8080'), {
  tenantId: 'demo',
  actorId: 'developer'
});

const execution = await client.execute({
  taskType: 'email.draft',
  input: { audience: 'investor', purpose: 'project update' },
  idempotencyKey: 'node-example-001'
});
```

See the repository's SDK guide for streaming, approvals, cancellation,
observability, and typed errors. Apache-2.0 licensed.
