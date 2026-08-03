import assert from 'node:assert/strict';
import { glob, readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { buildRoutingComparison } from './evaluation-harness.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const schemas = [];
for await (const file of glob('spec/schemas/*.json', { cwd: repositoryRoot })) {
  schemas.push(JSON.parse(await readFile(resolve(repositoryRoot, file), 'utf8')));
}

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
for (const schema of schemas) ajv.addSchema(schema);

test('all JSON schemas compile as draft 2020-12', () => {
  assert.ok(schemas.length >= 12, `expected at least 12 schemas, received ${schemas.length}`);
  for (const schema of schemas) {
    assert.equal(schema.$schema, 'https://json-schema.org/draft/2020-12/schema');
    assert.ok(schema.$id);
    assert.doesNotThrow(() => ajv.getSchema(schema.$id), schema.$id);
    assert.ok(ajv.getSchema(schema.$id), `schema did not compile: ${schema.$id}`);
  }
});

test('every published contract example validates', async () => {
  const manifestPath = resolve(repositoryRoot, 'spec/examples/v1/manifest.json');
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  for (const entry of manifest.examples) {
    const validate = ajv.getSchema(entry.schema);
    assert.ok(validate, `unknown schema ${entry.schema}`);
    const examplePath = resolve(dirname(manifestPath), entry.file);
    const example = JSON.parse(await readFile(examplePath, 'utf8'));
    assert.equal(
      validate(example),
      true,
      `${entry.file} failed ${entry.schema}: ${ajv.errorsText(validate.errors)}`
    );
  }
});

test('every evaluation fixture validates', async () => {
  const validate = ajv.getSchema('https://schemas.iroute.dev/v1/evaluation-fixture.schema.json');
  assert.ok(validate);
  for await (const file of glob('eval/fixtures/*.jsonl', { cwd: repositoryRoot })) {
    const lines = (await readFile(resolve(repositoryRoot, file), 'utf8'))
      .split('\n')
      .filter(Boolean);
    for (const [index, line] of lines.entries()) {
      const fixture = JSON.parse(line);
      assert.equal(
        validate(fixture),
        true,
        `${file}:${index + 1} failed: ${ajv.errorsText(validate.errors)}`
      );
    }
  }
});

test('golden datasets and generated regression results validate', async () => {
  const validateDataset = ajv.getSchema('https://schemas.iroute.dev/v1/evaluation-dataset.schema.json');
  const validateResult = ajv.getSchema('https://schemas.iroute.dev/v1/evaluation-result.schema.json');
  const validateReport = ajv.getSchema('https://schemas.iroute.dev/v1/routing-comparison-report.schema.json');
  assert.ok(validateDataset);
  assert.ok(validateResult);
  assert.ok(validateReport);

  for await (const file of glob('eval/datasets/*.json', { cwd: repositoryRoot })) {
    const dataset = JSON.parse(await readFile(resolve(repositoryRoot, file), 'utf8'));
    assert.equal(
      validateDataset(dataset),
      true,
      `${file} failed: ${ajv.errorsText(validateDataset.errors)}`
    );
  }

  const { report, results } = await buildRoutingComparison(repositoryRoot);
  assert.equal(validateReport(report), true, ajv.errorsText(validateReport.errors));
  for (const result of results) {
    assert.equal(validateResult(result), true, `${result.caseId}/${result.policyId}: ${ajv.errorsText(validateResult.errors)}`);
  }
});

test('error-code enums stay in sync across OpenAPI, JSON Schema, and the taxonomy', async () => {
  const openapi = parse(
    await readFile(resolve(repositoryRoot, 'spec/openapi/iroute.v1.yaml'), 'utf8')
  );
  const openapiCodes = openapi.components.schemas.ErrorCode.enum;
  const problem = schemas.find((schema) => schema.$id.endsWith('/problem.schema.json'));
  const taxonomy = await readFile(
    resolve(repositoryRoot, 'spec/errors/error-taxonomy.v1.md'),
    'utf8'
  );

  assert.deepEqual(
    problem.properties.code.enum,
    openapiCodes,
    'problem.schema.json must list exactly the OpenAPI ErrorCode values, in the same order'
  );

  for (const code of openapiCodes) {
    assert.ok(
      taxonomy.includes(code),
      `error-taxonomy.v1.md does not document the error code ${code}`
    );
  }
});
