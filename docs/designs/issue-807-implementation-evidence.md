# Issue #807 implementation evidence

This document records the migration, rollback, lifetime, and performance evidence for the file declared secret reference
implementation. Repository-wide validation is recorded separately from the targeted proofs below.

## Approved design and public guide

- [Approved issue #807 plan](issue-807-file-declared-secret-references.md)
- [File secret reference feature guide](../../Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md)

The guide defines the public `Secret<T>` contract, descriptor shape, direct-root completeness, provider selection,
atomic file layers, diagnostics, audit behavior, and migration guidance.

## Packed consumer proof

The targeted smoke ran successfully on September 15, 2026 with the direct-root and file-layer case fixes:

```text
WORK_DIR=/private/tmp/issue807-file-secret-smoke-20260915-065449 \
  ARTIFACT_REPORT=/private/tmp/issue807-file-secret-smoke-20260915-065449/file-secret-references-smoke.md \
  ./scripts/file-secret-references-smoke.sh
```

Exit code: `0`.

Logs and generated artifacts: `/private/tmp/issue807-file-secret-smoke-20260915-065449`.

- The immutable baseline was created with `git archive` at commit
  `e55c549ed23a9da9abb747c87bd202b504d1c736`; Core, Config, and Google Secret Manager baseline packages packed
  successfully without mutating the archived source.
- The baseline consumer used the real `AppSurfaceStartup<LegacyModule>` host, `AppSurfaceConfigModule`,
  `AppSurfaceGoogleSecretManagerModule`, and DI-resolved `IConfigManager`. It emitted
  `PREVIOUS_OK api-key=legacy-google-value plain=legacy-plain` before current packaging and again after all current
  cases.
- The current package source was copied into an isolated tree before packing, including the current direct-root and
  cross-layer case-insensitive raw-view fixes. The current consumer restored from the isolated feed and built with zero
  warnings. The smoke package set contains Core, Config, and Google Secret Manager; the consumer references those same
  three packages.
- Current case results were:
  - `PASS mode=Production hasValue=False provider=none googleCalls=0`
  - `PASS mode=Production hasValue=True provider=EnvironmentConfigProvider googleCalls=0`
  - `PASS mode=Development hasValue=True provider=google-secret-manager googleCalls=1`
  - `EXPECTED FAILURE: secret-not-found at FileSecretReferences:ApiKey`
- The failure log retained no `fake-google-value` payload.
- The published baseline artifact was unchanged across the current run. The before and after manifest files are byte
  identical, and both have SHA256
  `5975b9caa2c316838b652fd721229ecc441126eae689881108235d5f0e4df666`.
- Disabled plus rescue elapsed time was `1.011s`.

The example and generated baseline use the standard [AppSurface startup API](../../ForgeTrust.AppSurface.Core/README.md)
with explicit host construction, startup, DI resolution and assertions, then stop and disposal. The expected failure
case catches the typed composition exception; unexpected startup or assertion failures produce a nonzero exit.

## Existing lifetime and performance evidence

Source: `/private/tmp/issue807-composition-performance.md`, recorded September 15, 2026.

- Five lifetime tests and the benchmark passed in both Debug and Release in the recorded focused run.
- The Release measurement covered one `Secret<string>` destination, 256 warm sequential executions, 512 concurrent
  executions with at most eight workers, and a synthetic 1 ms provider delay. The warm sequential median was 22.2 µs
  versus 1,208.25 µs with the synthetic delay; this is fixture-local evidence, not a Google, LocalSecrets, or network
  latency claim.
- The lifetime checks covered successful binding, constructor failure, scalar conversion failure, sensitive raw bases,
  and concurrent executions. They showed no persistent reachability through the tested engine/cache/trace paths after
  completion; they do not establish memory zeroization or control retention by application consumers.
- The documentation assertions verify diagnostic fragments, the canonical sidecar, and relative guide links.

## Contract and migration references

- [Compiler contracts](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionCompilerTests.cs) and
  [runtime contracts](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionEngineTests.cs).
- [Cross-layer case regression tests](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionLayerCaseTests.cs)
  and [scalar, rescue, and audit edge cases](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionExtendedBehaviorTests.cs).
- [Shared runtime/audit parity](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionAuditTests.cs) and
  [startup lifecycle validation](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionStartupTests.cs).
- [Google two-sibling migration proof](../../Config/ForgeTrust.AppSurface.Config.GoogleSecretManager.Tests/FileSecretReferencesMigrationTests.cs).
- [Lifetime and performance fixture](../../Config/ForgeTrust.AppSurface.Config.Tests/ConfigCompositionLifetimeTests.cs).
- [Contract workflow](../../.github/workflows/config-secret-references.yml): core, Google, and LocalSecrets contracts
  on macOS, Ubuntu, and Windows, plus an Ubuntu packed-consumer and rollback job with full Git history.

Stable publication still requires the separately scoped real Skoolit root migration, measured glue deletion, Google
verification without environment rescue, and previous-artifact rollback rehearsal. That external work remains deferred
and unchanged.
