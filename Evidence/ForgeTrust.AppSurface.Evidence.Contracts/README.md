# ForgeTrust.AppSurface.Evidence.Contracts

`ForgeTrust.AppSurface.Evidence.Contracts` is the stable vocabulary for a CI run that says what changed, which evidence was required, what actually ran, and whether a downstream gate may consume the result.

Start with the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md) before installing a package. Use this package directly only when you are authoring a consumer-owned producer, policy tool, or gate integration. It starts no process, discovers no test code, provisions no resources, and sends no telemetry.

Canonical emitted plan and manifest snapshots are described by the versioned [1.0 JSON Schema](https://github.com/forge-trust/AppSurface/blob/58f30b019939f4a79bd51685324ec7e070f85c04/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/schemas/evidence-contracts-1.0.schema.json) and [1.1 JSON Schema](https://github.com/forge-trust/AppSurface/blob/58f30b019939f4a79bd51685324ec7e070f85c04/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/schemas/evidence-contracts-1.1.schema.json). These schemas use canonical PascalCase property names and are reference shape checks. `EvidenceCanonicalJson.Deserialize` checks supported versions and contract read rules, but does not verify the manifest digest, plan binding, or artifact declarations. Use `EvidenceManifestBuilder.Verify` for integrity and `VerifyForGate` for gate admission, and obtain both the plan and manifest through a trusted channel; neither operation provides attestation. Policy authoring readers may accept case-insensitive property names, while these schemas intentionally describe canonical output only.

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

### Bounded JSON input

Use `EvidenceCanonicalJson.DeserializeAsync<T>(Stream, maximumBytes, cancellationToken)` at a file or stream boundary. It counts bytes as they arrive and rejects input as soon as it reads a byte beyond the limit. The default ceiling is 20 MiB per document. A caller may lower it, including to zero, and retains ownership of the stream. `Deserialize<T>(ReadOnlySpan<byte>)` applies the same ceiling to bytes already in memory. Its explicit-limit overload lets callers use a smaller ceiling.

The reader rejects duplicate and case-colliding property names, malformed JSON, unknown and numeric enum values, missing required constructor members, and null items in typed Evidence contract collections. The maximum nesting depth is 64. Typed plans and manifests must declare `1.0` or `1.1`, including when they appear in a caller-owned wrapper or collection. Use `EvidencePlan.CurrentContractVersion` (`1.0`) when constructing a baseline plan directly. The [planner](../ForgeTrust.AppSurface.Evidence.Planner/README.md) selects the version automatically, and manifests carry forward the plan's version. Known enum names remain case-insensitive. Optional defaults, explicitly nullable fields, and unknown additive properties remain accepted. Unicode validation applies to all JSON strings, including values in ignored additive properties. Canonical serialization preserves the existing bytes for supported contracts.

Expect `JsonException` for malformed or unsupported JSON, `InvalidDataException` for a size limit or supported stream read failure, and `ArgumentOutOfRangeException` for a limit outside the supported range. Diagnostics from the reader omit the input. Cancellation propagates to the caller. Counted reads enforce the input limit even when stream length is unavailable. The reader validates the document's structure. Authenticating its source remains the caller's responsibility.

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

## Retained artifact check

`EvidenceArtifactFileValidation.CheckAsync(artifactRoot, producerId, artifact, cancellationToken)` compares one artifact recorded in an `EvidenceProducerResult` with the file at `<artifactRoot>/<producerId>/<artifact.RelativePath>` and returns an `EvidenceArtifactFileStatus`.

| Status | Meaning |
| --- | --- |
| `Valid` | An unlinked regular file with the recorded length and SHA-256. |
| `InvalidPath` | The producer id is not one path segment, the relative path is absolute, empty or traversing, or the recorded length is negative. |
| `Linked` | The artifact root, a directory on the way, or the file is a symbolic link or other reparse point. |
| `Missing` | The file or a directory on the way does not exist or cannot be opened. |
| `LengthMismatch` | The file length differs from the recorded length. Checked before hashing, and again while reading: a file that shrinks or grows after it is opened gets this status. |
| `DigestMismatch` | Same length, different SHA-256. At most the recorded length is hashed, and at most one byte past it is read. |

**Why it exists.** `EvidenceArtifactValidation.AreValid(...)` checks artifact metadata against a declaration, and `EvidenceManifestBuilder.Verify(...)` checks digests of the plan and manifest. Neither opens the artifact, so without this check a manifest can verify after its artifact is deleted or replaced. [`appsurface evidence verify`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md#verifying-evidence) and the repository validation tool both call this one method, so there is a single link-rejection rule.

**When to use something else.** A producer checking its own writes during a run should keep using `EvidenceArtifactWriter.VerifyWrittenArtifactsAsync()`.

**Pitfalls.** Links are rejected at the artifact root and below it, not above it, so pass a root you trust. The method returns a status and does not throw for a bad file; a caller that forgets to compare with `Valid` accepts everything.

## Test producer declaration (preview)

A producer of kind `test` declares the test projects it lists, runs and reconciles in an optional `test` block on `EvidenceProducerDeclaration`. It claims selection completeness, "every test the declared projects and filters selected ran and passed", and nothing about coverage.

```json
{
  "id": "tooling-tests",
  "kind": "test",
  "version": "1.0.0",
  "requiredResources": [],
  "assertionIds": ["appsurface/test/discovery-complete@1"],
  "artifactSlots": [
    { "logicalName": "test-receipt", "relativeRoot": "test", "mediaType": "application/json", "required": true, "maximumBytes": 65536 }
  ],
  "timeoutSeconds": 900,
  "test": {
    "projects": [
      { "path": "tests/Tooling.Tests/Tooling.Tests.csproj", "filter": "FullyQualifiedName~Tooling.Release", "minimumTests": 10 }
    ]
  }
}
```

| Member | Shape and default | Constraint |
| --- | --- | --- |
| `EvidenceProducerDeclaration.Test` | `EvidenceTestDeclaration?`, `null` | Required for kind `test`, rejected on every other kind. Omitted from canonical JSON when `null`. |
| `EvidenceTestDeclaration.Projects` | list of `EvidenceTestProject` | 1 to 32 entries, unique paths. |
| `EvidenceTestProject.Path` | string | Repository-relative, forward slashes, at most 512 characters, ends in `.csproj`, `.fsproj` or `.vbproj`. |
| `EvidenceTestProject.Filter` | `string?`, `null` | VSTest filter, at most 1024 characters, one line. Omitted when `null`. |
| `EvidenceTestProject.MinimumTests` | `int?`, `null` meaning 1 | 1 or greater. Omitted when `null`. |
| `EvidenceTestProducerContract` | constants | The kind, the single assertion id, the receipt slot name, media type and required `relativeRoot` (`test`), the limits above, the 30-second minimum timeout, and `GetMinimumReceiptBytes(projects)`: 4096 bytes plus, per project, 512 bytes and six bytes per character of its path. |

The [planner](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md#test-producer-rules-preview) enforces every constraint with its own diagnostic, `ASEVD137` through `ASEVD149`.

**When to use it.** Use kind `test` for paths with no production code to cover, such as workflows, scripts and release tooling, where the truthful evidence is that the tests exercising them ran. Use kind `coverage` when a coverage threshold is the evidence; a `test` producer cannot carry a coverage gate or close a coverage assertion.

**Digests.** The whole declaration is hashed, so changing a project, a filter or a minimum changes `PolicyDigest` and `PlanDigest`. A policy with no `test` block keeps its existing canonical bytes.

**Pitfalls.**

- Preview: the kind, the assertion id and this shape may change without a compatibility step until a real route has closed with them.
- The filter is public plan data. Do not put anything confidential in it.
- A filter that starts matching fewer tests can still pass if it matches at least `minimumTests` tests, or one when no minimum is set, and every selected test runs and passes. A filter that matches no test always fails.
- A reader that does not know the `test` property drops it and computes a different `PolicyDigest`. Upgrade every CLI that runs or verifies evidence before adding a `test` block to a shared policy, and remove the block before downgrading.

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

`EvidenceOwnedChild` is the BCL-only enrollment helper. `RunAsync(Func<CancellationToken,Task>, CancellationToken)` wraps asynchronous work, and `RunProcessAsync(ProcessStartInfo, CancellationToken)` starts and joins a process. Each has an overload accepting an explicit `IReadOnlyDictionary<string,string>` environment. When the three invocation channel values are present, incomplete/invalid/closed/full enrollment throws before calling work or starting the process. Process helpers pass the channel into the child's environment; child processes enroll separately. Owners must also enroll every test-created descendant and wait through its actual disposal; root exit, signals and process-tree kill are not substitutes. Enrollment and completion handshakes use the existing five-second protocol bound; expiration reports a failed handshake, never successful child settlement. Present-but-null channel keys are invalid configuration. Unenrolled Windows shell launches leave the environment dictionary uninitialized so ordinary shell execution remains available. The bounded protocol supports up to 64 enrollments and 4 KiB frames. If enrollment is incomplete or disconnects, settlement is unknown and receipt publication is suppressed.

A prepared host exposes `EvidenceProducerContext.ExecutionId` and `OwnedChildEnvironment` as `[JsonIgnore]` init-only capabilities. Version 1 supplies the channel only for eligible built-in coverage workloads; custom producers receive null and remain responsible for joining all their own children. These values never become plan/manifest properties, artifact data, logs, or key material. `EvidenceProducerKeyMaterialContext` receives neither. Use the explicit environment overload when the context supplies a channel; use the ambient overload when `OwnedChildEnvironment` is null. For example:

```csharp
if (context.OwnedChildEnvironment is { } environment)
    await EvidenceOwnedChild.RunProcessAsync(start, environment, cancellationToken);
else
    await EvidenceOwnedChild.RunProcessAsync(start, cancellationToken);
```

Do not pass null to the explicit environment overload or mutate global environment variables. Without the invocation channel, the ambient helper overloads remain ordinary fresh-only wrappers.

The [approved producer reuse design](../../docs/designs/issue-917-producer-reuse.md) defines opt-in local reuse. `EvidenceProducerDeclaration.Inputs` is a nullable init-only `EvidenceProducerInputs(Include, Exclude)` record: declare every file influencing the producer, including build configuration, tools and fixtures. Missing `Inputs` means ordinary fresh execution. An invocation must also explicitly enable reuse. Input completeness is the producer owner's promise; the framework does not infer a build graph or prove isolation. The [planner reference](../ForgeTrust.AppSurface.Evidence.Planner/README.md#declared-producer-inputs) describes static grammar validation; matching files and rejecting an empty final set belong to preparation.

A producer can also implement `IEvidenceProducerKeyMaterial.GetKeyMaterialAsync` returning `ValueTask<IReadOnlyDictionary<string, string>>`. Its `EvidenceProducerKeyMaterialContext(Plan, Producer, TimeProvider)` intentionally exposes no artifacts or ready resources. Use it only for additional local execution identity that file inputs cannot express. Return no secrets; use at most 64 entries, keys up to 128 characters, values up to 4 KiB UTF-8 each and 256 KiB total. Callbacks must be local, side-effect-free and cancellation-aware. The host owns the actual callback task: its deadline is the producer timeout capped at 30 seconds, followed by the host's cleanup grace period. Fresh fallback requires actual task settlement. A callback that remains active cannot authorize resource startup, fresh work or receipt publication; a bounded wait does not terminate in-process code.

The appended `Reused`, `NotRequiredForExecution`, and `LocalReusedComplete` enum members preserve existing numeric values. A reused result must have a lower-case 64-character SHA-256 `ProducerKey`, lower-case SHA-256 `OriginManifestDigest`, nondefault `DateTimeOffset` `OriginCompletedAt`, and zero current `ElapsedMilliseconds`. Historical execution duration belongs in the origin receipt. Fresh results carry none of these provenance properties. The host owns provenance and fresh-result admission; the builder validates metadata shape and current-plan assertions, without authenticating the original execution.

`EvidenceManifestBuilder.Build` permits reused results to close the current plan's obligations only with valid provenance. A resource may report `NotRequiredForExecution` only outside the transitive prerequisite closure of fresh producers. A fully successful local reused run yields `LocalReusedComplete` with `Informational` eligibility; release envelope and cleanup requirements still apply. Explicit observation mode remains `ObservationOnly`. `ReuseCount` is derived from results and omitted at zero; contract 1.0 never emits the property, while contract 1.1 emits it only when positive. The reader rejects an explicitly inconsistent count. `Verify` checks integrity, including informational manifests; use `VerifyForGate` for gate admission, which refuses reused claims. Both require trusted inputs and provide no attestation.

`EvidenceExecutionMetrics` adds nonnegative init-only `long` properties `PreparationMilliseconds`, `ReceiptWaitMilliseconds`, and `ArtifactRestoreMilliseconds`, defaulting to zero and omitted at zero. Nonzero values require contract 1.1. They measure this invocation; restored historical producer time must not enter fresh execution metrics or runtime baselines. All existing positional constructors remain unchanged.

### Multi-file slots and versioning

`EvidenceArtifactSlot.MaximumFiles` is nullable and init-only: absent means one file; explicit values must be 1 through 128. The 1.1 schema accepts omission or an integer in that range and rejects explicit null, fractional numbers, zero, and larger values. `MaximumBytes` bounds the sum of bytes across the slot, and the existing 256 MiB producer-wide budget still applies. Required slots need at least one file. Both writer overloads enforce the same limits and release failed reservations without discarding successful files. `WriteAsync(ReadOnlyMemory<byte>)` preserves overwrite behavior for legacy single-file slots where `MaximumFiles` is absent; when `MaximumFiles` is explicit, it creates the destination exclusively. `WriteAsync(Stream)` always creates destinations exclusively. The metadata validator rejects duplicate `(LogicalName, RelativePath)` pairs and duplicate destinations across slots. Multi-file paths must already be canonical, use forward slashes, stay beneath the declared root, and contain no traversal, absolute/drive paths, reserved names or control characters. Multi-file slots reject existing linked entries and case-colliding destinations. Legacy single-file writes and verification keep lexical path validation, which supports consumer-configured linked output directories. Reuse custody requires unlinked output paths even for single-file slots. Keep these roots under the invocation’s exclusive control for the entire write and verification. Writer output and 1.1 manifest inventories sort by logical name then relative path using ordinal ordering. Artifact custody and receipt restoration also require filesystem checks; metadata validation alone does not establish on-disk integrity.

Plans use `1.1` when any policy producer declares `Inputs`, an explicit `MaximumFiles`, or `CooperativeChildLifetime=true`, including producers outside the selected profile. Legacy policies and ordinary runs without additions retain `1.0`. Absent nullable additions, zero reuse counts and zero optional timings preserve the legacy canonical bytes and digests. The [bounded JSON reader](#bounded-json-input) requires named enum values for all inputs, including policies. Verifier admission rejects unsupported or inconsistent versions and 1.0 reuse additions. Canonical serialization preserves the existing enum bytes.

This API remains provisional: source byte goldens and current-reader tests do not establish compatibility with released readers. The actual released-package reader matrix and complete multi-file coverage custody/size proof described in the [design's compatibility requirements](../../docs/designs/issue-917-producer-reuse.md#contract-and-compatibility) must complete before a 1.1 compatibility freeze. Coordinate reader, verifier, CLI and host upgrades before committing `Inputs`, because even fresh plans then use 1.1.
