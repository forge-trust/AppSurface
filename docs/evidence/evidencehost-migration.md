# EvidenceHost migration and support reference

This page is the canonical #779 migration reference for the shared admission, protected worker, claim, and downstream gate contracts. For the short adoption path, start with the [EvidenceHost guide](../../start-here/evidencehost.md); for policy recipes, see the [cookbook](../../guides/evidencehost-cookbook.md). The [coordinated release links guide](../../releases/coordinated-release-links.md#evidencehost-migration) points package and release readers here.

## Support status

As of October 2, 2026, the supported consumer workflow remains **policy planning and structural verification**. The production Trusted proof allowlist is empty, so Trusted PR and release admission is closed on every provider and platform until the full consumer, production-broker, and CI proof set is accepted. The [consumer acceptance record](./issue779-consumer-acceptance.md) is the source of truth for those proof entries.

Linux x86_64 systemd/cgroup mechanism behavior was observed in CI. That mechanism run did not validate the updated production broker or complete consumer integration, so it does not admit Trusted execution or a gate. Windows and macOS worker mechanisms are unavailable. Do not interpret the Linux mechanism observation as platform support, and do not treat this document or local tests as proof.

The restricted coverage adapter is implemented: it launches the declared subject test through the broker, collects broker-retained reports after child and output-pump exit, and runs the protected merge and numeric gate procedure. The implementation still lacks full production-broker/consumer CI proof. Use the independent [`appsurface coverage run` and `coverage gate`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) workflow for numeric enforcement today. The adapter's implementation status does not change the closed EvidenceHost gate status.

`doctor` is planning-only. It resolves policy and local prerequisites, marks protected execution facts `unverified`, and never invokes the protected verifier or arms a supervisor. `explain` resolves explicit paths or a diff into a plan. `verify` checks structure and digest binding only; it does not authenticate provenance. An environment flag, uploaded hash, or `ValidatedNotAttested` enum cannot substitute for an admitted runtime capability or protected gate facts.

## Planning workflow

These commands are executable planning examples. They do not start a producer or admit a gate:

```bash
appsurface evidence init --sample
appsurface evidence doctor --policy .appsurface/evidence/evidence.policy.json --path src/Orders/SubmitOrder.cs
appsurface evidence explain --policy .appsurface/evidence/evidence.policy.json --path src/Orders/SubmitOrder.cs
```

Review the resolved profile, its explicit producers and resources, and each obligation before changing policy. Keep the empty `no-evidence` profile restricted to reviewed low-risk paths; unknown paths should resolve to a conservative non-empty profile. The [cookbook](../../guides/evidencehost-cookbook.md) has planning recipes for documentation-only and coverage changes.

Do not run `appsurface evidence run` as a PR or release gate in the current proof state. The protected command requires an explicit `--mode trusted` or `--mode observation` and a `--control` Unix socket delivered by an independently armed Linux launcher. `--observation-only` is a compatibility alias for Observation, not an admission shortcut. The current worker flow rejects missing or conflicting modes, unsupported platforms, and absent supervision before callbacks or subject execution.

## API migration

| Previous call or assumption | Current contract |
| --- | --- |
| `EvidenceManifestBuilder.Build(plan, results, observationOnly, envelopeStatus, ...)` treats Boolean/status fields as authority. | The compatibility overload always fails with `ASEVD400`. Use the overload that consumes a completed `EvidenceAdmissionResult` issued by shared protected admission. |
| Aspire `RunAsync()` or `RunAsync(bool)` selects a mode or grants supervision based on the Boolean. | Omission or `false` maps to `ASEVD401`. `true` selects only Observation and maps to `ASEVD402` when no independent supervisor is present. The Boolean never supplies supervisor authority. `EvidenceExecutionRequest` is the migration target, but the current production Aspire factory path is unsupported. |
| `EvidenceAspireApplication.StartAsync(existingBuilder)` is safe because `Build` has not run yet. | The public prebuilt-builder entry fails with `ASEVD400`; consumer configuration may already have run. A factory is the required ordering for a future supported path, but the current production host rejects factory-based startup. Do not rely on application startup until implementation and proof are complete. |
| A public envelope assertion, status enum, or environment variable can mint a trusted claim. | `EvidenceAdmissionResult` has no public constructor or JSON factory. Only the shared admission boundary issues it after validating protected inputs and supervision. |
| A valid request record or `verify` result proves provenance or guarantees that an application factory will run. | `EvidenceExecutionRequest` is a public record containing `(EvidenceExecutionMode Mode, string ControlChannel)`. It carries an explicit mode and absolute Unix socket path; it grants no authority. Any future supported execution entry must authenticate the control peer, worker identity, descriptor, proof, and allocation before callbacks. The current Aspire host rejects its application-factory path, so the request shape does not mean startup or callback invocation is available. `verify` remains structural only. |

`EvidenceAdmissionResult` is bound to one immutable plan and one run. The host activates it only after admission and fresh output allocation; it remains active while owned work runs. Complete it only after owned work has stopped, artifacts have been verified, and cleanup has succeeded. `EvidenceManifestBuilder.Build(plan, results, admission, ...)` then compares the entire plan snapshot and consumes the capability once. A different plan, incomplete lifecycle, or second consumption fails closed. See the [Contracts API reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#runtime-admission-and-gate-evaluation).

For Aspire, factory ordering is a required design constraint, not a currently working production path. Do not construct an `IDistributedApplicationBuilder` before admission or pass a preconfigured object into the host. The current host rejects the application-factory path, so it does not promise builder creation, application startup, resource readiness, or producer execution. Once implementation is complete, the factory must be invoked only while the shared capability is active, with readiness and producer callbacks inside the admitted bounded lifecycle.

## Restricted coverage path and packaging

The protected CLI adapter uses a fixed `dotnet test` invocation against the solution named by the protected worker descriptor. The subject process runs under the independent restricted broker; it does not select its executable, working directory, or artifact root. The host collects only broker-retained Cobertura reports under the fresh tokenized result directory after the child and output pumps have exited.

The adapter validates report paths, identities, XML, per-report size, report count, aggregate size, and declared artifact slots. It stages accepted reports under generated safe names, invokes the shared `CoverageMergeWorkflow`, and evaluates the exact overall and optional patch thresholds bound into the resolved plan. It writes only artifacts named in the producer's declaration and returns the declared coverage assertion only after the protected procedure and numeric gate pass. A passing `dotnet test` exit or a “tests passed” log line is not enough.

The protected merge launches `ReportGenerator.dll` only from `reportgenerator/net10.0/` beneath the authenticated tool root; it has no package-cache fallback. The AppSurface CLI NuGet tool-pack path includes the ReportGenerator payload. A normal `dotnet publish` output does not include this protected tool-root payload by default. A missing reporter fails with `ASEVD404`. This packaging fact does not make the production Aspire factory path available or admit a gate; the updated broker and consumer proof remain unverified.

## Protected gate and artifacts

`EvidenceProtectedGateExpectation` contains the independently obtained expected:

- `RunId`: exact current run and attempt;
- `BaseRevision`: immutable protected tool/policy revision;
- `SubjectRevision`: exact tested revision;
- `WorkflowIdentity`: immutable protected workflow;
- `PolicyDigest`: protected policy digest, which must also match the plan;
- `AcceptanceProofDigest`: current successful full consumer/platform proof digest;
- `OutputIdentity`: actual fresh output-handle identity;
- `VerifierId` and `VerifierVersion`: exact protected verifier registration;
- `Provider`: accepted CI provider;
- `CatalogueDigest`: protected producer/resource catalogue digest;
- `CapabilitiesDigest`: digest of accepted secret-free capabilities;
- `AllocationPolicyDigest`: protected allocation-policy digest;
- `ToolRootIdentity`: protected tool-root identity;
- `SubjectRootIdentity`: separately restricted subject-root identity; and
- `OutputParentIdentity`: protected allocation parent of the output handle.

These are the 16 required string parameters of the public positional record constructor; none has a default. The only
optional parameter is `AllowReleaseValidatedNotAttested`, which defaults to `false`. The record constructor does not
authenticate or normalize these values. Obtain every expected value and the plan/manifest artifacts through the
consumer's protected gate channel; never derive expected facts from uploaded subject files. The gate requires each
root identity to equal its corresponding assertion field.

The public gate entry is `bool EvidenceProtectedGate.Allows(EvidencePlan plan, EvidenceManifest? manifest, EvidenceProtectedGateExpectation expected)`. It throws `ArgumentNullException` for a null `plan` or `expected`, and returns `false` for a null manifest or any failed check. It requires Trusted mode, a passed execution verdict, `ValidatedNotAttested`, completed cleanup, no terminal failure, no unmediated obligations, a matching plan policy digest, a valid assertion for the expected run, exact equality for the expected assertion facts, a nonblank output identity, and a valid plan/manifest structural binding. `TargetedComplete` and `NoEvidenceRequired` can pass only with pull-request eligibility; `ReleaseComplete` additionally requires release eligibility and an explicit `AllowReleaseValidatedNotAttested: true`. The gate does not rerun a producer, mint runtime admission, prove application correctness, or independently attest a sandbox.

Obtain the expectation, plan, and manifest from the consumer's protected gate channel. A plan/manifest pair uploaded by the subject, even with matching SHA-256 values, cannot authenticate itself. `EvidenceManifestBuilder.Verify(...)` and `appsurface evidence verify` detect structural edits and inconsistent digests only. An absent, stale, mismatched, or quarantined artifact must make the downstream gate fail closed.

## Lifecycle and quota reference

Protected registration may reduce stage deadlines but may not raise the shared caps. The job allowance is frozen at admission and measured monotonically so wall-clock changes cannot extend it. Before execution, the host conservatively requires all declared serial stage durations plus collection and cleanup reserves to fit the remaining allowance. A stage starts only while its full deadline and later reserves still fit.

| Stage or resource | Maximum |
| --- | ---: |
| Admission | 30 seconds |
| Host/resource start | 120 seconds |
| Final collection | 60 seconds |
| Cleanup, including stop | 10 minutes |
| Stop grace within cleanup | 30 seconds |
| Aggregate artifact bytes per run | 256 MiB |
| Process/reporter bytes received per run | 16 MiB |
| Retained prefix per output stream | 1 MiB |
| Restricted Cobertura report | 20 MiB each; at most 64 reports |

Restricted reports and emitted artifacts share the aggregate 256 MiB ceiling. Process and ReportGenerator output share the received-byte quota. Retaining only an output prefix does not reduce the received-byte accounting.

## Diagnostics and recovery

The following codes are emitted by the current shared admission, lifecycle, and restricted coverage sources. Codes `ASEVD412` through `ASEVD419` are not assigned by this implementation; this table does not reserve meanings for them.

| Code | Meaning | Recovery |
| --- | --- | --- |
| `ASEVD400` | Legacy `EvidenceManifestBuilder.Build` Boolean/status claim construction or the public prebuilt Aspire builder entry cannot grant admission. | Migrate claim construction to a completed admission capability. Treat explicit request/factory startup as unavailable until the current Aspire host implementation is completed. |
| `ASEVD401` | Explicit mode is absent, invalid, conflicting with `--observation-only`, or mismatched with the protected worker. Aspire legacy `RunAsync()` omitted/false maps here because no mode was selected. | A future supported caller must pass exactly `--mode trusted` or `--mode observation`; remove conflicting aliases. The current production Aspire factory path is unsupported. |
| `ASEVD402` | Independent worker/control channel is absent, unauthenticated, stale, malformed, mismatched, or unavailable on this platform. Aspire legacy `RunAsync(true)` selects only Observation and reaches this diagnostic when no independent supervisor is present; the Boolean does not provide supervision. | A future supported caller needs an independently armed Linux launcher. Windows, macOS, local in-process execution, and unrelated long-lived hosts are not supported worker paths. |
| `ASEVD403` | Protected policy, diff, plan, or restricted coverage input does not match the admitted descriptor. | Re-resolve from protected base inputs and use the exact retained planning snapshot. Do not retry with subject-selected paths. |
| `ASEVD404` | Protected declaration catalogue is mismatched, or the packaged ReportGenerator dependency is missing. | Align protected declarations and tool root with the admitted package; for coverage, include the tool-pack payload. |
| `ASEVD405` | Requested protected secrets or sensitive projections are unsupported. | Remove the secret/sensitive projection from this evidence profile; do not place credentials in the subject worker. |
| `ASEVD406` | Observation profile is not allowlisted and dependency-free, a selected producer ID is not authorized, or a producer lacks an exact protected `(Kind, Version)` capability class. Selecting an ID/profile at the repository root does not authorize an unknown or privileged kind. | Use an allowlisted dependency-free profile with the exact selected producer IDs and registered class. The current class map admits only `coverage@1.0.0`; there is no public class-map API. |
| `ASEVD407` | Trusted consumer acceptance or registered protected verifier is unavailable. This is the current Trusted fail-closed outcome until full proof is accepted. | Keep the gate closed and complete the proof requirements in the [acceptance record](./issue779-consumer-acceptance.md). |
| `ASEVD408` | Registered verifier rejected the protected execution facts. | Correct the protected workflow/revision facts or verifier registration; do not replace them with environment flags. |
| `ASEVD409` | Fresh protected output allocation or activation failed. | Quarantine the attempted allocation and start a later run only with a new protected allocation after owned exit is confirmed. |
| `ASEVD410` | Admission/lifecycle closed, required reserve expired, collection or cleanup failed, or owned process/pump exit was not confirmed. | If exit is unconfirmed, stop publication and fail-stop the host. Do not write a final manifest or summary. Confirm termination before creating a fresh output root for a retry. |
| `ASEVD411` | Manifest construction lacks a completed admission for the exact plan, or the single-use capability was already consumed. | Finish and verify the admitted lifecycle, then build once with the same plan snapshot and capability. |
| `ASEVD420` | Process-output, artifact, or restricted-report count/size quota was exceeded or closed. | Reduce the declared workload/output or start a new run after confirmed cleanup; never raise the protected caps. |
| `ASEVD421` | Declared work plus collection and cleanup reserves exceed the remaining protected job allowance. | Reduce declared stage deadlines/work or use a job with enough protected allowance before execution begins. |

On a recoverable producer failure after owned work has exited, the host may write an ineligible manifest with `ClaimKind.None` after artifact checks and cleanup. On fatal stop or unconfirmed child/pump exit, it must not write a manifest or summary, and it must not automatically resume. Quarantine the output and allow the independent supervisor to confirm the old worker is gone before a retry receives a new output root. Never treat process termination alone as evidence that collected files are authentic.

## Decision and pitfalls

- Use EvidenceHost planning to explain which reviewed policy profile and obligations apply before choosing a test path.
- Use the existing coverage workflow when the immediate need is numeric local CI enforcement; it has no EvidenceHost admission dependency.
- Keep Aspire out of normal application startup. Its current factory path is rejected; defer builder construction until admission only after that implementation is completed and proved.
- Treat `TargetedComplete`, `ReleaseComplete`, and `NoEvidenceRequired` as structural manifest outcomes. They are not gate authority without current protected expected facts and the protected artifacts.
- Treat Observation as non-gating and supervised. `false`, `true`, a status enum, a verifier-shaped record, and environment variables cannot select or mint admission.
- Do not interpret `doctor: ready` as proof. It checks planning prerequisites while the protected verifier/worker remains `unverified`.
- Do not use `verify` as a provenance check. It does not establish who produced a plan or manifest.
- Do not publish after cleanup failure or while any owned work may still be active. Fatal recovery is supervisor-confirmed exit followed by a fresh allocation, with no automatic retry.
- Do not claim that the current restricted coverage implementation has production support until the updated broker and complete consumer/CI proof pass.

## Canonical references

- [EvidenceHost start page](../../start-here/evidencehost.md)
- [EvidenceHost cookbook](../../guides/evidencehost-cookbook.md)
- [Contracts and lifecycle limits](../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md)
- [Evidence CLI package reference](../../Evidence/ForgeTrust.AppSurface.Evidence.Cli/README.md)
- [AppSurface CLI command reference](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-evidence)
- [Consumer acceptance record](./issue779-consumer-acceptance.md)
- [Coverage gate inventory](./issue779-coverage-gate.md)
- [Coordinated release pointer](../../releases/current.md)
