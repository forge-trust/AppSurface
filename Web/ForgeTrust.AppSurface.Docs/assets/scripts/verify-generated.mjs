import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptRoot = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptRoot, '..', '..', '..', '..');
const trackedOutputPaths = [
  path.join('Web', 'ForgeTrust.AppSurface.Docs', 'wwwroot', 'docs', 'search-client.js'),
  path.join('Web', 'ForgeTrust.AppSurface.Docs', 'wwwroot', 'docs', 'minisearch.min.js')
];

const build = spawnSync(process.execPath, [path.join(scriptRoot, 'build.mjs')], {
  cwd: repoRoot,
  stdio: 'inherit'
});

if (build.status !== 0) {
  process.exit(build.status ?? 1);
}

const diff = spawnSync('git', ['diff', '--exit-code', '--', ...trackedOutputPaths], {
  cwd: repoRoot,
  encoding: 'utf8'
});

if (diff.status !== 0) {
  console.error('Generated Docs assets are stale. Run `pnpm --dir Web run assets:build` and commit the updated outputs:');
  for (const output of trackedOutputPaths) {
    console.error(`- ${output}`);
  }

  if (diff.stdout) {
    console.error(diff.stdout);
  }

  if (diff.stderr) {
    console.error(diff.stderr);
  }

  process.exit(diff.status ?? 1);
}
