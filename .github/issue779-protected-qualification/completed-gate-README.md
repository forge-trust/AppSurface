# Completed coverage-gate replay design

**Status: design only.** No completed-gate validator or replay command is implemented by this document. The retained run-109 coverage artifact is missing `coverage-gate.json`, so that run must fail closed for completed-gate validation. Its `ASCOV120` coverage-run failure log confirms that `scripts/coverage-solution.sh` stopped before calling `coverage-gate-fanin.sh`; the available Cobertura summary is not a gate result.

## Purpose and boundary

A future completed-run validator may replay the existing public [`appsurface coverage gate`](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-gate) command against a completed run's retained merged Cobertura report and source-bound comparison base. The command's existing `CoverageGateEvaluator` and `PatchCoverageEvaluator` remain the only numerical evaluators. The validator must not implement coverage arithmetic, alter thresholds, or treat replay as proof that the original test suite completed successfully.

The replay establishes only that the retained Cobertura bytes and captured source produce the same gate result under the fixed command. It does not rerun tests, prove collector completeness, establish native acceptance, create an `EvidenceManifest`, or grant coverage credit or Trusted eligibility. A replayed pass cannot replace proof that the original coverage workflow completed successfully and emitted its own gate artifact and CLI transcript.

## Required retained inputs

The provider inventory must be closed, bound to the completed run ID, attempt, repository, and head SHA, and include the coverage gate artifact by its exact expected name. Before reading members, the validator must bind the raw archive size and SHA-256 to provider metadata, enforce the role's existing archive and staging ceilings, and verify safe ZIP structure and bounded expansion.

The coverage artifact must include all of the following from the same run and attempt:

- the exact captured `source.json` plus the original merged `coverage.cobertura.xml`;
- `comparison-base.txt` with one full commit ID, and enough retained Git history to resolve it against the captured head;
- the original `coverage-gate.json`, plus the original CLI transcript or step log showing the exact public command, successful exit, and report paths;
- test-project inventory and per-project logs/results sufficient to show the original `coverage run` completed its fixed selection without a failed test, merge, or artifact step;
- the archive/provider binding and hashes for each required retained file.

Missing files, multiple candidate reports, incomplete test inventory, a failed or absent original command, unresolved comparison base, mismatched source/run/attempt, or any digest/size/path discrepancy rejects the completed-gate result. In particular, do not synthesize the missing run-109 JSON from the Cobertura summary or infer that the original gate passed from line and branch percentages.

## Source and execution binding

The validator must verify the complete source capture against a clean checkout at its exact head before building or executing source code. Require the checkout's `HEAD` and tree to match the captured source record. Resolve the comparison base as a full commit ID from that checkout; reject refs, moving branch names, shallow/missing commits, dirty worktrees, or ambiguous repositories. Compute the CLI's documented three-dot, zero-context diff from that base to `HEAD`, with external diff disabled and repository-relative paths, then record its exact byte count and SHA-256. The replayed gate report must bind the same diff digest and byte count as the original report.

Use the repository's pinned SDK and dependency lock state, a fresh isolated build/output directory, and the captured CLI project. Invoke only the public CLI command with a closed argument vector and a separate finite deadline for the full build-plus-replay procedure. Do not accept a caller-supplied command, callback, arbitrary CLI options, environment-driven threshold, diff source, or output path. The replay must pass explicit fixed options: the retained Cobertura path, repository root, exact base commit, 95% line and 85% branch thresholds, 95% patch-line and 85% patch-branch thresholds, and `codecov` patch-line mode. Leave tolerance at the public default of 0.5%. Write reports only into a newly created private replay directory; never reuse or replace retained original files. A nonzero exit, timeout, missing replay report, unexpected generated file, or cleanup failure is a failed replay.

The captured [runtime workflow](../workflows/evidencehost-runtime-proof.yml) sets
`EVIDENCEHOST_NATIVE_MEASUREMENT=1` and the fixed comparison base. It does not set
`COVERAGE_FANIN_NATIVE`, so it uses the single-report branch of the existing
[gate script](../../scripts/coverage-gate-fanin.sh). The ordinary repository build's separate
cross-platform fan-in path must not be projected onto this runtime workflow. Supporting that
other lane would require its own retained inputs and both original gate reports.

## Result comparison

Parse both JSON reports with duplicate-key rejection. Compare the pass value, tolerance, all configured and effective thresholds, patch-line mode, line and branch counts/percentages, patch line and branch counts/percentages, and patch diff source kind, label, byte count, empty flag, and SHA-256. The original report must itself have `passed: true`; a replayed failure or any metric mismatch rejects.

Normalize only known machine-local path fields for comparison: the top-level Cobertura path and path-valued members of `patchDiffSource`. Require each to point to the expected input role within its own original or replay root, then compare their normalized role-relative values. If the CLI report contains an absolute comparison-base path or diff-file path, compare the canonical resolved commit or fixed diff input identity rather than the host path. Do not globally replace strings, normalize metric fields, or discard unknown JSON fields; schema drift is a rejection until reviewed. Keep and hash the raw original and replay report bytes, and retain the exact replay argv, exit code, CLI stdout/stderr, diff digest, and output inventory in the private validation record.

## Limits and interpretation

The original run's source capture, raw Cobertura digest, comparison-base bytes, original gate JSON, CLI transcript, and test inventory must remain linked to the same provider artifact identity and run attempt. The replay validator may produce a mechanism-only projection with the source/run/attempt bindings, raw input digests, original/replay report digests, normalized metric comparison, and literal false authority fields. It must not create a gate eligibility enum, admission result, accepted-proof registry entry, or trusted evidence manifest.

Replay is useful for checking deterministic evaluator behavior over retained data when the original gate report exists. It cannot reconstruct an absent original report, establish that the original test suite passed, or repair a failed CI job. Any future implementation and its focused tests must be reviewed separately before use; this design document is not an implemented validator or validation result.
