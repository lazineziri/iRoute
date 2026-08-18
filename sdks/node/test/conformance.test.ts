import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import {
  IRouteApiError,
  IRouteClient,
  type ExecutionEvent,
  type TaskRequest
} from '../src/index.js';

const fixture = parseProperties(await readFile(
  new URL('../../../conformance/v1.properties', import.meta.url),
  'utf8'
));
const requestBody = decode('request.body.base64');
const executionRequest = JSON.parse(requestBody) as TaskRequest;

test('Node SDK matches the shared request fixture', async () => {
  let captured: { url: string; init: RequestInit } | undefined;
  const transport: typeof fetch = async (input, init) => {
    captured = { url: input.toString(), init: init ?? {} };
    return jsonResponse(Number(fixture['response.status']), decode('response.body.base64'));
  };
  const client = createClient(transport);

  const response = await client.execute(executionRequest);

  if (captured === undefined) throw new Error('Request was not captured.');
  const headers = new Headers(captured.init.headers);
  assert.equal(captured.init.method, fixture['request.method']);
  assert.equal(new URL(captured.url).pathname, fixture['request.path']);
  assert.equal(headers.get('authorization'), fixture['request.header.authorization']);
  assert.equal(headers.get('content-type'), fixture['request.header.content-type']);
  assert.equal(headers.get('x-tenant-id'), fixture['request.header.tenant']);
  assert.equal(headers.get('x-actor-id'), fixture['request.header.actor']);
  assert.equal(headers.get('x-permission-scopes'), fixture['request.header.permission-scopes']);
  assert.equal(headers.get('idempotency-key'), fixture['request.header.idempotency-key']);
  assert.deepEqual(JSON.parse(String(captured.init.body)), JSON.parse(requestBody));
  assert.equal(response.executionId, '019fbc00-0000-7000-8000-000000000014');
});

test('Node SDK matches the shared end-of-stream fixture', async () => {
  const transport: typeof fetch = async () => new Response(decode('stream.body.base64'), {
    status: Number(fixture['stream.status']),
    headers: { 'content-type': fixture['stream.content-type'] ?? 'text/event-stream' }
  });
  const client = createClient(transport);
  const events: ExecutionEvent[] = [];

  for await (const executionEvent of client.streamEvents(
    '019fbc00-0000-7000-8000-000000000014'
  )) {
    events.push(executionEvent);
  }

  assert.equal(events.length, Number(fixture['stream.expected.count']));
  assert.deepEqual(events.map(item => item.type), fixture['stream.expected.types']?.split(','));
});

test('Node SDK preserves an SSE CRLF delimiter split across chunks', async () => {
  const encoder = new TextEncoder();
  const payload = '{"sequence":1,"executionId":"019fbc00-0000-7000-8000-000000000014","type":"created","occurredAt":"2026-08-18T00:00:00Z","data":{}}';
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(encoder.encode(`data: ${payload}\r`));
      controller.enqueue(encoder.encode('\n\r'));
      controller.enqueue(encoder.encode('\n'));
      controller.close();
    }
  });
  const client = createClient(async () => new Response(body, {
    status: 200,
    headers: { 'content-type': 'text/event-stream' }
  }));

  const events: ExecutionEvent[] = [];
  for await (const event of client.streamEvents('019fbc00-0000-7000-8000-000000000014')) {
    events.push(event);
  }

  assert.equal(events.length, 1);
  assert.equal(events[0]?.type, 'created');
});

test('Node SDK cancels the response stream when iteration stops early', async () => {
  const encoder = new TextEncoder();
  let cancelled = false;
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(encoder.encode(
        'data: {"sequence":1,"executionId":"019fbc00-0000-7000-8000-000000000014","type":"created","occurredAt":"2026-08-18T00:00:00Z","data":{}}\n\n'
      ));
    },
    cancel() {
      cancelled = true;
    }
  });
  const client = createClient(async () => new Response(body, {
    status: 200,
    headers: { 'content-type': 'text/event-stream' }
  }));

  for await (const _event of client.streamEvents('019fbc00-0000-7000-8000-000000000014')) {
    break;
  }

  assert.equal(cancelled, true);
});

test('Node SDK matches the shared typed error fixture', async () => {
  const transport: typeof fetch = async () => jsonResponse(
    Number(fixture['error.status']),
    decode('error.body.base64')
  );
  const client = createClient(transport);

  await assert.rejects(client.execute(executionRequest), (error: unknown) => {
    if (!(error instanceof IRouteApiError)) return false;
    assert.equal(error.status, Number(fixture['error.status']));
    assert.equal(error.code, fixture['error.expected.code']);
    assert.equal(error.title, fixture['error.expected.title']);
    assert.equal(error.detail, fixture['error.expected.detail']);
    assert.equal(error.responseBody, decode('error.body.base64'));
    return true;
  });
});

function createClient(transport: typeof fetch): IRouteClient {
  return new IRouteClient(new URL('http://sdk-fixture.test'), {
    token: 'fixture-token',
    tenantId: required('request.header.tenant'),
    actorId: required('request.header.actor'),
    permissionScopes: required('request.header.permission-scopes').split(' '),
    fetch: transport
  });
}

function jsonResponse(status: number, body: string): Response {
  return new Response(body, { status, headers: { 'content-type': 'application/json' } });
}

function decode(key: string): string {
  return Buffer.from(required(key), 'base64').toString('utf8');
}

function required(key: string): string {
  const value = fixture[key];
  if (value === undefined) throw new Error(`Missing fixture key ${key}`);
  return value;
}

function parseProperties(value: string): Readonly<Record<string, string>> {
  return Object.fromEntries(value.split('\n').filter(Boolean).map(line => {
    const separator = line.indexOf('=');
    return [line.slice(0, separator), line.slice(separator + 1)];
  }));
}
