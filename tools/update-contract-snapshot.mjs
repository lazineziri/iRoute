import { glob, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const snapshotPath = resolve(
  repositoryRoot,
  'spec/compatibility/v1/public-contract.snapshot.json'
);
const snapshot = JSON.parse(await readFile(snapshotPath, 'utf8'));
const openApi = parse(await readFile(resolve(repositoryRoot, 'spec/openapi/iroute.v1.yaml'), 'utf8'));

snapshot.openApiPropertySchemas = Object.fromEntries(
  Object.entries(snapshot.openApiSchemas).map(([name, baseline]) => {
    const current = openApi.components?.schemas?.[name];
    if (!current) throw new Error(`OpenAPI schema was removed: ${name}`);
    return [name, Object.fromEntries(
      baseline.properties.map(property => {
        const schema = current.properties?.[property];
        if (!schema) throw new Error(`OpenAPI property was removed: ${name}.${property}`);
        return [property, schema];
      })
    )];
  })
);

const schemasById = new Map();
for await (const file of glob('spec/schemas/*.json', { cwd: repositoryRoot })) {
  const schema = JSON.parse(await readFile(resolve(repositoryRoot, file), 'utf8'));
  schemasById.set(schema.$id, schema);
}
snapshot.jsonPropertySchemas = Object.fromEntries(
  Object.entries(snapshot.jsonSchemas).map(([id, baseline]) => {
    const current = schemasById.get(id);
    if (!current) throw new Error(`JSON Schema was removed: ${id}`);
    return [id, Object.fromEntries(
      baseline.properties.map(property => {
        const schema = current.properties?.[property];
        if (!schema) throw new Error(`JSON Schema property was removed: ${id}.${property}`);
        return [property, schema];
      })
    )];
  })
);

await writeFile(snapshotPath, `${JSON.stringify(snapshot, null, 2)}\n`);
