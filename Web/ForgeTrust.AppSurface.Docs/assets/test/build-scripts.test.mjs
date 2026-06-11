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

test('authored search client emits product intelligence without raw query payloads', async () => {
  const source = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'assets', 'src', 'search-client.ts'), 'utf8');

  assert.match(source, /productIntelligenceEnabled/);
  assert.match(source, /docs\.search\.submitted/);
  assert.match(source, /docs\.search\.returned_zero_results/);
  assert.match(source, /docs\.search\.result_selected/);
  assert.match(source, /docs\.recovery_link\.selected/);
  assert.match(source, /docs\.search\.filter_changed/);
  assert.match(source, /docs\.search\.friction_feedback_submitted/);
  assert.match(source, /credentials:\s*'omit'/);
  assert.match(source, /keepalive:\s*true/);
  assert.match(source, /docs-search-page-feedback/);
  assert.match(source, /query_length/);
  assert.match(source, /getActiveFilterSignature/);
  assert.match(source, /`\$\{normalized\}:\$\{resultCount\}:\$\{activeFilterCount\}:\$\{filterSignature\}`/);
  assert.doesNotMatch(source, /raw_query/);
  assert.doesNotMatch(source, /query:\s*normalized/);
  assert.doesNotMatch(source, /query:\s*query/);
});

test('authored search client routes sidebar and full-page ordering through shared rank gateway', async () => {
  const source = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'assets', 'src', 'search-client.ts'), 'utf8');

  assert.match(source, /rankSearchResults/);
  assert.match(source, /function runRankedSearch\(query, filters, maxResults = null\)/);
  assert.match(source, /const queryResults = runRankedSearch\(query, createEmptySelectedFilters\(\), topResults\)/);
  assert.match(source, /const refreshed = runRankedSearch\(currentQuery, createEmptySelectedFilters\(\), topResults\)/);
  assert.match(source, /const resultDocs = normalizedQuery\s*\?\s*\(activeFilters\.length > 0\s*\?\s*runRankedSearch\(normalizedQuery, filters\)\s*:\s*baseDocs\)/);
  assert.match(source, /data-rw-product-result-rank="\$\{index \+ 1\}"/);
  assert.match(source, /link\.dataset\.rwProductResultRank = String\(options\.rank \|\| 0\)/);
});

test('generated asset verification compares rebuilt outputs against git', async () => {
  const verifier = await readFile(repoPath('Web', 'ForgeTrust.AppSurface.Docs', 'assets', 'scripts', 'verify-generated.mjs'), 'utf8');

  assert.match(verifier, /spawnSync\('git', \['diff', '--exit-code', '--', \.\.\.trackedOutputPaths\]/);
});
