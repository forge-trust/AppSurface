import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const runtimePath = new URL('../wwwroot/razorwire/razorwire.js', import.meta.url);
const islandsPath = new URL('../wwwroot/razorwire/razorwire.islands.js', import.meta.url);
const pageNavigationPath = new URL('../wwwroot/razorwire/page-navigation.js', import.meta.url);
const packageRoot = new URL('../', import.meta.url);
const packageRootPath = fileURLToPath(packageRoot);

test('generated runtime outputs keep provenance banners and public package paths', () => {
  const runtime = readFileSync(runtimePath, 'utf8');
  const islands = readFileSync(islandsPath, 'utf8');
  const pageNavigation = readFileSync(pageNavigationPath, 'utf8');

  assert.match(runtime, /^\/\/ Generated from assets\/src\/razorwire\.ts\./);
  assert.match(islands, /^\/\/ Generated from assets\/src\/razorwire\.islands\.ts\./);
  assert.match(pageNavigation, /^\/\/ Generated from assets\/src\/page-navigation\.ts\./);
  assert.equal(runtime.includes('sourceMappingURL'), false);
  assert.equal(islands.includes('sourceMappingURL'), false);
  assert.equal(pageNavigation.includes('sourceMappingURL'), false);
  assert.match(runtime, /RazorWireInitialized/);
  assert.match(runtime, /formFailureManager/);
  assert.match(islands, /RazorWireIslandsInitialized/);
  assert.match(islands, /data-rw-hydrated/);
  assert.match(pageNavigation, /pageNavigationManager/);
  assert.match(pageNavigation, /data-rw-page-nav/);
});

test('public contract manifest includes page navigation hooks', () => {
  const contracts = readFileSync(new URL('assets/contracts/razorwire-public-contracts.js', packageRoot), 'utf8');

  assert.match(contracts, /window\.RazorWire\.pageNavigationManager/);
  assert.match(contracts, /razorwire:page-nav:active-change/);
  assert.match(contracts, /data-rw-page-nav/);
  assert.match(contracts, /data-rw-page-nav-link/);
  assert.match(contracts, /data-rw-page-nav-toggle/);
  assert.match(contracts, /data-rw-page-nav-panel/);
});

test('generated verifier emits actionable stale-output diagnostics', () => {
  const verifier = readFileSync(new URL('assets/scripts/verify-generated.mjs', packageRoot), 'utf8');

  assert.match(verifier, /RWASSET003 RazorWire generated assets are stale/);
  assert.match(verifier, /page-navigation\.js/);
  assert.match(verifier, /Problem:/);
  assert.match(verifier, /Cause:/);
  assert.match(verifier, /Fix: run `pnpm --dir Web run assets:razorwire:build`/);
  assert.match(verifier, /Docs: Web\/ForgeTrust\.RazorWire\/Docs\/runtime-contract-pipeline\.md/);
});

test('generated verifier accepts outputs that are already fresh', () => {
  const result = spawnSync(process.execPath, ['assets/scripts/verify-generated.mjs'], {
    cwd: packageRootPath,
    encoding: 'utf8'
  });

  assert.equal(result.status, 0, result.stderr);
});
