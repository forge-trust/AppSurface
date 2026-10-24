# Control I/O drain

[`SupervisionControlDrain.RunAsync`](SupervisionControlDrain.cs) owns the ordered final I/O joins for the
[`LinuxEmptyObservationControlServer`](LinuxEmptyObservationControlServer.cs). It separates failure and
join bookkeeping from the server's native identity checks. The native server remains privately constructed
from the real owner, worker, listener and work registry. There is no public transport or handler injection API.

## Internal API

`RunAsync(exitIntent, pendingAccept, handlers, actions, recordFailure)` returns the original `Task<bool>`.
The server immediately awaits that task inside its existing `finally` block. `pendingAccept` may be null;
`handlers` contains the retained original tasks after the accept loop stops. `SupervisionControlDrainActions`
borrows the fixed operations, signal task and signal lifetime token from that server. Delegates and tasks
must be nonnull. The operations retain their original cancellation and cleanup clocks; the drain creates
no cancellation source, timer, timeout, retry or replacement task. The nonthrowing failure sink records the
closed stage in [`LinuxControlFailureLatch`](EvidenceNativeObservationFailure.cs) and marks the existing
[`SupervisionControlSequence`](SupervisionControlSequence.cs) failed.

| Phase | EXIT intent observed | No EXIT intent |
| --- | --- | --- |
| Containment | Join existing stop after handlers | Start failed-protocol stop before handler joins |
| Signal | Cancel its original lifetime and join its original task | Same |
| Accept | Cancel pending accept, close private checkpoint I/O | Same |
| Listener | Close only listening admission before handler joins | Dispose listener before handler joins to interrupt I/O |
| Pending accept | Join original task; listener owns its fault classification | Same |
| Handlers | Join every retained task, including ignored cancellation | Same |
| Final I/O | Dispose listener, join original stop, join control registrations | Join the already retained stop, join control registrations |

The result reports whether the I/O joins completed without a recoverable failure. Descendant-stop and
accept-cancel failures are separately latched by the failure sink, as they were in the original server.
Expected signal cancellation is ignored only after that signal's own lifetime is canceled. Listener
checkpoint closure retains its existing false-result and failure-latch semantics. The pending-accept
task is always observed, while the original listener drain classifies any unexpected accept failure.

## Ordering and failure rules

Starting containment after joining handlers can deadlock: a handler may be waiting for a descendant that
only stop can release. Disposing the full listener before valid EXIT handlers finish can instead interrupt
their ACK writes. These two cases deliberately use different ordering. A failed step still permits every
later recoverable cleanup step to run. A handler that ignores cancellation remains joined; a faulted sibling
does not permit `Task.WhenAll` to return before that handler finishes. Fatal runtime failures propagate.

The returned Boolean is not protocol success, physical exit, custody, account release or admission. The
server still checks connection-close failure, committed EXIT, registry settlement, actual owner identity
and the original cumulative deadline. Native stop and filesystem custody retain their existing guards.
Do not substitute a caller's canceled wait or a detached completion flag for any original task.

[`SupervisionControlDrainTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionControlDrainTests.cs)
use real tasks, barriers and closed failure projections to verify both orders, ignored cancellation,
failure continuation, first-failure retention and stop-task reuse. Those portable procedure controls create
no Linux server, peer, process, cgroup or native receipt. Genuine Linux acceptance is recorded separately in
the [runtime cutover record](../../docs/evidence/issue779-csharp-runtime-cutover.md).
