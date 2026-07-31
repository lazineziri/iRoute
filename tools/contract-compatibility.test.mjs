import assert from 'node:assert/strict';
import { glob, readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const snapshot = JSON.parse(await readFile(
  resolve(repositoryRoot, 'spec/compatibility/v1/public-contract.snapshot.json'),
  'utf8'
));
const openApi = parse(await readFile(resolve(repositoryRoot, 'spec/openapi/iroute.v1.yaml'), 'utf8'));

test('v1 OpenAPI operations remain available with stable operation ids', () => {
  assert.equal(Number.parseInt(openApi.info.version.split('.')[0], 10), snapshot.apiMajor);
  for (const [key, operationId] of Object.entries(snapshot.operations)) {
    const separator = key.indexOf(' ');
    const method = key.slice(0, separator).toLowerCase();
    const path = key.slice(separator + 1);
    assert.equal(openApi.paths?.[path]?.[method]?.operationId, operationId, key);
  }
});

test('v1 OpenAPI object contracts retain fields and required sets', () => {
  for (const [name, baseline] of Object.entries(snapshot.openApiSchemas)) {
    const current = openApi.components?.schemas?.[name];
    assert.ok(current, `OpenAPI schema was removed: ${name}`);
    assert.deepEqual(sorted(current.required ?? []), sorted(baseline.required), `${name} required fields changed`);
    for (const property of baseline.properties) {
      assert.ok(current.properties?.[property], `${name}.${property} was removed`);
    }
  }
});

test('v1 enum, event, and error values are never removed', () => {
  for (const [name, values] of Object.entries(snapshot.enums)) {
    const current = openApi.components?.schemas?.[name]?.enum;
    assert.ok(current, `OpenAPI enum was removed: ${name}`);
    for (const value of values) assert.ok(current.includes(value), `${name} removed ${value}`);
  }
});

test('v1 JSON Schema roots retain fields and required sets', async () => {
  const schemasById = new Map();
  for await (const file of glob('spec/schemas/*.json', { cwd: repositoryRoot })) {
    const schema = JSON.parse(await readFile(resolve(repositoryRoot, file), 'utf8'));
    schemasById.set(schema.$id, schema);
  }

  for (const [id, baseline] of Object.entries(snapshot.jsonSchemas)) {
    const current = schemasById.get(id);
    assert.ok(current, `JSON Schema was removed or changed id: ${id}`);
    assert.deepEqual(sorted(current.required ?? []), sorted(baseline.required), `${id} required fields changed`);
    for (const property of baseline.properties) {
      assert.ok(current.properties?.[property], `${id} removed property ${property}`);
    }
  }
});

function sorted(values) {
  return [...values].sort((left, right) => left.localeCompare(right));
}
