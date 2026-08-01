import { spawnSync } from 'node:child_process';

const runners = [
  {
    name: '.NET',
    executable: 'dotnet',
    command: 'dotnet',
    args: ['run', '--project', 'tests/iRoute.UnitTests', '--no-build', '--', '-reporter', 'quiet']
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

let failures = 0;
for (const runner of runners) {
  if (!available(runner.executable)) {
    console.log(`SKIP ${runner.name}: ${runner.executable} is not installed`);
    continue;
  }
  console.log(`RUN  ${runner.name}`);
  const result = spawnSync(runner.command, runner.args, {
    cwd: runner.cwd,
    env: { ...process.env, ...runner.environment },
    stdio: 'inherit'
  });
  if (result.status !== 0) failures++;
}

if (failures > 0) {
  console.error(`${failures} SDK conformance runner(s) failed.`);
  process.exitCode = 1;
} else {
  console.log('PASS all locally available SDK conformance runners');
}

function available(command) {
  const result = spawnSync(command, ['--version'], { stdio: 'ignore' });
  return result.status === 0;
}
