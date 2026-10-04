# Issue #806 coverage attempt 5

Exact command:

```sh
APPSURFACE_TEMPLATE_TEST_NATIVE_PG_BIN=/private/tmp/issue806-native-tools/install/bin ./scripts/coverage-solution.sh
```

The script and defaults were unchanged: aggregate95% line/85% branch and patch95% line/85% branch against `origin/main`, Codecov line semantics and the existing0.5-point tolerance. The native environment value supplies the actual PostgreSQL16.5 prerequisite for the existing opt-in adapter test; it changes no coverage policy.

Source was clean `e81c756c01ecd902f10bc546ca831b0c06846474` before and after. The run started12:57:04UTC and completed13:13:07UTC on2026-10-04. All56 projects completed:51 passed, five failed. Tests:15,972 passed,488 failed, four skipped. The solution build introduced zero warnings/errors. PackageIndex passed2,014 with three existing skips.

Docker unavailability caused487 failures across PostgreSQL, external activation, CLI and local PostgreSQL example suites. They failed during fixture setup before their required persistence assertions. Docker's backend independently recorded host disk exhaustion while writing its VM init log at12:46:13UTC. The remaining failure was the repository fixture-path policy: a new release-receipt test joined a dynamic manifest filename with `Path.Join`. It now uses the existing `TestPathUtils.PathUnder` helper. Fresh focused verification passes15/15 path-policy tests and103/103 release-receipt tests; formatting and diff checks pass. No production code changed after this attempt.

The script exited1/ASCOV120 after merging coverage and before threshold evaluation. Aggregate collected coverage was88.4772% line (133,705/151,118) and84.0061% branch (45,806/54,527). Patch thresholds were not evaluated. Missing Docker execution explains the aggregate regression; these values are failed-run diagnostics, not current green coverage. The prior two-shard94.7784% patch-line diagnostic is also not a substitute for the full unchanged gate.

Full log: `/tmp/issue806-coverage-attempt5.log`; terminal marker: `/tmp/issue806-coverage-attempt5.exit` (`1`); exact source record: `/tmp/issue806-coverage-attempt5-source.txt`. All-project logs, merged Cobertura and timings were preserved at `/tmp/issue806-coverage-attempt5-reports`.

## Required next prerequisite

Task-owned disposable caches, a stale task clone and native compiler objects were reclaimed; installed native tools, source and safe evidence remain. Idempotent Docker Desktop start returned0/“already running” without API recovery. A bounded restart returned1 because helper/renderer processes remained running. UI inspection reported a locked Mac. The human unlock/reopen request remains pending; no shared Docker images/volumes, VM data or unrelated caches were deleted.

After Docker is healthy, rerun the real generated first-Work probe and the exact unchanged solution command on the final committed tree. Until that succeeds, make-it-so Step7 and PR creation remain prohibited. The prepared outside-checkout [human trial assignment](../designs/issue-806-durable-worker-template.md#the-assignment), named adopter evidence and doctor coordination remain separate obligations, with their original approved ordering and deadlines.
