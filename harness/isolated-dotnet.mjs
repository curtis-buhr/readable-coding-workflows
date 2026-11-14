import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { lstatSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

const immutableImage = /^sha256:[0-9a-f]{64}$/;
const containerIdentifier = /^[0-9a-f]{64}$/;
const containerUser = '1654';
const archiveLimitBytes = 8 * 1024 * 1024;
const dockerControlTimeoutMs = 15_000;
const stagedMarker = 'BENCHMARK_INPUT_READY\n';

// Extract only the tar built by the runner into this container's writable scratch space.
// The tool receives a fresh environment rather than image, Docker client, or runner secrets.
const runTool = [
  'set -eu',
  'umask 077',
  'tar -xf - --no-same-owner --no-same-permissions',
  'command -v "$1" >/dev/null || { printf "Required benchmark tool missing: %s\n" "$1" >&2; exit 127; }',
  `printf '${stagedMarker}' >&2`,
  'exec env -i PATH="$PATH" HOME=/tmp TMPDIR=/tmp DOTNET_CLI_HOME=/tmp ' +
    'DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true ' +
    'DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0 ' +
    'NUGET_PACKAGES=/tmp/nuget/packages MSBUILDDISABLENODEREUSE=1 "$@"',
].join('\n');

/** Runs one Docker CLI command without a shell and bounds captured output and execution time. */
function execDocker(args, { input, timeoutMs = dockerControlTimeoutMs, maxOutputBytes = 32 * 1024, signal } = {}) {
  return new Promise((resolve, reject) => {
    signal?.throwIfAborted();
    const dockerEnvironment = { ...process.env };
    for (const key of Object.keys(dockerEnvironment)) {
      if (/^OPENAI_/i.test(key)) delete dockerEnvironment[key];
    }
    const child = spawn('docker', args, {
      env: dockerEnvironment,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });
    let receivedBytes = 0;
    let timedOut = false;
    let outputLimited = false;
    let aborted = false;
    const stdout = [];
    const stderr = [];

    // Stop the client when the shared output allowance is consumed. The caller removes the container.
    function capture(destination, chunk) {
      const remainingBytes = maxOutputBytes - receivedBytes;
      if (remainingBytes > 0) destination.push(chunk.subarray(0, remainingBytes));
      receivedBytes += chunk.length;
      if (receivedBytes > maxOutputBytes && !outputLimited) {
        outputLimited = true;
        child.kill('SIGKILL');
      }
    }
    child.stdout.on('data', chunk => capture(stdout, chunk));
    child.stderr.on('data', chunk => capture(stderr, chunk));
    child.stdin.on('error', () => {}); // Docker can exit before consuming the archive.
    child.stdin.end(input);
    const timeout = setTimeout(() => {
      timedOut = true;
      child.kill('SIGKILL');
    }, timeoutMs);
    const cancel = () => {
      aborted = true;
      child.kill('SIGKILL');
    };
    signal?.addEventListener('abort', cancel, { once: true });
    if (signal?.aborted) cancel();
    child.once('error', reject);
    child.once('close', exitCode => {
      clearTimeout(timeout);
      signal?.removeEventListener('abort', cancel);
      if (aborted) {
        reject(signal.reason);
        return;
      }
      resolve({
        exitCode,
        stdout: Buffer.concat(stdout).toString('utf8'),
        stderr: Buffer.concat(stderr).toString('utf8'),
        timedOut,
        outputLimited,
      });
    });
  });
}

/** Requires a successful Docker control command and preserves a bounded diagnostic on failure. */
function requireDockerSuccess(command, result) {
  if (result.exitCode !== 0 || result.timedOut || result.outputLimited) {
    const reason = result.timedOut ? 'timed out' : result.outputLimited ? 'exceeded its output limit' :
      `failed with status ${result.exitCode}${result.stderr.trim() ? ` (${result.stderr.trim()})` : ''}`;
    throw new Error(`Docker ${command} ${reason}`);
  }
  return result.stdout.trim();
}

/** Resolves an already-installed trusted Linux image and rejects its implicit volume mounts. */
export async function resolveImageId(imageReference, { docker = execDocker } = {}) {
  if (typeof imageReference !== 'string' || !/^[a-z0-9][a-z0-9_.:/@-]{0,254}$/i.test(imageReference)) {
    throw new Error('Supply a valid local Docker image reference');
  }
  const result = await docker(['image', 'inspect', '--format',
    '[{{json .Id}},{{json .Os}},{{json .Config}}]', imageReference]);
  const [imageId, operatingSystem, config] = JSON.parse(requireDockerSuccess('image inspect', result));
  if (!immutableImage.test(imageId) || operatingSystem !== 'linux' || !config || Object.keys(config.Volumes ?? {}).length > 0) {
    throw new Error('Use a Linux Docker image with an immutable image ID and no declared volumes');
  }
  return imageId;
}

/** Encodes a single regular file header using the portable ustar archive format. */
function tarHeader(relativePath, byteLength) {
  const header = Buffer.alloc(512);
  header.write(relativePath, 0, 100, 'ascii');
  header.write('0000644\0', 100, 'ascii');
  header.write('0000000\0', 108, 'ascii');
  header.write('0000000\0', 116, 'ascii');
  header.write(byteLength.toString(8).padStart(11, '0') + '\0', 124, 'ascii');
  header.write('00000000000\0', 136, 'ascii');
  header.fill(0x20, 148, 156);
  header.write('0', 156, 'ascii');
  header.write('ustar\0', 257, 'ascii');
  header.write('00', 263, 'ascii');
  const checksum = header.reduce((sum, byte) => sum + byte, 0);
  header.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 'ascii');
  return header;
}

/** Packages a staged directory without symlinks, device files, traversal, or host path metadata. */
function makeArchive(filesDirectory) {
  const root = path.resolve(filesDirectory);
  if (!lstatSync(root).isDirectory()) throw new Error('Expected a staging directory');
  const parts = [];
  let archiveBytes = 1024; // tar requires two zero blocks at the end.

  function appendDirectory(directory, prefix) {
    for (const name of readdirSync(directory).sort()) {
      const relativePath = `${prefix}${name}`;
      if (!/^[a-z0-9_.-]+$/i.test(name) || name === '.' || name === '..' || relativePath.length > 100) {
        throw new Error('Staged paths must use portable names of at most 100 characters');
      }
      const absolutePath = path.join(directory, name);
      const info = lstatSync(absolutePath);
      if (info.isDirectory()) {
        appendDirectory(absolutePath, `${relativePath}/`);
        continue;
      }
      if (!info.isFile()) throw new Error(`Unsupported staged file type at ${relativePath}`);

      // Account for tar headers and block padding before reading each file into memory.
      const padding = (512 - (info.size % 512)) % 512;
      archiveBytes += 512 + info.size + padding;
      if (archiveBytes > archiveLimitBytes) throw new Error('Staged input exceeds the 8 MiB archive limit');
      const contents = readFileSync(absolutePath);
      if (contents.length !== info.size) throw new Error(`Staged file changed while reading ${relativePath}`);
      parts.push(tarHeader(relativePath, contents.length), contents, Buffer.alloc(padding));
    }
  }

  appendDirectory(root, '');
  parts.push(Buffer.alloc(1024));
  return Buffer.concat(parts, archiveBytes);
}

/** Runs a trusted Node or .NET invocation in a disposable credential-free container. */
export async function runIsolated({ imageId, filesDirectory, argv, timeoutMs = 120_000,
  maxOutputBytes = 64 * 1024, docker = execDocker, signal }) {
  if (!immutableImage.test(imageId) || !Array.isArray(argv) || !['dotnet', 'node'].includes(argv[0]) ||
      !argv.every(value => typeof value === 'string' && !value.includes('\0'))) {
    throw new Error('An immutable image ID and a fixed dotnet or node invocation are required');
  }
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 600_000 ||
      !Number.isSafeInteger(maxOutputBytes) || maxOutputBytes < 1 || maxOutputBytes > 4 * 1024 * 1024) {
    throw new Error('Invalid container timeout or output limit');
  }
  signal?.throwIfAborted();
  const archive = makeArchive(filesDirectory);
  const name = `workflow-benchmark-${randomUUID()}`;
  let containerId;
  let cleanupRequired = false;
  let primaryError;

  try {
    // All writes go to capped private tmpfs mounts. The runner never binds a host path.
    cleanupRequired = true;
    const created = await docker(['container', 'create', '--name', name, '--pull', 'never', '--interactive',
      '--network', 'none', '--read-only', '--user', `${containerUser}:${containerUser}`,
      '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--pids-limit', '256',
      '--cpus', '2', '--memory', '2g', '--memory-swap', '2g', '--log-driver', 'none', '--no-healthcheck',
      '--tmpfs', `/workspace:rw,nosuid,nodev,size=256m,mode=0700,uid=${containerUser},gid=${containerUser}`,
      '--tmpfs', `/tmp:rw,nosuid,nodev,size=128m,mode=0700,uid=${containerUser},gid=${containerUser}`,
      '--workdir', '/workspace', '--entrypoint', '/bin/sh', imageId, '-c', runTool, 'benchmark-tool', ...argv], { signal });
    const rawId = requireDockerSuccess('container create', created);
    if (!containerIdentifier.test(rawId)) throw new Error('Docker returned an invalid container identifier');
    containerId = rawId;

    const result = await docker(['container', 'start', '--attach', '--interactive', containerId], {
      input: archive, timeoutMs, maxOutputBytes, signal,
    });
    if (result.timedOut || result.outputLimited) return result;

    // Inspect the container state so Docker transport failures do not masquerade as compiler failures.
    const inspected = await docker(['container', 'inspect', '--format', '{{json .State}}', containerId], { signal });
    const state = JSON.parse(requireDockerSuccess('container inspect', inspected));
    if (state.Error || state.Status !== 'exited' || !Number.isInteger(state.ExitCode) ||
        !result.stderr.includes(stagedMarker)) {
      const diagnostic = state.Error || result.stderr.trim();
      throw new Error(`Docker did not start the benchmark tool${diagnostic ? ` (${diagnostic})` : ''}`);
    }
    return { ...result, exitCode: state.ExitCode, stderr: result.stderr.replace(stagedMarker, '') };
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    // A failed create can still leave an exact-name container behind if the daemon outlives the client.
    if (cleanupRequired) {
      const target = containerId ?? name;
      try {
        const removed = await docker(['container', 'rm', '--force', '--volumes', target]);
        if (!(removed.exitCode === 1 && removed.stderr.includes('No such container:'))) {
          requireDockerSuccess('container rm', removed);
        }
      } catch (cleanupError) {
        throw new Error(`Could not clean up benchmark container ${target}`, {
          cause: primaryError ? new AggregateError([primaryError, cleanupError]) : cleanupError,
        });
      }
    }
  }
}
