# Standalone C2 root collection

This fixture measures actual root callbacks from the existing [seven C2 controls](../../issue779-checkpoint2/run-native.py),
using the ordinary installed `None` package and the existing compiled C2 mutation image.
It changes no production policy, worker isolation, account custody, runtime allowance,
qualification, threshold, exclusion, or native acceptance. No worker tracker is imported;
worker and producer PrivateTmp files may disappear without being collected. Root hits
are the only measurement claim. Portable data tests do not prove native execution.

## API and ordering

[`producer_roots.py`](../producer_roots.py) exposes three closed CLI operations:

1. `bind`: after the ordinary package smoke and existing C2 publish/acquisition, before
   either collector starts. It reads the complete existing `source.json` capture,
   verifies every listed source byte and mode, binds the successful package smoke,
   pinned console bytes, and all six DLL/PDB pairs **in each image**. All new output
   leaves and `evidence/native` must be absent. The None image must be the installed
   package image named by the unchanged successful package smoke receipt.
2. `target --stage inner`: the outer None collector's target. It launches one fixed
   inner C2 collector by argv, inherits the existing private log, joins it, and records
   its actual exit. `target --stage driver`, the inner collector's target, first binds
   **both already instrumented maps** and then runs the existing C2 driver exactly
   once with its original arguments via `runpy`. The driver stages both images after
   instrumentation. All six DLLs per image must differ from their originals; all
   twelve DLL/PDB hashes are retained, even when a PDB stays byte-identical.
3. `finalize --collector-exit N`: after the outer collector joins and restores its image.
   Require both actual collector exits and the driver exit to be zero; compare all
   twelve restored files per image with the originals; bind the actual native receipt,
   each case receipt and owner stdout/stderr, staged inventories, unchanged full source,
   raw inner report and merged outer report. Missing or failed evidence rejects the
   measurement; this API does not convert a failed case to success or fabricate a manifest.

Every operation takes the same required `--tool` (None), `--mutation-tool` (C2),
`--runtime`, `--subject`, `--evidence`, `--source` (full lower-case SHA), and `--base`
(`416b30919e6c4d03b634d9d3fe558884b02f0b91`). Paths must be absolute.
`--stage` is legal only for `target`; `--collector-exit` only for `finalize`.
Without the optional predecessor group below, these operations retain the v1 standalone
contract. There is no arbitrary command, callback, or image variant option.
Source completeness/HEAD provenance is established by the existing clean-tree source
capture step; this reader compares the supplied capture and all its physical source files,
and never invokes Git or refreshes the capture.

## Workflow command construction

Keep the original package smoke first, existing driver `1800` timeout, internal case
limits, and workflow job allowance. Install pinned `coverlet.console` **10.0.1** into
`$RUNNER_TEMP/checkpoint2-coverlet`, retaining its install output in an existing bounded
private log. The console must then be at the exact `.store` path shown below.
The [C2 workflow](../../workflows/issue779-checkpoint2-producer.yml) exposes
`measure_roots` defaulting to false. The [cutover workflow](../../workflows/evidencehost-runtime-proof.yml)
enables it. Ordinary direct checkpoint runs retain their original command. This
standalone v1 report is not a gate input; `ready_for_gate` remains false. V2 preparation
requires separate native proof, workflow wiring and independent lineage validation before
the parent may use it as a gate input.

The following Python is the complete argument construction. `collector` uses
[`measurement_arguments.arguments`](../measurement_arguments.py) for .NET's
`ProcessStartInfo.Arguments` grammar. It never constructs a shell command. Run this
small orchestration from the source root as root under the **existing single outer
`timeout --signal=KILL 1800`**; redirect stdout/stderr to the original `native-driver.log`.
The fresh driver creates exactly the original `evidence/native` directory.

```python
from pathlib import Path
from types import SimpleNamespace
import os
import subprocess
import sys

sys.path.insert(0, '.github/issue779-checkpoint4')
from producer_roots import BASE, SELF, collector, common

temporary = Path(os.environ['RUNNER_TEMP'])
workspace = Path(os.environ['GITHUB_WORKSPACE'])
a = SimpleNamespace(
    tool=Path(os.environ['CHECKPOINT2_TOOL']),
    mutation_tool=temporary / 'checkpoint2-tool-c2',
    runtime=Path(os.environ['CHECKPOINT2_SDK_ROOT']),
    subject=workspace / '.github/issue779-checkpoint2/subject',
    evidence=temporary / 'checkpoint2-evidence',
    source=os.environ['GITHUB_SHA'], base=BASE)
prefix = ['/usr/bin/python3', '-B', str(SELF)]
subprocess.run([*prefix, 'bind', *common(a)], check=True)
outer_exit = subprocess.run(collector(a, False), check=False).returncode
finalizer = subprocess.run([*prefix, 'finalize', *common(a),
                            '--collector-exit', str(outer_exit)], check=False)
raise SystemExit(outer_exit if outer_exit else finalizer.returncode)
```

The generated outer argv is exactly:

```text
RUNTIME/dotnet CONSOLE NONE/ForgeTrust.AppSurface.Cli.dll
  --target /usr/bin/python3 --targetargs arguments([
    '-B', SELF, 'target', '--stage', 'inner', *common(a)])
  --include [ForgeTrust.AppSurface.Cli]*
    [ForgeTrust.AppSurface.Evidence.Contracts]* [ForgeTrust.AppSurface.Evidence.Planner]*
    [ForgeTrust.AppSurface.Evidence.Coverage]* [ForgeTrust.AppSurface.Evidence.Aspire]*
    [ForgeTrust.AppSurface.Evidence.Supervision]*
  --include-test-assembly --format json --output EVIDENCE/native-producer-roots.json
  --merge-with EVIDENCE/native-producer-roots-c2.json
```

The inner argv has the same six includes and format, uses
`C2/ForgeTrust.AppSurface.Cli.dll`, `--stage driver`, and
`--output EVIDENCE/native-producer-roots-c2.json`, with **no `--merge-with` in v1**.
`CONSOLE` is exactly
`EVIDENCE/../checkpoint2-coverlet/.store/coverlet.console/10.0.1/coverlet.console/10.0.1/tools/net10.0/any/coverlet.console.dll`.
Thus inner C2 completes before outer None performs its one standard merge.
Use the returned argument arrays directly; do not concatenate these displays into shell text.

## Optional completed rollback predecessor (v2)

Every operation accepts the following **all-or-none** group. The four input leaves
must be distinct absolute paths outside the outgoing evidence tree:

| Option | Required input |
| --- | --- |
| `--prior-report` | Completed standard `native-accounts.json`, at most 32 MiB |
| `--prior-binding` | Completed rollback `measurement-binding.json`, at most 8 MiB |
| `--prior-source` | Full `source.json`, byte-identical to the current capture, at most 8 MiB |
| `--prior-native` | Actual `native-accounts-receipt.json`, at most 8 MiB |
| `--prior-artifact-name` | `issue779-c4-accounts-C4AccountRollback-RUN-ATTEMPT`, positive decimal identifiers |

Set `prior_report`, `prior_binding`, `prior_source`, `prior_native` (each a `Path`)
and `prior_artifact_name` on `a` in the construction above. `common(a)` forwards the
same five values to both target stages and finalization. `bind` records their hashes
in its context. Both target entries and finalization re-read and compare that context;
the outer target also rechecks after joining the inner collector, and finalization
rechecks immediately before publication. A later self-consistent but different packet
cannot replace the bound predecessor.

Only `issue779-c4-account-measurement-v2` / `C4AccountRollback` is accepted. The
reader requires the current full source/head, six standard modules, exact original
and instrumented twelve-file maps, changed DLLs, pinned collector, actual native
wrapper exit zero, settled groups and strict rollback receipt. The existing
[`run-accounts.py`](../run-accounts.py) **data-only** image, receipt and source-witness
validators enforce the original failure stage/code, utility settlement, original-handle
closure and five NSS IDs (distinct within UID/GID domains; equality across domains is
valid). They do not create holders or run the account driver. The rollback's unique
current-source collision `MoveNext` witness must be positive and exactly bound to the
private and production partial source hashes. Its immediate ancestor must be C4Accounts,
from the same run/attempt and full source; required ancestor hashes must be well formed.

The inner C2 argv adds exactly `--merge-with PRIOR_REPORT`. The outer None argv keeps
exactly `--merge-with EVIDENCE/native-producer-roots-c2.json`. This yields the supported
console chain **C3 → C4Accounts → C4AccountRollback → C2 → None**. The inner filename
is unchanged, but in v2 its report includes the completed rollback ancestry; it must
not be described as an isolated raw C2 report. No counters are edited, subtracted,
averaged, translated or merged by Python. No worker tracker is imported.

Completed v2 finalization uses schema `issue779-producer-roots-measurement-v2`, retains
all v1 binding fields and sets `external_predecessor` to exactly:

```json
{
  "kind": "accounts-rollback",
  "variant": "C4AccountRollback",
  "artifact_name": "issue779-c4-accounts-C4AccountRollback-RUN-ATTEMPT",
  "merge_order": ["C3", "C4Accounts", "C4AccountRollback", "C2", "None"],
  "source_capture_sha256": "SHA256_OF_EXACT_PRIOR_SOURCE_BYTES",
  "input_binding_sha256": "SHA256_OF_EXACT_PRIOR_BINDING_BYTES",
  "coverage_sha256": "SHA256_OF_EXACT_PRIOR_STANDARD_REPORT_BYTES",
  "native_receipt_sha256": "SHA256_OF_EXACT_PRIOR_NATIVE_RECEIPT_BYTES"
}
```

The same object is bound in `context.external_predecessor`; run and attempt are
encoded in the artifact name, with no additional fields. `ready_for_gate=true` means
only that this v2 measurement finalization completed. `native_acceptance=false`,
`worker_imported=false` and `root_only=true` remain fixed. The future caller must
independently validate the complete actual C3 → creation → rollback byte chain with
[`AccountMeasurementLineage`](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/AccountMeasurementLineage.cs)
and the root packet before the existing collector consumes it. This local reader has
only rollback inputs and cannot independently authenticate every earlier ancestor.
V1 retains its original schema, null external predecessor and false readiness.

The predecessor must contain **no C2 mutation method declaration**, even at zero hits.
The exact current quota statement must have zero prior hits **if its sequence point
exists**. A missing method or point stays absent; it is never converted into a synthetic
zero. Both exact current positive witnesses remain mandatory after collection. These
checks establish bounded contribution controls, not a numerical coverage gain or
per-case attribution. All incoming leaves stay outside the outgoing artifact, whose
seven filenames and total/count bounds remain unchanged.

## Retained file whitelist and interpretation

Exactly seven new retained leaves, in the existing evidence directory:

| Leaf | Content |
| --- | --- |
| `producer-roots-original.json` | Full-capture/package/console binding and both original 12-file maps |
| `producer-roots-instrumented.json` | Both instrumented 12-file maps, captured before staging |
| `producer-roots-inner-exit.json` | Actual joined inner collector exit and original binding hash |
| `producer-roots-target.json` | Actual driver exit and instrumented binding hash |
| `native-producer-roots-c2.json` | Unmodified standard inner C2 report (v1 alone; v2 with rollback ancestry) |
| `native-producer-roots.json` | Unmodified outer None report with completed C2 merged by Coverlet |
| `producer-roots-binding.json` | Successful final measurement binding, restoration maps and witnesses |

Existing `source.json`, `package-smoke.json`, `native-driver.log`, and `native/*`
remain at their original locations. Every retained leaf must be regular and single-link. Only the two exact root-level
standard report names above permit **32 MiB** each; logs and all other leaves remain
at **8 MiB**. The whole artifact, including the final binding, remains at **32 MiB /
128 files**. Nested files with the same basename receive no larger allowance. The
[ordinary retainer](../../issue779-checkpoint2/check-retention.py) requires the explicit
`--producer-roots` option for this report-only ceiling; its default remains 8 MiB for
every file. These are measurement-artifact bounds, not product/runtime budgets or gate
criteria. The [actual-file boundary tests](../test_producer_retention.py) verify both
readers' fixed report paths, adjacent limits, unchanged total/count, and link/FIFO rejection.
The final ordinary retention step still owns permissions and full artifact inventory.
Readers reject symlinks/FIFOs/hardlinks and changed leaves through bounded no-follow
FD reads with before/after/named metadata checks. These observations are data checks,
not a protected root capability or an adversarial ancestor-path proof.

Native validation requires the original seven cases in order, unique generations,
actual root wrapper exits, original native nonacceptance flags, initial and final empty
groups, account absence for six cases and **quarantine/presence for cleanup-failure**.
The cleanup failure is a genuine expected failure after a passing procedure; it must
never be relabeled successful cleanup. Cancel, ignored TERM, quota and mutation receipts
retain their specific positive control observations and the original driver assertions.

## Source-bound witnesses and known limits

The raw C2 report must hit the exact
`LinuxRetainedReportSet.MutateRetainedReportForC2(CancellationToken)` source span
(declaration line148 at this baseline), bound to its current physical source SHA.
This private method is absent from the prior C3 image. The combined report must preserve
that witness and positively hit the exact `Fail(QuotaExceeded)` statement in
`SupervisionRunOutputBudget.Charge(Int32)` (line173 at this baseline). Anchors are resolved
from exact current source text; another positive method or nearby line is insufficient.

The actual source25 `native-supervision.json` (5,931,061 bytes, SHA256
`627578769d5d1d234aa6aee7d372b3ef66735d1dfd34ca69a700e1b33194f3e5`)
has `Charge` line173 **0** hits, line172 **0**, line174 **0**, and line175 **255**.
Generic positive `Charge` entry hits therefore provide no new witness. No source25
report is imported here, and no per-case numerical attribution or coverage gain is claimed.

Fresh Linux proof remains required: package PDB/all-six-module availability, optimized
None sequence points, standard trackers surviving genuine root exit (including all
negative exits), actual restoration and report sizes must be observed. Missing symbols,
missing/zero witnesses, nonzero collectors, or exhausted original allowance are blockers,
not grounds for changing the source, timeout, report or policy. The v1 final binding sets
`ready_for_gate=false`. The v2 contract above binds actual ordered inputs and requires
current contribution controls. Workflow/caller integration and fresh native proof remain
separately reviewed; portable packets cannot supply them.

## Validation status

The private v2 preparation's single bounded Python suite passed all **30** data and
small-file tests (15 existing methods with unchanged ASTs plus 15 new predecessor
controls). It exercises exact source bytes, stage/order/hash bindings, rollback error
and NSS data, unique current witnesses, inherited positive rejection, changed-prior
replay, incoming node safety and readiness publication. No driver, collector or native
process ran in this suite. The v2 console chain still requires independent review and
fresh actual Linux proof; this status does not claim integration or gate consumption.

The initial v1 preparation passed fifteen focused data controls and independent
source review. Subsequently, [run 39203788097](https://github.com/forge-trust/AppSurface/actions/runs/39203788097)
on source `2dfb35e1155344b155fbe3154c7cb9773f54bf2b` passed all seven actual Linux producer
controls. Artifact SHA-256 `fadd35b2698af9dd98423656f8ece5e347ffbe378f1fac91b9a38476b31d6d59`
and the complete 3288-file capture, both original/instrumented/staged/restored image
maps, case/log hashes and current witnesses were verified. Its standard inner report
is 6,030,924 bytes; the combined report is 9,952,958 bytes. That completed v1 packet
is root-only, has no external predecessor and remains ineligible as the final gate
input. This observation does not establish a connected v2 run or a full gate pass.

After integration, all thirty Python data controls passed again. The C# root and
account consistency classes passed 189 source-built cases with zero failures/skips
or compiler diagnostics. Those are local data checks. The [native validation and
merge reference](../../../Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Native.Tests/README.md#producer-root-measurement-validation)
defines the mandatory actual v2 inputs and the unchanged final gate.

A first standalone collection failed, followed by the original generic leaf-bound
retention check; that check did not identify the offending file or predicate, so no
report size or successful collection is inferred from it. Failure-only diagnostics
must preserve the actual closed metadata and bounded private log slices independently
of the original artifact check. The fixed report allowance does not excuse unsafe
nodes, a failed collector, a failed native control, or missing witness/restoration data.

## Diagnostics after failure

Only when the measured step fails, the workflow calls
[producer_root_failure.collect](../producer_root_failure.py) after the original
collector process returns. It preserves the failed step. The helper has one five-second
budget and an external eight-second bound. Through retained no-follow descriptors it
records only the seven fixed measurement names and `native-driver.log`; report contents
are not read, even when oversized. Missing, unsafe, changed and late inputs get closed
unavailable reasons. Stable logs of at most 64 KiB are complete; larger logs retain only
the first and last 32 KiB with exact offsets and `complete=false`. File stability is not
independent proof that a writer joined. No log text is parsed or printed.

A separate artifact contains exactly `metadata.json` (at most 8,192 bytes) and
`native-driver-slices.log` (at most 65,536 bytes), in a fresh `0700` directory with exclusive
`0600` files, and a maximum combined size of 73,728 bytes. The workflow verifies these
limits before upload. This diagnostic upload does not bypass the original full-artifact
retention step, turn failed measurement into success, or provide namespace authority,
native acceptance or coverage credit. [Seventeen small-file controls](../test_producer_root_failure.py)
verify links/FIFOs, exact and sliced log boundaries, substitution/growth, unavailable
sources, deadlines, output exclusivity and private modes.
