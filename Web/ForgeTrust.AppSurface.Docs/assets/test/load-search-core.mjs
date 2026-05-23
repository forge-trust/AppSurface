import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { transform } from 'esbuild';

const testRoot = path.dirname(fileURLToPath(import.meta.url));
const sourcePath = path.resolve(testRoot, '..', 'src', 'search-core.ts');

export async function loadSearchCore() {
  const source = await readFile(sourcePath, 'utf8');
  const result = await transform(source, {
    format: 'esm',
    loader: 'ts',
    sourcemap: false,
    target: 'es2022'
  });

  const url = new URL(`data:text/javascript;base64,${Buffer.from(result.code).toString('base64')}`);
  return import(url.href);
}

export function repoPath(...segments) {
  return path.resolve(testRoot, '..', '..', '..', '..', ...segments);
}

export function fileUrl(filePath) {
  return pathToFileURL(filePath).href;
}
