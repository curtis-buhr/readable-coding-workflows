const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const [submission, revisionText, output] = process.argv.slice(2);
if (!submission || !output || !/^[0-3]$/.test(revisionText ?? '')) {
    throw new Error('Usage: node evaluate.cjs <submission> <revision 0..3> <output.json>');
}

const revision = Number(revisionText);
const resultPath = path.resolve(output);
const resultDirectory = path.dirname(resultPath);
fs.mkdirSync(resultDirectory, { recursive: true });
const work = fs.mkdtempSync(path.join(resultDirectory, 'grader-'));
const production = path.join(work, 'submission');
fs.mkdirSync(production);

// Keep the project under evaluator control. A candidate supplies only flat C# source files.
const seed = path.join(__dirname, '../seed');
fs.copyFileSync(path.join(seed, 'Challenge.csproj'), path.join(production, 'Challenge.csproj'));
fs.copyFileSync(path.join(seed, 'NuGet.Config'), path.join(work, 'NuGet.Config'));
for (const entry of fs.readdirSync(submission, { withFileTypes: true })) {
    if (entry.isFile() && /^[a-zA-Z_][a-zA-Z0-9_.-]*\.cs$/.test(entry.name)) {
        fs.copyFileSync(path.join(submission, entry.name), path.join(production, entry.name));
    }
}
fs.copyFileSync(path.join(__dirname, 'Program.cs'), path.join(work, 'Program.cs'));
const symbols = Array.from({ length: revision }, (_, index) => `REV${index + 1}`).join(';');
fs.writeFileSync(path.join(work, 'Grader.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>${symbols}</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <ProjectReference Include="submission/Challenge.csproj" />
  </ItemGroup>
</Project>\n`);

/** Runs a bounded .NET build or executable and retains its output in scratch. */
function invoke(args, timeout) {
    return spawnSync('dotnet', args, {
        cwd: work, encoding: 'utf8', timeout, windowsHide: true, maxBuffer: 1024 * 1024,
        env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' }
    });
}

/** Records a grader result in the schema used by the other fixtures. */
function writeResult(result) {
    fs.writeFileSync(resultPath, JSON.stringify(result, null, 2) + '\n');
}

/** Distinguishes build failures, runtime failures, and timeouts from case results. */
function recordFailure(result, phase) {
    const logPath = path.join(work, phase + '.log');
    fs.writeFileSync(logPath, String(result.stdout ?? '') + String(result.stderr ?? '') + String(result.error ?? ''));
    writeResult({
        status: result.error?.code === 'ETIMEDOUT' ? 'timeout' : (phase === 'build' ? 'build-failed' : 'crash'),
        cases: [],
        phase,
        logPath
    });
}

const build = invoke(['build', 'Grader.csproj', '-c', 'Release', '--verbosity', 'quiet',
    '--disable-build-servers', '-p:RestoreConfigFile=NuGet.Config', '-p:NuGetAudit=false'], 90_000);
if (build.status !== 0) {
    recordFailure(build, 'build');
} else {
    const evaluation = invoke([path.join('bin', 'Release', 'net10.0', 'Grader.dll')], 30_000);
    if (evaluation.status !== 0) {
        recordFailure(evaluation, 'execute');
    } else {
        try {
            const cases = JSON.parse(evaluation.stdout);
            if (!Array.isArray(cases) || cases.length === 0 || cases.some(result =>
                typeof result.id !== 'string' || typeof result.passed !== 'boolean' ||
                !Number.isInteger(result.introduced) || result.introduced < 0 || result.introduced > revision
            ) || new Set(cases.map(result => result.id)).size !== cases.length) {
                throw new Error('Unexpected grader output.');
            }
            writeResult({ status: 'evaluated', cases, passed: cases.filter(result => result.passed).length,
                total: cases.length });
        } catch (error) {
            writeResult({ status: 'crash', cases: [], detail: String(error) });
        }
    }
}
