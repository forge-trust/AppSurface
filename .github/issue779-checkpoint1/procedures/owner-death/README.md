# Current-source N13/N14 owner-death bridge

This private source port drives the **real** retained N13/N14 owner-death procedures.
It is source preparation, with no native execution or coverage result claimed.
The [shared current binding](../../binding.py) verifies the real full capture,
locked restore/publish logs and complete original tool/runtime inventories. No
historical build receipt, test command, TRX or review Boolean is synthesized.

## Entry contract

`adapter.execute(context: dict, *, deadline: float) -> dict` requires an already-root
Linux caller. The [shared dispatcher](../../run-native.py) owns the root entry,
selector allowlist, current-source verification and original absolute monotonic
end. `deadline` is not a duration and is never renewed. The exact context keys are:

- `source`: full immutable current HEAD; `selector`: `N13` or `N14`.
- `capture_path`, `capture_sha256`: original full current source capture bytes.
- `build_receipt_path`, `build_receipt_sha256`: actual current build receipt bytes.
- `tool_root`, `runtime_root`: exactly the original roots in that receipt.
- `output`: the dispatcher's existing root-owned 0700 evidence directory with
  exactly its five root-owned 0600 initial input files. The adapter adopts this
  directory; it must not require an absent output or replace prior leaves.
- `generation`: fresh 32 lowercase hexadecimal generation, never reused.
- `binding_validator`, `binding_validator_sha256`: captured current `binding.py`.
- `workflow_identity`: `github-actions-RUN-ATTEMPT`, using the existing grammar.

The complete current source must be verified before importing this fixed adapter.
The fixed shared [owner-death record parser](../parsers/check_owner_death_record.py)
is loaded from its source path and its exact bytes are bound to the full capture.
It validates data only and grants no ownership or native acceptance.

The return is exactly `{case, passed, skipped, procedure_sha256, evidence_sha256}`.
It is returned only after the real fixed shell exits0, its launcher/unit/pipe
assertions complete and the actual record and captured pipe hashes match. The
case evidence is `output/N13-evidence.json` or `output/N14-evidence.json`; raw
private logs remain separate and are not echoed or embedded into that JSON.

## Actual materialization and clocks

`prepare_inputs(context, *, deadline)` repeats the current source/build checks and
creates `/var/lib/appsurface-evidence-input-GENERATION` exclusively. Complete
source/tool/runtime copies have root ownership, regular single-link files,
no symlinks and bounded stable FD hashing. It writes the captured current control
inputs and truthful TSV/node inventories. Original publisher roots are verified
again after copying. A partial generation is retained, never adopted or deleted.

`materialize(context, prepared, *, deadline)` preserves the original fixed empty
observation policy and request. The current subject/source and its real Git parent
replace historical provenance. It copies the retained trees using the unchanged
historical `copy_role` and `verify_materialized` functions, seals the trees, and
writes/reads back the actual root600 request and root444 policy. The request's
original 240-second UTC/boottime budget starts once before this case workspace
copy, with its original60-second request cleanup reserve. No owner is launched
by materialization.

The fixed wrapper/common receives the original outer absolute deadline through
`--binding-deadline-monotonic`; its shared binding CLI checks actual original
publisher roots/logs, rather than pretending the sealed copies are publisher
outputs. The common shell's native600-second fixture/30-second cleanup reserve,
240-second request check, two-minute owner service and five-second stop policy
are unchanged. `run_bounded` respects the earlier outer bound and gives the
original shell its existing30-second cleanup interval. Any timeout, nonzero or
unjoined child is a failure; it cannot produce a successful case row.

On the existing `owner-terminal-not-sigkill` failure, the common shell now emits
one closed `issue779-owner-terminal-diagnostic-v1` JSON line to the already-private
fixture stderr. It retains numeric `ExecMainCode`, `ExecMainStatus` and `MainPID`,
plus closed active/substate enums. Missing, duplicate or malformed fields become
null/unknown. No raw property, unit path, exception or command text is emitted.
The original `ExecMainCode == 2` and `ExecMainStatus == 9` requirement, signals,
clocks and failure result are unchanged. These facts diagnose a failed check;
they cannot make a failed N14 pass. The [portable diagnostic/guard controls](../../test_owner_terminal_diagnostic.py)
execute only this shell fragment and establish no native behavior.

The native common's PID/starttime/UID/GID/cgroup checks, accepted phase, presignal
rereads, original systemd activation timer, exact signal, dependent worker checks,
launcher ownership/join, EOFs, empty positive output, account identity retention
and quarantine predicates are unchanged from the handed-off current shell.
The shell still preserves quarantined generation resources; the adapter never
repairs, deletes or reuses them.

## Shared staging for another fixed procedure

`prepare_verified_inputs(context, binding, capture, build, cap_raw, build_raw, *,
deadline, source_sealed=True) -> dict` is the shared root staging operation after
a caller's fixed provenance boundary. `binding` must be the current captured
shared module, not caller-supplied executable code. It repeats `verify_build`,
binds raw capture/receipt bytes and copies their actual complete inventories.
It does not dispatch a case or create fixture workspace parents.

The returned keys are `retained`, `control`, `roots`, `nodes`, `capture`, `build`,
`capture_raw`, `build_raw`; `roots` and `nodes` each contain source/tool/runtime.
`control/{source,tool,runtime}.tsv` and corresponding `*-nodes.json` describe the
actual copies, including the root `.` directory row. They are not rewritten build
receipts and do not claim production ownership.

N16 may pass `source_sealed=False` only because its own original shell owns the
subsequent source seal: retained source directories are0700 and file modes remain
0644/0755; tool/runtime directories are0555 and files0444/0555. It must retain its
own fixture/request/native assertions. Do not call the N13/N14 materializer for a
different original policy or budget. No fixture parent is precreated by staging.

## Evidence reconciliation

`validate_evidence(context, evidence_raw: bytes, *, capture_raw: bytes) -> dict`
is the data-only callback. It rechecks the exact envelope/source/generation,
procedure/common/parser hashes, the embedded actual owner-death record through
the original parser, canonical record binding and the fixed stdout/quarantine
bytes. A detached valid model proves only data grammar, never native execution.

`verify_retained_evidence(context, *, deadline) -> dict` additionally re-reads the
six retained actual leaves and checks their exact hashes/lengths before invoking
that callback. The shared aggregator should use it (or equivalently bind all
six actual leaves independently), rather than trusting a result-row hash alone.

Failure retains bounded private diagnostics when available without replacing the
original failure. Missing or malformed evidence cannot create a pass. The shared
dispatcher/inventory still needs integration and independent review; no all16-ready
flag is established by this directory.

## Preparation status

Only source/static AST checks were performed. No tests, compiler, .NET, native
procedure, CI action or Git mutation was executed by this preparation. The
[port manifest](port-manifest.json) binds the historical originals, handed-off
current shells and new source files; its readable deltas distinguish the existing
provenance-only shell changes from the newly implemented adapter. A fresh current
capture, independent review, portable validation and actual Linux execution are
remaining integration requirements.
