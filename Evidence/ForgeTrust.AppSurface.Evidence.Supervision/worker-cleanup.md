# Worker cleanup procedure

[`SupervisionWorkerCleanup.RunAsync`](SupervisionWorkerCleanup.cs) orders the existing finalization
operations of the private [`LinuxWorkerProcess`](LinuxWorkerProcess.cs). It separates cleanup ordering
and failure continuation from native identity and settlement checks. It is internal procedure bookkeeping;
it cannot create a worker, select a unit, authenticate a peer, extend a deadline or publish settlement.

## Internal API and order

`RunAsync(actions, recordFailure)` returns one task which the worker immediately awaits inside its
original finalization. `SupervisionWorkerCleanupActions` borrows fixed delegates from that worker.
All required delegates must be nonnull original operations. `JoinMonitor` may be null only when no
monitor was acquired. `Pipes` may be null only when no pipe owner was acquired; its three operations
form one `SupervisionWorkerPipeCleanup` binding.

| Order | Operation | Failure behavior |
| --- | --- | --- |
| 1 | Inspect stopped unit and physical group using the retained start connection | Latch failure and continue |
| 2 | Join the original exit monitor, if acquired | Preserve the original monitor helper's exception policy |
| 3 | Close original transferred pipe write copies | Latch failure and continue |
| 4 | Join both original output pumps and validate their drain | Latch failure and continue |
| 5 | Dispose the original pipe owner | Latch failure and continue |
| 6 | Close the original observation connection | Latch failure, invalidate the unit fact and continue |
| 7 | Perform final owner/unit/group/pending-start checks and close the start connection | Latch failure and continue |
| 8 | Attempt the original start-connection close again | Latch failure and continue |

The last close is the worker's existing defensive close attempt. It still runs when the first close or
final inspection throws. The procedure adds no other retry. The fixed `SupervisionWorkerCleanupStage`
values identify only the operation that threw. The nonthrowing `recordFailure` sink immediately sets
the worker's sticky local failure and invalidates its stage-specific facts before the next operation.
Caught exceptions and their messages are not retained or published by this helper.

## Native checks remain mandatory

The worker retains the actual captured PID, original authenticated connections, original monitor and
pump tasks, pending start/stop state, cleanup token and final publication guard. Its unit/group loop
and final native reads remain in the private worker. The procedure directly awaits every original task;
it has no timer, cancellation source, canceled-wait proxy, detached task or replacement completion flag.

An expected canceled exit monitor remains classified by
[`JoinOriginalExitMonitorAsync`](LinuxWorkerProcess.cs), using that monitor's original job token.
Exceptions escaping that helper still propagate at the same point and do not become a successful drain.
Other caught cleanup stages keep their original catch-all behavior, including cancellation and fatal-class
exceptions. This distinction preserves the existing worker policy rather than applying a new exception
classification to native teardown.

Returning from the procedure does not mean cleanup succeeded. Only the private worker can check all
its accumulated facts, capture the failure projection, reject on any failure, and finally publish
`PhysicallySettled`. That projection still does not grant successful execution, custody, account deletion,
admission or consumer proof. See the [worker lifecycle reference](README.md#same-image-worker-ownership)
and [terminal root custody contract](README.md#terminal-root-custody-and-shared-teardown).

## Verification

[`SupervisionWorkerCleanupTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionWorkerCleanupTests.cs)
use ordinary tasks and deterministic barriers to check ordering, failure continuation, optional resources,
failure-latch timing and actual joining of blocked operations. They instantiate no native worker or owner.
They cannot stand in for the [genuine Linux acceptance controls](../../docs/evidence/issue779-csharp-runtime-cutover.md),
which must still verify actual process/group/pump settlement and the unchanged coverage gates.
