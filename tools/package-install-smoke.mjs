import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const input = resolve(argument('--input') ?? 'artifacts/release');
const manifest = JSON.parse(await readFile(join(input, 'release-manifest.json'), 'utf8'));
const work = await mkdtemp(join(tmpdir(), 'iroute-package-smoke-'));

try {
  const nugetConfig = join(work, 'NuGet.Config');
  await writeFile(nugetConfig, `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="iroute-release" value="${xml(input)}" />
  </packageSources>
</configuration>
`, 'utf8');

  const toolPath = join(work, 'tool');
  run('dotnet', [
    'tool', 'install', 'iRoute.Cli',
    '--tool-path', toolPath,
    '--version', manifest.version,
    '--configfile', nugetConfig,
    '--no-cache'
  ]);
  const cli = run(join(toolPath, process.platform === 'win32' ? 'iroute.exe' : 'iroute'), ['--help']);
  assert.match(cli, /Usage:/);

  const app = join(work, 'dotnet-app');
  await mkdir(app);
  await writeFile(join(app, 'Smoke.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="iRoute.Sdk" Version="${manifest.version}" />
  </ItemGroup>
</Project>
`, 'utf8');
  await writeFile(join(app, 'Program.cs'), `using iRoute.Sdk.DotNet;

using var http = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };
var client = new IRouteClient(http, new IRouteClientOptions("package-smoke", "ci"));
Console.WriteLine(client.GetType().FullName);
`, 'utf8');
  run('dotnet', ['restore', join(app, 'Smoke.csproj'), '--configfile', nugetConfig]);
  run('dotnet', ['run', '--project', join(app, 'Smoke.csproj'), '--no-restore']);

  const npmTarball = (await readdir(input)).find(name => name.endsWith('.tgz'));
  assert.ok(npmTarball, 'release is missing the npm tarball');
  const nodeApp = join(work, 'node-app');
  await mkdir(nodeApp);
  await writeFile(join(nodeApp, 'package.json'), `${JSON.stringify({
    private: true,
    type: 'module',
    dependencies: { '@iroute-dev/sdk': `file:${join(input, npmTarball)}` }
  }, null, 2)}\n`, 'utf8');
  await writeFile(join(nodeApp, 'smoke.mjs'), `import { IRouteClient } from '@iroute-dev/sdk';

const client = new IRouteClient(new URL('http://localhost:8080'), {
  tenantId: 'package-smoke',
  actorId: 'ci'
});
console.log(client.constructor.name);
`, 'utf8');
  run('npm', ['install', '--ignore-scripts', '--no-audit', '--no-fund'], nodeApp);
  run('node', [join(nodeApp, 'smoke.mjs')]);
  console.log(`PASS package install smoke: iRoute ${manifest.version}`);
} finally {
  await rm(work, { recursive: true, force: true });
}

function run(command, args, cwd = process.cwd()) {
  const result = spawnSync(command, args, { cwd, encoding: 'utf8' });
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  assert.equal(result.status, 0, `${command} ${args.join(' ')} failed`);
  return result.stdout;
}

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0) return undefined;
  assert.ok(process.argv[index + 1], `${name} requires a value`);
  return process.argv[index + 1];
}

function xml(value) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}
