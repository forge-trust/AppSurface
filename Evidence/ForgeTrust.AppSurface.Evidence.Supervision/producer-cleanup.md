# Producer cleanup procedure

[`SupervisionProducerCleanup.RunAsync`](SupervisionProducerCleanup.cs) is an internal ordering
procedure for the private producer's existing finalization callbacks. It makes the callback order and
failure continuation directly reviewable without moving native ownership, pending-state projection,
output qualification, or settlement authority out of `LinuxProducerProcess.RunOwnedAsync`.

## Internal API

`RunAsync(actions, recordFailure)` accepts only callbacks borrowed from the one private producer
invocation. Its `DispatchAttempted` callback is evaluated only after `StopPending` has completed, so
the private caller's post-stop state is observed at the same point as the original procedure. It does
not create a dispatch, unit, account, owner, lease, task, deadline, or capability. The actions record
contains the following operations:

- `StopPending` always invokes the producer's existing pending stop procedure with its private cleanup
  token already captured by the caller.
- `DispatchAttempted` is a deferred scalar read after pending stop; `ObserveStopped` runs only when
  that post-stop read reports an actual dispatch attempt. The helper does not snapshot the value
  before stopping or return any settlement fact.
- `Pipes` is null only when no pipe owner was acquired. Its `CloseWriteCopies` operation is always
  attempted for an acquired pipe owner. `JoinPumps` is nullable only when the original collector was
  never acquired; the helper skips that operation in that case. The original `ValueTask`-based
  `DisposeAsync` is always attempted.
- `CloseRetainedOwners` is the caller's existing `SupervisionCloseSequence.CloseAll` binding. It is
  called directly after the pipe stage and remains outside the helper's catch boundaries, preserving
  the original close behavior.

`recordFailure` is the producer's existing nonthrowing cleanup-failure sink. Each caught callback
failure is sent to it immediately before the next cleanup operation begins. The helper returns no
result and creates no replacement latch, completion source, deadline, cancellation token, native
object, or output receipt.

## Order and exception policy

The fixed order is:

1. pending stop;
2. stopped-and-empty observation when dispatch was attempted;
3. pipe write-copy closure, when pipes were acquired;
4. collector/pump join, only when the original collector was acquired;
5. pipe disposal;
6. the caller's direct retained-owner close sequence.

The first five stages retain the original per-stage catch-all behavior, including cancellation and
fatal exceptions: a failure is latched and later cleanup continues. The retained-owner close is not
wrapped here because the original caller invokes `CloseAll` directly. The caller then performs its
existing pending snapshot, native settlement facts, output/result checks, and failure projection.

## Decision and pitfalls

This seam exists to expose the existing procedure order and sticky failure behavior to ordinary
callback tests while keeping the authentic Linux callbacks in the private producer. It must not be
used to prove process exit, cgroup emptiness, output success, account release, filesystem custody, or
accepted evidence.

Do not pass a synthetic owner, handle, lease, admission object, or replacement cancellation source.
Do not treat a null `JoinPumps` as a successful join; it means collection was never acquired, and the
caller remains responsible for its original output predicates. Do not move the pending snapshot,
native stopped/group observations, output quota checks, physical-settlement calculation, diagnostic
retention, or final result checks into this procedure. Do not catch or reclassify the direct retained
owner close; its original caller-owned semantics are part of the contract.
