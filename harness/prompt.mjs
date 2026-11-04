import { readFileSync } from 'node:fs';

const [family, revision, variant, ...extra] = process.argv.slice(2);
const families = ['cache', 'config', 'import', 'scheduler'];
const variants = ['A', 'B', 'C', 'D'];

if (extra.length || !families.includes(family) || !['1', '2', '3'].includes(revision) || !variants.includes(variant)) {
  console.error('Usage: node harness/prompt.mjs <cache|config|import|scheduler> <1|2|3> <A|B|C|D>');
  process.exitCode = 1;
} else {
  // The task-only arm stores a reference note, which never enters the prompt.
  const task = readFileSync(new URL(`../benchmarks/challenges/${family}/tasks/r${revision}.txt`, import.meta.url), 'utf8');
  const treatment = variant === 'A' ? '' : '\n\n' +
    readFileSync(new URL(`../workflows/${variant}.txt`, import.meta.url), 'utf8').trim();
  const common = 'Implement the following revision in the current workspace. Use the supplied project, existing source, and task requirements. The .benchmark folder is reserved for previous requirements and temporary scratch files. It is omitted from the submission. Edit the implementation files in this workspace and perform the task validation. Finish with a brief summary of the implementation and checks.';
  process.stdout.write(common + '\n\n' + task.trim() + treatment + '\n');
}
