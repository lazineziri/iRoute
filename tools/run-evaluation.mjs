import { readFile } from 'node:fs/promises';

const baseUrl = process.env.IROUTE_BASE_URL ?? 'http://localhost:8080';
const fixtureUrl = new URL('../eval/fixtures/email.draft.jsonl', import.meta.url);
const cases = (await readFile(fixtureUrl, 'utf8'))
  .split('\n')
  .filter(Boolean)
  .map(line => JSON.parse(line));
const evaluationRunId = Date.now();

let failed = 0;
for (const fixture of cases) {
  const run = async sequence => {
    const response = await fetch(new URL('/v1/executions', baseUrl), {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tenant-id': 'evaluation',
        'x-actor-id': 'evaluation-runner',
        'idempotency-key': `${fixture.caseId}-${Date.now()}-${sequence}`
      },
      body: JSON.stringify({
        ...fixture.request,
        projectId: `${fixture.request.projectId ?? 'evaluation'}-${evaluationRunId}-${fixture.caseId}`
      })
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}: ${await response.text()}`);
    return await response.json();
  };

  try {
    const first = await run(1);
    assertEqual(first.status, fixture.expected.status, 'status');
    if (fixture.expected.resolutionLevel) {
      assertEqual(first.outcome?.resolutionLevel, fixture.expected.resolutionLevel, 'resolutionLevel');
    }
    if (fixture.expected.errorCode) {
      assertEqual(first.error?.code, fixture.expected.errorCode, 'errorCode');
    }
    for (const field of fixture.expected.requiredOutputFields ?? []) {
      if (!first.outcome?.output?.[field]) throw new Error(`missing output field ${field}`);
    }

    if (first.status === 'Succeeded') {
      const artifactId = first.outcome?.artifacts?.[0]?.artifactId;
      if (!artifactId) throw new Error('successful execution did not reference an artifact');
      const artifactResponse = await fetch(
        new URL(`/v1/artifacts/${artifactId}`, baseUrl),
        { headers: { 'x-tenant-id': 'evaluation' } }
      );
      if (!artifactResponse.ok) throw new Error(`artifact lookup returned HTTP ${artifactResponse.status}`);
      const artifact = await artifactResponse.json();
      if (artifact.artifact?.artifactId !== artifactId) throw new Error('artifact lookup returned the wrong artifact');

      const eventResponse = await fetch(
        new URL(`/v1/executions/${first.executionId}/events?after=0`, baseUrl),
        { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
      );
      if (!eventResponse.ok) throw new Error(`event replay returned HTTP ${eventResponse.status}`);
      const events = await eventResponse.text();
      if (!events.includes('event: execution.completed')) throw new Error('event replay omitted execution.completed');
      if (!events.includes('event: artifact.materialized')) throw new Error('event replay omitted artifact.materialized');
      if (!events.includes('event: workflow.checkpointed')) throw new Error('event replay omitted workflow.checkpointed');
      if (!events.includes('event: step.completed')) throw new Error('event replay omitted step.completed');
    }

    if (fixture.repeat === 2) {
      const second = await run(2);
      assertEqual(second.outcome?.resolutionLevel, fixture.expected.secondResolutionLevel, 'secondResolutionLevel');
      assertEqual(second.outcome?.usage?.modelCalls, fixture.expected.secondModelCalls, 'secondModelCalls');
    }
    console.log(`PASS ${fixture.caseId}`);
  } catch (error) {
    failed++;
    console.error(`FAIL ${fixture.caseId}: ${error.message}`);
  }
}

if (failed > 0) process.exitCode = 1;

function assertEqual(actual, expected, label) {
  if (actual !== expected) throw new Error(`${label}: expected ${expected}, received ${actual}`);
}
