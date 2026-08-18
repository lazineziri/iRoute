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
    return await waitForExecution(await response.json(), 'evaluation');
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
      if (first.outcome?.usage?.modelCalls > 0) {
        const routing = first.outcome?.routing;
        if (!routing) throw new Error('generated outcome omitted routing decision');
        assertEqual(routing.policyVersion, 'routing.w18.v1', 'routing policy version');
        assertEqual(routing.path, 'Direct', 'routing path');
        assertEqual(routing.plannerInvoked, false, 'direct planner invocation');
        assertEqual(routing.planningCalls, 0, 'direct planning calls');
        if (!routing.selectedProfileId) throw new Error('routing omitted selected model profile');
        if (!routing.candidates?.every(candidate =>
          typeof candidate.expectedQuality === 'number' &&
          typeof candidate.expectedCost === 'number' &&
          typeof candidate.expectedLatencyMilliseconds === 'number' &&
          typeof candidate.score === 'number'
        )) throw new Error('routing candidates omitted measured policy inputs');
      }
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
      if (first.outcome?.usage?.modelCalls > 0 && !events.includes('event: routing.decided')) {
        throw new Error('event replay omitted routing.decided');
      }
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
  const healthResponse = await fetch(new URL('/health/model-gateway', baseUrl));
  if (healthResponse.status !== 200 && healthResponse.status !== 503) {
    throw new Error(`gateway health returned HTTP ${healthResponse.status}`);
  }
  const health = await healthResponse.json();
  if (!health.gatewayId) throw new Error('gateway health omitted gatewayId');
  if (!['Healthy', 'Degraded', 'Unavailable'].includes(health.status)) {
    throw new Error(`gateway health returned unknown status ${health.status}`);
  }
  if (health.status !== 'Healthy') throw new Error(`gateway health is ${health.status}`);

  const response = await fetch(new URL('/v1/executions', baseUrl), {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      'x-tenant-id': 'evaluation',
      'x-actor-id': 'evaluation-runner',
      'idempotency-key': `w09-gateway-${evaluationRunId}`
    },
    body: JSON.stringify({
      taskType: 'email.draft',
      projectId: `evaluation-w09-${evaluationRunId}`,
      input: {
        recipient: { name: 'Ada' },
        projectName: 'iRoute',
        objective: 'Validate the generic model-gateway contract.',
        activeDecisions: ['Keep provider-specific logic behind the external gateway boundary.']
      },
      constraints: {
        maxModelCalls: 1,
        deadlineMilliseconds: 30000
      }
    })
  });
  if (!response.ok) throw new Error(`execution returned HTTP ${response.status}: ${await response.text()}`);
  const execution = await waitForExecution(await response.json(), 'evaluation');
  assertEqual(execution.status, 'Succeeded', 'W09 execution status');
  if (typeof execution.outcome?.usage?.durationMilliseconds !== 'number') {
    throw new Error('W09 outcome omitted normalized observed latency');
  }

  const eventResponse = await fetch(
    new URL(`/v1/executions/${execution.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!eventResponse.ok) throw new Error(`W09 event replay returned HTTP ${eventResponse.status}`);
  const events = parseSseEvents(await eventResponse.text());
  const started = events.find(item => item.type === 'gateway.started');
  const completed = events.find(item => item.type === 'gateway.completed');
  if (!started || !completed) throw new Error('W09 gateway lifecycle events were incomplete');
  assertEqual(started.data?.deadlineMilliseconds, 30000, 'W09 gateway deadline');
  assertEqual(completed.data?.gatewayId, health.gatewayId, 'W09 configured gateway identity');
  if (!['Buffered', 'Streaming'].includes(completed.data?.transport)) {
    throw new Error(`W09 gateway transport is invalid: ${completed.data?.transport}`);
  }
  assertEqual(
    completed.data?.durationMilliseconds,
    execution.outcome.usage.durationMilliseconds,
    'W09 observed latency normalization'
  );
  if (completed.data.transport === 'Streaming' &&
      !events.some(item => item.type === 'gateway.streamed')) {
    throw new Error('W09 streaming gateway omitted gateway.streamed');
  }
  console.log('PASS w09-generic-model-gateway');
} catch (error) {
  failed++;
  console.error(`FAIL w09-generic-model-gateway: ${error.message}`);
}

try {
  const response = await fetch(new URL('/v1/executions', baseUrl), {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      'x-tenant-id': 'evaluation',
      'x-actor-id': 'evaluation-runner',
      'idempotency-key': `w08-routing-${evaluationRunId}`
    },
    body: JSON.stringify({
      taskType: 'email.draft',
      projectId: `evaluation-w08-${evaluationRunId}`,
      input: {
        recipient: { name: 'Ada' },
        projectName: 'iRoute',
        objective: 'Validate measured routing and escalation.',
        activeDecisions: ['Select the cheapest route that clears mandatory quality.']
      },
      constraints: {
        minimumQuality: 0.9,
        maxModelCalls: 1,
        maxTaskDepth: 1
      }
    })
  });
  if (!response.ok) throw new Error(`execution returned HTTP ${response.status}: ${await response.text()}`);
  const execution = await waitForExecution(await response.json(), 'evaluation');
  assertEqual(execution.status, 'Succeeded', 'W08 execution status');
  assertEqual(execution.outcome?.resolutionLevel, 'StrongModel', 'W08 resolution level');
  const routing = execution.outcome?.routing;
  if (!routing) throw new Error('W08 outcome omitted routing decision');
  assertEqual(routing.path, 'Direct', 'W08 routing path');
  assertEqual(routing.plannerInvoked, false, 'W08 planner invocation');
  assertEqual(routing.planningCalls, 0, 'W08 planning calls');
  assertEqual(routing.selectedProfileId, 'text.generation.strong.eval-v1', 'W08 selected profile');
  assertEqual(routing.selectedModelTier, 'Strong', 'W08 selected tier');
  assertEqual(routing.escalated, true, 'W08 escalation');
  const small = routing.candidates?.find(candidate => candidate.modelTier === 'Small');
  if (!small) throw new Error('W08 decision omitted the cheaper small candidate');
  assertEqual(small.eligible, false, 'W08 small eligibility');
  assertEqual(small.expectedQuality, 0.84, 'W08 small expected quality');
  assertEqual(small.expectedCost, 0.004, 'W08 small expected cost');
  assertEqual(small.expectedLatencyMilliseconds, 900, 'W08 small expected latency');
  if (!small.reason.includes('below floor')) throw new Error('W08 small rejection was not explained');

  const eventResponse = await fetch(
    new URL(`/v1/executions/${execution.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!eventResponse.ok) throw new Error(`W08 event replay returned HTTP ${eventResponse.status}`);
  const events = parseSseEvents(await eventResponse.text());
  const decision = events.find(item => item.type === 'routing.decided');
  const escalation = events.find(item => item.type === 'routing.escalated');
  if (!decision || !escalation) throw new Error('W08 routing audit events were incomplete');
  assertEqual(decision.data?.policyVersion, 'routing.w18.v1', 'W08 event policy version');
  assertEqual(decision.data?.selectedProfileId, routing.selectedProfileId, 'W08 event profile');
  assertEqual(escalation.data?.escalated, true, 'W08 event escalation');
  console.log('PASS w08-routing-and-escalation');
} catch (error) {
  failed++;
  console.error(`FAIL w08-routing-and-escalation: ${error.message}`);
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
  approved.execution = await waitForExecution(approved.execution, 'evaluation');
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
    return await waitForExecution(await response.json(), 'evaluation');
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

  const compiled = await execute({
    taskType: 'email.draft',
    projectId,
    input: {
      recipient: { name: 'Ada' },
      projectName: 'iRoute',
      objective: `Validate bounded W07 context ${evaluationRunId}.`,
      activeDecisions: [
        { key: 'architecture', value: 'Compile only evidence-backed project context.' },
        { key: 'duplicate', value: 'The signed specification is authoritative.' }
      ],
      authoritativeSources: [
        { reference: 'spec:w07', value: 'The signed specification is authoritative.' },
        { reference: 'adr:context', value: 'Every included fact needs provenance.' }
      ],
      contextArtifacts: [
        { artifactId: secondArtifact.artifactId, sections: ['subject'] }
      ],
      projectHistory: Array.from(
        { length: 8 },
        (_, index) => `Historical project event ${index} for W07 evaluation.`
      )
    },
    constraints: {
      maxInputTokens: 240,
      minimumQuality: 0.8,
      requireEvidence: true,
      maxModelCalls: 1
    }
  }, 'context-compiler');
  assertEqual(compiled.status, 'Succeeded', 'W07 execution status');
  assertEqual(compiled.outcome?.usage?.modelCalls, 1, 'W07 model calls');
  const manifest = compiled.outcome?.context;
  if (!manifest) throw new Error('W07 outcome omitted the context manifest');
  if (manifest.estimatedTokens > manifest.budgetTokens) {
    throw new Error('W07 compiled context exceeded its token budget');
  }
  assertEqual(manifest.budgetTokens, 240, 'W07 context budget');
  assertEqual(
    manifest.estimatedTokens,
    manifest.projectedInputTokens + manifest.contextTokens,
    'W07 context token accounting'
  );
  assertEqual(manifest.fullHistoryIncluded, false, 'W07 full-history flag');
  const includedEntries = manifest.entries.filter(entry => entry.included);
  if (includedEntries.length === 0) throw new Error('W07 context included no evidence');
  for (const entry of includedEntries) {
    if (!entry.outputPath) throw new Error(`W07 included entry ${entry.reference} omitted outputPath`);
    if (!manifest.provenance?.[entry.outputPath]?.reference) {
      throw new Error(`W07 included entry ${entry.reference} omitted provenance`);
    }
  }
  if (!manifest.entries.some(entry =>
    !entry.included && entry.reason.includes('exact duplicate')
  )) throw new Error('W07 context did not report duplicate removal');
  if (!manifest.entries.some(entry =>
    !entry.included && entry.reason.includes('full history')
  )) throw new Error('W07 context did not report bounded history');
  if (!manifest.entries.some(entry =>
    entry.included && entry.reference === `artifact:${secondArtifact.artifactId}#/subject`
  )) throw new Error('W07 context omitted the requested artifact section');

  const contextEventResponse = await fetch(
    new URL(`/v1/executions/${compiled.executionId}/events?after=0`, baseUrl),
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
  );
  if (!contextEventResponse.ok) throw new Error(`W07 event replay returned HTTP ${contextEventResponse.status}`);
  const contextEvent = parseSseEvents(await contextEventResponse.text())
    .find(item => item.type === 'context.compiled');
  if (!contextEvent) throw new Error('W07 event replay omitted context.compiled');
  assertEqual(contextEvent.data?.fullHistoryIncluded, false, 'W07 event full-history flag');
  if ((contextEvent.data?.provenance ?? 0) < 1) throw new Error('W07 event omitted provenance count');
  console.log('PASS w07-context-compiler');
} catch (error) {
  failed++;
  console.error(`FAIL w05-w07-state-context: ${error.message}`);
}

try {
  const cases = [
    {
      id: 'calendar',
      taskType: 'calendar.find_slots',
      permissionScope: 'calendar:read',
      input: { timezone: 'Europe/Tirane' },
      outputField: 'slots'
    },
    {
      id: 'database',
      taskType: 'database.answer',
      permissionScope: 'database:read',
      input: { queryId: 'project-status' },
      outputField: 'answer'
    }
  ];

  for (const item of cases) {
    const response = await fetch(new URL('/v1/executions', baseUrl), {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-tenant-id': 'evaluation',
        'x-actor-id': 'evaluation-runner',
        'x-permission-scopes': item.permissionScope,
        'idempotency-key': `w11-${item.id}-${evaluationRunId}`
      },
      body: JSON.stringify({
        taskType: item.taskType,
        projectId: `evaluation-w11-${evaluationRunId}`,
        input: item.input,
        constraints: { maxModelCalls: 0, maxToolCalls: 1, deadlineMilliseconds: 20000 }
      })
    });
    if (!response.ok) {
      throw new Error(`${item.id} execution returned HTTP ${response.status}: ${await response.text()}`);
    }

    const execution = await waitForExecution(await response.json(), 'evaluation');
    assertEqual(execution.status, 'Succeeded', `W11 ${item.id} status`);
    assertEqual(execution.outcome?.resolutionLevel, 'DeterministicCapability', `W11 ${item.id} resolution`);
    assertEqual(execution.outcome?.usage?.modelCalls, 0, `W11 ${item.id} model calls`);
    assertEqual(execution.outcome?.usage?.toolCalls, 1, `W11 ${item.id} tool calls`);
    if (execution.outcome?.output?.[item.outputField] === undefined) {
      throw new Error(`W11 ${item.id} output omitted ${item.outputField}`);
    }

    const eventResponse = await fetch(
      new URL(`/v1/executions/${execution.executionId}/events?after=0`, baseUrl),
      { headers: { accept: 'text/event-stream', 'x-tenant-id': 'evaluation' } }
    );
    if (!eventResponse.ok) throw new Error(`W11 ${item.id} event replay returned HTTP ${eventResponse.status}`);
    const events = parseSseEvents(await eventResponse.text());
    if (!events.some(event => event.type === 'capability.started')) {
      throw new Error(`W11 ${item.id} event replay omitted capability.started`);
    }
    const completed = events.find(event => event.type === 'capability.completed');
    if (!completed) throw new Error(`W11 ${item.id} event replay omitted capability.completed`);
    assertEqual(completed.data?.projected, true, `W11 ${item.id} projection flag`);
    if (!completed.data?.connectorId || !completed.data?.outputReference) {
      throw new Error(`W11 ${item.id} completion omitted connector metadata`);
    }
  }
  console.log('PASS w11-capability-connectors');
} catch (error) {
  failed++;
  console.error(`FAIL w11-capability-connectors: ${error.message}`);
}

if (failed > 0) process.exitCode = 1;

async function waitForExecution(execution, tenantId) {
  const settled = new Set(['WaitingForApproval', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut']);
  const deadline = Date.now() + 60_000;
  while (!settled.has(execution.status)) {
    if (Date.now() >= deadline) {
      throw new Error(`execution ${execution.executionId} did not settle within 60 seconds`);
    }

    await new Promise(resolve => setTimeout(resolve, 200));
    const response = await fetch(
      new URL(`/v1/executions/${execution.executionId}`, baseUrl),
      { headers: { 'x-tenant-id': tenantId } }
    );
    if (!response.ok) {
      throw new Error(`execution poll returned HTTP ${response.status}: ${await response.text()}`);
    }
    execution = await response.json();
  }

  return execution;
}

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
