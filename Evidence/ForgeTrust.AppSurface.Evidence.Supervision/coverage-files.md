# Terminal coverage Observation files

[`EvidenceCoverageObservationFiles`](EvidenceCoverageObservationFiles.cs) is the data-only terminal verifier for a
planner-resolved checkpoint-two coverage Observation. Its internal API accepts the expected `EvidencePlan`, canonical
plan, manifest and summary bytes, and an `IReadOnlyDictionary<string, ReadOnlyMemory<byte>>` of artifact bytes. The
artifact dictionary has exactly one key: `<producer-id>/merged/coverage.cobertura.xml`. This is the physical key retained by
the root custody reader. The manifest's `EvidenceArtifactResult.RelativePath` remains `merged/coverage.cobertura.xml`, excluding
the producer ID. The verifier returns the detached `EvidenceManifest` or the fixed `ASEVD410` diagnostic.

The verifier checks input bounds before copying, then requires the expected plan to contain its actual policy snapshot
and changed paths. It validates the complete policy with [`EvidencePlanner.ValidatePolicy`](../ForgeTrust.AppSurface.Evidence.Planner/EvidencePlanner.cs),
re-resolves the plan with the existing planner, and compares the canonical expected, resolved and supplied plan bytes.
The selected profile must be targeted, non-conservative, resource-free, and contain exactly one compatible producer:
kind `coverage`, version `1.0.0`, assertion `appsurface/coverage/behavioral-patch@1`, no required resources, and one
required `coverage-report` slot at `merged`, media type `application/xml`, with the existing 20 MiB declaration limit.

The terminal manifest must be Observation / Passed / ObservationOnly / Informational / NotRequired, have no envelope
assertion or resource results, and have one Passed producer result for the selected producer with exactly the declared
assertion and artifact. The physical artifact key includes the producer ID and `coverage.cobertura.xml`; the manifest-relative
path is exactly `merged/coverage.cobertura.xml`. The logical name, media type, declared bounds, actual copied byte length and
SHA-256 must all agree. Obligations and manifest digest are checked by
[`EvidenceManifestBuilder.Verify`](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs), so this verifier
does not reproduce the builder's closure rules. All supplied timings must be nonnegative; cleanup must be complete
with no diagnostic or terminal failure. The current CLI reports cleanup state while leaving wall-clock metric fields
at zero, so the producer result's independent elapsed value is not compared with those unreported fields. When a
nonzero total is supplied, the reported planning, producer and cleanup stages must fit within it. The canonical summary
has exactly seven fields and fixes `Procedure` to `registered-protected-producer` and `SandboxAttestation` to `false`.

This API verifies consistency of supplied data only. It does not authenticate a worker or producer, verify native root
custody, create admission or a lease, validate the Cobertura XML, evaluate its numeric thresholds, or establish a passing
coverage gate. The registered restricted producer owns report validation and numeric gate behavior; the native root
must establish execution and custody separately before relying on the files. A structurally valid manifest must never
be described as protected execution proof.

**Pitfalls:** pass the complete planner result, not a plan assembled from digests; provide artifact bytes under the exact
root-relative path and preserve the manifest-relative path separately; preserve the original caller cancellation token;
and keep XML parsing and numeric gate evaluation in the registered restricted producer. This verifier never renews a
deadline or converts a root request into admission.

The fixed published basename is `coverage.cobertura.xml`, matching the [shared producer artifact source](../ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs). ReportGenerator's intermediate filename is not the custody contract. A directory containing `Cobertura.xml` instead is still rejected. [Native run38864316374](https://github.com/forge-trust/AppSurface/actions/runs/38864316374) completed producer sealing but exposed the earlier mismatch at the final merged inventory. Inventory failure checkpoints are set after owner rechecks so that a rejected name cannot inherit an unrelated owner-identity label.

## Compiled application terminal files

`VerifyApplication` is a separate pure-data entry for the root's independently selected application plan. It
requires exactly one `aspire_health` resource, the coverage producer's exact dependency on it, one Ready result
with nonnegative elapsed time and no failure diagnostic, and readiness time included in nonnegative cumulative
metrics. All original canonical policy/plan/manifest/summary, artifact role/length/SHA, cleanup, obligation and
informational-only checks remain. `Verify` retains the strict resource-free checkpoint-two contract. Neither
method issues admission, a registered application, filesystem custody, numeric coverage proof or gate authority.

The [actual custody transfer](LinuxRunWorkspace.Custody.cs) selects this verifier only for an application request
already resolved by the [compiled root owner](application-execution.md). It re-resolves the same protected inputs
at both inventory selection and file verification, and still requires the original server/worker completion,
retained handles, physical joins, sealed tree checks, account release and final deadline checks. A successful
data-only verifier test cannot replace those native requirements.
