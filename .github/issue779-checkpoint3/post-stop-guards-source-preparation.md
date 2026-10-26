# C3Sdk post-stop guard source preparation

This is a private C3Sdk-only probe. It is not an ordinary EvidenceHost behavior, a native acceptance result, or a new artifact capability. The probe is inserted in the actual admitted Aspire run after the existing successful `StopAndDisposeAsync` call and the existing `OwnWorkStopped` check, before `admission.Complete` and final collection.

## Selector

Build the Aspire project with:

```sh
dotnet build Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj \
  -c Release -p:EvidencePrivateQualification=C3Sdk \
  -p:EvidencePrivateC3SdkPostStopGuards=true
```

The property accepts only literal `true` or `false`; a nonempty value is allowed only for `EvidencePrivateQualification=C3Sdk`. The true selector is mutually exclusive with any nonempty `EvidencePrivateC3SdkLifecycleStall`. The compile symbol is defined only by this Aspire project. Ordinary None, C3, and all builds omitting the new property contain no probe call or method. The payload preparation adds one optional zero-byte C3 fixture slot; it changes no production limit or output allowance.

## Probe ordering and assertions

The probe requires the actual retained artifact root to have a nonzero identity and the admitted worker UID/GID, the real worker still armed, the original admission still active for the complete plan, successful stop/dispose, closed lifecycle admission, completed cleanup, joined owned work, and terminal code None. It also requires the exact optional zero-byte declaration in that plan and an unused logical name and path. The admission, armed worker and terminal code are checked again before emitting the marker, so an expired lease or another failure cannot stand in for the closed-work guard. It then:

1. Calls the existing real lifecycle's `TrackOwnedWork` with a fresh callback whose only effect is an atomic launch counter. Rejection is accepted only when the returned task is null and the callback count remains zero. Any unexpected task is joined before the probe fails.
2. Calls the retained actual producer writer with the C3-only optional slot `post-stop-probe`, path `lifecycle-probe/denied.bin`, and empty contents. It accepts only code `ASEVD410`, no inner exception, and the fixed problem prefix `ASEVD410: Artifact write admission is closed.`. The writer's artifact list must remain byte-for-byte-equivalent by value to its pre-call snapshot.
3. Emits exactly one fixed private stderr JSON marker with schema `evidence-worker-post-stop-guards-v1`, case `post-stop-guards`, phase `completed`, the six agreed facts true, and `native_acceptance=false`; it then deliberately throws `EvidenceAdmissionException("ASEVD410", "Private post-stop admission guards completed.")`.

The deliberate terminal failure keeps the real root in the original failure-prefix retention path and prevents a final manifest. The zero-byte optional fixture slot is ignored by the fixed coverage adapter as an unknown optional slot; its maximum is zero and the denied write must not create it. A missing marker, wrong problem, artifact-list change, or successful write must fail the private control.

## Masking and interpretation

A non-None terminal code would latch the admission failure before `EvidenceArtifactWriter.WriteAsync` reaches its lifecycle tracking guard, so this probe requires the normal successful cleanup state (`TerminalCode=None`, active admission, armed worker). It does not claim the writer is usable after a failure-terminal admission latch. No caller-created supervisor, admission, worker, writer, transport, lease, or root is accepted by the probe.

The runtime lifecycle already closes registration, requests stop, joins callbacks/pumps and the supervisor's owned-exit acknowledgement under the existing stopping allowance, then runs cleanup under the existing cleanup allowance. This probe observes the joined state; it does not create a held-work barrier, claim to mutation-test the physical join, or change either allowance. The enclosing C3 case retains its existing shared deadline and output limits.

The existing private authenticated Linux C3Sdk fixture is required for execution. Portable lifecycle tests with a metadata/test supervisor are not substitutes. Native collection must separately verify the actual marker through the existing original failure-output path, all original stream EOF/byte reconciliation and root/application settlement facts, absence of owned groups/NSS residue, and absence of `coverage/lifecycle-probe/denied.bin`. No build, test, or native execution was performed as part of this source-preparation task.
