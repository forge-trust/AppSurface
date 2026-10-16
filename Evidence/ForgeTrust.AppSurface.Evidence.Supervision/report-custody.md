# Retained report file reader

`LinuxRetainedReportSet` is an internal file-custody primitive in the
[supervision package](README.md). It does not establish joined-producer,
subject-group, output-pump, workload or admission authority. There is no server
integration in this packet. Future protected integration must privately bind
authentic producer/group/pump joins before construction; possession of this
reader, an inventory, a digest or metadata is insufficient.

## Construction

`OpenActual(resultRoot, expectedRoot, reportPaths, expectedUid, expectedGid,
deadline, token)` requires Linux x64 and effective root. The caller supplies the
absolute root it selected and the `LinuxProtectedMetadata` obtained by that
authentic selection. Device major/minor, inode, owner, group and mode must match
the actual opened directory before report traversal. These supplied values are
comparison data and must never be treated as uploaded permission.

The factory uses the existing mandatory `openat2`, `statx` and `getdents64`
operations from the [contracts filesystem implementation](../ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxFileSystem.cs).
It retains the complete absolute parent chain. Explicit absolute ancestors may
enter the selected filesystem; every lookup beneath the selected root uses
no-XDEV, no-symlink, no-magic-link and beneath resolution, without a fallback.
An O_PATH probe verifies type before a content open. Links, special files,
multiple-linked regular files, unexpected names and unsafe modes reject.

The supplied inventory is exact and recursive. It permits at most 64 files,
each at most 1 MiB, with an independent 256 MiB aggregate ceiling. The current
count and per-file limits imply at most 64 MiB in practice. Relative names must
already be normalized: no traversal, backslash, colon, control characters,
empty components, case aliases or file/directory collisions. Limits are 4095
UTF-8 bytes per path, 255 per component and 32 components. Retained nodes and
directory enumeration blocks are also bounded. Empty files and an empty set
are permitted; they establish no successful output.

Result nodes must have the supplied UID/GID, owner read access (and directory
search access), no special mode bits and no group/other write access. Ancestors
must be root-owned and untrusted-unwritable. The initial full metadata is
retained; future descriptor/name samples must match it. Ancestor comparisons
preserve identity and mode while allowing unrelated directory-list churn.

## Reads and closure

`ListReports` returns read-only path/length/SHA256 data after rechecking the
tree. Initial hashes consume the entire actual retained file, sample custody
around reads, and require actual EOF at the sampled size.

`ReadChunk(path, offset, maximumBytes, token)` consumes only the next offset for
that report, starting at zero, with a maximum chunk size of 65536 bytes. Offset
replay, skipping, unknown paths and invalid bounds fail. The final chunk checks
actual EOF; subsequent reads fail. An empty file yields one empty final chunk.
Tree inventory, names, descriptor identity, UID/GID/mode, size and change/modify
times are checked around returned chunks. Reads use the original monotonic
deadline and cancellation; neither is replaced by a per-read allowance.

Reads and disposal are serialized. A failed check, invalid request, cancellation
or expiry poisons the set. Restoring a path or its contents cannot revive the
reader. Disposal waits for a current read and closes retained handles; reads
and listing after disposal fail. Previously returned metadata is only a sample.

## Portable controls and limits of proof

`FileReader.CapturePortableFileForTesting` transfers an actual temporary
`FileStream` into the same hash/chunk/lifecycle engine. It checks portable file
length and optional test samples, not Linux inode/name/root custody, and cannot
construct a `LinuxRetainedReportSet`. Its tests claim no native Linux authority.
Detached inventory, size, metadata and root-identity helpers likewise provide
comparison coverage only. Native openat2/traversal and protected integration
require separate Linux verification.
