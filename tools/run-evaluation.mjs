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

try {
  const idempotencyKey = `w04-email-send-${evaluationRunId}`;
  const sensitiveMarker = `w04-private-body-${evaluationRunId}`;
  const requestBody = {
    taskType: 'email.send',
    projectId: `evaluation-w04-${evaluationRunId}`,
    input: {
      to: 'evaluation@example.com',
      subject: 'W04 policy evaluation',
      body: sensitiveMarker
    },
    constraints: {
      allowExternalWrites: true,
      maxModelCalls: 0,
      maxToolCalls: 1,
      deadlineMilliseconds: 30000
    }
  };
  const executionHeaders = {
    'content-type': 'application/json',
    'x-tenant-id': 'evaluation',
    'x-actor-id': 'evaluation-requester',
    'x-permission-scopes': 'email:send',
    'idempotency-key': idempotencyKey
  };
  const executionResponse = await fetch(new URL('/v1/executions', baseUrl), {
    method: 'POST',
    headers: executionHeaders,
    body: JSON.stringify(requestBody)
  });
  if (!executionResponse.ok) {
    throw new Error(`execution returned HTTP ${executionResponse.status}: ${await executionResponse.text()}`);
  }
  const waiting = await executionResponse.json();
  assertEqual(waiting.status, 'WaitingForApproval', 'pre-approval status');

  const approvalResponse = await fetch(
    new URL(`/v1/executions/${waiting.executionId}/approvals`, baseUrl),
    {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tenant-id': 'evaluation',
        'x-actor-id': 'evaluation-approver',
        'x-permission-scopes': 'email:send approval:grant'
      },
      body: JSON.stringify({ actionId: 'execute', approved: true, reason: 'Evaluation approval.' })
    }
  );
  if (!approvalResponse.ok) {
    throw new Error(`approval returned HTTP ${approvalResponse.status}: ${await approvalResponse.text()}`);
  }
  const approved = await approvalResponse.json();
  assertEqual(approved.approval?.status, 'Approved', 'approval status');
  assertEqual(approved.execution?.status, 'Succeeded', 'post-approval status');
  assertEqual(approved.execution?.outcome?.resolutionLevel, 'DeterministicCapability', 'resolutionLevel');
  assertEqual(approved.execution?.outcome?.usage?.toolCalls, 1, 'toolCalls');

  const replayResponse = await fetch(new URL('/v1/executions', baseUrl), {
    method: 'POST',
    headers: executionHeaders,
    body: JSON.stringify(requestBody)
  });
  if (!replayResponse.ok) throw new Error(`idempotent replay returned HTTP ${replayResponse.status}`);
  const replay = await replayResponse.json();
  assertEqual(replay.executionId, waiting.executionId, 'idempotent execution id');

  const eventResponse = await fetch(
    new URL(`/v1/executions/${waiting.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!eventResponse.ok) throw new Error(`event replay returned HTTP ${eventResponse.status}`);
  const events = await eventResponse.text();
  for (const eventType of [
    'policy.evaluated',
    'approval.required',
    'approval.decided',
    'external_action.started',
    'external_action.completed'
  ]) {
    if (!events.includes(`event: ${eventType}`)) throw new Error(`event replay omitted ${eventType}`);
  }
  if ((events.match(/event: external_action\.completed/g) ?? []).length !== 1) {
    throw new Error('external action completed more than once');
  }
  if (events.includes(sensitiveMarker)) throw new Error('audit events exposed the external-action payload');
  console.log('PASS w04-policy-approval-idempotency');
} catch (error) {
  failed++;
  console.error(`FAIL w04-policy-approval-idempotency: ${error.message}`);
}

try {
  const projectId = `evaluation-w05-${evaluationRunId}`;
  const request = decision => ({
    taskType: 'email.draft',
    projectId,
    input: {
      recipient: { name: 'Ada' },
      projectName: 'iRoute',
      objective: 'Validate the artifact and memory lifecycle.',
      tone: 'professional',
      activeDecisions: [decision]
    },
    constraints: { maxModelCalls: 1 }
  });
  const execute = async (body, suffix, permissionScopes) => {
    const response = await fetch(new URL('/v1/executions', baseUrl), {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tenant-id': 'evaluation',
        'x-actor-id': 'evaluation-runner',
        'idempotency-key': `w05-${evaluationRunId}-${suffix}`,
        ...(permissionScopes ? { 'x-permission-scopes': permissionScopes } : {})
      },
      body: JSON.stringify(body)
    });
    if (!response.ok) throw new Error(`execution ${suffix} returned HTTP ${response.status}: ${await response.text()}`);
    return await response.json();
  };

  const first = await execute(request('Use SQLite for the prototype.'), 'v1');
  const firstArtifact = first.outcome?.artifacts?.[0];
  if (!firstArtifact) throw new Error('first execution did not materialize an artifact');
  assertEqual(firstArtifact.version, 1, 'first artifact version');

  const secondRequest = request('Use PostgreSQL for durable deployments.');
  const second = await execute(secondRequest, 'v2');
  const secondArtifact = second.outcome?.artifacts?.[0];
  if (!secondArtifact) throw new Error('second execution did not materialize an artifact');
  assertEqual(secondArtifact.version, 2, 'replacement artifact version');

  const invalidatedResponse = await fetch(
    new URL(`/v1/artifacts/${firstArtifact.artifactId}`, baseUrl),
    { headers: { 'x-tenant-id': 'evaluation' } }
  );
  if (!invalidatedResponse.ok) throw new Error(`invalidated artifact lookup returned HTTP ${invalidatedResponse.status}`);
  const invalidated = await invalidatedResponse.json();
  assertEqual(invalidated.lifecycleStatus, 'Invalidated', 'previous artifact lifecycle');
  assertEqual(invalidated.isActive, false, 'previous artifact active flag');

  const hiddenResponse = await fetch(
    new URL(`/v1/artifacts/${secondArtifact.artifactId}`, baseUrl),
    { headers: { 'x-tenant-id': 'evaluation-other-tenant' } }
  );
  assertEqual(hiddenResponse.status, 404, 'cross-tenant artifact status');

  const third = await execute(secondRequest, 'v2-reuse');
  assertEqual(third.outcome?.resolutionLevel, 'ExactArtifact', 'replacement reuse resolution');
  assertEqual(third.outcome?.usage?.modelCalls, 0, 'replacement reuse model calls');
  assertEqual(third.outcome?.artifacts?.[0]?.artifactId, secondArtifact.artifactId, 'replacement reuse artifact');

  const eventResponse = await fetch(
    new URL(`/v1/executions/${second.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!eventResponse.ok) throw new Error(`W05 event replay returned HTTP ${eventResponse.status}`);
  const events = await eventResponse.text();
  for (const eventType of ['memory.superseded', 'artifact.invalidated', 'artifact.materialized']) {
    if (!events.includes(`event: ${eventType}`)) throw new Error(`W05 event replay omitted ${eventType}`);
  }
  console.log('PASS w05-artifact-memory-lifecycle');

  const decision = await execute({
    taskType: 'project.decision.get',
    projectId,
    input: { key: 'activeDecisions[0]' },
    constraints: { maxModelCalls: 0 }
  }, 'decision-read', 'project:read');
  assertEqual(decision.status, 'Succeeded', 'decision lookup status');
  assertEqual(decision.outcome?.resolutionLevel, 'StructuredState', 'decision resolution level');
  assertEqual(decision.outcome?.usage?.modelCalls, 0, 'decision model calls');
  assertEqual(
    decision.outcome?.output?.value,
    'Use PostgreSQL for durable deployments.',
    'decision value'
  );
  const decisionEventResponse = await fetch(
    new URL(`/v1/executions/${decision.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!decisionEventResponse.ok) throw new Error(`W06 decision event replay returned HTTP ${decisionEventResponse.status}`);
  const decisionEvents = parseSseEvents(await decisionEventResponse.text());
  assertResolutionDecision(decisionEvents, 'exact-cache', 'exact_cache_miss', false);
  assertResolutionDecision(decisionEvents, 'fact-decision', 'state_hit', true);

  const deniedDecision = await execute({
    taskType: 'project.decision.get',
    projectId,
    input: { key: 'activeDecisions[0]' },
    constraints: { maxModelCalls: 0 }
  }, 'decision-denied');
  assertEqual(deniedDecision.status, 'Failed', 'permission-denied decision status');
  assertEqual(deniedDecision.error?.code, 'permission_scope_denied', 'permission-denied decision code');

  const explicitArtifact = await execute({
    taskType: 'email.draft',
    projectId,
    input: { artifactId: secondArtifact.artifactId }
  }, 'artifact-lookup');
  assertEqual(explicitArtifact.status, 'Succeeded', 'explicit artifact status');
  assertEqual(explicitArtifact.outcome?.resolutionLevel, 'ExactArtifact', 'explicit artifact resolution');
  assertEqual(explicitArtifact.outcome?.usage?.modelCalls, 0, 'explicit artifact model calls');
  assertEqual(
    explicitArtifact.outcome?.artifacts?.[0]?.artifactId,
    secondArtifact.artifactId,
    'explicit artifact id'
  );
  const artifactEventResponse = await fetch(
    new URL(`/v1/executions/${explicitArtifact.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!artifactEventResponse.ok) throw new Error(`W06 artifact event replay returned HTTP ${artifactEventResponse.status}`);
  assertResolutionDecision(
    parseSseEvents(await artifactEventResponse.text()),
    'artifact-lookup',
    'artifact_hit',
    true
  );
  console.log('PASS w06-no-model-resolution');
} catch (error) {
  failed++;
  console.error(`FAIL w05-w06-state-resolution: ${error.message}`);
}

if (failed > 0) process.exitCode = 1;

function assertEqual(actual, expected, label) {
  if (actual !== expected) throw new Error(`${label}: expected ${expected}, received ${actual}`);
}

function parseSseEvents(value) {
  return value
    .split('\n')
    .filter(line => line.startsWith('data:'))
    .map(line => JSON.parse(line.slice(5).trimStart()));
}

function assertResolutionDecision(events, resolver, code, accepted) {
  const event = events.find(item =>
    item.type === 'resolution.considered' && item.data?.resolver === resolver
  );
  if (!event) throw new Error(`resolution event omitted ${resolver}`);
  assertEqual(event.data.code, code, `${resolver} decision code`);
  assertEqual(event.data.accepted, accepted, `${resolver} accepted`);
  if (!event.data.reason) throw new Error(`${resolver} decision omitted its reason`);
  assertEqual(event.data.permissionChecked, true, `${resolver} permission check`);
}
