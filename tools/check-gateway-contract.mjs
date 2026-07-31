const baseUrl = process.env.IROUTE_GATEWAY_URL ?? 'http://127.0.0.1:5092';
const transport = process.env.IROUTE_GATEWAY_TRANSPORT ?? 'Buffered';
const apiKey = process.env.IROUTE_GATEWAY_API_KEY;
const headers = {
  ...(apiKey ? { authorization: `Bearer ${apiKey}` } : {})
};

const healthResponse = await fetch(new URL('/health', baseUrl), { headers });
if (!healthResponse.ok) fail(`health returned HTTP ${healthResponse.status}`);
const health = await healthResponse.json();
if (!health.gatewayId || health.status !== 'Healthy') fail('health envelope is not healthy');

const request = {
  capability: 'text.generation',
  input: {
    recipient: { name: 'Ada' },
    projectName: 'iRoute',
    objective: 'Verify the provider-neutral gateway contract.'
  },
  context: { activeDecisions: ['Provider formats remain outside iRoute.'] },
  maxOutputTokens: 800,
  correlationId: `gateway-contract-${Date.now()}`,
  profileId: 'text.generation.small.eval-v1',
  deadlineMilliseconds: 30000
};
const path = transport === 'Streaming' ? '/v1/stream' : '/v1/execute';
const response = await fetch(new URL(path, baseUrl), {
  method: 'POST',
  headers: {
    ...headers,
    'content-type': 'application/json',
    accept: transport === 'Streaming' ? 'application/x-ndjson' : 'application/json'
  },
  body: JSON.stringify(request)
});
if (!response.ok) fail(`${transport} execution returned HTTP ${response.status}`);

let result;
if (transport === 'Streaming') {
  const events = (await response.text())
    .split('\n')
    .filter(Boolean)
    .map(line => JSON.parse(line));
  if (events.length < 2) fail('stream did not contain incremental and completed events');
  let previousSequence = 0;
  for (const event of events) {
    if (!Number.isInteger(event.sequence) || event.sequence <= previousSequence) {
      fail('stream sequence is not monotonically increasing');
    }
    previousSequence = event.sequence;
  }
  const completed = events.at(-1);
  if (completed.kind !== 'Completed' || !completed.result) {
    fail('stream did not end with a completed result');
  }
  result = completed.result;
} else {
  result = await response.json();
}

if (!result.output || !result.usage || typeof result.confidence !== 'number') {
  fail('gateway result envelope is incomplete');
}
for (const field of [
  'inputTokens',
  'outputTokens',
  'cost',
  'durationMilliseconds',
  'modelCalls',
  'toolCalls'
]) {
  if (typeof result.usage[field] !== 'number' || result.usage[field] < 0) {
    fail(`normalized usage field ${field} is invalid`);
  }
}

console.log(`PASS gateway-contract ${health.gatewayId} ${transport}`);

function fail(message) {
  throw new Error(`Gateway contract failed: ${message}`);
}
