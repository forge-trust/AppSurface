import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const runtimePath = new URL('../wwwroot/razorwire/razorwire.js', import.meta.url);
const runtimeSourcePath = new URL('../assets/src/razorwire.ts', import.meta.url);
const islandsPath = new URL('../wwwroot/razorwire/razorwire.islands.js', import.meta.url);
const behaviorKitPath = new URL('../wwwroot/razorwire/behavior-kit.js', import.meta.url);
const pageNavigationPath = new URL('../wwwroot/razorwire/page-navigation.js', import.meta.url);
const sectionCopyPath = new URL('../wwwroot/razorwire/section-copy.js', import.meta.url);
const formInteractionsPath = new URL('../wwwroot/razorwire/form-interactions.js', import.meta.url);
const packageRoot = new URL('../', import.meta.url);
const packageRootPath = fileURLToPath(packageRoot);

test('generated runtime outputs keep provenance banners and public package paths', () => {
  const runtime = readFileSync(runtimePath, 'utf8');
  const islands = readFileSync(islandsPath, 'utf8');
  const behaviorKit = readFileSync(behaviorKitPath, 'utf8');
  const pageNavigation = readFileSync(pageNavigationPath, 'utf8');
  const sectionCopy = readFileSync(sectionCopyPath, 'utf8');
  const formInteractions = readFileSync(formInteractionsPath, 'utf8');

  assert.match(runtime, /^\/\/ Generated from assets\/src\/razorwire\.ts\./);
  assert.match(islands, /^\/\/ Generated from assets\/src\/razorwire\.islands\.ts\./);
  assert.match(behaviorKit, /^\/\/ Generated from assets\/src\/behavior-kit\.ts\./);
  assert.match(pageNavigation, /^\/\/ Generated from assets\/src\/page-navigation\.ts\./);
  assert.match(sectionCopy, /^\/\/ Generated from assets\/src\/section-copy\.ts\./);
  assert.match(formInteractions, /^\/\/ Generated from assets\/src\/form-interactions\.ts\./);
  assert.equal(runtime.includes('sourceMappingURL'), false);
  assert.equal(islands.includes('sourceMappingURL'), false);
  assert.equal(behaviorKit.includes('sourceMappingURL'), false);
  assert.equal(pageNavigation.includes('sourceMappingURL'), false);
  assert.equal(sectionCopy.includes('sourceMappingURL'), false);
  assert.equal(formInteractions.includes('sourceMappingURL'), false);
  assert.match(runtime, /RazorWireInitialized/);
  assert.match(runtime, /formFailureManager/);
  assert.match(runtime, /BehaviorKitNotLoaded/);
  assert.match(islands, /RazorWireIslandsInitialized/);
  assert.match(islands, /data-rw-hydrated/);
  assert.match(behaviorKit, /RazorWireBehaviorKitInitialized/);
  assert.match(behaviorKit, /registerLifecycle/);
  assert.match(pageNavigation, /pageNavigationManager/);
  assert.match(pageNavigation, /data-rw-page-nav/);
  assert.match(sectionCopy, /sectionCopyManager/);
  assert.match(sectionCopy, /data-rw-section-copy/);
  assert.match(formInteractions, /formInteractionsManager/);
  assert.match(formInteractions, /data-rw-form-collection/);
});

test('authored form failure product event keeps failure mode separate from response kind', () => {
  const source = readFileSync(runtimeSourcePath, 'utf8');

  assert.match(source, /failure_mode:\s*detail\.handled \? 'handled' : 'unhandled'/);
  assert.match(source, /response_kind:\s*detail\.responseKind \|\| 'unknown'/);
});

test('public contract manifest includes page navigation, section copy, and form interaction hooks', () => {
  const contracts = readFileSync(new URL('assets/contracts/razorwire-public-contracts.js', packageRoot), 'utf8');

  assert.match(contracts, /window\.RazorWire\.pageNavigationManager/);
  assert.match(contracts, /window\.RazorWire\.sectionCopyManager/);
  assert.match(contracts, /window\.RazorWire\.formInteractionsManager/);
  assert.match(contracts, /window\.RazorWire\.behaviors/);
  assert.match(contracts, /RazorWireBehaviorDefinition/);
  assert.match(contracts, /RazorWireLifecycleDefinition/);
  assert.match(contracts, /RazorWireBehaviorDiagnostic/);
  assert.doesNotMatch(contracts, /@manager/);
  assert.match(contracts, /razorwire:page-nav:active-change/);
  assert.match(contracts, /data-rw-page-nav/);
  assert.match(contracts, /data-rw-page-nav-link/);
  assert.match(contracts, /data-rw-page-nav-toggle/);
  assert.match(contracts, /data-rw-page-nav-panel/);
  assert.match(contracts, /data-rw-section-copy/);
  assert.match(contracts, /data-rw-section-copy-target/);
  assert.match(contracts, /data-rw-section-copy-state/);
  assert.match(contracts, /data-rw-section-copy-fallback/);
  assert.match(contracts, /razorwire:form-collection:before-add/);
  assert.match(contracts, /data-rw-form-toggle/);
  assert.match(contracts, /data-rw-form-collection/);
  assert.match(contracts, /data-rw-form-collection-row/);
  assert.match(contracts, /registerLifecycle/);
  assert.match(contracts, /BehaviorConnectFailed/);
  assert.match(contracts, /BehaviorLifecycleEventInvalid/);
});

test('generated verifier emits actionable stale-output diagnostics', () => {
  const verifier = readFileSync(new URL('assets/scripts/verify-generated.mjs', packageRoot), 'utf8');

  assert.match(verifier, /RWASSET003 RazorWire generated assets are stale/);
  assert.match(verifier, /behavior-kit\.js/);
  assert.match(verifier, /page-navigation\.js/);
  assert.match(verifier, /section-copy\.js/);
  assert.match(verifier, /form-interactions\.js/);
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
