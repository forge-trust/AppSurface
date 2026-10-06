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
were defined and unexecuted at their initial source handoff. A later verified run
37404309689 records `ProductCoverageError` / `tree-byte-bound` at checkpoint
`inventory`, before any entry ran. It establishes an aggregate Tool guard failure,
not the triggering filename or a successful publication size.

## Fixed private Linux publication

The private [preparer](prepare.py) now applies the fixed properties
`RuntimeIdentifier=linux-x64`, `SelfContained=false` and `NuGetAudit=true` to the
CLI metadata refresh, subsequent locked restore, both Debug publishes and the
Host clean/rebuild. The canonical RID matches the already authenticated
10.0.401 Linux-x64 SDK provenance. `UseAppHost` keeps its SDK default; the actual
entry still invokes the fixed dotnet host with the top-level CLI DLL. These
properties affect only the root-owned private build copy, not package defaults,
the immutable product171f build, selected three DLL/PDB pairs, ABI comparison,
Coverlet provider, application bundle or worker unit permissions.

The earlier authenticated publication map contained 272 cross-platform runtime
files, including TreeSitter and Onigwrap assets. It did not record their lengths.
RID selection asks the SDK to resolve the required Linux assets through the
normal publish pipeline; no manual file deletion, exclusion or dependency
selection is performed. Neither this source change nor that path count proves
the resulting Tool fits the unchanged 32 MiB/file, 256 MiB/tree, 2048-entry and
depth-eight guards. Complete sealed Tool measurement remains mandatory.

### Metadata APIs and ordering

`snapshot_private_publish_locks(root, deadline, expected_paths=None,
expected_owner_uid=0)` reads every `*.lock.json` under a protected root through
retained no-follow FDs. It bounds locks to 256, each to 256 KiB, combined bytes to
8 MiB, directory depth to sixteen and scanned entries to 32768, charging entries
before sorting. A supplied tuple must match the complete path set. Files must
be owned, regular and single-link; growth, named substitution, links, unexpected
paths and nonregular entries reject. The UID override is an ordinary metadata
test seam, never a root owner or admission. Archive child modes are not rewritten
by this read procedure; their protected root prevents worker access.

`validate_private_publish_lock(before_raw, after_raw)` accepts duplicate-free
JSON with the same lock version and byte-equivalent canonical original target
groups and rows, preserving package versions, content hashes, dependency
requirements and project nodes. The only new group allowed is
`net10.0/linux-x64`. Every new-group node must exist under the inherited
`net10.0` group with an identical complete row; a subset is permitted. Unknown
SDK/RID nodes fail closed for review, not dynamic enrollment. Existing RID groups
must remain unchanged. Malformed JSON, duplicate/case-folded members, non-JSON
numbers, removed groups, changed rows and other new RIDs reject with the existing
fixed preparation failure.

`prepare_private_linux_publish(build, source_files, protected_roots, runner,
dotnet)` first snapshots and authenticates the build-copy locks against the
frozen source map. The fixed `protected_roots` mapping contains source, baseline,
product_source and product_build; none may contain the mutable build copy.
Their original lock bytes and path sets must remain unchanged. The initial
build-copy-only restore uses `--force-evaluate --use-lock-file` and
`RestoreLockedMode=false` with auditing enabled to generate RID metadata. This
one metadata refresh is followed by strict graph comparison **before** the
ordinary `--locked-mode` restore with the same RID/self-contained properties.
No original source, baseline or pristine product lock is rewritten. The refreshed
lock bytes must also remain unchanged through locked restore and publication.

`private_publish_assets(build, deadline, expected_owner_uid=0)` reads the fixed
CLI `obj/project.assets.json`, capped at 8 MiB, through the retained protected
build root and no-follow child FDs. It requires a dictionary target named
`net10.0/linux-x64` and the exact selected private CLI project path. Applying a
RID only to `publish --no-restore` without these assets is invalid. The return
value binds its relative path, target, byte length and SHA256; it does not load
an assembly or grant runtime authority.

`verify_private_linux_publish_metadata(build, metadata, protected_roots,
deadline)` repeats lock/assets/protected-root verification after both publishes
and before final preparation publication. `private_linux_publish` in the build
binding records the fixed properties, per-lock before/after lengths and hashes,
assets identity and each protected role's count and canonical lock-map digest.
It is provenance of actual checks, not a binary compatibility, coverage or
qualification claim. All file reads, closes, commands and final checks share the
original Runner deadline; there is no new grace period or command owner.

### Validation and pitfalls

The eight new [metadata controls](test_preparation.py) define actual owned-file
snapshots, inherited-row neighbors, version/hash/requirement drift, unknown RID
nodes, bad/duplicate JSON, no-follow/link/count/byte/deadline boundaries, exact
assets targets and FD closure after growth/substitution. The previous 36 test
bodies are preserved. These controls are source definitions only at this handoff;
no refresh, restore, build, test or native publication has run for this change.

A refresh may affect several project locks. It cannot be accepted just because
the command exits zero; all original groups must still match and newly required
SDK/RID nodes need a separately reviewed plan if rejected. Keep the same fixed
properties on Host clean so it removes the correct RID-specific branch outputs.
Preserve the actual lock/command receipts on failure. The Linux package graph,
actual Tool fit and genuine mixed CLI/Host execution remain unverified. The local
SDK targets inspected for design were 10.0.102, not the native pinned 10.0.401.

Official reports are limited to 2 MiB XML and 4 MiB JSON. Utilities share the
existing deadline and a maximum five-second operation bound. No added job timer
or grace period can create success after expiry. Private logs and receipts stay
root-owned 0600, and raw exception/output text is never public.

[Procedure tests](test_product_coverage.py) use actual small files and owned
processes, including SIGKILL without an exit hook and two EOFs. They issue no
root owner or admission. Linux mount/systemd/credential proof and actual
qualified product coverage require separate native execution; source readiness
or portable tests are not such proof.
