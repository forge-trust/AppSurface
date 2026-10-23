# Per-selector C1 standard collector wrapper

[`measurement.py`](measurement.py) surrounds a real, external Coverlet console 10.0.1 collection with three fixed phases. It reuses the current [build and source binding](binding.py) and the unchanged [native driver](run-native.py). The wrapper does not instrument assemblies, merge counters, rewrite either report, recover worker temporary files, or issue native acceptance. Only standard Coverlet consumes the predecessor JSON through its ordinary `--merge-with` option; the parent workflow owns that invocation and semantic predecessor validation.

## CLI and ordering

All three commands require these common arguments:

```text
--source-capture ABS_EVIDENCE/source.json --source FULL_HEAD --selector SELECTOR
--build-receipt ABS_EVIDENCE/build.json --collector ABS_COLLECTOR_DLL
--evidence ABS_EVIDENCE --prior-report ABS_PRIOR_JSON
--prior-binding ABS_PRIOR_BINDING_JSON
--workflow-identity github-actions-RUN-ATTEMPT
```

Selectors are exactly `None`, `N04` through `N16`. The existing evidence directory must be canonical, mode `0700`. Its capture and original strict v1 build receipt are fixed names. The collector must be the actual `.store/coverlet.console/10.0.1/coverlet.console/10.0.1/tools/net10.0/any/coverlet.console.dll`; its raw hash is checked in every phase. Both predecessor files must be distinct, outside this evidence directory, and unchanged throughout all phases. Report leaves are bounded to 32 MiB; ordinary JSON records and each combined command log are bounded to 8 MiB. Reads reject symlinks, hardlinks, special nodes, parent substitution, growth and unstable metadata. No path or exception text is echoed publicly on failure.

1. `python3 -B measurement.py bind COMMON_ARGUMENTS` runs before the external collector. It requires the actual full v1 build, helper/runtime/source inventories and original command logs to verify. It records the complete original tool map and all raw input hashes in exclusive `measurement-original.json`. Bind has a finite 240-second verification budget and requires the evidence owner to be the caller. All measurement output names, plans and native output must be fresh.
2. The parent launches its pinned standard collector with this wrapper's `target COMMON_ARGUMENTS` as the target command, JSON and Cobertura formats, output stem `ABS_EVIDENCE/coverage`, and the fixed predecessor path as `--merge-with`. The target requires Linux root. It measures the full actual instrumented tool inventory, constructs the existing strict v2 build at `build-measured.json`, and verifies that only `ForgeTrust.AppSurface.Evidence.Supervision.dll` and its PDB may differ. That DLL must actually differ; its PDB bytes may remain equal. The other five managed DLL/PDB pairs remain byte-for-byte original. All other files, directory modes, original commands, helpers and runtime remain bound.
3. Target executes fixed `/usr/bin/python3 -B run-native.py prepare` and then `run-native.py run`. Preparation has at most 150 seconds, followed by a fresh at-most-900-second native run window; both plus verification fit one finite 1,150-second target budget. Remaining-time admission reserves the full following native run plus 60 seconds for final retained-input verification and target publication, and rejects instead of shortening either command allowance. The existing native environment, internal procedure clocks, real dispatch and mandatory retained-evidence callbacks are unchanged. `publish.command` bounds output and kills/joins the actual process group. Combined logs are `measurement-prepare.log` and `measurement-native.log`. Only successful actual commands and validated retained result/plan/evidence permit exclusive `measurement-target.json` publication.
4. After the collector has exited and restored its images, `python3 -B measurement.py finalize COMMON_ARGUMENTS --collector-exit NUM` requires numeric zero. Finalize requires Linux root and has a separate 240-second bound. It verifies the **whole original v1 inventory**, including bytes/modes and all unselected files, the actual successful target command rows and logs, the measured build, both copies of plan/build/source/inventory, actual execution identity, case evidence hashes, and both unmodified reports. It rechecks all predecessor hashes and source before writing exclusive `measurement-binding.json`. A failed target cannot be repaired by a collector exit of zero.

The parent must preserve the target exit when invoking Coverlet, provide sufficient finite collector time for the target, and run finalize even when collection fails so a nonzero collector exit remains failure. A retry needs a fresh evidence directory; existing records are never replaced. Bind/finalize verification does not grant access to any owner, admission, account, worker or private runtime surface.

## Root-only collection scope

The fixed standard collector filters are `[ForgeTrust.AppSurface.Evidence.Supervision]ForgeTrust.AppSurface.Evidence.Supervision.Linux*` and `[ForgeTrust.AppSurface.Evidence.Supervision]ForgeTrust.AppSurface.Evidence.Supervision.Supervision*`, also declared in [`binding.MEASUREMENT_FILTERS`](binding.py). They measure the root ownership component. CLI dispatch, the role parser, Contracts and the other managed images remain uninstrumented in this supplemental procedure. The completed consumer predecessor still supplies the full six-module report; the [solution coverage command](../../scripts/coverage-solution.sh) continues to measure all modules with its existing filters, base and thresholds. This scope adds root measurements without removing any gate denominator or counter.

Revision 57's actual measured `None` attempt failed: the direct non-root N03 probe emitted the expected root-peer rejection, then Coverlet's injected process-exit tracker aborted while opening `/tmp/.dotnet` with `EACCES`. The [pinned tracker](https://github.com/coverlet-coverage/coverlet/blob/v10.0.1/src/coverlet.core/Instrumentation/ModuleTrackerTemplate.cs) creates a named mutex before writing its temporary hits. The direct probe deliberately shares the fixture namespace across distinct UIDs; it is different from the production worker's private temporary namespace. The root-only filter keeps a tracker out of that bootstrap path. The native commands, expected exits, environment, credentials, permissions, unit policies, clocks and cleanup predicates are unchanged. No shared writable tracker directory, tracker patch, exception waiver, synthetic hit file or counter rewrite is introduced. A new genuine Linux run must verify the correction; the failed attempt is retained as failure.

## Final record

The exact final schema is `issue779-c1-final-measurement-v2`. The target schema is
`issue779-c1-measurement-target-v2`. Version 2 additionally binds the separate data-test receipt;
old version 1 receipts remain historical and cannot satisfy this current-source contract. Its fields are:

```text
schema, source, selector, workflow_identity,
collector, collector_exit, collector_sha256, source_capture_sha256,
original_binding_sha256, original_build_sha256, measured_build_sha256, target_sha256,
prior_report_sha256, prior_binding_sha256, report_json_sha256, report_cobertura_sha256,
instrumented_modules, restored_tool_sha256, restored, commands, data_tests_sha256,
native_acceptance, coverage_credit, plan_sha256, native_result_sha256,
execution_sha256, cases
```

`collector` is `coverlet.console/10.0.1`, `collector_exit` is integer zero, `restored` is true, and `native_acceptance` and `coverage_credit` are false. `instrumented_modules` maps exactly the root Supervision DLL/PDB pair to its observed SHA256. The complete original/measured inventories still retain all twelve fixed managed inputs. `restored_tool_sha256` hashes the canonical complete restored inventory. The two ordered `commands` rows have exactly `phase, argv, exit, timed_out, joined, log_path, log_sha256, log_bytes`, matching [`publish.command`](publish.py). `original_binding_sha256` binds the raw `measurement-original.json`; `target_sha256` binds raw `measurement-target.json`. Other SHA fields bind raw files except the explicitly canonical restored tool inventory. `cases` contains only this selector's actual retained case identifiers. A measured subset never proves the full sixteen-case matrix; the separate original matrix and aggregate remain required.

The final binding records predecessor hashes without interpreting predecessor success or lineage semantics. The parent must validate and stage its current C2 or earlier measured predecessor before bind. This split prevents a data hash from being mistaken for native authorship or a coverage gate verdict. No callable `validate_final_measurement` API is promised; the three CLI commands and strict retained files are the integration interface.

## Existing private-image data controls

[`measurement_data_tests.py`](measurement_data_tests.py) measures the existing metadata and ordering
tests against the actual selected runtime assembly. Ordinary `None` builds deliberately omit these
private implementations, and their linked test-assembly copies do not establish execution of the
native runtime's methods. This supplemental step uses the same instrumented runtime image already
owned by the standard collector; it neither imports a report nor writes a hit file.

The fixed recipes are `N04` (`LinuxN04CheckpointOrderTests`, `LinuxNegativeKernelObservationTests`),
`N07` (`LinuxFailedSettlementObservationTests`, `LinuxWorkerStopTimingTests`,
`LinuxN07FailureSettlementTests`), and `N16` (`LinuxN16ProgressDiagnosticTests`,
`LinuxNegativeKernelObservationTests`), all in the existing Supervision test namespace. `None` performs
no supplemental I/O. Other selectors are not part of this four-link measurement chain.

The internal `prepare(args, original, capture_raw, deadline=...)` API runs during bind's existing
240-second budget. It derives the SDK from the verified original build command, restores the fixed test
project in locked mode and builds the same Release selector with warnings as errors and shared compilation
disabled. It requires the test-output Supervision DLL/PDB bytes to equal the original published pair,
and retains those originals plus command logs in fresh private `data-tests/` evidence.

Each supplemental SDK command receives a fresh mode-0700 temporary `XDG_DATA_HOME`, outside the
source checkout and retained evidence tree. [NuGet's Unix data-directory selection](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Common/PathUtil/NuGetEnvironment.cs)
can otherwise place first-use migration state under a relative `.local/share` when the harness's fixed
`HOME=/nonexistent` supplies no usable home. The SDK directory is removed after the joined command,
including failure. It changes neither the native command environment nor a worker/subject sandbox;
all original command deadlines, clean-source checks and receipt schemas remain in force.

After the original native procedure has finished, `run(args, original, prepared, deadline=...)` copies
only the collector's actual instrumented Supervision DLL/PDB into the fixed test-output directory.
The DLL must actually differ from its original. Each replacement retains its original single-link inode
before the first write. The fixed SDK runs VSTest directly over the already-built tests, with a fixed
class filter and no restore or MSBuild evaluation. Its command is bounded by at most 180 seconds of the
original 1,150-second target remainder, leaving 60 seconds for final verification. It cannot renew or
shorten the original native command's clocks. Both test-output originals are restored and checked even
when a test or copy fails; a substituted inode cannot be adopted for restoration. The published image
stays with the standard collector, which performs its existing restoration after all target work joins.

`validate(raws, args, original, prepared, deadline=...)` is a process-free data API for the aggregate.
It checks the exact retained leaf set, original and measured image identities, fixed argv, both build
logs and the joined test command log, and actual TRX classes/counters/results. Failed, skipped, missing
or unselected cases reject. `data-tests/prepared.json` and `data-tests/result.json` retain the closed
`issue779-c1-data-prepared-v1` and `issue779-c1-data-result-v1` schemas. The final/target
`data_tests_sha256` is the raw result hash for the three recipes and null for `None`. The current source
capture pins this helper before import, and the chain rechecks every retained byte and the data receipt.
The new [portable controls](test_measurement_data_tests.py) validate receipt grammar and real owned-file
replacement only; their synthetic metadata fixtures are not evidence of a test process or native case.

These tests exercise data parsing, failure projection and bookkeeping. They supply no native owner,
admission, accepted lease, application health or physical-exit proof, and add no native case IDs. The
original sixteen controls, consumer acceptance and unchanged solution/patch gates remain separate
requirements. No coverage gain is credited merely because these controls are defined or pass locally.

## Validation limits

[`test_measurement.py`](test_measurement.py) defines detached grammar tests and ordinary owned-file controls. No test fabricates a successful owner, worker, lease or admission, and no test invokes a native adapter, collector or .NET host. Successful detached validation only establishes data grammar. Genuine Linux collection, restoration and native receipt verification still require the parent workflow. No test count, native result or coverage gain is implied by source preparation.

The bounded source-preparation run on 2026-10-21 executed `python3 -B -m unittest test_measurement` once with a 30-second outer limit: 21 tests passed, exit zero, 0.026 seconds reported by unittest. This receipt describes only those detached and ordinary-file controls. The collector, native target, restoration phase and workflow integration were not executed locally.
