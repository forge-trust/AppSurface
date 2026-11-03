# Application lifecycle coordinator

[`SupervisionApplicationLifetime`](SupervisionApplicationLifetime.cs) provides data-only ownership for one
application start, readiness waits, and a shared stop-and-join sequence. It is an internal API in the
[`ForgeTrust.AppSurface.Evidence.Supervision` package](README.md). It does not create a native process,
admission decision, process lease, or physical-exit receipt.

## API reference

Create one coordinator for the application and its owner's shared
[`SupervisionWorkRegistry`](SupervisionWorkRegistry.cs):

```csharp
var lifetime = new SupervisionApplicationLifetime(workRegistry);
Task<ApplicationStart> start = lifetime.StartAsync(StartApplicationAsync, startToken);
Task<ResourceReady> ready = lifetime.WaitResourceAsync("database", WaitForDatabaseAsync, readyToken);
Task stopped = lifetime.StopAndJoinAsync(StopApplicationAsync, cleanupToken);
```

`StartAsync<T>` consumes the sole start attempt before callback validation or dispatch. It registers a workload
before invoking the callback, then retains the task returned by that callback until the task actually returns.
The result task reports the callback result, a sanitized `ASEVD410` error, or cancellation. A failed or canceled
attempt cannot be retried on the same lifetime.

`WaitResourceAsync<T>` is available only after the start callback has completed successfully and while this
coordinator remains open. A resource ID must be nonempty and may be used once; IDs use ordinal, case-sensitive
comparison. Each accepted wait receives its own linked token and its own workload registration. Rejected IDs,
pre-start waits, and calls after closure never dispatch a callback.

`StopAndJoinAsync` closes this coordinator's admission before invoking the supplied stop callback. The first
call captures the stop delegate and cleanup token; every replay returns the same task and never retries a failed
callback. The stop callback runs before the coordinator joins its retained start and readiness tasks. When
startup was pending at closure, the callback runs a second time after those tasks return so the owner can
contain anything startup created late. Registered local disposers then run in registration order.

Use this coordinator for an application with one start attempt and keyed resource readiness waits. A worker
startup that owns its own containment and finalization phases should use
[`SupervisionWorkerLifetime`](SupervisionWorkerLifetime.cs), whose stop sequence models those worker-specific
phases directly.

Call `RegisterDisposer` before the first start attempt. It adds one local cleanup callback that receives the
first stop caller's cleanup token. `RequireJoinedCleanupPhase()` rejects before start/readiness joins finish;
it succeeds inside a registered disposer after those joins, or after the stop sequence reaches that phase. The
guard does not wait for the disposer that calls it. Lifecycle methods called reentrantly from a start, readiness,
stop, or disposer callback reject to prevent a callback from awaiting its own owned task.

## Failure and join behavior

`Failed` is sticky for a start/readiness error, any original start/readiness token cancellation, a stop/disposer
error, or cleanup-token cancellation. Start and readiness tasks preserve their own canceled or faulted outcome.
An application operation failure alone does not fault `StopAndJoinAsync`: if the owner-supplied stop and
disposers succeed and every owned task returns, the stop task succeeds so the physical owner can finish cleanup.
Stop, disposer, cleanup-token, or cleanup bookkeeping failure faults that shared task with a sanitized
`ASEVD410`; the owner can then reject settlement and quarantine as its policy requires. `Joined` means the stop
callback calls, owned operation tasks, and registered disposer callbacks have returned. It can be true while
`Failed` is true.

The first original start/readiness token stays linked with the coordinator's internal stop signal for that
operation. The first cleanup token is passed unchanged to each stop call and local disposer. A canceled start
or readiness token closes coordinator admission and sets `Failed`, but does not replace the cleanup token or
fault a successful cleanup task. Cleanup-token cancellation does fault that task. Neither token grants a new
cleanup interval.
The coordinator does not use a timeout, `WaitAsync`, detached task, or renewed deadline to abandon original
work. If a callback ignores cancellation, stop remains pending until that callback's original task returns.

## Shared ledger and native owner boundary

The coordinator completes only the workload registrations it created for its own start and readiness tasks.
It does not call `CloseAndJoinWorkloadsAsync` or `CloseAndJoinControlsAsync`. This prevents it from waiting on
the native application's separately registered active-unit workload, and it never joins control handlers.
The root owner closes and drains the shared ledger after its native stop callback and final physical checks;
see the [control sequence](SupervisionControlSequence.cs) and the registry's
[pending-work and control-handler guidance](README.md#pending-work-and-control-handlers).

`Joined` and `Failed` describe managed procedure data only. A successful stop task is not proof that a native
unit exited, an application lease was released, a process group is empty, output streams reached EOF, or
filesystem custody is safe. The native application owner must perform those checks with its retained handles,
original deadline, and separate workload registration before releasing application IDs or publishing success.
For the related worker startup sequence, see [`SupervisionWorkerLifetime`](SupervisionWorkerLifetime.cs).
