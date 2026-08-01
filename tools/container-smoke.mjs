import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = 'iroute-w15-smoke';
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
      'idempotency-key': 'w15-single-container-001'
    },
    body: await readFile(new URL('../examples/email-draft.json', import.meta.url), 'utf8')
  });
  const responseBody = await response.text();
  assert.equal(response.status, 200, responseBody);
  const execution = JSON.parse(responseBody);
  assert.equal(execution.status, 'Succeeded');
  console.log('PASS single-container SQLite quick start');
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
