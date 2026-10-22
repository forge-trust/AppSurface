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
2. The parent launches its pinned standard collector with this wrapper's `target COMMON_ARGUMENTS` as the target command, JSON and Cobertura formats, output stem `ABS_EVIDENCE/coverage`, and the fixed predecessor path as `--merge-with`. The target requires Linux root. It measures the full actual instrumented tool inventory, constructs the existing strict v2 build at `build-measured.json`, and verifies that only the twelve selected DLL/PDB records may differ. Every selected DLL must actually differ; PDB bytes may remain equal. All other files, directory modes, original commands, helpers and runtime remain bound.
3. Target executes fixed `/usr/bin/python3 -B run-native.py prepare` and then `run-native.py run`. Preparation has at most 150 seconds, followed by a fresh at-most-900-second native run window; both plus verification fit one finite 1,150-second target budget. Remaining-time admission reserves the full following native run plus 60 seconds for final retained-input verification and target publication, and rejects instead of shortening either command allowance. The existing native environment, internal procedure clocks, real dispatch and mandatory retained-evidence callbacks are unchanged. `publish.command` bounds output and kills/joins the actual process group. Combined logs are `measurement-prepare.log` and `measurement-native.log`. Only successful actual commands and validated retained result/plan/evidence permit exclusive `measurement-target.json` publication.
4. After the collector has exited and restored its images, `python3 -B measurement.py finalize COMMON_ARGUMENTS --collector-exit NUM` requires numeric zero. Finalize requires Linux root and has a separate 240-second bound. It verifies the **whole original v1 inventory**, including bytes/modes and all unselected files, the actual successful target command rows and logs, the measured build, both copies of plan/build/source/inventory, actual execution identity, case evidence hashes, and both unmodified reports. It rechecks all predecessor hashes and source before writing exclusive `measurement-binding.json`. A failed target cannot be repaired by a collector exit of zero.

The parent must preserve the target exit when invoking Coverlet, provide sufficient finite collector time for the target, and run finalize even when collection fails so a nonzero collector exit remains failure. A retry needs a fresh evidence directory; existing records are never replaced. Bind/finalize verification does not grant access to any owner, admission, account, worker or private runtime surface.

## Final record

The exact final schema is `issue779-c1-final-measurement-v1`. Its fields are:

```text
schema, source, selector, workflow_identity,
collector, collector_exit, collector_sha256, source_capture_sha256,
original_binding_sha256, original_build_sha256, measured_build_sha256, target_sha256,
prior_report_sha256, prior_binding_sha256, report_json_sha256, report_cobertura_sha256,
instrumented_modules, restored_tool_sha256, restored, commands,
native_acceptance, coverage_credit, plan_sha256, native_result_sha256,
execution_sha256, cases
```

`collector` is `coverlet.console/10.0.1`, `collector_exit` is integer zero, `restored` is true, and `native_acceptance` and `coverage_credit` are false. `instrumented_modules` maps all twelve fixed DLL/PDB basenames to their instrumented raw SHA256. `restored_tool_sha256` hashes the canonical complete restored inventory. The two ordered `commands` rows have exactly `phase, argv, exit, timed_out, joined, log_path, log_sha256, log_bytes`, matching [`publish.command`](publish.py). `original_binding_sha256` binds the raw `measurement-original.json`; `target_sha256` binds raw `measurement-target.json`. Other SHA fields bind raw files except the explicitly canonical restored tool inventory. `cases` contains only this selector's actual retained case identifiers. A measured subset never proves the full sixteen-case matrix; the separate original matrix and aggregate remain required.

The final binding records predecessor hashes without interpreting predecessor success or lineage semantics. The parent must validate and stage its current C2 or earlier measured predecessor before bind. This split prevents a data hash from being mistaken for native authorship or a coverage gate verdict. No callable `validate_final_measurement` API is promised; the three CLI commands and strict retained files are the integration interface.

## Validation limits

[`test_measurement.py`](test_measurement.py) defines detached grammar tests and ordinary owned-file controls. No test fabricates a successful owner, worker, lease or admission, and no test invokes a native adapter, collector or .NET host. Successful detached validation only establishes data grammar. Genuine Linux collection, restoration and native receipt verification still require the parent workflow. No test count, native result or coverage gain is implied by source preparation.

The bounded source-preparation run on 2026-10-21 executed `python3 -B -m unittest test_measurement` once with a 30-second outer limit: 21 tests passed, exit zero, 0.026 seconds reported by unittest. This receipt describes only those detached and ordinary-file controls. The collector, native target, restoration phase and workflow integration were not executed locally.
