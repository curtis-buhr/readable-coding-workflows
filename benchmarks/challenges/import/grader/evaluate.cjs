const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const [submission, revisionText, output] = process.argv.slice(2);
const revision = Number(revisionText);
if (!submission || !output || !/^[0-3]$/.test(revisionText ?? '')) {
  throw new Error('Usage: node evaluate.cjs <submission> <revision 0..3> <output.json>');
}

function copySource(source, target) {
  fs.mkdirSync(target, { recursive: true });
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    if (['bin', 'obj', '.git', '.codex', '.agents'].includes(entry.name)) continue;
    const from = path.join(source, entry.name);
    const to = path.join(target, entry.name);
    if (entry.isDirectory()) copySource(from, to);
    else if (entry.isFile()) fs.copyFileSync(from, to);
  }
}

const outputPath = path.resolve(output);
fs.mkdirSync(path.dirname(outputPath), { recursive: true });
const workspace = fs.mkdtempSync(path.join(path.dirname(outputPath), 'judge-'));
copySource(path.resolve(submission), path.join(workspace, 'submission'));
for (const name of ['Grade.csproj', 'Program.cs']) {
  fs.copyFileSync(path.join(__dirname, name), path.join(workspace, name));
}
fs.writeFileSync(path.join(workspace, 'NuGet.Config'), '<configuration><packageSources><clear /></packageSources></configuration>');

function execute(args, timeout) {
  return spawnSync('dotnet', args, {
    cwd: workspace,
    timeout,
    encoding: 'utf8',
    maxBuffer: 4 * 1024 * 1024,
    windowsHide: true,
    env: {
      ...process.env,
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      DOTNET_ADD_GLOBAL_TOOLS_TO_PATH: '0',
      DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: 'true',
      DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
      DOTNET_CLI_HOME: path.join(workspace, 'dotnet-home'),
      NUGET_PACKAGES: path.join(workspace, 'packages'),
      NUGET_HTTP_CACHE_PATH: path.join(workspace, 'http-cache'),
      NUGET_PLUGINS_CACHE_PATH: path.join(workspace, 'plugins-cache'),
      NUGET_SCRATCH: path.join(workspace, 'nuget-scratch'),
      MSBUILDDISABLENODEREUSE: '1'
    }
  });
}

function finish(payload) {
  fs.writeFileSync(outputPath, JSON.stringify({ revision, ...payload }, null, 2) + '\n');
}

const build = execute(['build', 'Grade.csproj', '-c', 'Release', '--nologo', '-v', 'quiet', '-p:RestoreConfigFile=' + path.join(workspace, 'NuGet.Config'), '-p:UseSharedCompilation=false', '-p:FixtureRevision=' + revision], 120000);
fs.writeFileSync(path.join(workspace, 'build.log'), (build.stdout ?? '') + (build.stderr ?? ''));
if (build.status !== 0) {
  finish({ status: build.error?.code === 'ETIMEDOUT' ? 'timeout' : 'build-failed', cases: [], detail: ((build.stdout ?? '') + (build.stderr ?? '') + (build.error?.message ?? '')).slice(-6000) });
} else {
  const results = path.join(workspace, 'case-results.json');
  const run = execute([path.join(workspace, 'bin/Release/net10.0/Grade.dll'), results], 45000);
  fs.writeFileSync(path.join(workspace, 'execute.log'), (run.stdout ?? '') + (run.stderr ?? ''));
  if (run.status !== 0 || !fs.existsSync(results)) {
    finish({ status: run.error?.code === 'ETIMEDOUT' ? 'timeout' : 'crash', cases: [], detail: ((run.stdout ?? '') + (run.stderr ?? '') + (run.error?.message ?? '')).slice(-6000) });
  } else {
    finish({ status: 'evaluated', cases: JSON.parse(fs.readFileSync(results, 'utf8')) });
  }
}

