import { createServer } from 'node:http';

const port = Number.parseInt(process.env.IROUTE_GATEWAY_PORT ?? '5092', 10);
const expectedApiKey = process.env.IROUTE_GATEWAY_API_KEY;

const server = createServer(async (request, response) => {
  if (request.method !== 'POST' || request.url !== '/v1/execute') {
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
      !Number.isInteger(body.maxOutputTokens) ||
      body.maxOutputTokens < 1
    ) {
      send(response, 400, { error: 'invalid_gateway_request' });
      return;
    }

    const projectName = body.input.projectName ?? 'the project';
    const recipient = body.input.recipient?.name ?? 'there';
    const objective = body.input.objective ?? 'Here is the requested update.';
    send(response, 200, {
      output: {
        subject: `Update on ${projectName}`,
        body: `Hi ${recipient},\n\n${objective}\n\nBest regards`,
        tone: body.input.tone ?? 'professional',
        generatedBy: 'iroute-gateway-conformance-server'
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
      evidence: []
    });
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
