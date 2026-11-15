# ForgeTrust.AppSurface.Evidence.Contracts

`ForgeTrust.AppSurface.Evidence.Contracts` is the stable vocabulary for a CI run that says what changed, which evidence was required, what actually ran, and whether a downstream gate may consume the result.

Start with the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md) before installing a package. Use this package directly only when you are authoring a consumer-owned producer, policy tool, or gate integration. It starts no process, discovers no test code, provisions no resources, and sends no telemetry.

Canonical emitted plan and manifest snapshots are described by the versioned [1.0 JSON Schema](./schemas/evidence-contracts-1.0.schema.json) and [1.1 JSON Schema](./schemas/evidence-contracts-1.1.schema.json). These schemas use canonical PascalCase property names and are reference shape checks; the contract reader also enforces semantic graph, digest, authorization, and artifact rules. Policy authoring readers may accept case-insensitive property names, while these schemas intentionally describe canonical output only.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Contract shape

An `EvidencePolicy` resolves into an immutable `EvidencePlan`. A run records declared `EvidenceProducerResult` values and then uses `EvidenceManifestBuilder.Build(...)` to produce an `EvidenceManifest`. Both plan and manifest have canonical JSON and SHA-256 digests, so a gate can verify a result without rerunning the test suite.

`EvidencePolicy.CombineProfiles` is an opt-in (`false` by default) policy property. When enabled, the planner unions distinct profiles selected by matched paths, requiring identical canonical declarations for repeated ids and a shared profile scope. Unknown paths continue to use `ConservativeProfileId`; see the [planner reference](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md) for merge rules and diagnostics. The default property is omitted from canonical JSON, so existing policies retain their serialized bytes and positional constructor.

| Type family | Purpose |
| --- | --- |
| `EvidencePolicy`, `EvidenceProfile`, `EvidencePolicyRule` | Checked-in change-risk policy and explicitly selected profile. |
| `EvidenceResourceDeclaration`, `EvidenceProducerDeclaration`, `EvidenceObligation` | Closed declaration of resource readiness, producer assertions/artifacts, and the risk obligation each assertion may close. |
| `EvidencePlan`, `NormalizedDiffPath` | Deterministic resolved input. `DiffDigest` binds normalized paths; optional `PatchSha256` binds the exact unified patch bytes. The plan digest also covers policy identity, the selected profile, and matched rules. |
| `EvidenceProducerResult`, `EvidenceManifest` | Terminal producer outcomes and the resulting gate claim. |
| `EvidenceCanonicalJson`, `EvidenceDigest`, `EvidenceManifestBuilder` | Canonical serialization, digesting, claim calculation, and manifest verification. |

`EvidenceClaimKind.TargetedComplete` is eligible for a pull-request gate; `ReleaseComplete` is eligible only for a release gate and requires `ValidatedNotAttested` envelope status. `ObservationOnly` is deliberately informative, never gate-eligible. `NoEvidenceRequired` is valid only when the selected profile declares no resources, producers, or obligations.

## Streaming artifact writes

Use [`EvidenceArtifactWriter.WriteAsync`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs) with a `Stream` when a producer has an artifact that may be large or is supplied by a non-seekable process stream. The writer copies in bounded chunks, computes the lower-case SHA-256 digest as bytes arrive, and enforces both the slot's `MaximumBytes` and the shared 256 MiB producer budget before accepting each chunk. The input stream remains owned by the caller and is read to end; the writer exposes only `EvidenceArtifactResult` metadata.

The existing `ReadOnlyMemory<byte>` overload remains useful for already-buffered small values. Use `VerifyWrittenArtifactsAsync` after producer work or before returning a result when the output directory may have been touched by another process. Verification also streams each file and returns `false` for missing, inaccessible, truncated, or tampered artifacts. Cancellation and copy errors remove the partial destination and release the logical-name, destination, slot, and aggregate reservations so the producer can retry safely.

Avoid seeking or rewinding the input stream: streaming writes consume it exactly once. Do not close the input stream as part of producer cleanup unless the producer owns it. A failed write cannot be resumed from its partial file; retry by supplying a fresh stream, because the partial destination is deleted before the reservation is released.

## Claim rules

A complete claim is deliberately conservative:

- every selected producer must report `Passed` for a gate-eligible claim (local informational completion also permits validated `Reused` results);
- every obligation's required producer and assertion must be present;
- producer results may not name undeclared producers or assertions;
- a release profile requires a registered CI-envelope verifier; and
- the manifest digest and plan digest must verify unchanged.

An unavailable capability, timeout, skipped producer, incomplete test profile, or failed assertion therefore produces `None`, not a partial success. This is how EvidenceHost distinguishes an observation from evidence that can mediate risk.

## Pitfalls

- Do not construct a `NoEvidenceRequired` result merely because a local run omitted tests. It is a policy outcome, not a convenience override.
- Do not treat a coverage collection artifact as a gate pass unless its producer has closed the assertion declared by the selected policy.
- Do not claim independent attestation in v1. An accepted envelope is represented as `ValidatedNotAttested`.
- Do not edit generated plan or manifest JSON. `EvidenceManifestBuilder.Verify(...)` and `appsurface evidence verify` detect inconsistent or edited claim fields by recomputing internal digests; they do not authenticate their inputs. Gates must obtain the plan and manifest through a trusted CI channel.

Read next: the [planner README](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), the [Aspire lifecycle README](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md), and the [EvidenceHost cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md).

Coverage declarations optionally include `coverageExecution`: `configuration` (default `Debug`, a simple identifier up to 32 characters), `parallelism` (1 through 32, default 1), and `testResults` (`none` or `junit`, default `none`). These settings control execution and report production; they do not alter coverage gates or source filters. Omitting the property preserves the previous canonical declaration bytes and legacy behavior. An explicitly supplied value participates in the policy and plan digests. See the [built-in coverage workflow](../ForgeTrust.AppSurface.Evidence.Cli/README.md) and [native consumer acceptance recipe](../../tools/ForgeTrust.AppSurface.Evidence.Validation/README.md).

## Local producer reuse contract (candidate 1.1)

The complete adoption, decisions, and pitfalls are in the canonical [local Evidence producer reuse guide](../../guides/evidence-reuse.md).

### Cooperative child lifetime

`EvidenceCoverageExecutionOptions.CooperativeChildLifetime` opts a coverage producer into the cooperative-v1 lifetime promise. It defaults to false and is omitted from canonical JSON when false; true requires contract 1.1 and participates in the plan identity. Every child asynchronous task/process must enroll before work begins and must be joined through actual cleanup. This is a trusted workload promise, not OS containment. Missing channel means ordinary fresh execution only.

`EvidenceOwnedChild` is the BCL-only enrollment helper. `RunAsync(Func<CancellationToken,Task>, CancellationToken)` wraps asynchronous work, and `RunProcessAsync(ProcessStartInfo, CancellationToken)` starts and joins a process. Each has an overload accepting an explicit `IReadOnlyDictionary<string,string>` environment. When the three invocation channel values are present, incomplete/invalid/closed/full enrollment throws before calling work or starting the process. Process helpers pass the channel into the child's environment; child processes enroll separately. Owners must also enroll every test-created descendant and wait through its actual disposal; root exit, signals and process-tree kill are not substitutes. The bounded protocol supports up to 64 enrollments and 4 KiB frames. If enrollment is incomplete or disconnects, settlement is unknown and receipt publication is suppressed.

Prepared producers receive `EvidenceProducerContext.ExecutionId` and `OwnedChildEnvironment` as `[JsonIgnore]` init-only capabilities. These values never become plan/manifest properties, artifact data, logs, or key material. `EvidenceProducerKeyMaterialContext` receives neither. Use the explicit environment overload, for example `await EvidenceOwnedChild.RunProcessAsync(start, context.OwnedChildEnvironment!, cancellationToken);`; do not mutate global environment variables. Without the invocation channel, the ambient helper overloads remain ordinary fresh-only wrappers.

The [approved producer reuse design](../../docs/designs/issue-917-producer-reuse.md) defines opt-in local reuse. `EvidenceProducerDeclaration.Inputs` is a nullable init-only `EvidenceProducerInputs(Include, Exclude)` record: declare every file influencing the producer, including build configuration, tools and fixtures. Missing `Inputs` means ordinary fresh execution. An invocation must also explicitly enable reuse. Input completeness is the producer owner's promise; the framework does not infer a build graph or prove isolation. The [planner reference](../ForgeTrust.AppSurface.Evidence.Planner/README.md#declared-producer-inputs) describes static grammar validation; matching files and rejecting an empty final set belong to preparation.

A producer can also implement `IEvidenceProducerKeyMaterial.GetKeyMaterialAsync` returning `ValueTask<IReadOnlyDictionary<string, string>>`. Its `EvidenceProducerKeyMaterialContext(Plan, Producer, TimeProvider)` intentionally exposes no artifacts or ready resources. Use it only for additional local execution identity that file inputs cannot express. Return no secrets; use at most 64 entries, keys up to 128 characters, values up to 4 KiB UTF-8 each and 256 KiB total. Callbacks must be local, side-effect-free and cancellation-aware. The host owns the actual callback task: its deadline is the producer timeout capped at 30 seconds, followed by the host's cleanup grace period. Fresh fallback requires actual task settlement. A callback that remains active cannot authorize resource startup, fresh work or receipt publication; a bounded wait does not terminate in-process code.

The appended `Reused`, `NotRequiredForExecution`, and `LocalReusedComplete` enum members preserve existing numeric values. A reused result must have a lower-case 64-character SHA-256 `ProducerKey`, lower-case SHA-256 `OriginManifestDigest`, nondefault `DateTimeOffset` `OriginCompletedAt`, and zero current `ElapsedMilliseconds`. Historical execution duration belongs in the origin receipt. Fresh results carry none of these provenance properties. The host owns provenance and fresh-result admission; the builder validates metadata shape and current-plan assertions, without authenticating the original execution.

`EvidenceManifestBuilder.Build` permits reused results to close the current plan's obligations only with valid provenance. A resource may report `NotRequiredForExecution` only outside the transitive prerequisite closure of fresh producers. A fully successful local reused run yields `LocalReusedComplete` with `Informational` eligibility; release envelope and cleanup requirements still apply. Explicit observation mode remains `ObservationOnly`. `ReuseCount` is derived from results and omitted at zero; contract 1.0 never emits the property, while contract 1.1 emits it only when positive. The reader rejects an explicitly inconsistent count. `Verify` checks integrity, including informational manifests; use `VerifyForGate` for gate admission, which refuses reused claims. Both require trusted inputs and provide no attestation.

`EvidenceExecutionMetrics` adds nonnegative init-only `long` properties `PreparationMilliseconds`, `ReceiptWaitMilliseconds`, and `ArtifactRestoreMilliseconds`, defaulting to zero and omitted at zero. Nonzero values require contract 1.1. They measure this invocation; restored historical producer time must not enter fresh execution metrics or runtime baselines. All existing positional constructors remain unchanged.

### Multi-file slots and versioning

`EvidenceArtifactSlot.MaximumFiles` is nullable and init-only: absent means one file; explicit values must be 1 through 128. `MaximumBytes` bounds the sum of bytes across the slot, and the existing 256 MiB producer-wide budget still applies. Required slots need at least one file. Both writer overloads enforce the same limits and release failed reservations without discarding successful files. `WriteAsync(ReadOnlyMemory<byte>)` preserves overwrite behavior for legacy single-file slots where `MaximumFiles` is absent; when `MaximumFiles` is explicit, it creates the destination exclusively. `WriteAsync(Stream)` always creates destinations exclusively. The metadata validator rejects duplicate `(LogicalName, RelativePath)` pairs and duplicate destinations across slots. Multi-file paths must already be canonical, use forward slashes, stay beneath the declared root, and contain no traversal, absolute/drive paths, reserved names or control characters. Existing linked entries and case-colliding destinations are rejected. Writer output and 1.1 manifest inventories sort by logical name then relative path using ordinal ordering. Artifact custody and receipt restoration also require filesystem checks; metadata validation alone does not establish on-disk integrity.

Plans use `1.1` when any policy producer declares `Inputs`, an explicit `MaximumFiles`, or `CooperativeChildLifetime=true`, including producers outside the selected profile. Legacy policies and ordinary runs without additions retain `1.0`. Absent nullable additions, zero reuse counts and zero optional timings preserve the legacy canonical bytes/digests. Unknown contract versions and numeric enum tokens/strings are rejected by the contract reader; verifier admission rejects unsupported or inconsistent versions and 1.0 reuse additions. This strict enum rule applies to typed plan and manifest reads. Generic canonical serialization and policy reads preserve their existing `JsonStringEnumConverter` behavior, including consumer-defined enums and legacy defined numeric policy values.

This API remains provisional: source byte goldens and current-reader tests do not establish compatibility with released readers. The actual released-package reader matrix and complete multi-file coverage custody/size proof described in the [design's compatibility requirements](../../docs/designs/issue-917-producer-reuse.md#contract-and-compatibility) must complete before a 1.1 compatibility freeze. Coordinate reader, verifier, CLI and host upgrades before committing `Inputs`, because even fresh plans then use 1.1.
