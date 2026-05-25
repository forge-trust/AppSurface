import assert from 'node:assert/strict';
import { access, readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { test } from 'node:test';
import { repoPath } from './load-search-core.mjs';

const require = createRequire(import.meta.url);

test('MiniSearch browser bundle is resolved from the pinned package distribution path', async () => {
  const entryPoint = require.resolve('minisearch');
  const packageJsonPath = path.resolve(path.dirname(entryPoint), '..', '..', 'package.json');
  const packageJson = JSON.parse(await readFile(packageJsonPath, 'utf8'));
  const bundlePath = path.join(path.dirname(packageJsonPath), 'dist', 'umd', 'index.js');

  assert.equal(packageJson.version, '7.2.0');
  assert.equal(packageJson.license, 'MIT');
  await access(bundlePath);
});

test('third-party notices document pinned MiniSearch provenance', async () => {
  const notices = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'THIRD-PARTY-NOTICES.md'), 'utf8');

  assert.match(notices, /MiniSearch/);
  assert.match(notices, /7\.2\.0/);
  assert.match(notices, /MIT/);
  assert.match(notices, /dist\/umd\/index\.js/);
});

test('generated search client keeps a first-party source banner', async () => {
  const searchClient = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'wwwroot', 'docs', 'search-client.js'), 'utf8');

  assert.match(searchClient, /^\/\/ Generated from assets\/src\/search-client\.ts\./);
});

test('authored search client no longer owns harvest completion navigation', async () => {
  const source = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'assets', 'src', 'search-client.ts'), 'utf8');

  assert.doesNotMatch(source, /window\.location\.reload/);
  assert.doesNotMatch(source, /harvestCompletionNavigationScheduled/);
});

test('generated asset verification compares rebuilt outputs against git', async () => {
  const verifier = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'assets', 'scripts', 'verify-generated.mjs'), 'utf8');

  assert.match(verifier, /spawnSync\('git', \['diff', '--exit-code', '--', \.\.\.trackedOutputPaths\]/);
});
