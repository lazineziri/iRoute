import { createServer } from 'node:http';

const port = Number.parseInt(process.env.IROUTE_GATEWAY_PORT ?? '5092', 10);
const expectedApiKey = process.env.IROUTE_GATEWAY_API_KEY;
const gatewayId = process.env.IROUTE_GATEWAY_ID ?? `conformance-${port}`;
const failureStatus = optionalInteger(process.env.IROUTE_GATEWAY_FAILURE_STATUS);
const failureCount = optionalInteger(process.env.IROUTE_GATEWAY_FAILURE_COUNT) ?? Number.POSITIVE_INFINITY;
const retryAfterSeconds = optionalInteger(process.env.IROUTE_GATEWAY_RETRY_AFTER_SECONDS);
let invocationCount = 0;

const server = createServer(async (request, response) => {
  if (request.method === 'GET' && request.url === '/health') {
    send(response, 200, {
      gatewayId,
      status: 'Healthy',
      latencyMilliseconds: 0,
      checkedAt: new Date().toISOString(),
      message: 'Gateway conformance endpoint is healthy.'
    });
    return;
  }

  if (request.method !== 'POST' || !['/v1/execute', '/v1/stream'].includes(request.url)) {
    send(response, 404, { error: 'not_found' });
    return;
  }

  if (expectedApiKey && request.headers.authorization !== `Bearer ${expectedApiKey}`) {
    send(response, 401, { error: 'invalid_api_key' });
    return;
  }

  try {
    const body = await readJson(request);
    if (
      body.capability !== 'text.generation' ||
      !body.input ||
      !body.context ||
      !body.profileId ||
      !Number.isInteger(body.maxOutputTokens) ||
      body.maxOutputTokens < 1 ||
      !Number.isInteger(body.deadlineMilliseconds) ||
      body.deadlineMilliseconds < 1
    ) {
      send(response, 400, { error: 'invalid_gateway_request' });
      return;
    }

    invocationCount++;
    if (failureStatus && invocationCount <= failureCount) {
      response.writeHead(failureStatus, {
        'content-type': 'application/json',
        ...(retryAfterSeconds === undefined ? {} : { 'retry-after': retryAfterSeconds.toString() })
      });
      response.end(JSON.stringify({ error: 'synthetic_gateway_failure', invocationCount }));
      return;
    }

    const projectName = body.input.projectName ?? 'the project';
    const recipient = body.input.recipient?.name ?? 'there';
    const objective = body.input.objective ?? 'Here is the requested update.';
    const result = {
      output: {
        subject: `Update on ${projectName}`,
        body: `Hi ${recipient},\n\n${objective}\n\nBest regards`,
        tone: body.input.tone ?? 'professional',
        generatedBy: gatewayId
      },
      usage: {
        inputTokens: Math.max(1, Math.ceil(JSON.stringify(body.input).length / 4)),
        outputTokens: 40,
        cost: 0,
        durationMilliseconds: 0,
        modelCalls: 1,
        toolCalls: 0
      },
      confidence: 0.92,
      evidence: [{ kind: 'request', reference: 'gateway-conformance-input' }],
      gatewayId,
      transport: request.url === '/v1/stream' ? 'Streaming' : 'Buffered',
      finishReason: 'Completed'
    };
    if (request.url === '/v1/stream') {
      response.writeHead(200, { 'content-type': 'application/x-ndjson' });
      writeEvent(response, { sequence: 1, kind: 'OutputDelta', delta: '{"subject":' });
      writeEvent(response, { sequence: 2, kind: 'Usage', usage: result.usage });
      writeEvent(response, { sequence: 3, kind: 'Completed', result });
      response.end();
      return;
    }

    send(response, 200, result);
  } catch {
    send(response, 400, { error: 'invalid_json' });
  }
});

server.listen(port, '127.0.0.1', () => {
  console.log(`iRoute gateway conformance server listening on http://127.0.0.1:${port}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.close(() => process.exit(0)));
}

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

function send(response, status, body) {
  response.writeHead(status, { 'content-type': 'application/json' });
  response.end(JSON.stringify(body));
}

function writeEvent(response, event) {
  response.write(`${JSON.stringify(event)}\n`);
}

function optionalInteger(value) {
  if (value === undefined || value === '') return undefined;
  const parsed = Number.parseInt(value, 10);
  if (!Number.isInteger(parsed) || parsed < 0) {
    throw new Error(`Expected a non-negative integer but received '${value}'.`);
  }
  return parsed;
}
