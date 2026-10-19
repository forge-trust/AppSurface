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
There is no predecessor, arbitrary command, callback, image variant, or report input option.
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
standalone report is not yet a gate input; `ready_for_gate` remains false.

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
`--output EVIDENCE/native-producer-roots-c2.json`, with **no `--merge-with`**.
`CONSOLE` is exactly
`EVIDENCE/../checkpoint2-coverlet/.store/coverlet.console/10.0.1/coverlet.console/10.0.1/tools/net10.0/any/coverlet.console.dll`.
Thus inner C2 completes before outer None performs its one standard merge.
Use the returned argument arrays directly; do not concatenate these displays into shell text.

## Retained file whitelist and interpretation

Exactly seven new retained leaves, in the existing evidence directory:

| Leaf | Content |
| --- | --- |
| `producer-roots-original.json` | Full-capture/package/console binding and both original 12-file maps |
| `producer-roots-instrumented.json` | Both instrumented 12-file maps, captured before staging |
| `producer-roots-inner-exit.json` | Actual joined inner collector exit and original binding hash |
| `producer-roots-target.json` | Actual driver exit and instrumented binding hash |
| `native-producer-roots-c2.json` | Unmodified standard inner C2 report, without predecessor |
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
not grounds for changing the source, timeout, report or policy. The final binding sets
`ready_for_gate=false`; future C3/accounts/rollback/C2 lineage validation and gate caller
integration require separate review. Adding a predecessor later changes the raw-report
contract and must bind the actual ordered inputs, not infer current contribution from
already positive ancestor counters.

## Validation status

Fifteen focused data controls pass locally after integration. Independent source
review found no remaining P1/P2 in the three-file collector preparation. The original
Linux artifact inventory includes all six DLL/PDB pairs in both images; a fresh run
must still establish actual instrumentation, flushes, witnesses, report size and
restoration. No new native result or coverage gain is inferred from these checks.

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
