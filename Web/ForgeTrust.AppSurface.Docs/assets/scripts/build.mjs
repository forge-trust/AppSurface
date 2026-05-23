import { createRequire } from 'node:module';
import { gzipSync } from 'node:zlib';
import { mkdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const require = createRequire(import.meta.url);
const assetRoot = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(assetRoot, '..');
const docsProjectRoot = path.resolve(packageRoot, '..');
const outputRoot = path.join(docsProjectRoot, 'wwwroot', 'docs');

const generatedBanner = 'Generated from assets/src/search-client.ts. Do not edit wwwroot/docs/search-client.js by hand.';
const searchClientOutput = path.join(outputRoot, 'search-client.js');
const miniSearchOutput = path.join(outputRoot, 'minisearch.min.js');
const miniSearchEntryPoint = require.resolve('minisearch');
const miniSearchPackageRoot = path.resolve(path.dirname(miniSearchEntryPoint), '..', '..');
const miniSearchSource = path.join(miniSearchPackageRoot, 'dist', 'umd', 'index.js');

const sizeBudgets = [
  { file: searchClientOutput, label: 'search-client.js', rawBytes: 120_000, gzipBytes: 45_000 },
  { file: miniSearchOutput, label: 'minisearch.min.js', rawBytes: 180_000, gzipBytes: 60_000 }
];

await mkdir(outputRoot, { recursive: true });

await esbuild.build({
  entryPoints: [path.join(packageRoot, 'src', 'search-client.ts')],
  outfile: searchClientOutput,
  bundle: true,
  minify: true,
  format: 'iife',
  platform: 'browser',
  target: ['es2022'],
  sourcemap: false,
  banner: {
    js: `// ${generatedBanner}`
  }
});

await esbuild.build({
  entryPoints: [miniSearchSource],
  outfile: miniSearchOutput,
  bundle: false,
  minify: true,
  format: 'iife',
  platform: 'browser',
  target: ['es2022'],
  sourcemap: false,
  legalComments: 'inline'
});

for (const budget of sizeBudgets) {
  const bytes = await readFile(budget.file);
  const rawSize = (await stat(budget.file)).size;
  const gzipSize = gzipSync(bytes).length;

  if (rawSize > budget.rawBytes || gzipSize > budget.gzipBytes) {
    throw new Error(
      `${budget.label} exceeds asset budget: raw ${rawSize}/${budget.rawBytes}, gzip ${gzipSize}/${budget.gzipBytes}`
    );
  }
}

export { generatedBanner, miniSearchSource, searchClientOutput, miniSearchOutput, sizeBudgets };
