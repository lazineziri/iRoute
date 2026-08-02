import { spawnSync } from 'node:child_process';
import { statSync } from 'node:fs';

// The README builds Debug and the installation guide builds Release. Run whichever
// configuration is actually present so both documented flows work with --no-build.
function builtConfiguration() {
  const explicit = process.env.IROUTE_BUILD_CONFIGURATION;
  if (explicit) return explicit;

  const built = ['Release', 'Debug']
    .map((configuration) => {
      try {
        return {
          configuration,
          builtAt: statSync(
            `tests/iRoute.UnitTests/bin/${configuration}/net10.0/iRoute.UnitTests.dll`
          ).mtimeMs
        };
      } catch {
        return null;
      }
    })
    .filter(Boolean)
    .sort((a, b) => b.builtAt - a.builtAt);

  return built.length > 0 ? built[0].configuration : 'Debug';
}

const runners = [
  {
    name: '.NET',
    executable: 'dotnet',
    command: 'dotnet',
    args: [
      'run',
      '--project',
      'tests/iRoute.UnitTests',
      '--configuration',
      builtConfiguration(),
      '--no-build',
      '--',
      '-reporter',
      'quiet'
    ]
  },
  {
    name: 'Node.js',
    executable: 'node',
    command: 'npm',
    args: ['test'],
    cwd: 'sdks/node'
  },
  {
    name: 'Python',
    executable: 'python3',
    command: 'python3',
    args: ['-m', 'unittest', 'discover', '-s', 'tests', '-v'],
    cwd: 'sdks/python',
    environment: { PYTHONPATH: 'src' }
  },
  {
    name: 'Java',
    executable: 'javac',
    command: './run-conformance.sh',
    args: [],
    cwd: 'sdks/java'
  },
  {
    name: 'PHP',
    executable: 'php',
    command: 'php',
    args: ['tests/conformance.php'],
    cwd: 'sdks/php'
  },
  {
    name: 'Rust',
    executable: 'cargo',
    command: 'cargo',
    args: ['test'],
    cwd: 'sdks/rust'
  }
];

// Set IROUTE_REQUIRE_SDKS=all (or a comma-separated subset) so CI fails on a missing
// toolchain instead of silently reporting success for SDKs that never ran.
const required = (process.env.IROUTE_REQUIRE_SDKS ?? '')
  .split(',')
  .map((name) => name.trim().toLowerCase())
  .filter(Boolean);
const requiresAll = required.includes('all');
const isRequired = (name) => requiresAll || required.includes(name.toLowerCase());

let failures = 0;
const executed = [];
const skipped = [];

for (const runner of runners) {
  if (!available(runner.executable)) {
    if (isRequired(runner.name)) {
      console.error(`FAIL ${runner.name}: ${runner.executable} is required but not installed`);
      failures++;
    } else {
      console.log(`SKIP ${runner.name}: ${runner.executable} is not installed`);
      skipped.push(runner.name);
    }
    continue;
  }
  console.log(`RUN  ${runner.name}`);
  const result = spawnSync(runner.command, runner.args, {
    cwd: runner.cwd,
    env: { ...process.env, ...runner.environment },
    stdio: 'inherit'
  });
  executed.push(runner.name);
  if (result.status !== 0) failures++;
}

console.log(
  `\nRan ${executed.length}/${runners.length} SDK conformance runners` +
    (skipped.length > 0 ? ` (skipped: ${skipped.join(', ')})` : '')
);

if (failures > 0) {
  console.error(`${failures} SDK conformance runner(s) failed.`);
  process.exitCode = 1;
} else if (executed.length === 0) {
  console.error('No SDK conformance runner executed; nothing was verified.');
  process.exitCode = 1;
} else {
  console.log(`PASS ${executed.join(', ')}`);
}

function available(command) {
  const result = spawnSync(command, ['--version'], { stdio: 'ignore' });
  return result.status === 0;
}
