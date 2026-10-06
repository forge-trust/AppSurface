# Private coverage owner within existing worker permissions

This private mechanism is used by the [qualification controller](run-qualification.py).
It grants no admission, public writer, registered production application or Trust
claim. The production registration table and proof registry remain unchanged.
The [task host](../PrivateProductCoverageTaskHost/README.md) runs the official
Coverlet tasks in one process. Only pristine Cli, Aspire and Coverage DLL/PDB
pairs are eligible; private Contracts/Planner and the entry executable are excluded.

## Ownership and ordering

`ProductCoverageOwner(tool, anchor, worker_uid, worker_gid, deadline, dotnet,
taskhost, reports, worker_unit)` receives launcher-selected paths and actual
accounts, never worker JSON. `prepare()` pins the entire original tool map,
mounts a bounded 32 MiB/64 inode sticky tmpfs below the existing worker-owned
0700 anchor, then starts an independent root watchdog. No new writable path is
added to the worker. Separate session FDs preserve the artifact allocator's
`openat2` mount-crossing restrictions. Root state/backups are 0600; genuine
worker-owned hits are checked after physical exit.

The watchdog is the actual parent of the task host and sole owner of its stdin.
It authenticates its private packets by kernel credentials and keeps a pidfd for
its controller parent. Launcher-generated worker, producer and application
units are registered before dispatch. The selected worker's real UID/GID tuples,
capabilities, NNP, cgroup, opened namespace mount and process start time are
checked before descriptor activation. `bind_tool_pin()` verifies the actual launcher permission formulas, copied hashes and selected group before dispatch. Original, instrumented and runtime permission maps remain separate. After physical exit, restoration reverses all captured modes and groups. `prepare()` returns a copied execution
SHA map; this map is distinct from the original restoration map.

`confirm_owned_exit()` is called after the launcher has stopped/joined every
consumer, pump and handler. `collect_and_restore()` rechecks all generated
cgroups, root state and genuine hits before sending `collect` to the living
provider. The official reports must contain real covered lines and branches.
Every eligible DLL/PDB must be restored to its original hash and 0444 mode, and
all other files must remain unchanged. The private receipt records actual
provider exit, both EOFs and watchdog join. `close()` closes the mount FD,
unmounts, then returns the strict account-release receipt. Accounts can be
removed only after that receipt is accepted by the completion owner.

## Failure and limits

Missing/malformed packets, replay, changed state, nonzero provider exit, failed
FD close, late deadlines or uncertain physical consumer exit permanently fail
this owner. Controlled abort kills the provider with SIGKILL before closing its
input: it sends neither EOF nor TERM nor a collect command. Mounted work and
accounts stay quarantined on failure. The independent watchdog seals a lost or
expired parent and stops the exact registered units. If the kernel cannot yet
confirm provider exit, that watchdog remains its actual parent/reaper and holds
stdin; this is failure quarantine, never a successful bounded cleanup claim.

All reads are bounded before buffering. Provider stdout/stderr are 64/128 KiB;
IPC is 256 KiB; packets 16 KiB; file/tree bounds are 32/256 MiB with 2,048
combined file and directory entries, including the tree root. The authenticated
CLI and Host publication in run 37389431507 contained 972 files and 84 directories
each; the former 512-entry inventory bound could not accept either tree. This
private metadata inventory limit permits those published dependencies while
retaining depth eight, the same byte limits, protected ownership and link checks,
and the original deadline. It changes no worker unit property, filesystem grant,
account permission, job output quota or qualification requirement.
Official reports are limited to 2 MiB XML and 4 MiB JSON. Utilities share the
existing deadline and a maximum five-second operation bound. No added job timer
or grace period can create success after expiry. Private logs and receipts stay
root-owned 0600, and raw exception/output text is never public.

[Procedure tests](test_product_coverage.py) use actual small files and owned
processes, including SIGKILL without an exit hook and two EOFs. They issue no
root owner or admission. Linux mount/systemd/credential proof and actual
qualified product coverage require separate native execution; source readiness
or portable tests are not such proof.
