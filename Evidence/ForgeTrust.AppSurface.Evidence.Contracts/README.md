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

Local path or text-diff planning retains contract version `1.0`. Revision-bound pull-request planning emits version `2.0` only when supplied a complete base commit ID, complete head commit ID, and a source diff that byte-for-byte matches the fixed-options Git diff for that pair. The v2 plan and manifest carry `BaseRevision`, `HeadRevision`, `SourceDiffDigest`, and `NameStatusDigest`. `DiffDigest` continues to bind the normalized path inventory; it is distinct from the exact source-diff digest. A trusted controller can also record `EvidencePullRequestRunIdentity` in the plan and manifest: numeric repository/head repository IDs, PR number, target branch, run ID, and attempt. A local rehearsal omits it; the final verifier must compare it with authoritative GitHub data. Optional v2 fields are omitted from v1 canonical JSON, preserving existing v1 digests. A v1 local manifest may still describe its local claim, but the [AppSurface CI gate design](../../docs/designs/issue-777-policy-driven-ci-evidence-gate.md) does not accept v1 as authorization.

`EvidenceRevisionPlanBuilder.ResolveForPullRequest(...)` requires the conservative mixed-profile policy and rejects a release-only profile on a PR. `EvidenceRevisionPlanBuilder.VerifyAsync(...)` independently regenerates the Git diff/status and policy selection from a trusted object store and trusted base-owned policy. `EvidenceManifestBuilder.Verify(...)` checks internal plan/manifest consistency, including v2 field shape and equality, but cannot authenticate the GitHub event, trusted policy source, or subject job. The final CI verifier must check those external facts and current PR base/head before issuing a gate verdict.

| Type family | Purpose |
| --- | --- |
| `EvidencePolicy`, `EvidenceProfile`, `EvidencePolicyRule` | Checked-in change-risk policy and explicitly selected profile. |
| `EvidenceResourceDeclaration`, `EvidenceProducerDeclaration`, `EvidenceObligation` | Closed declaration of resource readiness, producer assertions/artifacts, and the risk obligation each assertion may close. |
| `EvidencePlan`, `NormalizedDiffPath` | Deterministic resolved input. The plan binds policy identity, diff, selected profile, and matched rules. |
| `EvidenceProducerResult`, `EvidenceManifest` | Terminal producer outcomes and the resulting gate claim. |
| `EvidenceCanonicalJson`, `EvidenceDigest`, `EvidenceManifestBuilder` | Canonical serialization, digesting, claim calculation, and manifest verification. |
| `EvidenceArtifactWriter`, `EvidenceArtifactValidation` | Bounded artifact copying, streamed integrity verification, and declaration/path validation. |
| `EvidenceNoFollowArtifactExtractor` | Linux descriptor-relative extraction of explicitly selected untrusted scratch files into declared artifact slots. |

## Writing and verifying artifacts

`EvidenceArtifactWriter.WriteAsync(logicalName, relativePath, ReadOnlyMemory<byte>, cancellationToken)` remains available for artifacts already held in memory. Producers handling larger artifacts should use the stream overload:

```csharp
await using var input = File.OpenRead(sourcePath);
var artifact = await context.Artifacts!.WriteAsync(
    "report",
    "coverage/report.json",
    input,
    declaredLengthBytes,
    cancellationToken);
```

The stream overload requires a readable stream and an explicit, non-negative declared length. It checks that length against the selected slot's `MaximumBytes` and the remaining per-producer v1 allowance before reading. The v1 producer allowance is 256 MiB total across all artifact slots. The stream must yield exactly the declared number of bytes: an early end or any extra byte fails with `InvalidDataException`; an over-limit declaration fails before the stream is consumed. SHA-256 is computed as bytes are copied through a fixed-size buffer, so memory use does not grow with artifact size. The writer does not seek or rewind the input, and it does not dispose it; callers should pass a stream positioned at the artifact's first byte. A short stream is read through EOF, while an overlong stream is read through at most one byte beyond the declaration before failing. Cancellation propagates and a failed or cancelled stream write releases its reservation and removes its partial destination when possible.

After writing, `VerifyWrittenArtifactsAsync(cancellationToken)` opens each destination as a stream and recomputes its length and digest with the same bounded-buffer approach. It returns `false` if an artifact is missing, exceeds its slot or producer limit, or no longer matches the metadata; cancellation propagates. Verification detects later changes to the copied file, while the returned `EvidenceArtifactResult` contains only the declared name, normalized relative path, media type, byte length, and digest.

The writer's destination handling is path-based beneath a controlled evidence root. Neither the stream overload nor `VerifyWrittenArtifactsAsync` is a no-follow, descriptor-relative extraction primitive. Pass only separately validated trusted streams and use a dedicated no-follow extraction boundary for untrusted scratch paths; a path that appears contained is not proof that symlinks or concurrent path changes were excluded. Prefer the stream overload when the artifact is not already in memory, and use the memory overload for small content that the producer already owns as a byte buffer.

For untrusted scratch output on Linux x64 or arm64, use `EvidenceNoFollowArtifactExtractor.ExtractAsync` with an already-open handle to the trusted scratch directory, an `EvidenceArtifactWriter` configured for the declared producer, and an explicit list of `EvidenceNoFollowArtifact` source/destination mappings. The extractor selects the no-follow open flag for the running architecture, then opens each source relative to that handle with Linux `openat2` no-symlink and beneath-root constraints, verifies regular-file identity and a single hard link with `statx`, streams it into the declared slot, and checks for mutation. It does not discover files or accept a subject-provided artifact list as authority. Source and destination paths must be canonical slash-separated relative paths without empty, `.` or `..` components; duplicate mappings are rejected.

By default one extraction accepts at most 64 files and 256 MiB in two minutes, in addition to each slot's own byte limit. `EvidenceNoFollowArtifactExtractionLimits` can lower those limits but cannot raise them. Unsupported operating systems, architectures, or kernels fail closed; there is no path-based fallback. Extraction is sequential, not transactional: an error on a later file does not undo already written artifacts, so a caller must treat the whole producer result as failed and clean its artifact root before any claim. This primitive validates scratch-to-writer copying; the [trusted PR verifier](../ForgeTrust.AppSurface.Evidence.Planner/README.md) must separately authenticate the final handoff artifact bytes and job provenance.

`EvidenceClaimKind.TargetedComplete` is eligible for a pull-request gate; `ReleaseComplete` is eligible only for a release gate and requires `ValidatedNotAttested` envelope status. `ObservationOnly` is deliberately informative, never gate-eligible. `NoEvidenceRequired` is valid only when the selected profile declares no resources, producers, or obligations.

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
- Do not interpret `2.0` as a runtime sandbox claim. Revision binding says which commit pair was planned; a trusted controller and verifier must still establish job provenance, isolation, cleanup, and current PR identity. The built-in `appsurface evidence run` emits `ObservationOnly` for v2 inputs because that local command cannot establish those conditions.

Read next: the [planner README](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Planner/README.md), the [Aspire lifecycle README](https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Aspire/README.md), and the [EvidenceHost cookbook](https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md).
