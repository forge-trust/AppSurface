# ForgeTrust.AppSurface.Evidence.Contracts

`ForgeTrust.AppSurface.Evidence.Contracts` is the stable vocabulary for a CI run that says what changed, which evidence was required, what actually ran, and whether a downstream gate may consume the result.

Start with the [EvidenceHost guide](../../start-here/evidencehost.md) and the [migration reference](../../docs/evidence/evidencehost-migration.md) before installing a package. Use this package directly only when you are authoring a consumer-owned producer, policy tool, or gate integration. It starts no process, discovers no test code, provisions no resources, and sends no telemetry.

<!-- appsurface-release-guidance: begin -->
## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package
from a prerelease feed, check the [package chooser](https://github.com/forge-trust/AppSurface/blob/main/packages/README.md) and [release hub](https://github.com/forge-trust/AppSurface/blob/main/releases/README.md)
for current release risk, migration guidance, and readiness.
<!-- appsurface-release-guidance: end -->

## Contract shape

An `EvidencePolicy` resolves into an immutable `EvidencePlan`. A run records declared `EvidenceProducerResult` values and then uses `EvidenceManifestBuilder.Build(...)` to produce an `EvidenceManifest`. Both plan and manifest have canonical JSON and SHA-256 digests, so a gate can verify a result without rerunning the test suite.

| Type family | Purpose |
| --- | --- |
| `EvidencePolicy`, `EvidenceProfile`, `EvidencePolicyRule` | Checked-in change-risk policy and explicitly selected profile. |
| `EvidenceResourceDeclaration`, `EvidenceProducerDeclaration`, `EvidenceObligation` | Closed declaration of resource readiness, producer assertions/artifacts, and the risk obligation each assertion may close. |
| `EvidencePlan`, `NormalizedDiffPath` | Deterministic resolved input. The plan binds policy identity, diff, selected profile, and matched rules. |
| `EvidenceProducerResult`, `EvidenceManifest` | Terminal producer outcomes and the resulting gate claim. |
| `EvidenceCanonicalJson`, `EvidenceDigest`, `EvidenceManifestBuilder` | Canonical serialization, digesting, claim calculation, and manifest verification. |

### Bounded JSON input

`EvidenceCanonicalJson.Deserialize<T>(ReadOnlySpan<byte>)` preserves the existing in-memory entry point and enforces a protected **20 MiB** input ceiling. Use `DeserializeAsync<T>(Stream, maximumBytes, cancellationToken)` when reading a file or another stream: it counts bytes while reading, does not depend on seekability or advertised length, and stops after observing the first byte over the limit, before parsing or allocating a full document. The stream remains owned by the caller. `maximumBytes` may lower the ceiling (including zero) but cannot raise it. The span overload with an explicit limit is available for callers that already hold bytes.

All deserialization entry points reject malformed JSON, duplicate or case-colliding property names anywhere in the document, unknown enum strings, and numeric enum values. Documents may contain at most 64 nested object or array levels. Invalid UTF-8 property names and unpaired surrogate escapes also produce a generic `JsonException` without exposing the underlying decoder exception or input. Valid Unicode names and paired surrogate escapes remain supported. Known enum names remain case-insensitive. Required constructor members must be present; non-nullable members and contract collection items cannot be `null`. Optional constructor defaults and explicitly nullable fields remain supported. Unknown object properties are ignored so additive optional fields remain compatible; they are also omitted when the parsed contract is serialized again. `EvidencePlan` and `EvidenceManifest` must declare contract version `1.0`, the only version currently emitted by the planner and manifest builder. Diagnostics use bounded generic messages: size failures throw `InvalidDataException`; stream reads normalize `IOException`, `UnauthorizedAccessException`, `NotSupportedException` and `ObjectDisposedException` to the same safe exception. Schema and JSON failures throw `JsonException`, invalid lower-limit arguments throw `ArgumentOutOfRangeException`, and stream cancellation propagates `OperationCanceledException`. Other exceptions from a caller-supplied custom stream propagate unchanged; the caller must handle or sanitize them at its own boundary.

`Serialize<T>` retains the existing canonical output, including numeric output for undefined enum values supplied by in-process callers. Such numeric enum input is rejected by deserialization; producing bytes does not grant evidence authority. The strict enum converter is used only when reading.

Schema and contract-collection checks run for every typed contract object that is deserialized, including values inside arrays, lists, dictionaries, or caller-defined wrapper objects. Wrapping an `EvidencePlan` or `EvidenceManifest` does not bypass its supported-version check. A raw `JsonElement` remains untyped JSON; deserializing it does not validate it as an Evidence contract or grant authority.

The span overload cannot limit memory already allocated by its caller. Prefer the counted stream overload at an untrusted file or stream boundary, and do not use a stream length check as a substitute for counted reading. These methods validate structure and format; they do not authenticate who supplied the JSON or make a plan trusted.

`EvidenceClaimKind.TargetedComplete`, `ReleaseComplete`, and `NoEvidenceRequired` describe structural manifest outcomes. They do not, by themselves, admit a protected pull-request or release gate. `ObservationOnly` is informational and never gate-eligible. A protected downstream consumer must call `EvidenceProtectedGate.Allows(...)` with current expected facts and plan/manifest artifacts obtained through its protected channel; see [runtime admission and gate evaluation](#runtime-admission-and-gate-evaluation). `NoEvidenceRequired` is valid only when the selected profile declares no resources, producers, or obligations.

## Claim rules

A complete claim is deliberately conservative:

- every selected producer must report `Passed`;
- every obligation's required producer and assertion must be present;
- producer results may not name undeclared producers or assertions;
- a release profile requires a registered CI-envelope verifier; and
- the manifest digest and plan digest must verify unchanged.

An unavailable capability, timeout, skipped producer, incomplete test profile, or failed assertion therefore produces `None`, not a partial success. This is how EvidenceHost distinguishes an observation from evidence that can mediate risk.

Required artifact slots are part of successful producer completion. A `Passed` result that omits a required slot makes the manifest `Invalid`. An unsuccessful producer may omit outputs it could not produce; valid absent or partial outputs leave its manifest `Incomplete`, with no closed obligation or gate eligibility. All returned metadata must still name declared slots and satisfy the byte, media type, digest and containment rules, regardless of outcome. Malformed partial outputs remain `Invalid`.

The public `EvidenceArtifactValidation.AreValid(producer, artifacts)` always checks every required slot, including when `artifacts` is null or empty. Use it to check complete artifact sets. The manifest builder and structural verifier share an internal overload that relaxes only required-slot presence for unsuccessful outcomes; it neither grants runtime admission nor skips metadata validation. Use the [runtime admission and gate evaluation APIs](#runtime-admission-and-gate-evaluation) to construct or consume a claim.

## Runtime admission and gate evaluation

Runtime claim construction is separate from JSON structure and from downstream authorization:

| API | Contract |
| --- | --- |
| `EvidenceExecutionMode` | Explicit `Trusted` or `Observation`; environment, branch, and Boolean defaults do not select it. |
| `EvidenceExecutionRequest(EvidenceExecutionMode Mode, string ControlChannel)` | Carries the requested mode and absolute Unix control socket supplied by the protected Linux launcher. It is request data, not proof or authority. The execution entry authenticates the independent worker and protected descriptor before admission. |
| `EvidenceAdmissionResult` | Publicly visible single-use runtime capability with no public constructor or JSON factory. Only shared protected admission can issue it. |
| `EvidenceManifestBuilder.Build(plan, results, admission, ...)` | Accepts a completed admission bound to the exact plan. The capability is consumed once, after owned work has stopped, artifacts have been verified, and cleanup has completed. |
| `EvidenceProtectedGateExpectation` | Required protected expected facts plus a final release opt-in that defaults to `false`; exact positional constructor shape and root-field mapping follow. |
| `EvidenceProtectedGate.Allows(EvidencePlan plan, EvidenceManifest? manifest, EvidenceProtectedGateExpectation expected)` | Throws for null `plan` or `expected`; returns `false` for a null manifest or failed check. Requires Trusted mode, passed verdict, `ValidatedNotAttested`, completed cleanup, no terminal failure or unmediated obligations, a matching plan policy digest, a valid assertion, exact expected facts, and valid structural bindings. It does not rerun work or create runtime admission. |

The public expectation record has this positional constructor. All string parameters are required; the final release
opt-in is the only parameter with a default:

```csharp
public sealed record EvidenceProtectedGateExpectation(
    string RunId,
    string BaseRevision,
    string SubjectRevision,
    string WorkflowIdentity,
    string PolicyDigest,
    string AcceptanceProofDigest,
    string OutputIdentity,
    string VerifierId,
    string VerifierVersion,
    string Provider,
    string CatalogueDigest,
    string CapabilitiesDigest,
    string AllocationPolicyDigest,
    string ToolRootIdentity,
    string SubjectRootIdentity,
    string OutputParentIdentity,
    bool AllowReleaseValidatedNotAttested = false);
```

The plan, manifest, and expectation are not their own provenance. Obtain the expectation, including all three root
identities, and collected artifacts through the consumer's protected gate channel; do not derive expected values
from uploaded subject files or an untrusted artifact pair. `ToolRootIdentity` identifies the protected tool root,
`SubjectRootIdentity` identifies the separately restricted subject root, and `OutputParentIdentity` identifies the
protected allocation parent of the output handle. The gate compares each expected root with the corresponding
assertion field. The positional record has 16 required string arguments and no constructor validation; `Allows`
checks exact values against the protected assertion and requires a nonblank output identity. Its other assertion
constraints include schema `1.0`, a nonblank run ID of at most 256 characters without controls, nonblank verifier,
provider, workflow, revision, and root identities of at most 256 characters, pairwise-distinct root identities,
64-character hexadecimal allocation, catalogue, capability, and acceptance-proof digests, and a nondefault verifier
time. A release gate must explicitly set `AllowReleaseValidatedNotAttested` to `true`; its default is `false`. This
opt-in accepts `ValidatedNotAttested`, which records registered-verifier validation and does not claim independent
attestation.

Observation admission requires an allowlisted dependency-free profile, its exact selected producer IDs, and an
exact internally registered producer class `(Kind, Version)` for every producer. The current class map admits only
`coverage@1.0.0`; selecting an ID or profile at the repository root cannot authorize an unknown or privileged
producer kind. The class map is internal and adds no public API.

The legacy `EvidenceManifestBuilder.Build(plan, results, bool observationOnly, EvidenceEnvelopeStatus, ...)`
overload always fails with `ASEVD400`; false/true Boolean values and status enums cannot construct authority. The
Aspire early-builder API and legacy `RunAsync(bool)` migration diagnostics are listed in the [migration reference](../../docs/evidence/evidencehost-migration.md#diagnostics-and-recovery).

### Shared lifecycle and output limits

Protected registrations may lower stage deadlines, but cannot raise these limits. Declared serial work, collection,
and cleanup reserves are admitted against one frozen allowance using a monotonic clock. Admission is conservative:
all stage caps plus collection and cleanup must fit before work starts. Stopping is included in cleanup and is not
counted twice. These are implementation ceilings, not a support claim: the production Trusted proof allowlist is
empty, and the production Aspire host rejects configured application factories with `ASEVD407`. See the migration
reference's [support status](../../docs/evidence/evidencehost-migration.md#support-status).

A callback, tracked write, or collector must settle **before** its monotonic deadline. Completion exactly at
the deadline is a timeout, even if the timer notification has been delayed. Stop acknowledgements and disposal
are subject to the same elapsed-time check before cleanup or collection may advance. A late disposer or stop
acknowledgement takes the fatal termination path; it cannot restore eligibility or permit further finalization.

| Limit | Maximum |
| --- | ---: |
| Admission | 30 seconds |
| Host/resource start | 120 seconds |
| Final collection | 60 seconds |
| Cleanup, including stop | 10 minutes |
| Stop grace within cleanup | 30 seconds |
| Aggregate artifacts and restricted reports | 256 MiB |
| Process/reporter output bytes received per run | 16 MiB |
| Retained output prefix | 1 MiB per stream |

The restricted coverage path additionally limits each Cobertura report to 20 MiB and the report count to 64; the
same 256 MiB aggregate bounds apply. See the [restricted coverage implementation and packaging notes](../../docs/evidence/evidencehost-migration.md#restricted-coverage-path-and-packaging).

## Pitfalls

- Do not construct a `NoEvidenceRequired` result merely because a local run omitted tests. It is a policy outcome, not a convenience override.
- Do not treat a coverage collection artifact as a gate pass unless its producer has closed the assertion declared by the selected policy.
- Do not claim independent attestation in v1. An accepted envelope is represented as `ValidatedNotAttested`.
- Do not treat `EvidenceManifestBuilder.Verify(...)` or `appsurface evidence verify` as provenance checks. They detect inconsistent or edited fields by recomputing structural digests only; gates must obtain plan, manifest, and expected facts through a protected CI channel.
- Do not finalize while a child process, output pump, callback, or writer may still be active. An unconfirmed stop is a fatal path: no final manifest or summary is written, and recovery starts only in a fresh protected allocation after the supervisor confirms owned exit.

Read next: the [planner README](../ForgeTrust.AppSurface.Evidence.Planner/README.md), [Aspire lifecycle README](../ForgeTrust.AppSurface.Evidence.Aspire/README.md), [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md), and [migration/support guide](../../docs/evidence/evidencehost-migration.md).
