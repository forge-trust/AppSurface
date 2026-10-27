# Protected parent semantic gate

This validation-only .NET executable reconstructs a plan with the actual [EvidencePlanner](../../../Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidencePlanner.cs) before comparing the uploaded plan and evaluating [EvidenceProtectedGate](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceProtectedGate.cs). Its assembly has no friend access and references Contracts and Planner only. It creates no admission, loads no subject assembly and does not enroll an accepted provider or proof.

## Transport and ordering

Supply one UTF-8 JSON envelope on standard input and close EOF. No command arguments are accepted. The parent must impose an independent process deadline in addition to the reader's 30-second cancellation deadline. The complete envelope is counted against the existing 20 MiB canonical-input ceiling. Each of the four document byte arrays is nonempty and at most 1 MiB. These are lower fixture transport limits, not changes to production limits.

The required fields are `Schema` (exact `issue779-protected-parent-gate-v1`), `PolicyBytes`, `PolicySha256`, `DiffBytes`, `DiffSha256`, `PlanBytes`, `ManifestBytes`, `Expected` and `Artifacts`. Byte arrays use normal JSON base64. `Expected` has the public `EvidenceProtectedGateExpectation` shape. `Artifacts` contains objects with required `ProducerId`, `RelativePath`, and base64 `Bytes`; there may be at most 64, each at most 1 MiB and within the envelope ceiling. Comparison keys are never opened as paths. Empty artifact bytes are supported when permitted by the selected declaration.

The protected parent supplies the original policy, diff and their independently expected raw hashes. The uploaded archive supplies only plan and manifest after [provenance verification](../README.md). The executable checks the raw policy/diff hashes, uses the existing unified-diff reader and planner, compares the complete typed reconstructed plan, checks every manifest artifact's actual bytes/length/SHA and exact body set, and finally invokes the public gate. Missing/duplicate/extra bodies reject; hashes in a manifest cannot stand in for actual bytes.

| Exit | Exact diagnostic | Meaning |
| --- | --- | --- |
| 0 | stdout `EVIDENCE_PARENT_GATE_ALLOWED` | Supplied semantic inputs satisfy the public gate. |
| 1 | stderr `EVIDENCE_PARENT_GATE_DENIED` | Plan, artifacts or claim do not match. |
| 2 | stderr `EVIDENCE_PARENT_GATE_INPUT_REJECTED` | Bounded input, hash, planning, encoding or schema rejection. |
| 3 | stderr `EVIDENCE_PARENT_GATE_FAILED` | Other nonfatal consumer failure. |

Each diagnostic has one trailing newline. Raw bytes, values, paths and exception messages never enter output. Runtime-fatal failures remain unsuccessful. Duplicate/case-colliding names reject through the canonical reader; additive properties retain its existing compatibility behavior.

## Authority and prerequisites

The transport does not authenticate any caller. A malicious uploader can create coherent Trusted-shaped public JSON and supply matching expectations; the parser must not be treated as an origin verifier. The parent must independently pin this executable and dependencies, resolve the protected policy and exact diff, authenticate the producing workflow/run/artifact, verify retained root/capability/proof facts, and keep all these inputs outside subject control. Detached artifact arrays do not prove custody or that a test actually ran.

The production request parser remains Observation-only and the accepted-consumer registry remains closed. These changes do not enable Trusted, establish protected-branch review, or substitute for the real consumer/fork and platform proofs in the [acceptance plan](../../../docs/plans/issue-779-evidencehost-test-plan.md).

## Local validation

Run locked restore, build and the real-process controls:

```sh
dotnet restore .github/issue779-protected-consumer/semantic-gate/ProtectedParentGate.csproj --locked-mode
dotnet build .github/issue779-protected-consumer/semantic-gate/ProtectedParentGate.csproj -c Release --no-restore -p:UseSharedCompilation=false
python3 -B .github/issue779-protected-consumer/test_semantic_gate.py --dotnet "$(command -v dotnet)" --consumer .github/issue779-protected-consumer/semantic-gate/bin/Release/net10.0/EvidenceHost.ProtectedParentGate.dll
```

The tests use synthetic public contract JSON and actual child processes. They exercise real planner reconstruction, coherent uploaded substitutions, independent expected-fact mismatches, empty/nonempty artifact bytes and hostile input. The positive fixture includes the public producer default `CoverageArtifactRetention: "managed"` before hashing the policy and plan. A separate negative fixture confirms that omitting this serialized default produces a plan mismatch even when the older shape is coherently rehashed. Their positive is explicitly a public-API semantic control, not a protected Trusted run.
