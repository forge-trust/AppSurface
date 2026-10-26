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
3. Emits exactly one fixed private stderr JSON marker with schema `evidence-worker-post-stop-guards-v1`, case `post-stop-guards`, phase `completed`, the six agreed facts true, and `native_acceptance=false`; it then deliberately throws `EvidenceAdmissionException("ASEVD410", "Private post-stop admission guards completed.")`.

The deliberate terminal failure keeps the real root in the original failure-prefix retention path and prevents a final manifest. The ordinary generated catalogue and policy keep one coverage slot, as required by the unchanged terminal verifier. The attempted write uses a fresh path, is empty, and must not create it. The writer is created while admission is active and lifecycle launch/write admission is still open; only its attempted write occurs after stop/join. A missing marker, wrong problem, artifact-list change, or successful write must fail the private control.

## Masking and interpretation

A non-None terminal code would latch the admission failure before `EvidenceArtifactWriter.WriteAsync` reaches its lifecycle tracking guard, so this probe requires the normal successful cleanup state (`TerminalCode=None`, active admission, armed worker). It does not claim the writer is usable after a failure-terminal admission latch. No caller-created supervisor, admission, worker, transport, lease, or root is accepted. The only extra writer is created internally from the actual admitted protected root and the original quota/lifecycle/admission, before stop.

The runtime lifecycle already closes registration, requests stop, joins callbacks/pumps and the supervisor's owned-exit acknowledgement under the existing stopping allowance, then runs cleanup under the existing cleanup allowance. This probe observes the joined state; it does not create a held-work barrier, claim to mutation-test the physical join, or change either allowance. The enclosing C3 case retains its existing shared deadline and output limits.

The existing private authenticated Linux C3Sdk fixture is required for execution. Portable lifecycle tests with a metadata/test supervisor are not substitutes. Native collection must separately verify the actual marker through the existing original failure-output path, all original stream EOF/byte reconciliation and root/application settlement facts, absence of owned groups/NSS residue, and absence of `coverage/merged/post-stop-denied.xml`. No build, test, or native execution was performed as part of this source-preparation task.
