# Application terminal scalar policy

[`LinuxApplicationProcess.StopAndJoinAsync`](LinuxApplicationProcess.cs) retains the native stop sequence and
performs all systemd and cgroup reads. After the recipe confirms the second sample is stopped, the process calls
[`LinuxApplicationTerminalPolicy.RequireStableMain`](LinuxApplicationTerminalPolicy.cs) with the first and second
`ExecMainPid`, `ExecMainCode`, and `ExecMainStatus` values and the optional retained AppHost PID.

The policy is data-only. It does not read systemd, inspect a process, check a cgroup, grant ownership, or decide
whether a workload succeeded. A missing AppHost PID means that no PID binding is available; it does not create one.
If the stable terminal result has a nonzero code or status, the caller still records the existing workload failure
after physical settlement. Rejection calls the original `LinuxApplicationProcess.Rejected()` factory, preserving
the fixed `ASEVD410: Restricted application ownership or independent resource readiness was not confirmed.`
message and no inner exception.

The caller deliberately keeps the recipe stopped check before this policy and the second cgroup emptiness probe
after it. This ordering is part of the cleanup boundary and must not be moved into the data-only helper.

See [`LinuxApplicationTerminalPolicyTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxApplicationTerminalPolicyTests.cs)
for scalar-only mismatch and optional-binding cases. These tests manufacture facts only; they do not provide a
process, group, native backend, or application authority.
