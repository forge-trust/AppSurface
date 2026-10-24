# Control server local state guards

[`LinuxControlServerStatePolicy`](LinuxControlServerStatePolicy.cs) verifies the original completed-task,
I/O-join, local-owner-close and connection-close flags used by
[`LinuxEmptyObservationControlServer.RequireCustodyOwner`](LinuxEmptyObservationControlServer.cs).
Detached flags create no server, worker, owner, admission or physical-exit receipt.

The native caller still authenticates all reference-equal owners and checks the original token before
and after its existing execution lock. After the local flag guard succeeds it reads the control/workload
ledger in its original short-circuit order. The separate success guard still checks actual task success,
exit acknowledgement and ledger settlement at the original callsite; these locking getters are not
read eagerly during local flag sampling.

`RequireNegativeReady` requires a past committed READY event and descriptor presence. The caller still
rejects an absent committed event before reading the descriptor; no new observation clears failure or
makes the protocol successful. Failures retain the same fixed `ASEVD410` factory and diagnostic.

The [portable tests](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxControlServerStatePolicyTests.cs)
exercise local flags only. Genuine root-authenticated protocol and custody controls remain necessary.
