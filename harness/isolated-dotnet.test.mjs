import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { resolveImageId, runIsolated } from './isolated-dotnet.mjs';

const imageId = `sha256:${'a'.repeat(64)}`;
const containerId = 'b'.repeat(64);
const successful = (stdout = '', stderr = '') => ({
  exitCode: 0, stdout, stderr, timedOut: false, outputLimited: false,
});

/** Creates the small candidate/grade staging shape without using a Docker daemon. */
function fixture(context) {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'workflow-sandbox-test-'));
  context.after(() => rmSync(directory, { recursive: true, force: true }));
  mkdirSync(path.join(directory, 'submission'));
  writeFileSync(path.join(directory, 'submission', 'One.cs'), 'class One {}\n');
  writeFileSync(path.join(directory, 'grade.cjs'), 'console.log("fixture");\n');
  return directory;
}

/** Captures the Docker lifecycle and provides targeted failure injection. */
function fakeDocker(overrides = {}) {
  const calls = [];
  return {
    calls,
    docker: async (args, options = {}) => {
      calls.push({ args, options });
      const [group, command] = args;
      if (overrides[command]) return overrides[command](args, options);
      if (group === 'image' && command === 'inspect') return successful(JSON.stringify([imageId, 'linux', null]));
      if (command === 'create') return successful(containerId + '\n');
      if (command === 'start') return successful('build report\n', 'BENCHMARK_INPUT_READY\ncompiler output\n');
      if (command === 'inspect') return successful(JSON.stringify({ Status: 'exited', ExitCode: 0, Error: '' }));
      if (command === 'rm') return successful(containerId + '\n');
      throw new Error(`Unexpected Docker command ${args.join(' ')}`);
    },
  };
}

/** Reads the generated archive to verify source files and tar header integrity. */
function archiveFiles(archive) {
  const entries = {};
  let cursor = 0;
  while (archive[cursor] !== 0) {
    const header = archive.subarray(cursor, cursor + 512);
    const field = (start, end) => header.subarray(start, end).toString('ascii').split('\0')[0];
    const relativePath = field(0, 100);
    const size = parseInt(field(124, 136), 8);
    const checksum = parseInt(field(148, 156), 8);
    const calculated = header.reduce((total, byte, index) => total + (index >= 148 && index < 156 ? 0x20 : byte), 0);
    assert.equal(checksum, calculated);
    assert.equal(field(156, 157), '0');
    assert.equal(field(257, 263), 'ustar');
    entries[relativePath] = archive.subarray(cursor + 512, cursor + 512 + size).toString('utf8');
    cursor += 512 + 512 * Math.ceil(size / 512);
  }
  assert.deepEqual(archive.subarray(cursor), Buffer.alloc(1024));
  return entries;
}

test('resolves an installed image and rejects an implicit image volume', async () => {
  const valid = fakeDocker();
  assert.equal(await resolveImageId('local/benchmark:10', valid), imageId);
  assert.deepEqual(valid.calls[0].args.slice(0, 3), ['image', 'inspect', '--format']);
  const withVolume = fakeDocker({ inspect: () => successful(JSON.stringify([imageId, 'linux', { '/data': {} }])) });
  await assert.rejects(resolveImageId('local/benchmark:10', withVolume), /no declared volumes/);
  await assert.rejects(resolveImageId('--help', valid), /valid local Docker image/);
});

test('streams only staged files into a constrained container and removes the exact ID', async context => {
  const filesDirectory = fixture(context);
  const { docker, calls } = fakeDocker();
  const result = await runIsolated({ imageId, filesDirectory, argv: ['node', 'grade.cjs'], docker });
  assert.deepEqual(result, successful('build report\n', 'compiler output\n'));
  assert.deepEqual(calls.map(call => call.args[1]), ['create', 'start', 'inspect', 'rm']);

  // Check the effective Docker request and the in-memory payload, including nested grader files.
  const [create, start, , remove] = calls;
  assert.deepEqual(create.args.slice(-2), ['node', 'grade.cjs']);
  for (const [flag, value] of [['--network', 'none'], ['--pull', 'never'], ['--user', '1654:1654'],
    ['--cap-drop', 'ALL'], ['--security-opt', 'no-new-privileges'], ['--log-driver', 'none']]) {
    assert.equal(create.args[create.args.indexOf(flag) + 1], value);
  }
  for (const flag of ['--mount', '--volume', '--privileged', '--env-file']) assert.ok(!create.args.includes(flag));
  assert.ok(create.args.includes('--read-only'));
  assert.ok(create.args.some(arg => arg.includes('exec env -i')));
  assert.deepEqual(archiveFiles(start.options.input), {
    'grade.cjs': 'console.log("fixture");\n', 'submission/One.cs': 'class One {}\n',
  });
  assert.equal(remove.args.at(-1), containerId);
});

test('returns the exit status from the container even if the attached client status differs', async context => {
  const filesDirectory = fixture(context);
  const { docker } = fakeDocker({
    inspect: () => successful(JSON.stringify({ Status: 'exited', ExitCode: 7, Error: '' })),
  });
  const result = await runIsolated({ imageId, filesDirectory, argv: ['dotnet', 'build', 'Challenge.csproj'], docker });
  assert.equal(result.exitCode, 7);
});

test('removes the container after either a timeout or an output limit', async context => {
  const filesDirectory = fixture(context);
  for (const limit of ['timedOut', 'outputLimited']) {
    const { docker, calls } = fakeDocker({ start: () => ({ ...successful(), [limit]: true, exitCode: null }) });
    const result = await runIsolated({ imageId, filesDirectory, argv: ['dotnet', '--version'], docker });
    assert.equal(result[limit], true);
    assert.deepEqual(calls.map(call => call.args[1]), ['create', 'start', 'rm']);
    assert.equal(calls.at(-1).args.at(-1), containerId);
  }
});

test('keeps cleanup independent from cancellation of the run', async context => {
  const filesDirectory = fixture(context);
  const controller = new AbortController();
  const { docker, calls } = fakeDocker({ start: (_, { signal }) => {
    assert.equal(signal, controller.signal);
    controller.abort(new Error('Revision deadline'));
    signal.throwIfAborted();
  } });
  await assert.rejects(runIsolated({ imageId, filesDirectory, argv: ['dotnet', '--version'],
    signal: controller.signal, docker }), /Revision deadline/);
  assert.equal(calls.at(-1).args.at(-1), containerId);
  assert.equal(calls.at(-1).options.signal, undefined);
});

test('removes by generated name after uncertain create and reports a staging failure separately', async context => {
  const filesDirectory = fixture(context);
  const uncertain = fakeDocker({ create: () => ({ ...successful(), timedOut: true, exitCode: null }) });
  await assert.rejects(runIsolated({ imageId, filesDirectory, argv: ['dotnet', '--version'], docker: uncertain.docker }),
    /create timed out/);
  const create = uncertain.calls[0].args;
  assert.equal(uncertain.calls.at(-1).args.at(-1), create[create.indexOf('--name') + 1]);

  const stagingFailure = fakeDocker({ start: () => successful('', 'tar: cannot extract\n') });
  await assert.rejects(runIsolated({ imageId, filesDirectory, argv: ['node', '--version'], docker: stagingFailure.docker }),
    /did not start the benchmark tool/);
  assert.equal(stagingFailure.calls.at(-1).args.at(-1), containerId);
});

test('rejects symlinks before calling Docker', async context => {
  const filesDirectory = fixture(context);
  const { docker, calls } = fakeDocker();
  const invocation = { imageId, filesDirectory, argv: ['dotnet', '--version'], docker };
  const linked = path.join(filesDirectory, 'linked');
  try {
    symlinkSync(path.join(filesDirectory, 'submission'), linked, process.platform === 'win32' ? 'junction' : 'dir');
  } catch (error) {
    if (error.code !== 'EPERM' && error.code !== 'EACCES') throw error;
    context.skip('This host disallows creating links');
    return;
  }
  await assert.rejects(runIsolated(invocation), /Unsupported staged file type/);
  assert.equal(calls.length, 0);
});

test('rejects oversized staging inputs before calling Docker', async context => {
  const filesDirectory = fixture(context);
  const { docker, calls } = fakeDocker();
  const invocation = { imageId, filesDirectory, argv: ['dotnet', '--version'], docker };
  writeFileSync(path.join(filesDirectory, 'large.cs'), Buffer.alloc(8 * 1024 * 1024));
  await assert.rejects(runIsolated(invocation), /archive limit/);
  assert.equal(calls.length, 0);
});

const smokeImage = process.env.WORKFLOW_DOCKER_SMOKE_IMAGE;
test('builds and runs .NET in a restricted container with an explicitly selected local image', {
  skip: smokeImage ? false : 'Set WORKFLOW_DOCKER_SMOKE_IMAGE to run the Docker integration check',
}, async context => {
  const filesDirectory = fixture(context);
  writeFileSync(path.join(filesDirectory, 'Challenge.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
  </Project>`);
  writeFileSync(path.join(filesDirectory, 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>');
  writeFileSync(path.join(filesDirectory, 'Program.cs'), 'System.Console.WriteLine("ISOLATED_DOTNET_OK");');
  writeFileSync(path.join(filesDirectory, 'probe.cjs'), `
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
assert.equal(process.getuid(), 1654);
assert.deepEqual(fs.readdirSync('/sys/class/net'), ['lo']);
assert.equal(process.env.OPENAI_API_KEY, undefined);
assert.throws(() => fs.writeFileSync('/isolation-probe', 'forbidden'), { code: 'EROFS' });
execFileSync('dotnet', ['build', 'Challenge.csproj', '--configfile', 'NuGet.Config'], { stdio: 'inherit' });
execFileSync('dotnet', ['bin/Debug/net10.0/Challenge.dll'], { stdio: 'inherit' });
`);
  const pinnedImage = await resolveImageId(smokeImage);
  const result = await runIsolated({ imageId: pinnedImage, filesDirectory, argv: ['node', 'probe.cjs'] });
  assert.equal(result.exitCode, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /ISOLATED_DOTNET_OK/);
});
