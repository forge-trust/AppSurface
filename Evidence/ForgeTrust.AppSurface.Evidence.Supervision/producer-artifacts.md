# Linux producer report artifacts

> **Integration status:** [`LinuxProducerArtifactCustody`](LinuxProducerArtifactCustody.cs) is the internal wrapper over [`LinuxRetainedReportSet`](LinuxRetainedReportSet.cs). The existing internal coverage control owner invokes it for one inventory and its subsequent reads. It is not a public API; callers use the authenticated control protocol, which owns token and offset sequencing. The native workspace and producer guards remain mandatory. See the [retained report custody reference](report-custody.md) for the underlying filesystem reader.

## Purpose and trust boundary

A protected Linux run needs a way to retain and inspect the exact coverage reports written by its fixed producer while preserving root custody and the run's original account identities. The producer artifact set binds that work to the already selected owner, accounts, workspace, producer, and results token. Its purpose is bounded file custody and data transfer after the producer reports readiness; it does not itself establish workload admission, accepted coverage, proof, or successful producer exit. A nonzero test exit code must not be reinterpreted as success by the presence of report files.

The wrapper is internal to `ForgeTrust.AppSurface.Evidence.Supervision`. It is not a supported public package API. The underlying `LinuxRetainedReportSet.OpenActual` remains a native filesystem primitive that accepts an exact root and inventory supplied by its caller. The wrapper obtains those from authentic producer/workspace state and retains owner/account references while artifact operations remain open. For low-level root, metadata, and chunk behavior, use the [report custody reference](report-custody.md).

## Capture contract

The factory shape is:

```csharp
internal static LinuxProducerArtifactCustody Capture(
    EvidenceProtectedLaunchInput input,
    LinuxOwnerActivation owner,
    LinuxRunAccounts accounts,
    LinuxProducerWorkspace workspace,
    LinuxProducerProcess producer,
    string resultsToken,
    CancellationToken token)

internal IReadOnlyList<LinuxRetainedReportSet.ReportInfo> ListReports(
    CancellationToken token = default)

internal byte[] ReadChunk(
    string relativePath,
    long offset,
    int maximumBytes,
    CancellationToken token = default)
```

`Capture` returns the one internal holder; `ListReports` returns its immutable report snapshot and `ReadChunk` returns a new byte array. `ResultsToken` returns the selected token string as data. `Dispose` joins operations serialized on the holder and closes the retained report descriptors. These signatures are internal implementation details, not a supported library surface.

`Capture` first requires the same owner and accounts references bound to the workspace and producer, and requires those exact workspace references to agree as well. It then calls `producer.RequireReportsReady(owner, accounts, workspace, token)` before opening report data. Readiness is a sequencing condition supplied by the authentic producer; it is not a successful exit claim or an admission decision. Any reference mismatch or failed readiness check rejects before opening.

The factory asks the workspace to validate ownership with `RequireOwnedBy` and the token with `RequireResultsToken`. The token must use the existing Linux input grammar: 1 to 96 ASCII letters, digits, or dashes. The validated token selects a directory beneath the workspace's root-owned `ResultsRoot`. Its parent is root:root mode `0711`; the selected directory is owned by the retained subject UID/GID with mode `0700`. Do not accept a caller-provided absolute path in place of this token or derive the token from a report name.

After those checks, root-side `getdents` and descriptor-relative traversal discovers the actual tree. The resulting retained set is backed by `LinuxRetainedReportSet.OpenActual` using the actual selected root metadata and the names discovered from the opened tree. Names or metadata supplied as detached data are comparison inputs only; they do not grant filesystem authority. Linux x64 and mandatory `openat2` behavior apply, with no pathname fallback when required kernel support is unavailable. The exact ancestor and beneath-root resolution rules are described in the [low-level reference](report-custody.md#construction).

## Inventory and limits

Discovery is recursive and bounded. The tree may contain at most 64 accepted report files, each no larger than 1 MiB, with a 256 MiB aggregate ceiling. Each accepted file must be a regular file with exactly one link and the expected subject ownership and safe metadata. The accepted filename is exactly `coverage.cobertura.xml`; unexpected names, file types, links, unsafe modes, or metadata changes reject the set. Traversal also has bounded retained-node and directory-enumeration work, as detailed in the [custody reference](report-custody.md#construction).

These checks define which files can be retained; they do not validate report XML, prove that coverage was accepted, or attest that a test run succeeded. An empty report set or empty file is allowed by the underlying reader and is not evidence of successful producer output. The current count and per-file limits also mean the theoretical total of accepted file bytes is at most 64 MiB, despite the independent 256 MiB aggregate guard.

## Inventory and reads

The immutable inventory exposes each discovered relative path, byte length, and SHA-256 digest. `ResultsToken` is separately available as data on the same custody object; together these let the internal control owner bind its inventory reply to the workspace-selected token. The digest is computed from the retained file bytes; by itself it is neither authorization nor proof. Listing rechecks the retained names and metadata before returning the inventory.

`ReadChunk` addresses an exact inventory path and requires the next exact offset for that file, starting at zero. The wrapper accepts a requested chunk bound from 1 through 128 KiB and may join up to two 64 KiB reads from the underlying retained reader. The control owner currently requests 32 KiB to fit the response frame. Unknown paths, replayed or skipped offsets, invalid bounds, and reads after final EOF fail. Each file has an independent offset sequence. An empty file yields one empty final chunk.

Reads recheck the retained tree, descriptor identity, names, ownership, mode, size, and change/modify times around data access. A read/check failure, invalid request, cancellation, or deadline expiry poisons the set; retrying or restoring files does not revive it. Reads and disposal are serialized. Returned inventory or chunk bytes are data snapshots, not continuing custody authority. See [Reads and closure](report-custody.md#reads-and-closure) for the underlying engine's exact behavior.

## Required lifetime and teardown order

The wrapper retains references to the original input, owner, accounts, workspace, and producer while it is open. Keep those native holders valid for the full report listing and chunk-read interval. Coordinate all readers and handlers that can use the set and join them before disposal.

The required lifecycle order is:

1. Obtain the authentic producer's reports-ready result for the exact owner, accounts, and workspace.
2. Capture the report set using the workspace-owned results token and retain its account identity.
3. List and read only through the retained immutable inventory while the original deadline and cancellation token remain in force.
4. Join every handler and read using the set.
5. Dispose the report set after every handler and read has joined; this closes its retained report descriptors.
6. Seal the producer workspace so it rechecks and chowns the original result tree after those joins, while the account holder remains retained.
7. Release accounts only after producer workspace final seal has completed.

Do not close or release accounts while a handler/read may still be active. Do not transfer ownership based solely on a completed read, hash, inventory, readiness flag, or producer exit code. The broader supervision teardown and custody constraints are documented in the [package README](README.md) and the [report custody reference](report-custody.md).

## Pitfalls and limits

- The results token is an opaque selector under the retained workspace results root. It is not a filesystem path or a permission-bearing credential.
- A syntactically valid token does not prove the selected directory is safe; workspace ownership checks and root-side opening perform that binding.
- Directory enumeration is closed to the expected report name. Extra files and unexpected nested content are failures, not ignored artifacts.
- File discovery and hashing do not parse Cobertura XML or decide whether reported coverage is acceptable.
- The inventory digest and immutable data shape do not create admission, proof, or successful-run status.
- Report custody depends on real producer readiness, root filesystem checks, original deadlines, joined handlers/readers, and final account/workspace custody. Portable tests or detached metadata cannot establish those Linux facts.
- Do not describe this internal wrapper as a public API. The authenticated control protocol owns the external inventory and read operations; callers cannot provide a root or choose a different token after selection.


## Selected results descriptor chain

[`LinuxSelectedProducerResultsRoot`](LinuxSelectedProducerResultsRoot.cs) retains the existing
component-by-component `openat2` chain used after the authentic `Capture` factory has validated the
input, owner, accounts, workspace and settled producer. It compares the selected 0700 subject UID/GID
root with the workspace's kernel metadata and requires its immediate results parent to remain root-owned
0711. Every recheck compares retained descriptors and their original parent names; renamed or replaced
ancestors, symlinks, changed owners, unsafe modes and expired original deadlines reject. Disposal closes
the acquired descriptors in reverse order and is idempotent. Callers must join readers before disposal.

This internal holder exposes actual filesystem descriptors for the existing custody procedure and its
[native Linux controls](../ForgeTrust.AppSurface.Evidence.Supervision.Tests/LinuxSelectedProducerResultsRootNativeTests.cs).
It cannot authenticate a producer, create a workspace or grant artifact custody. The outer factory still
selects the actual token directory only after the original readiness and ownership checks. Use the holder
only for that descriptor-chain responsibility; use the authentic `Capture` path for producer artifacts.
The twelve new root-only controls require a fresh Linux run. Compilation or skipped macOS rows are not
native evidence. The existing measurement partition requires all twenty-nine audit/discovery/selected-root
cases and exact test names; it retains its original byte, time and coverage bounds.
