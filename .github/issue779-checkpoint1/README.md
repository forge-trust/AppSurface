# Current-source C1 native acceptance adapter

This private acceptance lane publishes fresh current-source CLI images for fourteen
fixed compile selectors: ordinary `None` runs N01–N03, and N04–N16 each run their
named case. Historical captures, images, review flags, command rows and TRX facts
are not runtime inputs. C1 is uninstrumented and supplies no coverage credit.

## Entry points and publication data

`publish.py` is the parent-owned publisher. Its receipt records the actual CLI
locked restore and Release publish, complete current source capture, complete tool
and compact runtime inventories, actual SDK version 10.0.401, command exits and log
hashes. `helpers` contains separately executed helper restore/publish pairs and
complete helper images; it never adds synthetic rows to the two CLI commands.
The protected runtime must be the actual SDK-free FDD runtime, while the publisher
may use a separate SDK host. Images and source roots must be disjoint.

[`prepare-runtime.py`](prepare-runtime.py) exits 1 when preparation is rejected.
Its failure stdout is one `issue779-checkpoint1-runtime-failure-v1` JSON object:
`category`, `error_kind` (`input` or `os`), and nullable `errno` in 1..4095, plus
`schema`. Known custody categories identify the failed guard; unknown input
errors use `runtime-input-invalid`, and OS errors use `runtime-os-error`. No raw
exception text, path or stack is emitted. Retain `runtime-preparation.log` to
diagnose a failed copy before attempting publication. A diagnostic is not a
runtime receipt, an OS qualification, or permission to continue after failure.
The workflow also retains `runtime-input-metadata.json`: a read-only snapshot of
five fixed SDK directory roles and bounded numeric .NET 10 version directories.
It records modes, numeric ownership, link counts and closed read errors using a
five-second clock, six-second process bound and 128 entries per version container. It neither changes the SDK
nor substitutes for the runtime copy's retained-FD, mode and byte checks.

Required helper IDs are `peer` for None, N04–N08, N11 and N12, plus `n04` for N04 and
`n07` for N07. Other selectors require an empty helper map. Projects and assembly
entries are fixed in [`binding.py`](binding.py). Each helper phase is named
`helper-ID-locked-restore` or `helper-ID-publish`, uses warnings as errors and
Release, and carries no runtime qualification selector. Full helper dependencies
are retained through the actual image map, not a copied entry DLL alone.

The current source capture uses `issue779-checkpoint2-source-v1` and binds every
tracked file's SHA256, Git blob and mode. The build receipt uses
`issue779-checkpoint1-build-v1`. `binding.validate_capture`, `validate_image`,
`validate_build`, `validate_helper_commands` and `required_helpers` validate detached
data only. `inventory(root, entry, *, deadline)`, `verify_source`, `verify_image`
and `verify_build` additionally inspect actual files, bounded retained FDs, logs,
complete inventories and immutable Git objects. All deadlines are absolute
`time.monotonic()` values. Read-only Git uses command-local `safe.directory` for
that one canonical source root; no global or wildcard trust setting is changed.

## Fixed adapter CLI

```text
run-native.py prepare --source-capture FILE --source HEAD --selector SELECTOR
  --tool TOOL --runtime RUNTIME --build-receipt FILE --output FRESH_PLAN_DIR
run-native.py run --plan PLAN_DIR/plan.json --output FRESH_NATIVE_DIR
  --workflow-identity github-actions-RUN-ATTEMPT
run-native.py aggregate --source-capture FILE --receipts FOURTEEN_NATIVE_DIRS
  --output FRESH_AGGREGATE_DIR
```

Preparation performs real prerequisite checks and starts no native work. `run`
requires Linux root; the parent workflow owns the `sudo` entry. It uses a fixed
source-owned selector table, verifies the actual source before importing an
adapter, and executes the exact hash-checked adapter bytes. A missing/incomplete
port is rejected before unit I/O. One original 900-second outer deadline includes
verification, staging, native work, cleanup and retention. Each procedure retains
its own original product, fixture and containment clocks within that deadline.

The dispatcher creates a fresh root-owned 0700 native output directory, applies
umask 077, and writes these five initial 0600 files before calling an adapter:
`plan.json`, `source.json`, `build-receipt.json`, `procedure-inventory.json`, and
`execution.json`. The latter binds the fixed workflow identity and fresh input
generation. Adapters must adopt that exact existing directory and create their
additional leaves exclusively. Sealed source/tool/runtime trees stay in separate
root-private retained roots; they must not be copied into the artifact output.

Every adapter implements these two dispatcher entry points:

- `execute(context, *, deadline)`: actual native procedure, returning one case row
  or, only for None, ordered N01/N02/N03 rows.
- `verify_retained_evidence(context, *, deadline)`: mandatory actual finite raw-leaf
  readback plus original case parsers, returning the same row or rows. Both `run`
  and `aggregate` call it and require exact row equality; an evidence JSON hash
  alone cannot pass. There is no fallback to a lower-level data validator.

A family's lower-level `validate_evidence` is an internal detached data parser.
Its signature is family-specific (some require raw build/member bytes and the
original deadline); the CLI never calls it directly or assumes a three-argument
shape. It never establishes a native holder or performs a launch.

The context has exactly `source`, `selector`, `capture_path`, `capture_sha256`,
`build_receipt_path`, `build_receipt_sha256`, `tool_root`, `runtime_root`, `output`,
`generation`, `binding_validator`, `binding_validator_sha256`, and
`workflow_identity`. The workflow grammar is
`github-actions-[1-9][0-9]*-[1-9][0-9]*`. A case row has exactly `case`, `passed`,
`skipped`, `procedure_sha256`, and `evidence_sha256`; success requires true passed
and false skipped. A failed/missing record, raw leaf, parser, command, join or
cleanup never publishes `result.json` as a successful selector.

## Reconciliation and limits

Each native output retains its plan and original prerequisite bytes. Aggregate
checks their hashes against the same current source capture and current procedure
inventory, rechecks its local current source before loading parsers, re-reads the
fixed raw leaves, and requires exactly fourteen distinct selectors and all sixteen
named cases once, with no skips. It does not rebuild, dispatch, remerge coverage,
edit counters, or promote proof/catalogue authority. Detached receipt models alone
cannot pass this command.

Procedure availability is recorded separately from native results. During porting,
`procedures/inventory.json` may contain incomplete selectors; those selectors and
whole-lane aggregation remain closed. Source-ready adapters, static parsing and
portable tests are not a fresh sixteen-case Linux result. Fresh locked builds,
peer review, portable validation and actual Linux acceptance remain required.

[N16's port](procedures/n16/README.md) documents its concrete native boundary.
The [slot bridge](procedures/slot/README.md) documents archive custody, complete
command-log readback and the four negative fixture prerequisite schemas.
The [N09 retained-slot port](procedures/n09/README.md) preserves its original raw
projection and final path comparison.
The [owner-death adapter](procedures/owner-death/README.md) owns the reusable current
input materializer and N13/N14 bridge. Source changes to a fixed native predicate
or source/report mapping require an explicit inventory and review update.

## Shared OS audit helper roots

`procedures/os_audit.py` exposes `Audit.build(runtime, tool, include_tool, helper,
*, additional_helpers=())`. Every caller must call `initialize_tools()` first.
The optional keyword is a tuple with at most one canonical, disjoint additional
helper root. N04 and N07 pass their actual coordinator helper in addition to the
peer helper; N11/N12 and other callers keep the empty default. Every helper is
inventoried before and after and its ELF closure is inspected even when
`include_tool` is false. All original OS/ELF/hash/alias, membership, deadline and
output bounds apply to the additional helper; it cannot replace or waive peer.
