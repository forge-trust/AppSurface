# Private coverage owner within existing worker permissions

This private mechanism is used by the [qualification controller](run-qualification.py).
It grants no admission, public writer, registered production application or Trust
claim. The production registration table and proof registry remain unchanged.
The [task host](../PrivateProductCoverageTaskHost/README.md) runs the official
Coverlet tasks in one process. Only pristine Cli, Aspire and Coverage DLL/PDB
pairs are eligible; private Contracts/Planner and the entry executable are excluded.

## Ownership and ordering

`ProductCoverageOwner(tool, anchor, worker_uid, worker_gid, deadline, dotnet,
taskhost, reports, worker_unit, *, published_files=())` receives launcher-selected paths and actual
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
IPC is 256 KiB; packets 16 KiB; buffered file reads and eligible DLL/PDB pairs
remain bounded to 32 MiB. Only files declared by the selected application audit may use its existing
128 MiB bundle envelope, and are hashed in 64 KiB chunks. Unlisted files keep
the 32 MiB limit. The total tree
bound remains 256 MiB with 2,048
combined file and directory entries, including the tree root. The authenticated
CLI and Host publication in run 37389431507 contained 972 files and 84 directories
each; the former 512-entry inventory bound could not accept either tree. This
private metadata inventory limit permits those published dependencies while
retaining depth eight, protected ownership and link checks,
and the original deadline. It changes no worker unit property, filesystem grant,
account permission, job output quota or qualification requirement.
The pinned published tree includes the DCP dependency directory `_manifest`.
The private [inventory and restoration procedures](product-coverage.py) accept
an ASCII letter, digit or underscore as the first component character, followed
by ASCII letters, digits, underscores, dots or hyphens, up to 128 characters.
Leading dots, consecutive dots, separators and other characters remain invalid.
This component grammar applies before file opening during inventory and
restoration; no-follow handles, ownership, byte, depth and deadline checks still
apply. The [published-name controls](test_product_coverage.py) exercise the seven
actual manifest names with owned bytes and an inventory/restoration round trip.
They do not issue a root coverage owner or demonstrate native qualification.

The authenticated bundle in run 37394601651 contains an 84,042,030-byte DCP
binary and a 61,567,278-byte tunnel dependency. Neither fits a 32 MiB buffered
read. `hash_published_file(parent_fd, name, deadline, *, uid=0, gid=None,
mode=None, cap=FILE_LIMIT)` borrows the directory FD, validates size
before reading, hashes with bounded chunks, and returns the SHA256 plus pinned
file identity after EOF, named-identity, close and original-deadline checks.
Both `snapshot_tree(..., published_files=())` and
`restore_metadata(..., published_files=())` use this procedure. The optional
immutable tuple contains `(relative_path, length_bytes, sha256)` rows projected
by the launcher from the already selected and pinned application audit.
`published_file_bindings(rows)` validates tuple shape, at most 256 rows, safe
relative paths, positive lengths up to 128 MiB, unique names and SHA256 syntax;
it grants no selection, admission or lease. A declared file must match its exact
length and hash, and every declared file must exist in the inventory. Rows cannot
raise eligible-pair limits. Unlisted files and eligible pairs keep 32 MiB.
Restoration requires declarations to match its captured original hash map before
any metadata change. Inventory also rejects a file that exceeds the
remaining tree budget before opening it. Restoration passes the remaining
budget into the file preflight and still verifies every original hash before
changing any mode or group. The buffered `read_file()` API is unchanged.

The [private bundle layout](README.md#private-bundle-source-layout) uses the
compile-owned launcher marker to keep one root-only application input outside
both tools, before bundle canonicalization. In this generated variant the owner
receives `published_files=()` because there are no in-tool application-bundle rows.
The ordinary default marker remains false and still projects the exact selected
audit's in-tool length/hash tuples. Neither branch accepts an environment value,
worker path or caller-provided audit. The source bundle remains fully audited and
copied into the existing `/run` application workspace under the same permissions.

`measure_product_tool_inventory` in [prepare.py](prepare.py) measures the complete
sealed tool using this module's actual bounded snapshot and streaming procedures,
without a declaration override or file exclusion. Its `published_inventory`
summary accompanies the complete tool SHA map and records file/directory counts,
total bytes, largest file and relative-path depth. It uses the original build deadline
including FD closure and repeat verification. The 32 MiB file / 256 MiB tree /
2,048-entry / depth-eight limits remain unchanged. The previously retained
`tree-byte-bound` failure does not identify the precise triggering file or
snapshot; remaining tool sizes and native product execution still require a new
actual preparation and qualified run. This source-only change claims no gain in
coverage and supplies no admission or Trusted authority.

This metadata procedure reuses the [application bundle limit](../../../scripts/evidencehost_linux_application.py)
without changing that limit, worker access, unit limits or deadlines. The closed
private startup categories `file-shape`, `published-binding` and
`published-files-missing` record only guard families, not raw exception text or
authority claims.
Streaming [procedure controls](test_product_coverage.py) use sparse owned files
and demonstrate actual reads and restoration, not native qualification.

## Private tool-sealing failure diagnostics

Run 37401725081 retained a `tool-sealing` failure labeled `OtherException` with
no product category. The previous [preparation diagnostic](prepare-product.py)
mapped `ProductCoverageError` to that fallback, so this label did not exclude
`file-shape`, `tree-byte-bound` or another inventory guard. It does not identify
an exact failing file, operation or exception, and motivates diagnostics only.

`capture_product_preparation_failure(tool, receipt_path, phase, error, *, deadline,
expected_owner_uid=0, checkpoint=None)` now adds nullable `product_category` and
`checkpoint` fields to the same private schema. Checkpoint defaults to null;
`permission-sealing` and `inventory` are the only non-null values. The owning
[prepare.py](prepare.py) sets the former before changing permissions and the latter
immediately before complete tool measurement. Invalid checkpoints fail capture
before filesystem I/O and cannot replace the original preparation failure.

The `ProductCoverageError` diagnostic family is the exact class-name label on a
`RuntimeError` instance. Only its single exact-string argument can become a
category, and only when present in the fixed 18-category inventory/streaming
allowlist: `basename`, `deadline`, `directory-changed`, `directory-owner`,
`directory-path`, `file-changed`, `file-count`, `file-gid`, `file-mode`,
`file-shape`, `file-short`, `published-binding`, `published-files-missing`,
`tree-bound`, `tree-byte-bound`, `tree-changed`, `tree-mode`, `tree-owner`.
Unknown or malformed arguments and messages from another exception family yield
null; exception formatting and `str(error)` are never used. Existing exception
labels retain their previous handling. These labels are diagnostic data and
cannot authenticate a class's origin, issue authority, or establish execution.

The same original deadline, stat-only target inspection, exclusive/no-follow
root-owned `0600` destination and 4096-byte record bound remain in effect.
Capture failure still leaves the original exception to be rethrown. File/tree
bounds, bytes, filesystem selection, worker policy and proof requirements do not
change. The new [diagnostic controls](test_product_preparation_diagnostics.py)
are defined source only at this handoff; no tests or native run have executed for
this change, and the prior failure's actual guard remains unknown.

Official reports are limited to 2 MiB XML and 4 MiB JSON. Utilities share the
existing deadline and a maximum five-second operation bound. No added job timer
or grace period can create success after expiry. Private logs and receipts stay
root-owned 0600, and raw exception/output text is never public.

[Procedure tests](test_product_coverage.py) use actual small files and owned
processes, including SIGKILL without an exit hook and two EOFs. They issue no
root owner or admission. Linux mount/systemd/credential proof and actual
qualified product coverage require separate native execution; source readiness
or portable tests are not such proof.
