# Local producer owner closure

[`SupervisionCloseSequence.CloseAll`](SupervisionCloseSequence.cs) attempts each original local
`IDisposable` in order. Null entries mean acquisition never occurred. There are no default limits or
optional callbacks. Each close failure atomically sets the caller’s sticky failure latch to one, retains no exception, and still attempts
every later close exactly once. A null sequence throws `ArgumentNullException` before dispatch.

The [producer](LinuxProducerProcess.cs) calls it only at its existing final local-close position, after
the original pending stop, physical group observations and paired pump joins. Its fixed array contains
the original process identity, observation connection, start connection and output deadline source.
The original cleanup-failure latch is set immediately before the next close begins and remains set through the original final settlement checks.
This preserves the original catch-all blocks, including their attempt of later closes when a close
throws a fatal-class exception. Enumeration errors propagate; production supplies a fixed local array.

The helper does not stop or kill a process, acquire a descriptor, retry a close, await detached work,
extend a deadline or issue custody. Successful local closure cannot stand in for real process/group
exit, pump EOF, account cleanup, admission or a successful run. See [producer execution](producer-execution.md)
and [artifact custody](producer-artifacts.md) for those independent requirements.

The [tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/SupervisionCloseSequenceTests.cs) use
ordinary owned file descriptors plus counted local closers. They prove that an earlier close fault
does not leak later local owners and does not change sentinel bytes. Constructed exceptions exercise
the original exception classification without causing actual exhaustion, stack overflow or native faults.
These tests authenticate no Linux process, workspace, account or native settlement.
