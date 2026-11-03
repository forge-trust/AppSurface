# Worker settlement policy

`LinuxWorkerSettlementPolicy` is an internal data-only predicate used after the
existing reference guards, `server.RequireCustodyOwner`, and `owner.RequireControlIdentity`.
It grants no worker ownership, lifecycle authority, admission, account release, callback, or native
capability.

`RequirePrefix` copies only the original boolean custody facts: stop joined, physical settlement,
pending stop joined, pending failure, start reservation joined, and completed exit monitoring. A failed
pending start is rejected for worker custody, including a `StartFailed` pending snapshot; this remains
distinct from producer cleanup policy.

The caller evaluates its unchanged `OutputDrainedForCleanup()` result after the prefix. `RequireOutputDrain`
checks only that pure result, so a false result rejects before another run-ledger getter is read. The caller
then preserves the original native-position check: an absent run budget is accepted, while a present budget
whose `Drained` property is false rejects. Only after those checks does the caller read the actual cgroup
sample with `LinuxCgroupProbe.Read(Unit, token)` and return that native observation. The policy does not read
or create native state.

[`EvaluateFinalSettlement`](LinuxWorkerSettlementPolicy.cs) centralizes the final copied join decision used by
[`LinuxWorkerProcess.FinalizeAsync`](LinuxWorkerProcess.cs).
It returns both the pending-adjusted `GroupJoined` result, which failure capture still records, and the local
failure decision. Its pending-start predicate is exactly `!StopJoined || IsFailed || (StartReserved && !StartJoined)`;
it intentionally does not add a `Closed` requirement. The caller keeps all original unit/group, monitor, and pipe
observations, then applies the returned values before its existing owner recheck, cleanup failure latch, and
publication sequence. The result is a data decision only, not proof of physical settlement.

Tests use copied booleans only and cover each one-field rejection with an adjacent valid control. They do not
claim a native owner, cgroup emptiness, output authority, or worker custody. Final-settlement controls use only
copied join flags and constructed pending-start snapshots; they do not construct a worker or native owner.


## Ordered original observations

[`SupervisionWorkerSettlementObservation.WaitAsync`](SupervisionWorkerSettlementObservation.cs) orders the
finalizer's existing cleanup guard, asynchronous unit/group observation, conditional 25 ms wait and local
publication. Its callbacks are fixed methods/closures from the authentic private worker in production.
The compound observation keeps the original retained backend read, stopped-unit comparison, optional
worker PID match and short-circuited kernel group read together. A false observation joins the original
wait before the next guard; true records the existing milestone and join flags once. Any exception
propagates into the existing [`SupervisionWorkerCleanup`](SupervisionWorkerCleanup.cs) catch boundary
without retry or publication.

This internal procedure owns no timer, token or native object. The owner supplies its original cleanup
token and deadline; it cannot renew them. Its ordinary task/barrier tests check procedure ordering only,
including pending inspections, pending waits and original failures. They cannot authenticate a unit,
establish an empty cgroup or issue physical-settlement authority.
