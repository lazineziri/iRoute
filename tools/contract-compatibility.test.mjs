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
      assertNotNarrowed(
        snapshot.openApiPropertySchemas?.[name]?.[property],
        current.properties[property],
        `${name}.${property}`
      );
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
      assertNotNarrowed(
        snapshot.jsonPropertySchemas?.[id]?.[property],
        current.properties[property],
        `${id}#${property}`
      );
    }
  }
});

function assertNotNarrowed(baseline, current, path) {
  assert.ok(baseline, `Missing property-shape baseline for ${path}; run npm run contracts:snapshot`);
  assert.ok(current && current !== false, `${path} no longer accepts values`);
  if (baseline === true) {
    assert.equal(current, true, `${path} added constraints to an unconstrained property`);
    return;
  }

  if (baseline.$ref !== undefined) {
    assert.equal(current.$ref, baseline.$ref, `${path} changed referenced contract`);
  }
  assertTypeSuperset(baseline, current, path);
  assertValueSuperset(baseline.enum, current.enum, `${path} enum`);
  if (baseline.enum === undefined) {
    assert.equal(current.enum, undefined, `${path} narrowed a previously open value to an enum`);
  }
  if (baseline.const !== undefined) {
    assert.deepEqual(current.const, baseline.const, `${path} changed const`);
  } else {
    assert.equal(current.const, undefined, `${path} added a const restriction`);
  }

  assertLowerBound(baseline, current, path, 'minimum');
  assertLowerBound(baseline, current, path, 'exclusiveMinimum');
  assertLowerBound(baseline, current, path, 'minLength');
  assertLowerBound(baseline, current, path, 'minItems');
  assertUpperBound(baseline, current, path, 'maximum');
  assertUpperBound(baseline, current, path, 'exclusiveMaximum');
  assertUpperBound(baseline, current, path, 'maxLength');
  assertUpperBound(baseline, current, path, 'maxItems');

  for (const keyword of ['pattern', 'format']) {
    if (baseline[keyword] === undefined) {
      assert.equal(current[keyword], undefined, `${path} added ${keyword}`);
    } else {
      assert.equal(current[keyword], baseline[keyword], `${path} changed ${keyword}`);
    }
  }

  for (const keyword of ['anyOf', 'oneOf', 'allOf', 'not']) {
    if (baseline[keyword] !== undefined) {
      assert.deepEqual(current[keyword], baseline[keyword], `${path} changed ${keyword}`);
    } else {
      assert.equal(current[keyword], undefined, `${path} added ${keyword} constraints`);
    }
  }

  if (baseline.items !== undefined) {
    assertNotNarrowed(baseline.items, current.items, `${path} items`);
  } else {
    assert.equal(current.items, undefined, `${path} added item constraints`);
  }

  if (baseline.properties) {
    const baselineRequired = new Set(baseline.required ?? []);
    const currentRequired = new Set(current.required ?? []);
    for (const required of currentRequired) {
      assert.ok(baselineRequired.has(required), `${path} made nested property '${required}' required`);
    }
    for (const [name, schema] of Object.entries(baseline.properties)) {
      assert.ok(current.properties?.[name], `${path}.${name} was removed`);
      assertNotNarrowed(schema, current.properties[name], `${path}.${name}`);
    }
  }

  if (baseline.additionalProperties !== false && current.additionalProperties === false) {
    assert.fail(`${path} stopped allowing additional properties`);
  }
}

function assertTypeSuperset(baseline, current, path) {
  const baselineTypes = acceptedTypes(baseline);
  const currentTypes = acceptedTypes(current);
  if (baselineTypes === null) {
    assert.equal(currentTypes, null, `${path} added a type restriction`);
    return;
  }
  if (currentTypes === null) return;
  for (const type of baselineTypes) {
    assert.ok(currentTypes.has(type), `${path} stopped accepting type '${type}'`);
  }
}

function acceptedTypes(schema) {
  if (!schema || schema === true || schema.type === undefined) return null;
  const types = new Set(Array.isArray(schema.type) ? schema.type : [schema.type]);
  if (schema.nullable === true) types.add('null');
  return types;
}

function assertValueSuperset(baseline, current, path) {
  if (baseline === undefined) return;
  assert.ok(Array.isArray(current), `${path} was removed`);
  for (const value of baseline) {
    assert.ok(current.some(candidate => Object.is(candidate, value)), `${path} removed ${JSON.stringify(value)}`);
  }
}

function assertLowerBound(baseline, current, path, keyword) {
  if (baseline[keyword] === undefined) {
    assert.equal(current[keyword], undefined, `${path} added ${keyword}`);
  } else if (current[keyword] !== undefined) {
    assert.ok(current[keyword] <= baseline[keyword], `${path} raised ${keyword}`);
  }
}

function assertUpperBound(baseline, current, path, keyword) {
  if (baseline[keyword] === undefined) {
    assert.equal(current[keyword], undefined, `${path} added ${keyword}`);
  } else if (current[keyword] !== undefined) {
    assert.ok(current[keyword] >= baseline[keyword], `${path} lowered ${keyword}`);
  }
}

function sorted(values) {
  return [...values].sort((left, right) => left.localeCompare(right));
}
