import { randomUUID } from 'node:crypto';
import { IRouteClient } from '../../../sdks/node/dist/index.js';

const client = new IRouteClient(
  new URL(process.env.IROUTE_URL ?? 'http://localhost:8080'),
  {
    token: process.env.IROUTE_TOKEN,
    tenantId: process.env.IROUTE_TENANT ?? 'demo',
    actorId: process.env.IROUTE_ACTOR ?? 'sdk-example'
  }
);
const result = await client.execute({
  taskType: 'email.draft',
  input: { purpose: 'Confirm the SDK quick start.' },
  idempotencyKey: `node-example-${randomUUID()}`
});
console.log(JSON.stringify(result, null, 2));
