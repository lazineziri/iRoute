import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildRoutingComparison, renderRoutingComparisonMarkdown } from './evaluation-harness.mjs';

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const jsonPath = resolve(repositoryRoot, 'eval/reports/routing-policy.v1.json');
const markdownPath = resolve(repositoryRoot, 'eval/reports/routing-policy.v1.md');
const { report } = await buildRoutingComparison(repositoryRoot);
const json = `${JSON.stringify(report, null, 2)}\n`;
const markdown = renderRoutingComparisonMarkdown(report);
const command = process.argv[2] ?? '--check';

if (command === '--write') {
  await writeFile(jsonPath, json);
  await writeFile(markdownPath, markdown);
  console.log(`Wrote ${jsonPath}`);
  console.log(`Wrote ${markdownPath}`);
} else if (command === '--print-json') {
  process.stdout.write(json);
} else if (command === '--print-markdown') {
  process.stdout.write(markdown);
} else if (command === '--check') {
  assert.deepEqual(JSON.parse(await readFile(jsonPath, 'utf8')), report, 'checked JSON report is stale; run npm run eval:write after recording fresh results');
  assert.equal(await readFile(markdownPath, 'utf8'), markdown, 'checked Markdown report is stale; run npm run eval:write after recording fresh results');
  assert.equal(report.gate.passed, true, report.gate.failures.join('\n'));
  console.log(`PASS regression gate: ${report.datasetId} v${report.datasetVersion}`);
  console.log(`PASS supported-task coverage: ${report.policies.find(item => item.role === 'candidate').aggregate.evaluatedCases} cases`);
  console.log(`PASS policy comparison: quality ${signed(report.comparison.qualityDelta, 3)}, cost ${signed(report.comparison.costDelta, 6)}, latency ${signed(report.comparison.latencyDeltaMilliseconds, 1)} ms`);
} else {
  throw new Error(`Unknown option '${command}'. Use --check, --write, --print-json, or --print-markdown.`);
}

function signed(value, digits) {
  return `${value >= 0 ? '+' : ''}${value.toFixed(digits)}`;
}
