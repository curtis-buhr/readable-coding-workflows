const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const [submission, revisionText, output] = process.argv.slice(2);
const revision = Number(revisionText);
if (!submission || !output || !/^[0-3]$/.test(revisionText ?? '')) {
    throw new Error('Usage: node evaluate.cjs <submission> <revision 0..3> <output.json>');
}

const outputDirectory = path.dirname(path.resolve(output));
fs.mkdirSync(outputDirectory, { recursive: true });
const work = fs.mkdtempSync(path.join(outputDirectory, 'grader-'));
const projectDirectory = path.join(work, 'submission');
const excluded = new Set(['bin', 'obj', '.git', '.codex', '.agents']);
fs.cpSync(path.resolve(submission), projectDirectory, {
    recursive: true,
    filter: item => !excluded.has(path.basename(item).toLowerCase())
});

fs.copyFileSync(path.join(__dirname, 'Program.cs'), path.join(work, 'Program.cs'));
fs.copyFileSync(path.join(__dirname, '../seed/NuGet.Config'), path.join(work, 'NuGet.Config'));
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
</Project>`);

function invoke(args) {
    return spawnSync('dotnet', args, {
        cwd: work, encoding: 'utf8', timeout: 90000,
        windowsHide: true, maxBuffer: 8 * 1024 * 1024,
        env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' }
    });
}

function writeResult(result) {
    fs.writeFileSync(output, JSON.stringify(result, null, 2) + '\n');
}

function failure(result, phase) {
    const logPath = path.join(work, phase + '.log');
    fs.writeFileSync(logPath, String(result.stdout ?? '') + String(result.stderr ?? '') + String(result.error ?? ''));
    writeResult({
        status: result.error?.code === 'ETIMEDOUT' ? 'timeout' : (phase === 'build' ? 'build-failed' : 'crash'),
        cases: [],
        phase,
        logPath
    });
}

const build = invoke(['build', 'Grader.csproj', '-c', 'Release', '--nologo', '--verbosity', 'quiet', '--disable-build-servers']);
if (build.status !== 0) {
    failure(build, 'build');
} else {
    const evaluation = invoke([path.join('bin', 'Release', 'net10.0', 'Grader.dll')]);
    if (evaluation.status !== 0) {
        failure(evaluation, 'execute');
    } else {
        try {
            const cases = JSON.parse(evaluation.stdout);
            if (!Array.isArray(cases) || !cases.length || cases.some(result =>
                typeof result.id !== 'string' || typeof result.passed !== 'boolean' ||
                !Number.isInteger(result.introduced) || result.introduced < 0 || result.introduced > revision
            ) || new Set(cases.map(result => result.id)).size !== cases.length) {
                throw new Error('Unexpected grader output.');
            }
            writeResult({ status: 'evaluated', cases, passed: cases.filter(result => result.passed).length, total: cases.length });
        } catch (error) {
            writeResult({ status: 'crash', cases: [], detail: String(error), stdout: evaluation.stdout });
        }
    }
}

