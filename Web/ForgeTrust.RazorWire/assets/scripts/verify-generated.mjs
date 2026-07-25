import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptRoot, '..', '..', '..', '..');
const trackedOutputPaths = [
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'razorwire.js'),
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'razorwire.islands.js'),
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'behavior-kit.js'),
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'page-navigation.js'),
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'section-copy.js'),
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'form-interactions.js')
];
const copiedThirdPartyOutputPaths = [
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'turbo.es2017-umd.js')
];

function readGeneratedOutput(relativePath) {
  try {
    return readFileSync(path.join(repoRoot, relativePath), 'utf8');
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return null;
    }

    throw error;
  }
}

export function runGeneratedAssetVerification(operations = {}) {
  const readOutput = operations.readOutput ?? readGeneratedOutput;
  const spawnBuild = operations.spawnBuild ?? (() => spawnSync(process.execPath, [path.join(scriptRoot, 'build.mjs')], {
    cwd: repoRoot,
    stdio: 'inherit'
  }));
  const writeError = operations.writeError ?? console.error;
  const allTrackedOutputPaths = [...trackedOutputPaths, ...copiedThirdPartyOutputPaths];
  const beforeBuild = new Map(allTrackedOutputPaths.map(output => [output, readOutput(output)]));
  const build = spawnBuild();

  if (build.error) {
    const errorDetail = build.error.code ?? build.error.message ?? 'unknown error';
    writeError(`RWASSET001 Could not start the RazorWire asset verifier. Problem: the generated runtime freshness check cannot spawn the current Node.js executable at ${process.execPath}. Cause: the executable is missing, inaccessible, or blocked by the environment (${errorDetail}). Fix: rerun from a working Node.js installation, then run \`pnpm --dir Web install --frozen-lockfile\` and \`pnpm --dir Web run assets:build\`. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.`);
    return 1;
  }

  if (build.status !== 0) {
    return build.status ?? 1;
  }

  const staleOutputs = trackedOutputPaths.filter(output => beforeBuild.get(output) !== readOutput(output));
  const staleThirdPartyOutputs = copiedThirdPartyOutputPaths.filter(
    output => beforeBuild.get(output) !== readOutput(output)
  );

  if (staleOutputs.length > 0) {
    writeError('RWASSET003 RazorWire generated assets are stale. Problem: package outputs changed when rebuilt from assets/src. Cause: runtime TypeScript was changed without rebuilding the wwwroot outputs. Fix: run `pnpm --dir Web run assets:razorwire:build` or `pnpm --dir Web run assets:build` and commit the updated files. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.');
    for (const output of staleOutputs) {
      writeError(`- ${output}`);
    }

    return 1;
  }

  if (staleThirdPartyOutputs.length > 0) {
    writeError('RWASSET004 RazorWire copied third-party assets are stale. Problem: committed package outputs differ from the verified bytes in the pinned dependency. Cause: the Turbo output is missing, was edited by hand, or was not rebuilt after dependency custody changed. Fix: run `pnpm --dir Web install --frozen-lockfile`, then `pnpm --dir Web run assets:razorwire:build` and commit the byte-for-byte copied output. Do not edit copied third-party assets by hand. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.');
    for (const output of staleThirdPartyOutputs) {
      writeError(`- ${output}`);
    }

    return 1;
  }

  return 0;
}

if (path.resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  process.exit(runGeneratedAssetVerification());
}
