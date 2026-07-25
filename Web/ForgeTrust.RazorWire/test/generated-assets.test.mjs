import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { copiedThirdPartyOutputs, copyThirdPartyOutput, generatedOutputs } from '../assets/scripts/build.mjs';
import { runGeneratedAssetVerification } from '../assets/scripts/verify-generated.mjs';

const runtimePath = new URL('../wwwroot/razorwire/razorwire.js', import.meta.url);
const runtimeSourcePath = new URL('../assets/src/razorwire.ts', import.meta.url);
const islandsPath = new URL('../wwwroot/razorwire/razorwire.islands.js', import.meta.url);
const behaviorKitPath = new URL('../wwwroot/razorwire/behavior-kit.js', import.meta.url);
const pageNavigationPath = new URL('../wwwroot/razorwire/page-navigation.js', import.meta.url);
const sectionCopyPath = new URL('../wwwroot/razorwire/section-copy.js', import.meta.url);
const formInteractionsPath = new URL('../wwwroot/razorwire/form-interactions.js', import.meta.url);
const turboPath = new URL('../wwwroot/razorwire/turbo.es2017-umd.js', import.meta.url);
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
  assert.match(contracts, /BehaviorDiagnostic/);
  assert.match(contracts, /BehaviorConnectFailed/);
  assert.match(contracts, /BehaviorLifecycleEventInvalid/);
});

test('core behavior stub de-dupes missing-kit diagnostics', () => {
  const source = readFileSync(runtimeSourcePath, 'utf8');

  assert.match(source, /code:\s*'BehaviorKitNotLoaded'/);
  assert.match(source, /diagnostics\.some\(diagnostic =>/);
});

test('generated asset manifests list each behavior kit output once', () => {
  const build = readFileSync(new URL('assets/scripts/build.mjs', packageRoot), 'utf8');
  const verifier = readFileSync(new URL('assets/scripts/verify-generated.mjs', packageRoot), 'utf8');

  assert.equal(build.match(/label:\s*'behavior-kit\.js'/g)?.length, 1);
  assert.equal(verifier.match(/'behavior-kit\.js'/g)?.length, 1);
});

test('Turbo stays a byte-for-byte copied third-party output outside the esbuild manifest', () => {
  const [turbo] = copiedThirdPartyOutputs;
  const bytes = readFileSync(turboPath);

  assert.equal(turbo.component, '@hotwired/turbo');
  assert.equal(turbo.version, '8.0.23');
  assert.equal(turbo.label, 'turbo.es2017-umd.js');
  assert.equal(generatedOutputs.some(output => output.label === turbo.label), false);
  assert.equal(createHash('sha256').update(bytes).digest('hex'), turbo.sha256);
  assert.equal(bytes.equals(readFileSync(turbo.source)), true);
  assert.equal(bytes.includes(Buffer.from('sourceMappingURL')), false);
});

test('third-party copy reports actionable RWASSET004 diagnostics for a missing package entry', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-missing-'));
  try {
    const asset = {
      ...copiedThirdPartyOutputs[0],
      source: path.join(root, 'missing.js'),
      output: path.join(root, 'output.js')
    };

    await assert.rejects(
      copyThirdPartyOutput(asset),
      error => error.message.includes('RWASSET004')
        && error.message.includes('Problem:')
        && error.message.includes('Cause:')
        && error.message.includes('Fix:')
        && error.message.includes('Docs:')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('third-party copy reports actionable RWASSET004 diagnostics for malformed package metadata', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-manifest-'));
  try {
    const packageManifest = path.join(root, 'package.json');
    const source = path.join(root, 'readable.js');
    writeFileSync(packageManifest, '{ malformed json');
    writeFileSync(source, 'readable package entry');
    const asset = {
      ...copiedThirdPartyOutputs[0],
      packageManifest,
      source,
      output: path.join(root, 'output.js')
    };

    await assert.rejects(
      copyThirdPartyOutput(asset),
      error => error.message.includes('RWASSET004')
        && error.message.includes(`the pinned package manifest ${packageManifest} could not be read`)
        && error.message.includes('package metadata is invalid')
        && error.message.includes('pnpm --dir Web install --frozen-lockfile')
        && error.message.includes('Node error:')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('third-party copy rejects bytes that do not match the approved digest', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-digest-'));
  try {
    const source = path.join(root, 'source.js');
    writeFileSync(source, 'not the approved Turbo runtime');
    const asset = {
      ...copiedThirdPartyOutputs[0],
      source,
      output: path.join(root, 'output.js')
    };

    await assert.rejects(
      copyThirdPartyOutput(asset),
      error => error.message.includes('RWASSET004')
        && error.message.includes('approved SHA-256')
        && error.message.includes('Actual SHA-256')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('third-party copy rejects a package whose declared UMD entry changed', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-entry-'));
  try {
    const packageManifest = path.join(root, 'package.json');
    writeFileSync(packageManifest, JSON.stringify({
      name: '@hotwired/turbo',
      version: '8.0.23',
      main: 'dist/a-different-entry.js'
    }));
    const asset = {
      ...copiedThirdPartyOutputs[0],
      packageManifest,
      output: path.join(root, 'output.js')
    };

    await assert.rejects(
      copyThirdPartyOutput(asset),
      error => error.message.includes('RWASSET004')
        && error.message.includes('main entry dist/turbo.es2017-umd.js')
        && error.message.includes('main=dist/a-different-entry.js')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('third-party copy reports an actionable failure when the verified output cannot be written', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-copy-'));
  try {
    const output = path.join(root, 'existing-directory');
    mkdirSync(output);
    const asset = {
      ...copiedThirdPartyOutputs[0],
      output
    };

    await assert.rejects(
      copyThirdPartyOutput(asset),
      error => error.message.includes('RWASSET004')
        && error.message.includes('could not be copied')
        && error.message.includes('assets:razorwire:build')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('third-party copy wraps output read-back failures in RWASSET004', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-readback-'));
  try {
    const asset = {
      ...copiedThirdPartyOutputs[0],
      output: path.join(root, 'output.js')
    };

    await assert.rejects(
      copyThirdPartyOutput(asset, {
        readOutput: async () => {
          const error = new Error('simulated read-back failure');
          error.code = 'EACCES';
          throw error;
        }
      }),
      error => error.message.includes('RWASSET004')
        && error.message.includes('could not be read back')
        && error.message.includes('Node error: EACCES')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('third-party copy rejects output bytes that change after the copy', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'razorwire-turbo-corrupt-'));
  try {
    const asset = {
      ...copiedThirdPartyOutputs[0],
      output: path.join(root, 'output.js')
    };

    await assert.rejects(
      copyThirdPartyOutput(asset, { readOutput: async () => Buffer.from('corrupted output') }),
      error => error.message.includes('RWASSET004')
        && error.message.includes('modified or corrupted')
        && error.message.includes('Actual SHA-256')
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('generated verifier emits actionable stale-output diagnostics', () => {
  const verifier = readFileSync(new URL('assets/scripts/verify-generated.mjs', packageRoot), 'utf8');

  assert.match(verifier, /RWASSET003 RazorWire generated assets are stale/);
  assert.match(verifier, /behavior-kit\.js/);
  assert.match(verifier, /page-navigation\.js/);
  assert.match(verifier, /section-copy\.js/);
  assert.match(verifier, /form-interactions\.js/);
  assert.match(verifier, /turbo\.es2017-umd\.js/);
  assert.match(verifier, /RWASSET004 RazorWire copied third-party assets are stale/);
  assert.match(verifier, /Problem:/);
  assert.match(verifier, /Cause:/);
  assert.match(
    verifier,
    /Fix: run `pnpm --dir Web install --frozen-lockfile`, then `pnpm --dir Web run assets:razorwire:build`/
  );
  assert.match(verifier, /Docs: Web\/ForgeTrust\.RazorWire\/Docs\/runtime-contract-pipeline\.md/);
});

test('generated verifier rejects a stale copied Turbo output with its exact path', () => {
  const outputContents = new Map();
  const errors = [];
  const seenOutputs = new Set();
  const status = runGeneratedAssetVerification({
    readOutput: output => {
      if (output.endsWith('turbo.es2017-umd.js') && seenOutputs.has(output)) {
        return 'changed Turbo bytes';
      }

      seenOutputs.add(output);
      return outputContents.get(output) ?? 'fresh bytes';
    },
    spawnBuild: () => ({ status: 0 }),
    writeError: message => errors.push(message)
  });

  assert.equal(status, 1);
  assert.match(errors.join('\n'), /RWASSET004 RazorWire copied third-party assets are stale/);
  assert.match(errors.join('\n'), /Web[/\\]ForgeTrust\.RazorWire[/\\]wwwroot[/\\]razorwire[/\\]turbo\.es2017-umd\.js/);
});

test('generated verifier reports non-ENOENT spawn failures', () => {
  const errors = [];
  const status = runGeneratedAssetVerification({
    readOutput: () => 'fresh bytes',
    spawnBuild: () => ({ error: { code: 'EACCES' } }),
    writeError: message => errors.push(message)
  });

  assert.equal(status, 1);
  assert.match(errors.join('\n'), /RWASSET001 Could not start the RazorWire asset verifier/);
  assert.match(errors.join('\n'), /EACCES/);
});

test('generated verifier accepts outputs that are already fresh', () => {
  const result = spawnSync(process.execPath, ['assets/scripts/verify-generated.mjs'], {
    cwd: packageRootPath,
    encoding: 'utf8'
  });

  assert.equal(result.status, 0, result.stderr);
});
