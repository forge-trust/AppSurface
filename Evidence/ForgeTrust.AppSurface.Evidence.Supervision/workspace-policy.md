# Workspace identity policy

[`LinuxWorkspaceOwnerPolicy`](LinuxWorkspaceOwnerPolicy.cs) contains the two data-only checks used by the application and producer workspace
wrappers: `RequireApplicationData` and `RequireProducerData`. Each receives immutable layout data and the
expected owner generation.

| Internal method | Required data | Rejection |
| --- | --- | --- |
| `RequireProducerData(identity, rootName, ownerRunId)` | Nonnull captured [`LinuxRunAccountSnapshot`](LinuxRunAccounts.cs), exact producer root name, prevalidated owner generation | Fixed `ASEVD402`, without an inner exception |
| `RequireApplicationData(layoutRunId, rootName, ownerRunId)` | Nonempty layout generation, exact application root name, prevalidated owner generation | Fixed `ASEVD410`, without an inner exception |

Neither method has optional arguments or performs I/O. The exact names are
`appsurface-evidence-producer-data-` or `appsurface-app-`, followed by the owner's 32 lowercase GUID digits.
The producer snapshot carries worker UID/GID, subject UID/GID and results GID. Its UIDs must differ, its
three GIDs must differ, and every scalar excludes zero and `uint.MaxValue`. Valid captured values at either
remaining numeric boundary are preserved. Full layout construction and owner authentication stay in the
[producer](LinuxProducerWorkspace.cs) and [application](LinuxApplicationWorkspace.cs) wrappers.

These methods do not acquire a lock, inspect live accounts, open a descriptor, perform authentication, or
grant an admission capability. `LinuxApplicationWorkspace.RequireAccountOwner` and
`LinuxProducerWorkspace.RequireAccountOwner` retain their reference-equality checks before invoking them.
The surrounding methods also retain their existing authentication, lock, native recheck, exception, and
call ordering.

`RequireProducerData` preserves the original producer scalar order: zero and `uint.MaxValue`, worker/subject
UID equality, zero and `uint.MaxValue` GIDs, the three GID collisions, then the producer root-name comparison.
`RequireApplicationData` preserves the original empty-layout-generation check followed by the application
root-name comparison. Boundary tests call these internal policy methods with immutable data only; they do
not create a workspace, account holder, native node, or process.

The native `RequireSameNamed` and `RecheckNode` paths remain in their existing wrappers. They combine
`openat2`, descriptor metadata reads, and retained-node state, so moving them into this predicate would lose
the native ordering that the workspace custody contract depends on.
