import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = 'iroute-w17-smoke';
const compose = [
  'compose',
  '--project-name', project,
  '--file', 'deploy/compose.sqlite.yaml'
];

cleanup();
try {
  run('docker', [...compose, 'up', '--build', '--detach', '--wait']);
  const live = await fetch('http://127.0.0.1:8080/health/live');
  const ready = await fetch('http://127.0.0.1:8080/health/ready');
  assert.equal(live.status, 200);
  assert.equal(ready.status, 200);

  const response = await fetch('http://127.0.0.1:8080/v1/executions', {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      'x-tenant-id': 'container-smoke',
      'x-actor-id': 'ci',
      'idempotency-key': 'w17-durable-worker-001'
    },
    body: await readFile(new URL('../examples/email-draft.json', import.meta.url), 'utf8')
  });
  const responseBody = await response.text();
  assert.equal(response.status, 202, responseBody);
  let execution = JSON.parse(responseBody);
  assert.equal(response.headers.get('location'), `/v1/executions/${execution.executionId}`);
  const deadline = Date.now() + 20_000;
  while (!['Succeeded', 'Failed', 'Cancelled', 'TimedOut'].includes(execution.status)) {
    assert.ok(Date.now() < deadline, `Execution ${execution.executionId} did not finish`);
    await new Promise(resolve => setTimeout(resolve, 200));
    const poll = await fetch(`http://127.0.0.1:8080/v1/executions/${execution.executionId}`, {
      headers: { 'x-tenant-id': 'container-smoke' }
    });
    const pollBody = await poll.text();
    assert.equal(poll.status, 200, pollBody);
    execution = JSON.parse(pollBody);
  }
  assert.equal(execution.status, 'Succeeded');
  const replay = await fetch(
    `http://127.0.0.1:8080/v1/executions/${execution.executionId}/events?after=0`,
    { headers: { accept: 'text/event-stream', 'x-tenant-id': 'container-smoke' } }
  );
  assert.equal(replay.status, 200);
  const frames = parseSse(await replay.text());
  assert.deepEqual(frames.map(frame => frame.id), frames.map(frame => frame.id).toSorted((a, b) => a - b));
  assert.ok(frames.some(frame => frame.event === 'execution.queued'));
  assert.ok(frames.some(frame => frame.event === 'execution.lease_claimed'));
  assert.ok(frames.some(frame => frame.event === 'execution.completed'));

  const cursor = frames[Math.floor(frames.length / 2)].id;
  const reconnected = await fetch(
    `http://127.0.0.1:8080/v1/executions/${execution.executionId}/events`,
    {
      headers: {
        accept: 'text/event-stream',
        'x-tenant-id': 'container-smoke',
        'last-event-id': String(cursor)
      }
    }
  );
  assert.equal(reconnected.status, 200);
  assert.ok(parseSse(await reconnected.text()).every(frame => frame.id > cursor));
  console.log('PASS durable SQLite API and worker quick start');
} finally {
  cleanup();
}

function cleanup() {
  spawnSync('docker', [...compose, 'down', '--volumes', '--remove-orphans'], {
    cwd: root,
    stdio: 'inherit'
  });
}

function run(command, args) {
  const result = spawnSync(command, args, { cwd: root, stdio: 'inherit' });
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} failed with exit code ${result.status}`);
  }
}

function parseSse(body) {
  return body
    .replaceAll('\r\n', '\n')
    .split('\n\n')
    .filter(Boolean)
    .map(block => {
      const lines = block.split('\n');
      return {
        id: Number(lines.find(line => line.startsWith('id:'))?.slice(3).trim()),
        event: lines.find(line => line.startsWith('event:'))?.slice(6).trim()
      };
    });
}
