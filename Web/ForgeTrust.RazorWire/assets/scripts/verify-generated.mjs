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
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'form-interactions.js'),
  path.join('Web', 'ForgeTrust.RazorWire', 'wwwroot', 'razorwire', 'behavior-kit.js')
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

const beforeBuild = new Map(trackedOutputPaths.map(output => [output, readGeneratedOutput(output)]));

const build = spawnSync(process.execPath, [path.join(scriptRoot, 'build.mjs')], {
  cwd: repoRoot,
  stdio: 'inherit'
});

if (build.error?.code === 'ENOENT') {
  console.error(`RWASSET001 Could not start the RazorWire asset verifier. Problem: the generated runtime freshness check cannot spawn the current Node.js executable at ${process.execPath}. Cause: the executable is missing, inaccessible, or blocked by the environment. Fix: rerun from a working Node.js installation, then run \`pnpm --dir Web install --frozen-lockfile\` and \`pnpm --dir Web run assets:build\`. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.`);
  process.exit(1);
}

if (build.status !== 0) {
  process.exit(build.status ?? 1);
}

const staleOutputs = trackedOutputPaths.filter(output => beforeBuild.get(output) !== readGeneratedOutput(output));

if (staleOutputs.length > 0) {
  console.error('RWASSET003 RazorWire generated assets are stale. Problem: package outputs changed when rebuilt from assets/src. Cause: runtime TypeScript was changed without rebuilding the wwwroot outputs. Fix: run `pnpm --dir Web run assets:razorwire:build` or `pnpm --dir Web run assets:build` and commit the updated files. Docs: Web/ForgeTrust.RazorWire/Docs/runtime-contract-pipeline.md.');
  for (const output of staleOutputs) {
    console.error(`- ${output}`);
  }

  process.exit(1);
}
