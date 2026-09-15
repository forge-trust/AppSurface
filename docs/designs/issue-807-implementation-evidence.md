# Issue #807 implementation evidence

This document records the migration, rollback, lifetime, and performance evidence for the file declared secret reference
implementation. The final repository and packed-consumer gates below ran against code commit
`db45ecf5857b2903d50749a596810ad73385c8ff` on September 15, 2026. Later evidence-only documentation changes
do not change the code that was tested.

## Approved design and public guide

- [Approved issue #807 plan](issue-807-file-declared-secret-references.md)
- [File secret reference feature guide](../../Config/ForgeTrust.AppSurface.Config/docs/file-secret-references.md)

The guide defines the public `Secret<T>` contract, descriptor shape, direct-root completeness, provider selection,
atomic file layers, diagnostics, audit behavior, and migration guidance.

## Packed consumer proof

The [packed-consumer smoke](../../scripts/file-secret-references-smoke.sh) ran successfully against the final code:

```text
WORK_DIR=/private/tmp/issue807-final-packed-Xj4TNw \
  ARTIFACT_REPORT=/private/tmp/issue807-final-packed-Xj4TNw/file-secret-references-smoke.md \
  ./scripts/file-secret-references-smoke.sh
```

Exit code: `0`.

Logs and generated artifacts: `/private/tmp/issue807-final-packed-Xj4TNw`.

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
  `2f3a53469d6e4c56bd115bdce7669816352c620add0e776ef7f7ff1e15f9ade9`.
- Disabled plus rescue elapsed time was `0.942s`.

The example and generated baseline use the standard [AppSurface startup API](../../ForgeTrust.AppSurface.Core/README.md)
with explicit host construction, startup, DI resolution and assertions, then stop and disposal. The expected failure
case catches the typed composition exception; unexpected startup or assertion failures produce a nonzero exit.

## Final repository gates

The unchanged [solution coverage command](../../scripts/coverage-solution.sh), `./scripts/coverage-solution.sh`,
completed with exit code `0` between `07:17:49` and `07:30:46` UTC. All 52 test projects passed, and the working tree
was clean before and after the run. The existing gate requests 95% line and 85% branch coverage with a 0.5 percentage
point tolerance; no threshold, tolerance, or coverage policy was changed.

| Metric | Measured coverage | Existing effective threshold | Result |
| --- | ---: | ---: | --- |
| Overall lines | 94.67% (125426/132491) | 94.5% | Pass |
| Overall branches | 88.33% (40005/45291) | 84.5% | Pass |
| Patch lines, Codecov mode | 94.57% (1637/1731 measurable) | 94.5% | Pass |
| Patch branches | 94.35% (1938/2054 measurable) | 84.5% | Pass |

The patch was measured against `origin/main` at `e0618ac8dcc3b5903517e9a711f42959534b7fb5`. The first coverage
attempt identified a test-path policy violation and insufficient patch line coverage; the final run includes the path
helper correction and meaningful scalar, nested-value, lifecycle, layer, and failure-path regressions. Final raw logs
and run metadata are in `/private/tmp/issue807-coverage-attempt2`; merged reports are in `TestResults/coverage-merged`.

The final [package artifact proof](../../tools/ForgeTrust.AppSurface.PackageIndex/README.md) also passed in an isolated, clean checkout
of the same code commit: 47 package artifacts and all three packaged consumer proofs (Coverage CLI, Docs, and Tailwind).
The [package index](../../tools/ForgeTrust.AppSurface.PackageIndex/README.md) verification passed. Builds completed
without compiler or documentation warnings. The expected explicit MSBuild coverage compatibility warning occurred
inside its dedicated compatibility proof; the collector proof passed. Evidence is in
`/private/tmp/issue807-final-package-gate-gcLN`. The Core package has version `0.0.0-ci.local`, and its DLL informational
version includes the full tested commit hash.

The [documentation health command](../../Web/ForgeTrust.AppSurface.Docs/README.md)
returned `Healthy (200)` with 2222 documents and 2198 search records. It completed within the unchanged 30-second
startup timeout. An initial local startup timeout cleared when the retry disabled configuration reload watching with
`DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false`; this is a runner workaround, not proof of the timeout's precise
cause. The same setting was used for the final one-shot test hosts, together with disabled build-server reuse and
`UseSharedCompilation=false`. No production defaults were changed.

The health result retained the existing `appsurfacedocs.routes.lossy_slug_normalization` warning for the Docs consumer
fixture's `MULTI_INSTANCE_WALKTHROUGH.md`. The isolated host also logged its absent `wwwroot`, unconfigured diagnostic
read policy, and a duplicate PostgreSQL namespace documentation path; none made health verification fail. Logs are in
`/private/tmp/issue807-docs-health-retry-RlTD`.

The enhancement review completed clean after three cycles, including testing, security, maintainability, performance,
API-contract, simplification, adversarial, and red-team passes. Browser QA does not apply to this configuration library;
the real packed consumer and rollback exercise above is its user-facing validation.

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
