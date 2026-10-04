# Private VSTest diagnostic retention

This candidate captures private raw diagnostic data after the caller has already
established authenticated owned exit, stopped all selected units, joined handlers
and pumps with error-free EOF, and checked its existing quotas and broker terminal
checkpoints. A successful copy is **not** a consumer success, process identity,
qualification, admission, Trusted proof or coverage result. The original failure
must survive missing traces, rejection, capture errors and cleanup errors.

## API and ownership

[`capture_traces`](../../../scripts/evidencehost_private_vstest_diagnostics.py)
has the shape:

```python
capture_traces(result_parent_fd, result_tokens, destination_fd,
               subject_uid, results_gid, *, deadline,
               expected_root_uid=0) -> bool
```

The FDs are borrowed and remain open, with offsets unchanged. The root caller
must duplicate and retain its actual selected results-parent and destination FDs
before its existing handle-close operation. It supplies exactly one already
recorded safe token, not a directory discovered by this helper. This module opens
independent directory descriptions relative to the borrowed FDs, and closes all
its own FDs. It has no systemd, subprocess, enrollment or exit-establishment API.
The runtime owner is root UID/GID 0. `expected_root_uid` labels current-user
portable file controls only; it cannot establish root or protected authority.

The results parent and token must be actual directories owned by the subject UID
and results GID, with mode `2770`. The destination parent must be owned by root
and have owner rwx, no special bits and no group/other write. The caller is
responsible for the borrowed root FDs' original named-parent provenance; this API
cannot reconstruct an external name from a directory FD. It rechecks retained
parent, named token and selected file identity throughout its operation.

## Exact sibling selection

The root-selected private `--diag` argument is the **sibling**
`<recorded-token>-qualification-vstest.log` directly below the selected
test-output parent. Logs must not be put inside the producer's token: the
producer's report grammar permits only its coverage report there. This helper
does not change the report filter, functional argv or production diagnostics.
The separate integration owner adds the private argument and supplies the FDs.

For the same basename without its final `.log`, the only companions selected are:

```text
<token>-qualification-vstest.datacollector.yy-MM-dd_HH-mm-ss_fffff_<managed-thread-id>.log
<token>-qualification-vstest.host.yy-MM-dd_HH-mm-ss_fffff_<managed-thread-id>.log
```

The timestamp is ASCII with five fractional digits, and its calendar/time fields
must be valid. The suffix is a positive managed thread ID, bounded to Int32; it is
**not a PID** or a process-authentication fact. At most one exact file per role is
selected. Two current files for one role reject diagnostic capture; this module
does not choose the first, newest or last. Rolling `.bak` and GUID fallback names
are excluded, as are unfamiliar naming forms. An absent/unrecognized companion
remains missing data. A selected symlink, FIFO, directory or hardlink is rejected.

Microsoft's [VSTest CLI documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest#options)
describes companion logs, but its examples do not define this exact grammar.
The official `v18.7` source at commit
`6f58ced50b40e074a07ffc21fb2d2eef95d31b60` defines
[host naming](https://github.com/microsoft/vstest/blob/6f58ced50b40e074a07ffc21fb2d2eef95d31b60/src/Microsoft.TestPlatform.CrossPlatEngine/Client/ProxyOperationManager.cs#L463-L474)
and [collector naming](https://github.com/microsoft/vstest/blob/6f58ced50b40e074a07ffc21fb2d2eef95d31b60/src/Microsoft.TestPlatform.CrossPlatEngine/DataCollection/ProxyDataCollectionManager.cs#L327-L336).
Host timestamp culture is invariant; collector timestamp culture is current.
The closed native environment expects ASCII naming; other representations are
not guessed or widened.

The inspected SDK `10.0.401` `vstest.console.deps.json` and bundled testhost report
`18.7.0-release-26423-113`. The SDK source dependency manifest instead reports
`18.7.0-release-26374-102`. Exact prerelease binary-to-source equivalence is
**unproved**; project package `18.5.1` does not establish the actual runtime host.
The official source grammar is a bounded diagnostic selection policy, not proof
that a particular companion ran. Earlier preparation used the SDK source manifest
and placed logs within the token; that unexecuted two-file history is preserved
outside the checkout and superseded by this sibling policy.

## Bounds, snapshots and retained shape

The parent listing stops at the first entry beyond 64. No recursive traversal or
broad glob is used. All selected source files must have subject UID/results GID,
regular type, one link, and ordinary owner-readable mode: `400`, `440`, `444`,
`600`, `640` or `644`. Executable, special-bit and group/other-write files fail.
The helper does not chown, chmod or otherwise normalize any source node.

The monotonic budget is the lesser of the caller deadline and five seconds from
entry. It is checked during listing, selection, each bounded read/write and final
rechecks, and checked afresh after every owned FD has been closed before returning
True. Cleanup uses the original clamped deadline, not a renewed allowance. A
close error or a deadline crossed during close returns False; a completed private
copy may remain and carries no success authority. File reads retain at most
128 KiB: complete bytes up to that limit, or
exactly a 64 KiB prefix and 64 KiB suffix for a larger stable file. There is no
unbounded read or truncation of the source. Unexpected EOF, read failure, growth,
substitution, changed directory listing or deadline expiry makes capture False.
A synchronous filesystem call can still stall: the enclosing caller's finite
process owner remains responsible for any external hard deadline.

The destination is a fresh exclusive `vstest-diagnostics` directory, root mode
`700`, containing exactly root mode `600`, single-link files:

- `runner.log`
- `collector.log`
- `host.log`
- `index.json`

Missing roles are empty files, with `length: null` in the index. An existing empty
source instead has `length: 0`; neither is successful startup evidence. If all
roles are missing, capture returns False before destination creation. The JSON
contains only `roles`, each with `role`, original `length`, `retained_length`,
`truncated` and SHA256 of **retained** bytes. It contains no source filename,
token, path, exception, stdout/stderr or trace message. The index is at most 4096
bytes, each raw role at most 131072 bytes, and total files at most 409600 bytes.
Partial capture is best-effort removed only while newly created named identities
still match. Cleanup failure may leave a private quarantine; it cannot upgrade
False into success or replace the consumer failure.

## Preparation status and controls

[`test_private_vstest_diagnostics.py`](../test_private_vstest_diagnostics.py)
defines portable actual-file/FD data controls for binary/empty/missing data,
size boundaries and prefix/suffix retention, exact names and ambiguity, source
permissions/owners/links/FIFO, bounded fanout, substitution/growth/short reads,
read errors, entry and mid-read expiry, exclusive destination and write failure.
A real-FD procedure advances a controlled clock during the final owned close and
requires False with both borrowed FDs still usable and the source unchanged. The
duplicate-token negative has an otherwise-valid binary trace and a distinct
fresh destination, alongside a fresh positive one-token neighbor, so neither
missing data nor an existing destination can mask the token-count guard.
Current-user stat projection controls reject invalid file identity; they do not
issue a protected lease or authenticate a native root caller.

The **initial source handoff** defined these tests without executing them.
Later local validation receipts distinguish portable data controls from the
pristine-baseline transport build. No SDK,
subject, root, systemd or native qualification was executed; no production
registry, guard, unit, timeout, report grammar or original argv was changed.
Parent integration and peer review are required before a finite portable suite.
