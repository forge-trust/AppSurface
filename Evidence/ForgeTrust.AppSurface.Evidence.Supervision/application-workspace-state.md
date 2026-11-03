# Application workspace state policy

[`LinuxApplicationWorkspaceStatePolicy`](LinuxApplicationWorkspaceStatePolicy.cs) is an internal data-only validator for the workspace's existing
initialization, quarantine, seal, retention, and closed-state gates. The native caller remains responsible
for reference identity, locks, owner/account checks, deadlines, descriptors, filesystem observations, physical
settlement, and quarantine writes in [`LinuxApplicationWorkspace`](LinuxApplicationWorkspace.cs). A policy
result grants no owner, handle, callback, admission, or release authority.

Each caller snapshots only the relevant fields while holding the existing workspace lock, then invokes the
operation-specific void predicate. Recheck and ordinary bundle use share `RequireBundleUse` because their
five state conditions are identical; the native call sites remain separate. The predicates preserve the
existing distinctions: application retention
requires initialization without quarantine, seal attempt, or a prior retention; active and owned access reject
quarantine, sealing, and closed handles; sealing requires the mounted scratch state; sealed release requires a
completed seal with no mounted scratch; and disposal additionally requires the existing release guard.

The policy is intentionally limited to state flags that already form coherent boolean gates. Native metadata,
path checks, owner identity, output probes, deadlines, filesystem I/O, and physical application settlement stay
in their original order in [`LinuxApplicationWorkspace`](LinuxApplicationWorkspace.cs). The focused tests in
[`LinuxApplicationWorkspaceStatePolicyTests`](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxApplicationWorkspaceStatePolicyTests.cs)
use copied state facts only, with adjacent valid controls, one-field rejection cases, and irrelevant-neighbor
controls; they do not manufacture a workspace or native owner.
