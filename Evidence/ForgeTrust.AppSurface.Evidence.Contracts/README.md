# ForgeTrust.AppSurface.Evidence.Contracts

`ForgeTrust.AppSurface.Evidence.Contracts` is the stable vocabulary for a CI run that says what changed, which evidence was required, what actually ran, and whether a downstream gate may consume the result.

Start with the [EvidenceHost guide](https://github.com/forge-trust/AppSurface/blob/main/start-here/evidencehost.md) before installing a package. Use this package directly only when you are authoring a consumer-owned producer, policy tool, or gate integration. It starts no process, discovers no test code, provisions no resources, and sends no telemetry.

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

`EvidenceClaimKind.TargetedComplete` is eligible for a pull-request gate; `ReleaseComplete` is eligible only for a release gate and requires `ValidatedNotAttested` envelope status. `ObservationOnly` is deliberately informative, never gate-eligible. `NoEvidenceRequired` is valid only when the selected profile declares no resources, producers, or obligations.

## Streaming artifact writes

Use [`EvidenceArtifactWriter.WriteAsync`](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs) with a `Stream` when a producer has an artifact that may be large or is supplied by a non-seekable process stream. The writer copies in bounded chunks, computes the lower-case SHA-256 digest as bytes arrive, and enforces both the slot's `MaximumBytes` and the shared 256 MiB producer budget before accepting each chunk. The input stream remains owned by the caller and is read to end; the writer exposes only `EvidenceArtifactResult` metadata.

The existing `ReadOnlyMemory<byte>` overload remains useful for already-buffered small values. Use `VerifyWrittenArtifactsAsync` after producer work or before returning a result when the output directory may have been touched by another process. Verification also streams each file and returns `false` for missing, inaccessible, truncated, or tampered artifacts. Cancellation and copy errors remove the partial destination and release the logical-name, destination, slot, and aggregate reservations so the producer can retry safely.

Avoid seeking or rewinding the input stream: streaming writes consume it exactly once. Do not close the input stream as part of producer cleanup unless the producer owns it. A failed write cannot be resumed from its partial file; retry by supplying a fresh stream, because the partial destination is deleted before the reservation is released.

## Claim rules

A complete claim is deliberately conservative:

- every selected producer must report `Passed`;
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
