# Coverage execution and final custody

The internal [coverage plan resolver](coverage-plan.md) selects the existing single declared coverage procedure from actual protected policy and diff bytes. [LinuxCoverageControlOperations](LinuxCoverageControlOperations.cs) consumes one attempt before validating the worker's request. It compares the runtime, source root, solution and complete fixed test argv; a client supplies only an opaque results token beneath the root-selected producer results directory. A rejected or interrupted attempt cannot be retried.

## Native inputs and workspace

[EvidenceProtectedLaunchInput](EvidenceProtectedLaunchInput.cs) retains the managed deployment, a separate SDK deployment, the root-selected subject, and the protected `tool/nuget` package tree. `PrepareCoverageInputs` inventories these bytes and requires exactly one SDK `dotnet.dll` plus packaged `reportgenerator/net10.0/ReportGenerator.dll` before any subject MSBuild runs. This performs no project evaluation. The runtime and tool trees must be disjoint. The workspace owner copies from retained source descriptors into the fresh subject-owned build tree; mutable `bin` and `obj` output never modifies the protected source.

[LinuxProducerWorkspace](LinuxProducerWorkspace.cs) retains the fresh account-associated source, scratch and result directories. The results parent is root owned and nonwritable to the subject. Its selected token directory is `0700` under the actual subject UID/GID. `RequireOwnedBy(owner, accounts, token)` checks the original native objects; `RequireResultsToken(token, cancellationToken)` returns the selected path and its actual metadata. A string token or metadata value is not a retained object or execution capability.

## Execution, accounting and wire framing

The server invokes [LinuxProducerProcess](LinuxProducerProcess.cs) outside its reply-order semaphore. Authenticated STOP can therefore close admission while the original producer request is pending. The pending native start, process monitor and both pumps remain owned independently of the request handler. `RequireReportsReady` is available only after normal process and pump settlement; `RequirePhysicalSettlement` also supports unsuccessful cleanup without reclassifying it as success.

One [run output budget](SupervisionRunOutputBudget.cs) charges the original worker and subject pumps. The worker's protected ReportGenerator runner mirrors each actual reporter byte to the worker stderr pipe, including bytes discarded by its local retention limit. The existing worker-local process quota remains in force. Mirroring is awaited and write failures fail the procedure; it adds no second stdout/stderr reader. Final root accounting joins every registered pump and checks exact byte reconciliation after the worker itself exits. This is stream accounting, not process-exit proof.

The RUN response omits raw stdout/stderr; the root retains its private prefixes separately. Actual received-byte accounting and the original quota/truncation failure remain in the response. This keeps the encoded frame within the existing 64 KiB control limit without turning harmless transport-prefix omission into a producer failure. Artifact reads return at most 32 KiB raw chunks, preserving space for base64 and the closed response envelope. [Producer artifact custody](producer-artifacts.md) serves the actual original report handles and exact next offsets; it does not accept client-selected absolute paths.

## Final root custody

The root execution joins the server handlers and the actual worker and producer before account release. `LinuxProducerWorkspace.SealForCleanup` joins the retained producer even after partial server construction, then seals every account-associated producer node. A failed seal preserves identities for quarantine.

[LinuxRunWorkspace.RootCustody](LinuxRunWorkspace.Custody.cs) retains the three canonical JSON outputs and the one fixed declared artifact path `<producer-id>/merged/Cobertura.xml`. It accepts partial artifact directories only for unsuccessful cleanup; a successful coverage result requires the complete expected artifact, its canonical manifest metadata, exact length and hash. The whole closed tree is checked before mutation, sealed root:root, and rechecked through retained descriptors. The pure [coverage files verifier](coverage-files.md) checks copied data only; native custody and real process settlement cannot be supplied by that helper.

Original account reservations remain active until both workspace seals and strict native account closure finish. Handles and local owners close before the final original-deadline check. Neither a successful protocol ACK, detached JSON, output EOF nor an artifact digest establishes admission, accepted consumer proof or Trusted eligibility. The [migration checkpoint](../../docs/plans/issue-779-csharp-supervision-migration.md#checkpoint-2-declared-producer-and-artifact-custody) still requires genuine Linux coverage and failure controls.

## Closed stream observations

After every registered collection task joins, the root may emit one `evidence-coverage-output-v1` JSON line to its private stderr. [LinuxCoverageOutputObservation](LinuxCoverageOutputObservation.cs) includes aggregate received bytes, the original limit, first closed failure, quota/stop flags, and each pair's received counts, two EOF facts and failure. It includes no retained prefixes or paths. This lets native controls distinguish measured quota exhaustion from an unrelated nonzero exit. Encoding or writing failure cannot erase the original failed disposition; missing observations cannot prove a control passed. The observation grants neither physical-exit nor cleanup authority and is emitted before original final custody and account closure.
