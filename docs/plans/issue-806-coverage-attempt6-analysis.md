# Issue #806 coverage attempt 6

The [unchanged solution coverage command](../../scripts/coverage-solution.sh) ran with its actual native prerequisite:

```sh
APPSURFACE_TEMPLATE_TEST_NATIVE_PG_BIN=/private/tmp/issue806-native-tools/install/bin ./scripts/coverage-solution.sh
```

Policy remained aggregate95% line/85% branch and patch95% line/85% branch against `origin/main`, Codecov line semantics and the existing0.5-point tolerance. Source remained clean `b819d0d6e97ddf6e12aa9545a35a626ee9fde941` before and after. The run completed17:12:59–17:47:44UTC on2026-10-04:56 projects,53 passed and three failed;16,411 tests passed,49 failed and four skipped. The solution build introduced zero warnings/errors. PackageIndex passed2,014 with three existing skips.

| Failure owner | Actual result |
| --- | --- |
| RazorWire integration | One scroll-reset test failed at the first0–8 assertion with actual15105. An unchanged isolated Debug reproduction passed1/1; it does not waive the failed solution run. |
| CLI PostgreSQL tests |44 failures: one PostgreSQL WAL fsync I/O error and43 Docker metadata read-only filesystem failures. |
| Local PostgreSQL example | Four failures from Docker metadata's read-only filesystem. |

Preflight Docker was healthy29.7.2 with about27.5GiB free. Disk headroom later fell to128MiB. The parent reclaimed only four verified unused package/HTTP caches in the completed owned QA root, retaining evidence, archives, tools, generated source and restored-graph records. This recovered about382MiB while other allocations changed; later free space was about2.2GiB. No shared Docker images, volumes, VM data or unrelated worktree files were deleted.

The merged report collected94.8610% line (143,352/151,118) and89.1026% branch (48,585/54,527). The script exited1/ASCOV120 before threshold evaluation; patch metrics are unavailable. These are failed-run diagnostics, not a current gate pass. Read-only preserved evidence is under `/tmp/issue806-coverage-attempt6-reports`, with the final machine summary at `/tmp/issue806-coverage-attempt6.json`.

## Current generated proof

Fresh generated CLI/API/worker QA at the same clean source passed the actual canonical feed first-Work command1/1 and an independent exact-archive three-location replacement. Replacement build was warning-clean;75 non-Docker contracts and one real first-Work test passed. Both Docker probes asserted authorization, persisted terminal Work, readiness transition, SDK export and cleanup. Both private template installations were uninstalled and absent; all six observed owned containers were absent. Report: `/private/tmp/issue806-qa-b819d0d6/report.md`.

The separately copied Mac CLI passed30 phases but did not select Docker first Work or native smoke. Its four Docker flags remain false and its runner image is empty; it is not hosted release provenance. The earlier actual native PostgreSQL16.5 proof remains separately bound to `e81c756c`; later test-helper changes did not change template or production content. None of these results establishes the human trial, actual hosted OS/timing matrix or public-feed replay.

## Focused scroll remedy and next prerequisite

The failed test injects a60ms bottom scroll, then accepts a temporary new-page/top observation before the injected callback runs. Independent review confirmed that race and identified pending80/240ms and animation-frame restoration callbacks that could interfere with the later manual-scroll phase. The focused test-only remedy requires a marker set after the injected callback and uses the existing [Playwright controlled clock](https://playwright.dev/dotnet/docs/api/class-clock#clock-run-for) to execute pending restoration work before the subsequent assertions. Existing routes, headings, sentinel, timeouts and scroll bounds are retained. Fresh Debug build and the full owning wayfinding class passed21/21 with zero failures/skips and zero compiler/analyzer warnings/errors; the scroll-reset test passed in249ms. The included-file formatter and diff check passed. The independent remedy recheck found no actionable issue in this narrow delta. No production behavior, dependency or coverage policy changes were made; the current full gate remains unverified.

A fresh positively owned metadata-write probe at17:55:42UTC still failed with Docker `meta.db: read-only file system`. The container was never created or started and no persistent volume was allocated. Human input requests sufficient free space and writable Docker storage; permission to delete shared volumes is not inferred. After local remedies are verified and committed, rerun the exact unchanged current-tree gate with a writable Docker prerequisite. Ship and draft PR creation remain prohibited until it passes. The [approved outside-checkout human assignment](../designs/issue-806-durable-worker-template.md#the-assignment), hosted matrix/timing, separate Skoolit release evidence and doctor coordination retain their original obligations and ordering.
