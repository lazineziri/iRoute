import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const metadata = JSON.parse(await readFile(join(root, 'release.json'), 'utf8'));
const output = resolve(root, argument('--output') ?? 'artifacts/release');
const requestedVersion = argument('--version') ?? metadata.version;
const useIndex = process.argv.includes('--use-index');
const requireClean = process.argv.includes('--require-clean');

assert.equal(requestedVersion, metadata.version, 'requested version must match release.json');
if (requireClean) {
  assert.equal(run('git', ['status', '--porcelain']).trim(), '', 'release worktree must be clean');
}

await ensureEmptyDirectory(output);
run('node', ['--test', 'tools/release-readiness.test.mjs']);

for (const project of [
  'src/iRoute.Contracts/iRoute.Contracts.csproj',
  'src/iRoute.Sdk.DotNet/iRoute.Sdk.DotNet.csproj',
  'src/iRoute.Cli/iRoute.Cli.csproj'
]) {
  run('dotnet', [
    'pack', project,
    '--configuration', 'Release',
    '--no-restore',
    '--disable-build-servers',
    '--output', output,
    `/p:PackageVersion=${metadata.version}`,
    '/m:1',
    '/nodeReuse:false',
    '/p:UseSharedCompilation=false'
  ]);
}

run('npm', ['run', 'build', '--prefix', 'sdks/node']);
const stagingRoot = await mkdtemp(join(tmpdir(), 'iroute-node-package-'));
try {
  const packageRoot = join(stagingRoot, 'package');
  await mkdir(packageRoot);
  await mkdir(join(packageRoot, 'dist'));
  await copyFile(join(root, 'sdks/node/dist/index.js'), join(packageRoot, 'dist/index.js'));
  await copyFile(join(root, 'sdks/node/dist/index.d.ts'), join(packageRoot, 'dist/index.d.ts'));
  await copyFile(join(root, 'sdks/node/package.json'), join(packageRoot, 'package.json'));
  await copyFile(join(root, 'sdks/node/README.md'), join(packageRoot, 'README.md'));
  await copyFile(join(root, 'LICENSE'), join(packageRoot, 'LICENSE'));
  await copyFile(join(root, 'NOTICE'), join(packageRoot, 'NOTICE'));
  run('npm', ['pack', packageRoot, '--pack-destination', output]);
} finally {
  await rm(stagingRoot, { recursive: true, force: true });
}

const sourceRef = useIndex ? run('git', ['write-tree']).trim() : 'HEAD';
const sourceName = `iroute-${metadata.version}-source.tar.gz`;
run('git', [
  'archive',
  '--format=tar.gz',
  `--prefix=iroute-${metadata.version}/`,
  `--output=${join(output, sourceName)}`,
  sourceRef
]);

await copyFile(join(root, metadata.releaseNotes), join(output, 'RELEASE_NOTES.md'));

const distributed = (await readdir(output)).sort();
const artifacts = [];
for (const name of distributed) {
  const path = join(output, name);
  const info = await stat(path);
  artifacts.push({ name, bytes: info.size, sha256: await sha256(path) });
}

const manifest = {
  schemaVersion: 1,
  product: 'iRoute',
  version: metadata.version,
  channel: metadata.channel,
  releaseDate: metadata.releaseDate,
  apiVersion: metadata.apiVersion,
  commit: run('git', ['rev-parse', 'HEAD']).trim(),
  sourceRef,
  artifacts
};
await writeFile(
  join(output, 'release-manifest.json'),
  `${JSON.stringify(manifest, null, 2)}\n`,
  'utf8'
);

const checksumFiles = (await readdir(output)).filter(name => name !== 'SHA256SUMS').sort();
const checksumLines = [];
for (const name of checksumFiles) {
  checksumLines.push(`${await sha256(join(output, name))}  ${name}`);
}
await writeFile(join(output, 'SHA256SUMS'), `${checksumLines.join('\n')}\n`, 'utf8');

run('node', ['tools/verify-release.mjs', '--input', output]);
run('node', ['tools/package-install-smoke.mjs', '--input', output]);

console.log(`PASS release ${metadata.version}: ${checksumFiles.length} checksummed artifacts in ${output}`);

async function ensureEmptyDirectory(path) {
  await mkdir(path, { recursive: true });
  const entries = await readdir(path);
  assert.equal(entries.length, 0, `release output must be empty: ${path}`);
}

async function sha256(path) {
  const value = await readFile(path);
  return createHash('sha256').update(value).digest('hex');
}

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0) return undefined;
  assert.ok(process.argv[index + 1], `${name} requires a value`);
  return process.argv[index + 1];
}

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: root,
    encoding: 'utf8',
    env: {
      ...process.env,
      CI: process.env.CI ?? 'true',
      npm_config_cache: process.env.npm_config_cache ?? join(tmpdir(), 'iroute-npm-cache')
    }
  });
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  assert.equal(result.status, 0, `${command} ${args.join(' ')} failed`);
  return result.stdout;
}
