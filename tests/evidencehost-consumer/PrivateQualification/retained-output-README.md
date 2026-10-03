# Private retained output collection

[`retained-output.py`](retained-output.py) provides
`collect(completion, expected_plan_bytes: bytes, deadline: float, *, entry: str = "cli") -> dict[str, bytes]`
for the private qualification controller. Call it before closing the actual
[launcher completion](../../../scripts/evidencehost-linux-launcher.py).
It returns opaque bytes, not a passed result, manifest authority, claim or gate.
The caller retains responsibility for actual C# structural verification, all
compiled source/build/policy/bundle/provenance bindings, cleanup and qualification.

## Controller authority and ordering

The importing controller MUST first assert
`type(completion) is launcher._LaunchCompletion`, using the exact launcher module
that performed the real launch. This helper deliberately does not dynamically
import another copy of the hyphenated launcher, whose distinct class object would
not authenticate the controller's completion. Attribute shape or JSON is not an
authority substitute. The helper's filesystem checks describe only the actual
retained bytes; they cannot establish the missing exact-type check themselves.

Use the parent-frozen expected canonical plan bytes, never a plan read from output
as its own expected value. Supply the existing caller-owned monotonic deadline;
the helper never renews it. Worker UID/GID must be literal 65010/65011 and output
slot `qualification`. Descriptor output-parent identity must equal the completion
receipt; completion output path must equal the descriptor parent plus that fixed
slot. All identity values are exact integers, with no Boolean substitutes.

The helper obtains caller-owned duplicates using
`duplicate_output_directory()` and `duplicate_output_parent()`, closes all of its
duplicates on every success/failure path, and never calls `completion.close()` or
deletes accounts. The controller must close completion strictly, with all collection
duplicates already gone, before any gate invocation; a close failure remains a
failure and retains the existing quarantine semantics. No acceptance is inferred
from successfully reading an output directory.

## Exact tree and bounds

The caller selects the literal data layout `entry="cli"` (default) or `entry="host"`;
all other values reject before duplicate I/O. This selects a closed output grammar,
not execution authority, a caller path or an arbitrary producer slot. CLI contains
exactly these four regular files:

- `evidence-plan.json`
- `evidence-manifest.json`
- `evidence-summary.json`
- `qual-coverage/merged/coverage.cobertura.xml`

Host contains exactly `manifest.json` and
`qual-coverage/merged/coverage.cobertura.xml`. No plan/summary is synthesized.
Host's returned bytes are only those two actual files; its already protected
expected plan remains the controller's separate structural-verification input.

Only `qual-coverage` and `qual-coverage/merged` are allowed child directories.
There are no optional slots or accepted staging leftovers. The parent policy must
declare producer `qual-coverage`, only the report slot, and relative root `merged`.
The [coverage producer](../../../Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs)
writes only declared slots; its gate/merge staging is separate and removed.
The [protected writer](../../../Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs)
prepends the producer ID to the physical report path. Changing the policy to
include gate/summary slots requires a separately reviewed collector grammar; it
must not be accepted by discovering files or trusting manifest-provided paths.

Root output/parent and every retained child directory require exact worker UID/GID
and `0700`. Files require exact worker UID/GID, `0600`, regular type and one link.
Every directory component of the current absolute parent is opened without
following links. The retained parent, current named parent and named output must
match completion device-major/device-minor/inode/owner receipts; retained and
named metadata is rechecked at the end, including the current absolute parent.
All child/file FDs remain held through final rechecks. Reads use `O_NOFOLLOW`,
`O_NONBLOCK` and `O_CLOEXEC`, and metadata comparisons include mode, link count,
length, mtime and ctime before/after each read and final directory pins.

Limits remain explicit even though the grammar allows only four files: file count
64, directory depth 8, each file at most 20 MiB, aggregate file bytes at most
20 MiB, read chunks at most 64 KiB. The deadline is checked on entry, each tree
entry, open/read chunk, EOF and final recheck. Missing/extra entries, symlinks,
hardlinks, special files, unsafe modes/owners, substitution, growth, short reads,
plan mismatch, expiry or descriptor closure failure reject with the fixed
`RetainedOutputError("retained-output-failed")`. No hostile bytes, paths or native
exception text are logged or retained in that error.

The CLI's existing three `evidence-*.json` filenames are checked against its exact
four-file grammar, including byte-for-byte expected plan equality. The
[HostBootstrap finalizer](../../../Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceHostBootstrap.cs)
writes only `manifest.json` alongside declared producer artifacts; its protected
writer also prefixes the physical report with the producer ID. The host grammar
matches those actual paths. This helper does not compare a nonexistent Host plan
file or silently rename/synthesize outputs; the controller still independently
verifies the Host manifest using its frozen compiled expected plan.

## Portable source-stage controls

`_collect_fds` is an intentionally read-only data/FD seam. Its owner overrides
permit portable tests of actual temporary files owned by the local user; they
cannot create a launcher completion, reserve accounts or issue a lease. Incoming
FDs are borrowed, while every FD opened by that seam is closed on all paths.
[`test_retained_output.py`](test_retained_output.py) covers exact binary reads,
plan mismatch, missing/extra output, link/special-file rejection, modes/owners,
named ancestor/output substitution, growth, late file replacement, byte/count/depth
bounds, expiry and descriptor cleanup. Public-wrapper controls are negative only;
no synthetic object is counted as a protected positive completion.

No tests have run at source-ready. These prepared controls make no root/native,
owned-exit, producer, consumer, qualification or gate claim.

## Private worker fatal-journal diagnostics

The [private launcher](../../../scripts/evidencehost-linux-launcher.py) queries only
its root-generated worker unit with `journalctl --lines=256`, retaining a prefix
of at most 16 KiB within the existing five-second read/reap bound. A full prefix
is marked truncated. This widens preceding stack context; it does not guarantee
that an initiating fatal message survives every long journal or identify its cause.

`WORKER_JOURNAL_LIMIT` is separate from the unchanged 4 KiB
`FAILURE_DIAGNOSTIC_LIMIT` for safe failure JSON. Raw journal bytes stay in the
exclusive no-follow `0600` journal file beneath the protected diagnostic directory;
only bounded byte counts, states and allowlisted ASEVD tokens enter the closed JSON.
Raw content and tokens are diagnostic observations, never admission or gate authority.
The [private archive controller](run-qualification.py) selects only the fixed journal
filename with the same 16 KiB limit, retaining existing ownership, no-follow,
identity, five-second archive and total-size checks.

Prepared [launcher controls](../test_linux_launcher.py) use a bounded real-pipe
writer and model journal tail selection to retain a header more than 32 lines back.
[Archive controls](test_diagnostics.py) check separate raw/JSON limits, fixed names,
private modes and link rejection. These changes are private diagnostic preparation;
no new tests or native execution have run for this fork at source-ready. They do not
change the original failure/status, execution caps, lifecycle or accepted proof.
Qualification outputs remain `None`/`None`; production registries stay empty.

## Private owned-exit failure observations

This private diagnostic fork adds `launcher-owned-exit.json` alongside the existing
failed-worker files. It is collected only from a negative root owned-exit path;
an ordinary successful worker does not perform this new file I/O. The public
failure schema and exact `wait` ACK remain unchanged. The record cannot repair a
failed ACK, authenticate an uploaded result, or establish qualification.

The [application module](../../../scripts/evidencehost_linux_application.py)
sets closed `JOIN_PHASES` immediately before its existing checks. Its
`join_diagnostic_snapshot(state, pumps, watchdog)` is a diagnostic-only data seam:
it performs no process poll, syscall, callback or wait. Busy state/pump locks are
skipped with unknown (`null`) observations. The phase defaults to `not-started`,
or `unknown` for an invalid internal value. Snapshots include only exact bounded
integers and Booleans for failure/active/group/process-code, pump EOF/error-free,
and disarmed watchdog state. They are observations, not joined-exit receipts.

The [broker](../../../scripts/evidencehost-linux-launcher.py) caches the first
`app-join-fault`, `output-latched`, `inspection` or `deadline` failure observation
as immutable bytes. Later diagnostics cannot replace it, even if a later inspection
recovers. Error classes are a fixed exact-type map, with `unknown` for any other
class; messages, exception args, errno, custom data, paths and child bytes are
never copied. The diagnostic lock and ownership snapshots use nonblocking acquisition
so this mechanism cannot wait beyond the caller's cleanup deadline. All original
calls, guards, deadlines and cleanup ordering remain in place.

`_capture_owned_exit_diagnostic_fd` accepts only the cached closed schema, at most
4096 bytes, and writes the fixed filename exclusively through the retained root
directory FD. It checks root UID/GID, a nonwritable parent, no-follow creation,
regular single-link `0600` file identity and named/retained parent/file rechecks
within a five-second capture bound. Capture absence/error cannot replace the
original launcher failure. Owner overrides exist only for portable real-FD metadata
controls and are never supplied by the real launcher.

The private controller adds only `failure-cli/launcher-owned-exit.json` and
`failure-host/launcher-owned-exit.json` to its fixed whitelist, with a 4096-byte
per-file bound and unchanged canonical USTAR/index/total limits. Unknown files are
not discovered. A missing record means diagnostics were unavailable, not that
owned exit succeeded. Existing [archive controls](test_diagnostics.py) are extended
with fixed-name exact-byte, mode and oversize controls; launcher/application controls
cover first-failure latching, negative ACK preservation, canaries, closed value bounds,
busy-lock skipping, real pumps, exclusive files, links and no-I/O success.

These are prepared, unexecuted portable controls at source-ready. No root, systemd,
native, qualification, coverage or Trusted acceptance is claimed by this fork.

### Private startup observations (owned-exit schema v2)

This incremental private fork retains the same filename and 4096-byte cap, but
uses `issue779-owned-exit-diagnostic-v2`. Historical v1 records are not rewritten
or accepted by the v2 validator. The public launcher failure JSON remains unchanged;
the archive controller still copies only opaque fixed-name bytes and grants no origin.

| New required field | Closed value/meaning |
| --- | --- |
| `startup_phase` | First captured failed `STARTUP_PHASES` operation; otherwise the current phase, initially `not-started`, or `unknown` for invalid internal metadata |
| `startup_error_class` | `none`, `application`, `os`, `subprocess-timeout`, `subprocess`, `memory`, `value` or `unknown`; exact known Python classes only |
| `startup_errno` | `null`, or an exact integer 1–4095 from a recognized direct `OSError` at capture time; Booleans reject |

[`StartupDiagnostics`](../../../scripts/evidencehost_linux_application.py) is an
internal data/procedure seam. `phase` receives only host-defined literals in the
real start path; `observing()` preserves the existing return/exception while recording
failure. `capture(error)` latches one immutable phase/class/errno tuple using a
nonblocking lock. `snapshot()` returns a fresh closed field dictionary without locks,
syscalls, process polling, callbacks or waits. `lock` is available only for portable
contention controls. A busy capture lock skips that observation rather than delaying
cleanup; missing diagnostics are never execution success or a replacement exception.

The actual start path labels request validation and attempt claim, then workspace
validation, selected bundle verification, protected probe audit, dotnet validation,
pipe creation, watchdog process/owner construction, watchdog start/ACK, open check,
armed-file creation, fixed command construction, pre-Popen check, Popen, attachment,
pump construction/start and startup inspection. `running-ack` is an observation
after the original identity/liveness guards, not a substitute ACK. Existing calls,
guards, deadlines and ordering remain unchanged. The original inner catch captures
its exception before replacing it with the existing fixed `ApplicationError`; the
outer observer covers the existing pre-try guards without overriding the first tuple.

No messages, paths, exception arguments, custom data, arbitrary type names or
stack text enter the record. In particular, an `ApplicationError` from a helper
that already discarded native errno yields `null`; this fork never samples a later
ctypes/thread-local errno or guesses the lost value. A known direct `OSError` uses
its current bounded numeric attribute only. An unknown subclass yields `unknown`
and `null`, even if it carries a plausible errno. Memory/value categories remain
diagnostic-only. No startup observation may select a registration, issue a lease,
change stop/join failure, or authorize a passed manifest.

Prepared `StartupDiagnosticControls` exercise actual missing-file FD failure,
pinned-bundle mode rejection and managed-probe rejection through the existing
read-only local-owner seams. They check failed procedure results, exact phases,
original exceptions, unavailable errno, first-failure retention, busy-lock behavior
and canary exclusion without constructing a protected lease. `StartupRecordControls`
cover strict schema/types/bounds, an exclusive actual-FD private write, unchanged
negative wait ACK and no new successful-worker I/O. `StartupArchiveControls` cover
both fixed entry names, exact opaque v2 bytes, the unchanged archive index and cap.
All earlier controls remain intact. These new controls are defined but unexecuted
at this fork's source-ready; no native or qualification result is inferred.
