import { readdir, readFile } from 'node:fs/promises';
import { extname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const sourceRoot = fileURLToPath(new URL('../src/', import.meta.url));
const allowedExtensions = new Set(['.html', '.scss', '.ts']);
const mojibake = /Ã|Â|â|�/u;
const failures = [];

async function inspect(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await inspect(path);
    else if (allowedExtensions.has(extname(entry.name))) {
      const content = await readFile(path, 'utf8');
      if (mojibake.test(content)) failures.push(relative(sourceRoot, path));
    }
  }
}

await inspect(sourceRoot);
if (failures.length) {
  console.error(`Character-encoding corruption found in: ${failures.join(', ')}`);
  process.exitCode = 1;
}
