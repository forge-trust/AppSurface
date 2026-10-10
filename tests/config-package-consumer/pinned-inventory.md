# Pinned Skoolit configuration selection inventory

This checklist records the selections in Skoolit commit
`a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e`, pinned for issue #864 against AppSurface
`c2f062f2a22afeff972c5c6512f1f34dd182db90`. It is the fixture acceptance list:
the two Web methods and all six public worker selections are represented. The
[packed Domain fixture](fixtures/domain/DomainConfigs.cs) contains the catalog
and parity wrappers outside discovery. The four worker-local wrappers remain in
[worker entry/root discovery](WorkerRootConfigs.cs). The constructor-injection
example is a separate fixture-only wrapper; it does not describe a dependency
in the pinned source.

| Caller and phase | Wrapper type | AppSurface key | Selection and activation | Constructor / Init dependencies | Discovery membership |
| --- | --- | --- | --- | --- | --- |
| Web `RegisterProductFeatureCatalog`; eventual Web provider | `Skoolit.Domain.Features.ProductFeatureCatalogConfig` | `Skoolit.Features` | Added when the startup context is non-null; lazy singleton factory constructs and initializes it when resolved | Implicit parameterless constructor; factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In `Skoolit.Domain`, a referenced assembly outside Web module/entry discovery |
| Web `RegisterProductFeatureWorkerParityReceipt`; eventual Web provider | `Skoolit.Domain.Features.ProductFeatureWorkerParityReceiptConfig` | `Skoolit.FeatureWorkerParity` | Added when the startup context is non-null; lazy singleton factory constructs and initializes it when resolved | Implicit parameterless constructor; factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In `Skoolit.Domain`, a referenced assembly outside Web module/entry discovery |
| Worker public planning stage, first provider | `Skoolit.Domain.Features.ProductFeatureCatalogConfig` | `Skoolit.Features` | Always registered; resolved with `GetRequiredService` before composition planning | Implicit parameterless constructor; typed factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In `Skoolit.Domain`, outside the configured `Skoolit.DurableWorker` entry/root scan; explicit selection is required |
| Worker public planning stage, first provider | `Skoolit.Domain.Features.ProductFeatureWorkerParityReceiptConfig` | `Skoolit.FeatureWorkerParity` | Always registered; resolved with `GetRequiredService` before composition planning | Implicit parameterless constructor; typed factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In `Skoolit.Domain`, outside the configured `Skoolit.DurableWorker` entry/root scan; explicit selection is required |
| Worker public planning stage, first provider | `DurableWorkerPublicOptionsConfig` | `Skoolit.DurableWorkers` | Always registered and resolved before composition planning | Implicit parameterless constructor; typed factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In the `Skoolit.DurableWorker` entry/root scan; has a matching root `ConfigKey` |
| Worker public planning stage, conditional first-provider resolution | `AlphaEvidenceSourceOnlyPublicOptionsConfig` | `Skoolit.AlphaEvidence.SourceOnly` | Registered only for lane `EvidenceSupport`; resolved only when the lane is `EvidenceSupport` and public options have both `Enabled` and `WorkerAdmissionEnabled` true (`evidenceMayRun`) | Implicit parameterless constructor; typed factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In the `Skoolit.DurableWorker` entry/root scan; has a matching root `ConfigKey` |
| Active EvidenceSupport stage, second provider | `ForwardingExtractionPublicOptionsConfig` | `Skoolit.Forwarding.Extraction` | Added and resolved only when the plan is `Active` and lane is `EvidenceSupport` | Implicit parameterless constructor; typed factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In the `Skoolit.DurableWorker` entry/root scan; has a matching root `ConfigKey` |
| Active EvidenceSupport stage, second provider | `SharedDataProtectionPublicOptionsConfig` | `Skoolit.DataProtection` | Added and resolved only when the plan is `Active` and lane is `EvidenceSupport` | Implicit parameterless constructor; typed factory resolves `IConfigManager` and `IEnvironmentProvider` for `IConfig.Init` | In the `Skoolit.DurableWorker` entry/root scan; has a matching root `ConfigKey` |

The two Web methods are `SkoolitWebModule.RegisterProductFeatureCatalog` and
`SkoolitWebModule.RegisterProductFeatureWorkerParityReceipt`. Both add
`TryAddSingleton` factories to `StartupContext.CustomRegistrations` and both
manually call `IConfig.Init` with their table key. The worker uses one generic
`RegisterTypedConfig<TConfig>` registrar for all six rows; its factory also
manually calls `Init`. The worker context sets
`OverrideEntryPointAssembly = typeof(DurableWorkerPublicCompositionBootstrapper).Assembly`.

The worker's first provider resolves the catalog, parity receipt, and public
worker options for planning. The source-only wrapper is additionally resolved
from that first provider only under the `evidenceMayRun` condition above. The
active EvidenceSupport branch then adds extraction and Data Protection
wrappers and resolves them from a separately built second provider. Thus
registration, first-stage resolution, and evidence-stage resolution are
separate observable phases.

The candidate packed fixture must use `AddAppSurfaceConfig<T>` for its selected Domain
wrappers and must contain no copied `Init`, handwritten key, or private audit
registration on those candidate selection paths. The baseline intentionally
retains manual initialization for comparison. Keep a separate unselected-wrapper activation tripwire and
provider-read counter. Record registration, selected runtime resolution, and
audit inspection counts independently. A fixture-only selected wrapper may
have a constructor-injected dependency to prove normal DI activation; all six
worker selections and both Web wrappers listed above have parameterless
constructors in the pinned source.

## Executable fixture assertions

- The two source Domain wrappers, the separate `PaymentsEndpointConfig`
  [constructor-injection example](ExplicitRegistrationExample.cs), and an
  unselected `Config<Secret<string>>` tripwire live in the packed Domain assembly.
  Every Web and worker discovery input excludes that assembly. There are no
  duplicate worker-root catalog/parity types.
- The [worker proof](WorkerStageProof.cs) discovers only the four local wrappers.
  Repeating the explicit helper on all four preserves one unkeyed descriptor per
  wrapper. Every final audit has six unique resolved attributed entries with the
  expected value type, using the default host translation of source dot keys.
- The Web baseline manually initializes the two source Domain wrappers and the
  separate injected example. Runtime succeeds while their declarations are
  absent. The candidate selects them with `AddAppSurfaceConfig<T>` and obtains
  matching attributed identities. This is an equivalent fixture, not a claim of
  an actual downstream migration.
- The worker clones the base collection for its public planning stage, resolves
  catalog/parity/DurableWorker from the first provider, and conditionally resolves
  AlphaSourceOnly. It clones that public collection into a second collection and
  builds a distinct second provider only for Active EvidenceSupport, resolving
  ForwardingExtraction and SharedDataProtection there. Discovery already provides
  those four local descriptors; the evidence records conditional helper selection
  separately from runtime resolution.
- Five scenarios cover another lane, disabled EvidenceSupport, disabled worker
  admission, standby EvidenceSupport, and active EvidenceSupport. Every provider
  independently validates startup. Exact per-wrapper constructor counts, typed-key
  manager calls during `Init`, and provider reads are asserted as phase deltas for
  registration, startup before resolution, runtime, and audit.
- Registration and startup deltas are zero. Runtime activates only the selected
  stage wrappers, once each. Audit constructs one separate inspection wrapper
  per declared key and reads that key once, with zero `Init` calls. The first
  provider retains its singleton identities and read/Init counters after the
  second stage.
- The unselected secret-bearing wrapper stays unconstructed, and the fixture
  secret provider's payload reads remain zero through all phases. Evidence
  includes source revisions, package IDs/versions, discovery input names, stage
  names, counts, declaration identities and safe status markers. It excludes
  resolved configuration values and secret sentinels.

Pinned source locations:

- Web: `src/Skoolit.Web/SkoolitWebModule.cs#L538-L584`.
- Worker selections and stages: `src/Skoolit.DurableWorker/DurableWorkerPublicCompositionBootstrapper.cs#L13-L145`.
- Domain wrapper definitions: `src/Skoolit.Domain/Features/ProductFeatureCatalog.cs#L92-L99` and `#L272-L277`.
- Worker wrapper definitions: `src/Skoolit.DurableWorker/DurableWorkerComposition.cs#L184-L189` and `#L416-L431`.
- Source revision: `a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e`.
