# Current-source N10 pending-start control

This adapter ports the historical N10 pending-start procedure to the
[current C1 source/build binding](../../binding.py). It requires a fresh CLI
published with the compiled `N10` selector and the actual compact runtime from
that build receipt. N10 requires no peer helper image. Preparation and detached
parser tests do not establish a native result or a coverage contribution.

## Adapter API and ownership

The [fixed dispatcher](../../run-native.py) calls
`execute(context, *, deadline) -> dict` as Linux UID/GID 0. The context has exactly
these keys:

```text
source, selector, capture_path, capture_sha256,
build_receipt_path, build_receipt_sha256, tool_root, runtime_root,
output, generation, binding_validator, binding_validator_sha256,
workflow_identity
```

`selector` is `N10`; `generation` is 32 lowercase hexadecimal characters;
`workflow_identity` matches `github-actions-RUN-ATTEMPT`, with positive decimal
run and attempt numbers. Paths and hashes must identify the actual full current
source capture, fresh build receipt, fixed validator, tool, and runtime. The
current source is verified before any procedure module is imported, and the
adapter repeats actual build/source verification before root staging. Every
subsequent imported module and installed procedure is pinned by that capture.

`output` must already be a canonical root-owned 0700 directory containing exactly
the dispatcher's five root-owned, single-link, regular 0600 inputs:
`plan.json`, `source.json`, `build-receipt.json`, `procedure-inventory.json`, and
`execution.json`. The adapter verifies the actual copies, plan hashes, and
execution identity before adopting this directory. It retains command records,
both original streams for each of the two utilities, `native-private.tar`, and
`N10-evidence.json`; it does not copy protected source/tool/runtime trees into the
artifact output.

Staging uses
[`prepare_verified_inputs(..., source_sealed=False)`](../owner-death/adapter.py).
The source copy retains original 0644/0755 files and 0700 directories so the
original full shell owns its subsequent sealing. Tool and runtime copies are
sealed. Actual copied nodes produce TSV/node inventories; wrapper progress is
checked against these inventories. The original empty helper directory is
created and audited together with the tool and runtime using the initialized
[same-host OS audit](../os_audit.py). No helper image is invented.

## Clocks and original native procedure

The caller starts one 900-second absolute monotonic clock before verification
and staging. It is passed through every phase and is never renewed. The adapter
requires the original preparation/fixture/retention reserves before launching.
The shell retains its 600-second fixture clock and 30-second fixture cleanup
reserve; the owner unit retains `RuntimeMaxSec=240s`. The original request UTC
deadline is computed from the remaining fixture time minus one second, with
unchanged request phase budgets, including its 60-second cleanup field. The
30-second private-retention operation remains inside the original outer clock.
Utility guard closure, client waits, process-group absence, and root utility
cgroup joins remain required even when the original fixture fails.

The [full shell](checkpoint-n10-pending-start.sh) still requires the actual
StartTransientUnit reply and pending-start observation, exactly two calls to the
original Stop delegate, the latched `StartCancelled` failure, no accepted running
identity, pump joins and error-free EOF, and the final empty cgroup sample. The
original PID/starttime/UID/GID, NSS, unit, custody, protected path, cleanup, and
quarantine predicates are retained. `physically_settled` and
`root_custody_completed` remain false for this negative case. Existing
`native_authority`/`native_acceptance` fields remain false.

## Retained evidence and aggregation

`verify_retained_evidence(context, *, deadline) -> dict` is mandatory for both
execution readback and aggregation. It starts no process. It reopens the actual
archive, each original command JSON, and exactly both stdout/stderr files for
each utility, then repeats the original archive and record predicates. The
archive writer is the bounded, exclusive
[`save_archive`](../slot/bridge.py) API: 33,619,968 bytes maximum. The original
archive parser requires at most 32 MiB of expanded fixed-name regular members,
0600 mode and zero owner/group/time metadata, sorted unique names, and an exact
USTAR repack byte comparison. Diagnostic-only or incomplete archives cannot
produce a passing N10 row.

`validate_evidence(context, evidence_raw, *, capture_raw, build_raw, archive_raw,
deadline)` is the lower-level data parser. It is not the aggregate entry point:
it cannot replace the mandatory reread of actual command records and streams.
The current build receipt bytes/hash are the root projection. The actual current
Git parent is read before native dispatch and is bound to the retained request;
historical source counts, parent/tree hashes, review Booleans, command rows, and
TRX results are not manufactured.

The dispatcher generation names the retained input root. The original full shell
independently generates the fixture generation. Both identities are retained:
the fixture generation must match its actual request, worker unit, fixture result,
and parser summary. They must not be conflated by passing the input generation
as if it came from the actual shell result.

Only complete validation returns the exact five-field row:

```text
case=N10, passed=true, skipped=false, procedure_sha256, evidence_sha256
```

Missing bytes, stale source/build pins, native/retention failures, incomplete
joins, command stream substitutions, or any original parser rejection raise an
exception. Retention is attempted after dispatch failure, while the original
failure remains decisive. The row is an acceptance test result for this fixed
procedure; detached metadata never grants runtime authority.

## Port provenance and validation boundary

[`port-manifest.json`](port-manifest.json) records relative historical paths,
historical/current hashes, and mechanical preservation checks.
[`checkpoint-n10-pending-start.sh.patch`](checkpoint-n10-pending-start.sh.patch)
shows the initial shell-port delta: current provenance replaces the retired packed reader,
while the fixed bootstrap adds `XDG_RUNTIME_DIR=/tmp` as the sixth environment
entry. The original native suffix is otherwise unchanged apart from removal of
the retired source-review-file pin. The dispatch literal, retainer, and
[strict N10 parser](../parsers/check_n10_pending_start.py) remain byte-identical.
Record-function changes only make actual source, parent, capture, and parser
hashes explicit parameters. The stdlib declaration-cache and repeated root-fit
guard follow-up is listed separately in the port manifest; it is not represented
by that historical port patch.

[`test_n10.py`](../../test_n10.py) defines detached data controls for Stop/join
failure precedence, fixture identity and custody, closed diagnostics, all twelve
tree observations, canonical USTAR rejection, actual node projections, and a
complete request/summary/fixture model with one-field negative neighbors. These
tests create no owner, worker, lease, unit, or admission. Fresh Linux execution
with the actual published N10 image and retained artifact verification is still
required before any native acceptance claim.

## Stdlib declaration lookup follow-up

The stdlib audit loop obtains each declared digest from the already initialized
immutable OS-audit lookup table. It still runs the physical `verify_os_path`
check for every member, verifies ELF dependencies and alias chains, and checks
the complete stdlib membership both before and after the per-file scan. The
same original root-start fit check (`remaining > 246` and at least 246 seconds
until the request deadline) runs again after the scan immediately before the
240-second owner interval is computed. Neither the 240-second owner limit nor
the 30-second reply/cleanup bounds are extended. The earlier N10 timeout does
not establish that the repeated lookup caused or would prevent that failure.

[`test_n10_stdlib_declarations.py`](../../test_n10_stdlib_declarations.py)
reuses the detached declaration-loop harness from N04 against this N10 shell.
It checks that N10 consumes only the initialized digest cache, retains physical
verification and before/after membership checks, and rejects both root-fit
boundary values. These portable controls do not execute the native procedure.
