# Completed coverage-gate data comparison

`completed_gate.compare_gate_reports` compares two already-retained JSON reports from the
public `appsurface coverage gate` command. It validates the exact `CoverageGateReportWriter`
JSON shape in [`CoverageGate.cs`](../../Cli/ForgeTrust.AppSurface.Cli/CoverageGate.cs#L2152), rejects
duplicate JSON keys, non-finite numbers, unknown fields, and schema drift, and compares the
reported metrics and policies as data. It performs no coverage arithmetic and invokes no CLI,
build, workflow, or evaluator.

The current public writer emits these top-level members: `passed`, `coverage`,
`tolerancePercent`, `thresholds`, `effectiveThresholds`, `patchLineMode`, `patchDiffSource`,
`line`, `branch`, `patchLine`, and `patchBranch`. The comparison is restricted to the runtime
workflow's fixed base commit `1991280e8af049db592ebcb705cbf66f4b4c6e8b`, 95/85 global and patch
thresholds, 0.5 tolerance, and `codecov` patch-line mode. It requires both reports to pass and
compares every metric and patch diff provenance field. The source must be a local Git base with
no source path. The only normalized field is `coverage`, after the caller supplies each report's
exact expected Cobertura role path. JSON decimal values are parsed as `Decimal` and compared
exactly; equivalent integer and decimal spellings compare equal. Metric percentages must be
finite and within 0–100; counters are bounded nonnegative signed 64-bit integers.

## Original artifact limits

The completed-run provider names the runtime coverage archive
`issue779-checkpoint4-coverage-{run_id}-{run_attempt}`. The workflow uploads
`_temp/checkpoint4-evidence/source.json`, `comparison-base.txt`, and `coverage.log` alongside
`AppSurface/AppSurface/TestResults/coverage-merged/coverage.cobertura.xml`, the gate reports,
TRX files, timings, and project logs. The source capture is stored at
`_temp/checkpoint4-evidence/source.json` inside this archive. The
`inspect_completed_gate_archive` preflight binds the raw archive digest and actual safe ZIP
projection to the matching typed stage/inventory before extracting the fixed input set. It
requires exact source-capture bytes, the fixed comparison-base bytes, and a passing original
report whose coverage path matches the observed runtime path. Individual limits are 1 MiB for
source and gate JSON, 256 bytes for the comparison base, 8 MiB for the transcript, and 40 MiB
for Cobertura XML; declared sizes are checked before extraction. The archive has the existing
32 MiB staging ceiling and shared ZIP parser's 512 MiB expansion ceiling.

For run 40483696166 attempt 1, the retained coverage archive has no
`AppSurface/AppSurface/TestResults/coverage-merged/coverage-gate.json`. Its retained
`coverage.log` ends in `ASCOV120 Coverage run failed` and `fixture_process_exit=1`; this is a
failed original coverage run. The retained Cobertura file (about 25 MiB) and comparison-base
file do not create an original gate result. No result can be synthesized for this run.

The preflight is not proof that the original command completed successfully. It returns the
source-bound raw inputs and transcript with `original_gate_success_proven: false`; it does not
parse opaque log text or infer success from present files. An unambiguous successful test/merge
inventory and workflow job completion binding are still needed to establish that the original
command exited successfully. The current run fails before extraction because its gate JSON is
absent; its transcript independently records `ASCOV120`.

## Authority boundary

The frozen `GateArchiveInputs` records source-bound member bytes and hashes; the frozen
`GateReportComparison` records raw report SHA-256 values and compared fields. Both types keep
their flags for validator execution, original gate success, gate eligibility, admission, and
qualification false. A future execution controller must use the original public CLI
and its existing evaluator, preserve the original and replay reports separately, and retain
the exact arguments, exit status, stdout/stderr, comparison-base identity, and generated-file
inventory. Neither report comparison nor replay can establish test-suite success, collector
completeness, native acceptance, admission, or Trusted eligibility.
