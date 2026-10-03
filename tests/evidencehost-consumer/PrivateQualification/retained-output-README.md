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
