# C3Sdk post-stop guard source preparation

This is a private C3Sdk-only probe. It is not an ordinary EvidenceHost behavior, a native acceptance result, or a new artifact capability. The probe is inserted in the actual admitted Aspire run after the existing successful `StopAndDisposeAsync` call and the existing `OwnWorkStopped` check, before `admission.Complete` and final collection.

## Selector

Build the Aspire project with:

```sh
dotnet build Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj \
  -c Release -p:EvidencePrivateQualification=C3Sdk \
  -p:EvidencePrivateC3SdkPostStopGuards=true
```

The property accepts only literal `true` or `false`; a nonempty value is allowed only for `EvidencePrivateQualification=C3Sdk`. The true selector is mutually exclusive with any nonempty `EvidencePrivateC3SdkLifecycleStall`. The compile symbol is defined only by this Aspire project. Ordinary None, C3, and all builds omitting the new property contain no probe call or method. The payload preparation retains the original single required coverage slot. The private image creates a separate unused writer bound to that exact declaration, actual protected root, run quota, lifecycle and admission before stopping; it changes no production limit or output allowance.

## Probe ordering and assertions

The probe requires the actual retained artifact root to have a nonzero identity and the admitted worker UID/GID, the real worker still armed, the original admission still active for the complete plan, successful stop/dispose, closed lifecycle admission, completed cleanup, joined owned work, and terminal code None. Before stopping, it requires the exact single coverage declaration and captures an unused writer. After stopping, that writer still has an unused logical slot and fresh path, so duplicate-artifact rejection cannot mask a missing lifecycle guard. The admission, armed worker and terminal code are checked again before emitting the marker, so an expired lease or another failure cannot stand in for the closed-work guard. It then:

1. Calls the existing real lifecycle's `TrackOwnedWork` with a fresh callback whose only effect is an atomic launch counter. Rejection is accepted only when the returned task is null and the callback count remains zero. Any unexpected task is joined before the probe fails.
2. Calls the unused admitted writer with the existing `coverage-report` slot, fresh path `merged/post-stop-denied.xml`, and empty contents. It accepts only code `ASEVD410`, no inner exception, and the fixed problem prefix `ASEVD410: Artifact write admission is closed.`. Both the unused writer's empty artifact list and the completed producer writer's artifact list must remain equivalent by value to their pre-call snapshots.
3. Emits exactly one fixed private stderr JSON marker with schema `evidence-worker-post-stop-guards-v1`, case `post-stop-guards`, phase `completed`, the six agreed facts true, and `native_acceptance=false`.
4. Claims the existing collection reserve and runs a bounded unpublished completion: dispose the original artifact root, check the original collection token/remainder, then send the existing authenticated EXIT request while the worker is still alive. No artifact verification, admission completion, manifest construction or terminal-file publication occurs. Failure to close or complete still fails the control.
5. Deliberately throws `EvidenceAdmissionException("ASEVD410", "Private post-stop admission guards completed.")`, preserving its nonzero result after protocol completion.

The deliberate terminal failure keeps the real root in the original failure-prefix retention path and prevents a final manifest. The ordinary generated catalogue and policy keep one coverage slot, as required by the unchanged terminal verifier. The attempted write uses a fresh path, is empty, and must not create it. The writer is created while admission is active and lifecycle launch/write admission is still open; only its attempted write occurs after stop/join. A missing marker, wrong problem, artifact-list change, or successful write must fail the private control.

The internal `EvidenceHostBootstrap.CompleteUnpublishedWorkerAsync` procedure accepts the original host budget, joined lifecycle, retained output owner and existing completion callback. It claims collection once, joins output disposal before EXIT, suppresses EXIT after a failed close or expired reserve, and requires the original collection to finish successfully. It uses the existing collection, stopping and job bounds. Replay, pending cleanup and an unavailable reserve reject before dispatch. Its ordinary compiled internal test seam verifies ordering and failure behavior; only the compile-owned private probe calls it during execution. It provides no output, admission or native custody authority.

The [deterministic completion tests](../../Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidenceUnpublishedWorkerCompletionTests.cs)
also distinguish original-budget expiry from lifecycle-token cancellation: separately controlled clocks
keep the token usable while the original reserve expires, requiring rejection before EXIT. A second
case acknowledges EXIT and then advances the shared clock past collection expiry; that late
acknowledgement cannot complete collection or produce evidence. These are procedure controls,
not native timing measurements or a relaxation of either clock.

Revision 120's [runtime run](https://github.com/forge-trust/AppSurface/actions/runs/40631127475) passed the two stall cases, then failed the post-stop case's original account-custody predicate. Its marker and actual application/output settlement succeeded, but the owner recorded `ProcessNamedStatus` during `ServerRun`. The former probe threw before collection and its only EXIT call, allowing worker termination to race the listener's required live process check. The preparation above completes that existing protocol before the deliberate failure; it changes no peer check, root custody, account release or success predicate. Fresh source-bound Linux execution must establish its effect. Revision 120 remains failed.

## Masking and interpretation

A non-None terminal code would latch the admission failure before `EvidenceArtifactWriter.WriteAsync` reaches its lifecycle tracking guard, so this probe requires the normal successful cleanup state (`TerminalCode=None`, active admission, armed worker). It does not claim the writer is usable after a failure-terminal admission latch. No caller-created supervisor, admission, worker, transport, lease, or root is accepted. The only extra writer is created internally from the actual admitted protected root and the original quota/lifecycle/admission, before stop.

The runtime lifecycle already closes registration, requests stop, joins callbacks/pumps and the supervisor's owned-exit acknowledgement under the existing stopping allowance, then runs cleanup under the existing cleanup allowance. This probe observes the joined state; it does not create a held-work barrier, claim to mutation-test the physical join, or change either allowance. The enclosing C3 case retains its existing shared deadline and output limits.

The existing private authenticated Linux C3Sdk fixture is required for execution. Portable lifecycle tests with a metadata/test supervisor are not substitutes. Native collection must separately verify the actual marker through the existing original failure-output path, all original stream EOF/byte reconciliation and root/application settlement facts, absence of owned groups/NSS residue, and absence of `coverage/merged/post-stop-denied.xml`. The original preparation had no executed validation; the revision-120 correction has separate local ordering/failure tests and still requires a fresh native result.
