import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import YAML from 'yaml';

test('SQLite Compose is a persistent single-container quick start', async () => {
  const compose = YAML.parse(await read('deploy/compose.sqlite.yaml'));
  assert.deepEqual(Object.keys(compose.services), ['api']);
  const api = compose.services.api;
  assert.equal(api.build.target, 'api');
  assert.equal(api.environment.Storage__Provider, 'Sqlite');
  assert.equal(api.environment.Storage__AutoInitialize, 'true');
  assert.match(api.environment.ConnectionStrings__iRoute, /\/var\/lib\/iroute\/iroute\.db/);
  assert.ok(api.volumes.includes('sqlite-data:/var/lib/iroute'));
  assert.equal(api.read_only, true);
  assert.ok(api.healthcheck.test.includes('http://127.0.0.1:8080/health/ready'));
});

test('PostgreSQL Compose migrates once before API and worker startup', async () => {
  const compose = YAML.parse(await read('deploy/compose.yaml'));
  assert.deepEqual(
    Object.keys(compose.services).sort(),
    ['api', 'migrate', 'postgres', 'worker']
  );
  assert.equal(compose.services.migrate.build.target, 'migrate');
  assert.deepEqual(compose.services.migrate.command, ['up']);
  assert.equal(compose.services.api.environment.Storage__AutoInitialize, 'false');
  assert.equal(compose.services.worker.environment.Storage__AutoInitialize, 'false');
  assert.equal(
    compose.services.api.depends_on.migrate.condition,
    'service_completed_successfully'
  );
  assert.equal(
    compose.services.worker.depends_on.migrate.condition,
    'service_completed_successfully'
  );
});

test('Kubernetes reference separates migrations and horizontally scales only the API', async () => {
  const resources = await kubernetesResources([
    'deploy/kubernetes/namespace.yaml',
    'deploy/kubernetes/configmap.yaml',
    'deploy/kubernetes/serviceaccounts.yaml',
    'deploy/kubernetes/api.yaml',
    'deploy/kubernetes/worker.yaml',
    'deploy/kubernetes/migrate-job.yaml',
    'deploy/kubernetes/secret.example.yaml',
    'deploy/kubernetes/kustomization.yaml'
  ]);
  const api = resource(resources, 'Deployment', 'iroute-api');
  const worker = resource(resources, 'Deployment', 'iroute-worker');
  const autoscaler = resource(resources, 'HorizontalPodAutoscaler', 'iroute-api');
  const config = resource(resources, 'ConfigMap', 'iroute-config');
  const migration = resources.find(item => item.kind === 'Job');

  assert.equal(api.spec.replicas, 2);
  assert.equal(api.spec.strategy.rollingUpdate.maxUnavailable, 0);
  assert.equal(worker.spec.replicas, 1);
  assert.equal(worker.spec.strategy.type, 'Recreate');
  assert.equal(autoscaler.spec.minReplicas, 2);
  assert.ok(autoscaler.spec.maxReplicas > autoscaler.spec.minReplicas);
  assert.equal(config.data.Storage__Provider, 'Postgres');
  assert.equal(config.data.Storage__AutoInitialize, 'false');
  assert.ok(migration);
  assert.match(migration.metadata.generateName, /^iroute-migrate-/);
  assert.deepEqual(migration.spec.template.spec.containers[0].args, ['up']);

  const apiContainer = api.spec.template.spec.containers[0];
  assert.equal(apiContainer.readinessProbe.httpGet.path, '/health/ready');
  assert.equal(apiContainer.livenessProbe.httpGet.path, '/health/live');
  assert.equal(apiContainer.securityContext.readOnlyRootFilesystem, true);
  assert.ok(apiContainer.resources.requests.cpu);
  assert.ok(apiContainer.resources.limits.memory);
});

test('Container build publishes distinct non-root API, worker, and migration targets', async () => {
  const dockerfile = await read('deploy/Dockerfile');
  assert.match(dockerfile, /FROM runtime-base AS api/);
  assert.match(dockerfile, /FROM runtime-base AS worker/);
  assert.match(dockerfile, /FROM runtime-base AS migrate/);
  assert.match(dockerfile, /USER iroute/);
  assert.match(dockerfile, /HEALTHCHECK[\s\S]*\/health\/ready/);
  assert.doesNotMatch(dockerfile, /:latest/);
});

async function kubernetesResources(paths) {
  const documents = [];
  for (const path of paths) {
    const parsed = YAML.parseAllDocuments(await read(path));
    for (const document of parsed) {
      if (document.errors.length > 0) throw document.errors[0];
      const value = document.toJSON();
      if (value) documents.push(value);
    }
  }
  return documents;
}

function resource(resources, kind, name) {
  const result = resources.find(item => item.kind === kind && item.metadata?.name === name);
  assert.ok(result, `Missing ${kind}/${name}`);
  return result;
}

async function read(path) {
  return await readFile(new URL(`../${path}`, import.meta.url), 'utf8');
}
