import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { copyFile, mkdir, readFile, stat } from 'node:fs/promises';
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
  },
  {
    entry: path.join(assetRoot, 'src', 'behavior-kit.ts'),
    output: path.join(outputRoot, 'behavior-kit.js'),
    label: 'behavior-kit.js',
    banner: 'Generated from assets/src/behavior-kit.ts. Do not edit wwwroot/razorwire/behavior-kit.js by hand.',
    rawBytes: 18_000,
    gzipBytes: 5_500
  },
  {
    entry: path.join(assetRoot, 'src', 'page-navigation.ts'),
    output: path.join(outputRoot, 'page-navigation.js'),
    label: 'page-navigation.js',
    banner: 'Generated from assets/src/page-navigation.ts. Do not edit wwwroot/razorwire/page-navigation.js by hand.',
    // #485 added active-link reveal, resize sync, and ResizeObserver resync to the
    // page-nav contract; keep the budget close to the generated asset plus headroom.
    rawBytes: 12_550,
    gzipBytes: 3_700
  },
  {
    entry: path.join(assetRoot, 'src', 'section-copy.ts'),
    output: path.join(outputRoot, 'section-copy.js'),
    label: 'section-copy.js',
    banner: 'Generated from assets/src/section-copy.ts. Do not edit wwwroot/razorwire/section-copy.js by hand.',
    rawBytes: 12_000,
    gzipBytes: 4_000
  },
  {
    entry: path.join(assetRoot, 'src', 'form-interactions.ts'),
    output: path.join(outputRoot, 'form-interactions.js'),
    label: 'form-interactions.js',
    banner: 'Generated from assets/src/form-interactions.ts. Do not edit wwwroot/razorwire/form-interactions.js by hand.',
    rawBytes: 24_000,
    gzipBytes: 7_000
  }
];

const copiedThirdPartyOutputs = [
  {
    packageManifest: path.join(packageRoot, 'node_modules', '@hotwired', 'turbo', 'package.json'),
    source: path.join(packageRoot, 'node_modules', '@hotwired', 'turbo', 'dist', 'turbo.es2017-umd.js'),
    output: path.join(outputRoot, 'turbo.es2017-umd.js'),
    label: 'turbo.es2017-umd.js',
    component: '@hotwired/turbo',
    version: '8.0.23',
    expectedEntry: 'dist/turbo.es2017-umd.js',
    sha256: 'f9e09e3a3093874fe56d5341ca3594ac959f8b097c9b6171a5b37838da3aec81',
    rawBytes: 220_000,
    gzipBytes: 47_000
  }
];

function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}

function thirdPartyAssetError(asset, problem, cause, fix, details = '') {
  const suffix = details ? ` ${details}` : '';
  return new Error(
    `RWASSET004 ${asset.component} ${asset.version} asset custody failed. Problem: ${problem}. Cause: ${cause}. Fix: ${fix}. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.${suffix}`
  );
}

async function copyThirdPartyOutput(asset, operations = {}) {
  let packageMetadata;
  try {
    packageMetadata = JSON.parse(await readFile(asset.packageManifest, 'utf8'));
  } catch (error) {
    throw thirdPartyAssetError(
      asset,
      `the pinned package manifest ${asset.packageManifest} could not be read`,
      'the dependency is missing, its package metadata is invalid, or the install is incomplete',
      'run `pnpm --dir Web install --frozen-lockfile` and verify the pinned package metadata before rebuilding',
      `Node error: ${error?.code ?? error?.message ?? 'unknown'}.`
    );
  }

  if (packageMetadata.name !== asset.component
      || packageMetadata.version !== asset.version
      || packageMetadata.main !== asset.expectedEntry) {
    throw thirdPartyAssetError(
      asset,
      `the installed package metadata does not declare ${asset.component} ${asset.version} with main entry ${asset.expectedEntry}`,
      'the wrong package or version is installed, or the upstream UMD entry changed',
      'restore the frozen lockfile install; if upstream changed the entry, review and update the explicit path, digest, notice, and tests together',
      `Actual metadata: name=${packageMetadata.name ?? 'missing'}, version=${packageMetadata.version ?? 'missing'}, main=${packageMetadata.main ?? 'missing'}.`
    );
  }

  let sourceBytes;
  try {
    sourceBytes = await readFile(asset.source);
  } catch (error) {
    throw thirdPartyAssetError(
      asset,
      `the pinned package entry ${asset.source} could not be read`,
      'the dependency is missing, the package entry changed, or the install is incomplete',
      'run `pnpm --dir Web install --frozen-lockfile`; if the package entry changed, update the explicit source path and custody evidence in the same reviewed change',
      `Node error: ${error?.code ?? error?.message ?? 'unknown'}.`
    );
  }

  const sourceDigest = sha256(sourceBytes);
  if (sourceDigest !== asset.sha256) {
    throw thirdPartyAssetError(
      asset,
      `the pinned package bytes do not match the approved SHA-256 ${asset.sha256}`,
      'the registry payload, dependency version, or expected custody digest changed',
      'do not publish the asset; verify the upstream release and update the pinned version, digest, notice, and tests together after review',
      `Actual SHA-256: ${sourceDigest}.`
    );
  }

  try {
    await mkdir(path.dirname(asset.output), { recursive: true });
    await copyFile(asset.source, asset.output);
  } catch (error) {
    throw thirdPartyAssetError(
      asset,
      `the verified package bytes could not be copied to ${asset.output}`,
      'the output directory is missing, read-only, or blocked by the environment',
      'restore write access to the RazorWire wwwroot output and rerun `pnpm --dir Web run assets:razorwire:build`',
      `Node error: ${error?.code ?? error?.message ?? 'unknown'}.`
    );
  }

  let copiedBytes;
  try {
    copiedBytes = await (operations.readOutput ?? readFile)(asset.output);
  } catch (error) {
    throw thirdPartyAssetError(
      asset,
      `the copied output ${asset.output} could not be read back for verification`,
      'the output was removed, permissions changed, or the environment blocked verification after the copy',
      'restore access to the RazorWire wwwroot output and rerun `pnpm --dir Web run assets:razorwire:build`',
      `Node error: ${error?.code ?? error?.message ?? 'unknown'}.`
    );
  }

  const copiedDigest = sha256(copiedBytes);
  if (copiedDigest !== asset.sha256) {
    throw thirdPartyAssetError(
      asset,
      'the copied output no longer matches the verified package bytes',
      'the output was modified or corrupted during the copy',
      'remove the corrupted output and rerun `pnpm --dir Web run assets:razorwire:build`',
      `Actual SHA-256: ${copiedDigest}.`
    );
  }
}

async function buildAssets() {
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

  for (const copied of copiedThirdPartyOutputs) {
    await copyThirdPartyOutput(copied);
  }

  for (const budget of [...generatedOutputs, ...copiedThirdPartyOutputs]) {
    const bytes = await readFile(budget.output);
    const rawSize = (await stat(budget.output)).size;
    const gzipSize = gzipSync(bytes).length;

    if (rawSize > budget.rawBytes || gzipSize > budget.gzipBytes) {
      throw new Error(
        `RWASSET002 ${budget.label} exceeds asset budget. Problem: package output is larger than allowed. Cause: the browser payload changed or grew without an intentional budget update. Fix: verify the output provenance, then reduce the payload or update the budget in Web/ForgeTrust.RazorWire/assets/scripts/build.mjs with reviewer context. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md. Size: raw ${rawSize}/${budget.rawBytes}, gzip ${gzipSize}/${budget.gzipBytes}.`
      );
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await buildAssets();
}

export { buildAssets, copiedThirdPartyOutputs, copyThirdPartyOutput, generatedOutputs, outputRoot };
