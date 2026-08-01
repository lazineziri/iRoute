import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, readdir, stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const input = resolve(argument('--input') ?? 'artifacts/release');
const checksumText = await readFile(join(input, 'SHA256SUMS'), 'utf8');
const expected = new Map();

for (const line of checksumText.trim().split('\n')) {
  const match = /^([a-f0-9]{64})  ([^/]+)$/.exec(line);
  assert.ok(match, `invalid SHA256SUMS line: ${line}`);
  assert.equal(expected.has(match[2]), false, `duplicate checksum entry: ${match[2]}`);
  expected.set(match[2], match[1]);
}

const files = (await readdir(input)).filter(name => name !== 'SHA256SUMS').sort();
assert.deepEqual([...expected.keys()].sort(), files, 'checksum file list does not match release directory');
for (const [name, digest] of expected) {
  assert.equal(await sha256(join(input, name)), digest, `checksum mismatch: ${name}`);
}

const manifest = JSON.parse(await readFile(join(input, 'release-manifest.json'), 'utf8'));
assert.match(manifest.version, /^\d+\.\d+\.\d+-alpha\.\d+$/);
for (const artifact of manifest.artifacts) {
  const path = join(input, artifact.name);
  assert.equal((await stat(path)).size, artifact.bytes, `size mismatch: ${artifact.name}`);
  assert.equal(await sha256(path), artifact.sha256, `manifest digest mismatch: ${artifact.name}`);
}

const manifestNames = manifest.artifacts.map(artifact => artifact.name).sort();
const payloadNames = files.filter(name => name !== 'release-manifest.json').sort();
assert.deepEqual(manifestNames, payloadNames, 'manifest payload list does not match release directory');
console.log(`PASS verified ${manifest.product} ${manifest.version}: ${files.length} files`);

async function sha256(path) {
  return createHash('sha256').update(await readFile(path)).digest('hex');
}

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0) return undefined;
  assert.ok(process.argv[index + 1], `${name} requires a value`);
  return process.argv[index + 1];
}
