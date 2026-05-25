import { gzipSync } from 'node:zlib';
import { mkdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';

const scriptRoot = path.dirname(fileURLToPath(import.meta.url));
const assetRoot = path.resolve(scriptRoot, '..');
const packageRoot = path.resolve(assetRoot, '..');
const outputRoot = path.join(packageRoot, 'wwwroot', 'razorwire');

const generatedOutputs = [
  {
    entry: path.join(assetRoot, 'src', 'razorwire.ts'),
    output: path.join(outputRoot, 'razorwire.js'),
    label: 'razorwire.js',
    banner: 'Generated from assets/src/razorwire.ts. Do not edit wwwroot/razorwire/razorwire.js by hand.',
    rawBytes: 35_000,
    gzipBytes: 12_000
  },
  {
    entry: path.join(assetRoot, 'src', 'razorwire.islands.ts'),
    output: path.join(outputRoot, 'razorwire.islands.js'),
    label: 'razorwire.islands.js',
    banner: 'Generated from assets/src/razorwire.islands.ts. Do not edit wwwroot/razorwire/razorwire.islands.js by hand.',
    rawBytes: 9_000,
    gzipBytes: 3_500
  }
];

await mkdir(outputRoot, { recursive: true });

for (const generated of generatedOutputs) {
  await esbuild.build({
    entryPoints: [generated.entry],
    outfile: generated.output,
    bundle: true,
    minify: true,
    format: 'iife',
    platform: 'browser',
    target: ['es2022'],
    sourcemap: false,
    legalComments: 'none',
    banner: {
      js: `// ${generated.banner}`
    }
  });
}

for (const budget of generatedOutputs) {
  const bytes = await readFile(budget.output);
  const rawSize = (await stat(budget.output)).size;
  const gzipSize = gzipSync(bytes).length;

  if (rawSize > budget.rawBytes || gzipSize > budget.gzipBytes) {
    throw new Error(
      `RWASSET002 ${budget.label} exceeds asset budget. Problem: generated output is larger than allowed. Cause: the runtime bundle grew without an intentional budget update. Fix: reduce the bundle or update the budget in Web/ForgeTrust.RazorWire/assets/scripts/build.mjs with reviewer context. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md. Size: raw ${rawSize}/${budget.rawBytes}, gzip ${gzipSize}/${budget.gzipBytes}.`
    );
  }
}

export { generatedOutputs, outputRoot };
