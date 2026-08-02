import assert from 'node:assert/strict';
import { access, readFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const root = fileURLToPath(new URL('..', import.meta.url));
const release = JSON.parse(await read('release.json'));

test('canonical release metadata aligns every versioned artifact', async () => {
  assert.match(release.version, /^\d+\.\d+\.\d+-alpha\.\d+$/);
  assert.equal(release.channel, 'alpha');
  assert.equal(release.apiVersion, 'v1');
  assert.match(release.releaseDate, /^\d{4}-\d{2}-\d{2}$/);

  for (const project of [
    'src/iRoute.Contracts/iRoute.Contracts.csproj',
    'src/iRoute.Sdk.DotNet/iRoute.Sdk.DotNet.csproj',
    'src/iRoute.Cli/iRoute.Cli.csproj'
  ]) {
    assert.match(await read(project), new RegExp(`<Version>${escape(release.version)}</Version>`));
  }

  const nodePackage = JSON.parse(await read('sdks/node/package.json'));
  assert.equal(nodePackage.version, release.version);
  assert.equal(nodePackage.private, false);
  assert.equal(nodePackage.license, 'Apache-2.0');
  assert.equal(nodePackage.publishConfig.access, 'public');
  assert.equal(nodePackage.exports['.'].types, './dist/index.d.ts');

  const nodeLock = JSON.parse(await read('sdks/node/package-lock.json'));
  assert.equal(nodeLock.version, release.version);
  assert.equal(nodeLock.packages[''].version, release.version);

  const pythonVersion = release.version.replace('-alpha.', 'a');
  assert.match(await read('sdks/python/pyproject.toml'), new RegExp(`version = "${escape(pythonVersion)}"`));
  assert.match(await read('sdks/java/pom.xml'), new RegExp(`<version>${escape(release.version)}</version>`));
  assert.match(await read('sdks/rust/Cargo.toml'), new RegExp(`version = "${escape(release.version)}"`));
  assert.match(await read('sdks/php/composer.json'), /"license": "Apache-2\.0"/);

  const dockerfile = await read('deploy/Dockerfile');
  assert.match(dockerfile, new RegExp(`ARG VERSION=${escape(release.version)}`));
  for (const manifest of ['api.yaml', 'worker.yaml', 'migrate-job.yaml']) {
    assert.match(
      await read(`deploy/kubernetes/${manifest}`),
      new RegExp(`:${escape(release.version)}`)
    );
  }

  assert.ok((await read(release.releaseNotes)).includes(`# iRoute ${release.version}`));
  assert.ok((await read('CHANGELOG.md')).includes(`## [${release.version}] - ${release.releaseDate}`));
});

test('license, community, security, and compatibility policies are adoption ready', async () => {
  assert.match(await read('LICENSE'), /Apache License\s+Version 2\.0, January 2004/);
  assert.match(await read('NOTICE'), /Copyright 2026 iRoute contributors/);
  assert.match(await read('Directory.Build.props'), /<PackageLicenseExpression>Apache-2\.0<\/PackageLicenseExpression>/);

  const required = [
    'CODE_OF_CONDUCT.md',
    'CONTRIBUTING.md',
    'GOVERNANCE.md',
    'SECURITY.md',
    'SUPPORT.md',
    'docs/architecture.md',
    'docs/compatibility.md',
    'docs/installation.md',
    'docs/releasing.md'
  ];
  for (const path of required) {
    assert.ok((await read(path)).length > 400, `${path} is unexpectedly thin`);
  }

  const security = await read('SECURITY.md');
  assert.match(security, /Report a vulnerability/);
  assert.match(security, /Do not disclose.*public issue/is);
  assert.match(security, new RegExp(escape(release.version)));

  const contributing = await read('CONTRIBUTING.md');
  assert.match(contributing, /Compatibility and versioning/);
  assert.match(contributing, /Conventional Commit/);
  assert.match(contributing, /CHANGELOG\.md/);

  const compatibility = await read('docs/compatibility.md');
  assert.match(compatibility, /at least 12 months/);
  assert.match(compatibility, /requires `\/v2`/);
  assert.match(compatibility, /Application rollback over an additive schema is supported/);
});

test('documented clean install and release workflow are executable and publication gated', async () => {
  const installation = await read('docs/installation.md');
  for (const command of [
    'dotnet restore iRoute.slnx',
    'dotnet build iRoute.slnx --configuration Release --no-restore',
    'npm ci',
    'npm run test:contracts',
    'npm run test:release',
    'docker compose --file deploy/compose.sqlite.yaml up --build --wait'
  ]) {
    assert.ok(installation.includes(command), `clean-install guide is missing: ${command}`);
  }

  const workflowText = await read('.github/workflows/release.yml');
  const workflow = YAML.parse(workflowText);
  assert.ok(workflow.on.workflow_dispatch);
  assert.deepEqual(workflow.on.push.tags, ['v*.*.*']);
  assert.match(workflowText, /npm run test:release/);
  assert.match(workflowText, /tools\/build-release\.mjs/);
  assert.match(workflowText, /startsWith\(github\.ref, 'refs\/tags\/v'\)/);
  assert.match(workflowText, /gh release create/);
  assert.match(workflowText, /--verify-tag/);

  for (const template of [
    '.github/ISSUE_TEMPLATE/bug_report.yml',
    '.github/ISSUE_TEMPLATE/feature_request.yml',
    '.github/ISSUE_TEMPLATE/config.yml',
    '.github/dependabot.yml',
    '.github/workflows/ci.yml',
    '.github/workflows/codeql.yml'
  ]) {
    assert.doesNotThrow(() => YAML.parse(workflowSafeRead(template)));
  }
});

test('alpha disclosures and development identity defaults fail closed', async () => {
  const releaseNotes = await read(release.releaseNotes);
  for (const requiredDisclosure of [
    /experimental/i,
    /breaking changes are expected/i,
    /no production SLA/i,
    /no security-response.*SLA/i,
    /not production integrations/i,
    /not validated production measurements/i,
    /Self-hosters are responsible/i
  ]) {
    assert.match(releaseNotes, requiredDisclosure);
  }

  const identity = await read('src/iRoute.Api/IdentityConfiguration.cs');
  assert.match(identity, /Environments\.Development/);
  assert.match(identity, /DevelopmentHeaders is permitted only/);
  assert.match(await read('src/iRoute.Api/Program.cs'), /builder\.Environment\.EnvironmentName/);

  const localCompose = YAML.parse(await read('deploy/compose.sqlite.yaml'));
  assert.equal(localCompose.services.api.environment.ASPNETCORE_ENVIRONMENT, 'Development');
  assert.equal(localCompose.services.api.environment.Identity__Mode, 'DevelopmentHeaders');

  const productionCompose = YAML.parse(await read('deploy/compose.yaml'));
  assert.equal(productionCompose.services.api.environment.ASPNETCORE_ENVIRONMENT, 'Production');
  assert.equal(productionCompose.services.api.environment.Identity__Mode, '${IROUTE_IDENTITY_MODE:-Jwt}');

  const kubernetesConfig = YAML.parse(await read('deploy/kubernetes/configmap.yaml'));
  assert.equal(kubernetesConfig.data.ASPNETCORE_ENVIRONMENT, 'Production');
  assert.equal(kubernetesConfig.data.Identity__Mode, 'Jwt');
});

test('release inputs exclude private references, local secrets, and generated state', () => {
  const trackedDocuments = git('ls-files', '*.docx');
  assert.equal(trackedDocuments, '');
  assert.equal(git('ls-files', '.env', 'deploy/kubernetes/secret.yaml'), '');

  const attributes = workflowSafeRead('.gitattributes');
  assert.match(attributes, /\*\.docx binary/);
  const ignore = workflowSafeRead('.gitignore');
  assert.match(ignore, /\/docs\/reference\/Task-Aware-LLM-Router-Project-Definition\.docx/);
  assert.match(ignore, /\/deploy\/kubernetes\/secret\.yaml/);
});

test('every official SDK has a complete standalone usage guide', async () => {
  const guides = [
    'src/iRoute.Sdk.DotNet/README.md',
    'sdks/node/README.md',
    'sdks/python/README.md',
    'sdks/java/README.md',
    'sdks/php/README.md',
    'sdks/rust/README.md'
  ];
  const requiredSections = [
    '## Start iRoute',
    '## Create a client',
    '## Submit an execution',
    '## Poll until terminal',
    '## Cancel an execution',
    '## Approve or deny an action',
    '## Retrieve an artifact',
    '## Health and observability',
    '## Method reference',
    '## Run the example and tests'
  ];

  for (const guide of guides) {
    const source = await read(guide);
    assert.ok(source.length > 5_000, `${guide} is unexpectedly thin`);
    for (const section of requiredSections) {
      assert.ok(source.includes(section), `${guide} is missing ${section}`);
    }
    assert.match(source, /does not\s+(?:automatically\s+)?retry/i, `${guide} must state retry ownership`);
    assert.match(source, /idempotency key/i, `${guide} must explain idempotency`);
  }
});

test('release, adoption, and community documentation has no broken local links', async () => {
  const documents = [
    'README.md',
    'CONTRIBUTING.md',
    'GOVERNANCE.md',
    'SECURITY.md',
    'SUPPORT.md',
    'docs/README.md',
    'docs/architecture.md',
    'docs/compatibility.md',
    'docs/installation.md',
    'docs/releasing.md',
    'docs/sdk-usage.md',
    'sdks/README.md',
    'examples/sdks/README.md',
    'src/iRoute.Sdk.DotNet/README.md',
    'sdks/node/README.md',
    'sdks/python/README.md',
    'sdks/java/README.md',
    'sdks/php/README.md',
    'sdks/rust/README.md',
    release.releaseNotes
  ];

  for (const document of documents) {
    const source = await read(document);
    for (const match of source.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
      const href = match[1].split('#', 1)[0];
      if (!href || /^(?:https?:|mailto:)/.test(href)) continue;
      const target = resolve(root, dirname(document), decodeURIComponent(href));
      await assert.doesNotReject(access(target), `${document} has a broken link to ${href}`);
    }
  }
});

function git(...args) {
  const result = spawnSync('git', args, { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout.trim();
}

async function read(path) {
  return await readFile(new URL(`../${path}`, import.meta.url), 'utf8');
}

function workflowSafeRead(path) {
  const result = spawnSync('git', ['show', `:${path}`], { encoding: 'utf8' });
  if (result.status === 0) return result.stdout;
  const fallback = spawnSync('sed', ['-n', '1,10000p', path], { encoding: 'utf8' });
  assert.equal(fallback.status, 0, fallback.stderr);
  return fallback.stdout;
}

function escape(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
