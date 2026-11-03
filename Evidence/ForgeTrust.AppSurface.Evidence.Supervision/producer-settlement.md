# Producer settlement policy

[`LinuxProducerSettlementPolicy`](LinuxProducerSettlement.cs) is an internal data-only validator used after
[`LinuxProducerProcess`](LinuxProducerProcess.cs) has completed its existing reference and native-owner checks. The process copies
the observations needed by the policy; the policy creates no owner, process, unit, account, admission, lease,
callback or native receipt.

Report readiness preserves the original evaluation order. `RequireReportPrefix` checks the successfully
completed run, physical settlement, both terminal observations, successful output with no discarded bytes, and the immutable
output receipt. Only after that prefix succeeds does `LinuxProducerProcess` read the existing locked
`_outputBudget.QuotaExceeded` property. It then passes the four simple latches to `RequireReportLatches`,
which checks failure, cleanup failure, group-empty-twice, and joined pumps. The policy does not inspect an exit
code: a normal nonzero producer/test result remains ordinary result data when the existing run has otherwise
completed successfully.

## Internal API and inputs

- `RequireReportPrefix(runCompletedSuccessfully, physicallySettled, normalTerminalObserved,
  stoppedTerminalObserved, output)` rejects a false prerequisite, absent output, unsuccessful output, or
  discarded bytes. `output` is the immutable [`SupervisionOutputReceipt`](SupervisionOutputCollector.cs),
  not a stream or process handle.
- `RequireReportLatches(failed, cleanupFailed, groupEmptyTwice, pumpJoined)` rejects either failure latch or
  either missing join observation. The native caller reads the existing quota latch between these methods.
- `RequirePhysicalSettlement(facts)` consumes `LinuxProducerCleanupFacts`: the original pending-start
  snapshot, dispatch/physical/cleanup state, terminal/group/pump observations, drained output and whether
  the original collection is pending. `CollectionPending` is false for an absent or completed collection,
  exactly matching the original nullable task pattern. The pending snapshot must be nonnull.

There are no optional arguments or new limits. Every policy rejection uses the existing fixed `ASEVD410`
exception with no inner exception. The immutable facts carry no owner or handle; passing copied facts
never issues a producer or permission to open reports. In particular, successful cleanup data cannot
clear the producer's separate failed-run latch or authorize account release.

Physical settlement preserves the existing distinction between a failed start and a failed stop. A failed
start may pass when the original pending-start snapshot is fully joined and closed and all existing cleanup
facts are complete. A stop failure or cancellation always rejects. After dispatch, stopped-terminal,
group-empty-twice, pump-joined, drained output, and a nonpending collection remain required. A null collection
is preserved as `CollectionPending == false`, matching the existing `_collect is { IsCompleted: false }`
pattern; null is not turned into a new rejection.

The validator tests use copied immutable facts only. They verify one-field rejection and adjacent valid data,
but do not claim Linux process ownership, native settlement, account release, output acceptance, or report
authority. Those claims remain obligations of the surrounding native/reference-bound lifecycle.

The [settlement tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxProducerSettlementTests.cs) exercise the enforcing predicates. See [producer execution](producer-execution.md) and [artifact custody](producer-artifacts.md) for the genuine native prerequisites.
