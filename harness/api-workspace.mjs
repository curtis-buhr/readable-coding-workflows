import { createHash } from 'node:crypto';
import { readdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const maximumFileBytes = 24_000;
const maximumSourceBytes = 64_000;
const maximumSourceFiles = 12;
const csharpFile = /^[A-Za-z][A-Za-z0-9_]*\.cs$/;
const fixedFiles = new Set(['Challenge.csproj', 'NuGet.Config']);

function tool(name, description, properties = {}) {
  return { type: 'function', name, description, strict: true, parameters: {
    type: 'object', properties, required: Object.keys(properties), additionalProperties: false,
  } };
}

const string = { type: 'string' };
export const workspaceTools = [
  tool('list_files', 'List the project files.'),
  tool('read_file', 'Read up to 1,000 characters from a project file. Start at offset 0 and follow nextOffset until null.',
    { file: string, offset: { type: 'integer' } }),
  tool('replace_text', 'Replace exactly one matching span in a C# file. Match line endings as returned by read_file.',
    { file: string, old_text: string, new_text: string }),
  tool('create_file', 'Create a new C# source file at the project root.', { file: string, content: string }),
  tool('build_project', 'Build the project with the pinned .NET SDK and return compiler diagnostics.'),
];

export const sha256 = data => createHash('sha256').update(data).digest('hex');

/** Load the fixture's flat project, rejecting unexpected files rather than leaking them to an agent. */
export async function readProject(directory) {
  const files = new Map();
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && ['bin', 'obj'].includes(entry.name)) continue;
    if (!entry.isFile() || (!fixedFiles.has(entry.name) && !csharpFile.test(entry.name))) {
      throw new Error(`Unexpected project entry: ${entry.name}`);
    }
    files.set(entry.name, (await readFile(join(directory, entry.name), 'utf8')).replaceAll('\r\n', '\n'));
  }
  if (![...fixedFiles].every(file => files.has(file))) throw new Error('Missing fixed project files');
  checkSize(files);
  return files;
}

function checkSize(files) {
  const entries = [...files].filter(([file]) => csharpFile.test(file));
  const sizes = entries.map(([, contents]) => Buffer.byteLength(contents));
  if (entries.length === 0 || entries.length > maximumSourceFiles || sizes.some(bytes => bytes > maximumFileBytes) ||
      sizes.reduce((total, bytes) => total + bytes, 0) > maximumSourceBytes) {
    throw new Error('Project exceeds the pilot source size limits');
  }
}

/** Materialize a snapshot for a build, grader, or later revision. */
export async function writeProject(directory, files) {
  for (const [file, content] of files) await writeFile(join(directory, file), content, { flag: 'wx' });
}

export function projectHashes(files) {
  return Object.fromEntries([...files].sort(([a], [b]) => a.localeCompare(b)).map(([file, content]) => [file, sha256(content)]));
}

/** Keep model edits in memory and send a snapshot to the supplied isolated builder on demand. */
export function createWorkspace(source, build) {
  let files = new Map(source);
  let builtSnapshot;

  async function executeTool(name, args, { signal } = {}) {
    const file = args.file;
    switch (name) {
      case 'list_files': return [...files.keys()].sort();
      case 'read_file': {
        const content = files.get(file);
        const { offset } = args;
        if (content === undefined) return { error: 'Unknown file' };
        if (!Number.isSafeInteger(offset) || offset < 0 || offset > content.length) return { error: 'Invalid offset' };
        const end = Math.min(offset + 1_000, content.length);
        return { file, content: content.slice(offset, end), nextOffset: end < content.length ? end : null };
      }
      case 'build_project': {
        const result = await build(new Map(files), signal);
        if (result.exitCode === 0 && !result.timedOut && !result.outputLimited) builtSnapshot = projectHashes(files);
        return result;
      }
      case 'create_file':
      case 'replace_text': break;
      default: return { error: 'Unknown tool' };
    }

    if (typeof file !== 'string' || !csharpFile.test(file) || file.length > 100) {
      return { error: 'Use a simple root-level .cs filename' };
    }
    let updated;
    if (name === 'create_file') {
      if ([...files.keys()].some(existing => existing.toLowerCase() === file.toLowerCase())) {
        return { error: 'File already exists' };
      }
      if (typeof args.content !== 'string') return { error: 'Expected source text' };
      updated = args.content;
    } else {
      const current = files.get(file);
      if (current === undefined) return { error: 'Unknown source file' };
      const { old_text: oldText, new_text: newText } = args;
      if (typeof oldText !== 'string' || !oldText || typeof newText !== 'string') return { error: 'Expected nonempty old text and new text' };
      const start = current.indexOf(oldText);
      if (start < 0 || current.indexOf(oldText, start + 1) >= 0) return { error: 'Old text must match exactly once' };
      updated = current.slice(0, start) + newText + current.slice(start + oldText.length);
    }

    const next = new Map(files).set(file, updated.replaceAll('\r\n', '\n'));
    try { checkSize(next); } catch (error) { return { error: error.message }; }
    files = next;
    return { file, sha256: sha256(files.get(file)) };
  }

  return {
    executeTool,
    snapshot: () => new Map(files),
    builtCurrent: () => JSON.stringify(builtSnapshot) === JSON.stringify(projectHashes(files)),
  };
}
