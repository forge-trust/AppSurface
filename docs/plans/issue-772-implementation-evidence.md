# Persona fixture activation implementation evidence (#772)

This records execution of the [approved plan](issue-772-persona-scoped-fixture-activation.md) and [contract map](issue-772-test-plan.md). The [design](../designs/issue-772-persona-scoped-fixture-activation.md) remains the decision record. A checked implementation item requires its stated verification; authored code alone is not a pass.

Branch: `codex/make-it-so-772-fixture-activation`.

| Facet | Acceptance baseline evidence | Status |
| --- | --- | --- |
| Optional scoped selection hook | Interface and endpoint complete; captured token checked before and after one await; package suite 156/156 passed | Passed |
| Shared candidate and readiness | Immutable snapshots, short synchronous lock, role-specific ready-only actions and read-only pages; sample suite 90/90 and actual browser journey passed | Passed |
| Safe failure recovery | Typed sample middleware before auth/endpoints; retained identity 500 → 409 → explicit retry 200 proved through HTTP tests and real-Kestrel browser | Passed |
| Existing verifier extension | Stable ID, separate progress and repeated selections; real-socket verifier exit 0, contract tests 60/60 | Passed |
| Source-backed docs and discovery | Source-backed docs generated/verified; package chooser verify and brand gate passed after planning wording correction | Passed |
| Enhancement review | Enhance cycle 1: production core/adversarial review no actionable findings; focused suites and browser acceptance passed; fresh ship reviews pending | Completed locally |
| Standard browser QA and prepared journey | [Browser and prepared-developer execution record](issue-772-browser-qa.md#execution-record-2026-10-01); all browser checks passed; proxy 7m16s exceeds 5min target | Passed with timing concern |
| Coverage gate | Exact command `./scripts/coverage-solution.sh` exited 0: aggregate 95.11% line/89.04% branch; patch 99.51% line/97.67% branch; configured 95%/85% thresholds and 0.5-point tolerance unchanged | Passed |
| Draft PR | Requires all gates above | Pending |

Existing admin/viewer, control/status, clear, marker and verifier ownership/redaction/deadline contracts remain required. This work introduces no runtime fault switch, external provider, release publication or durable/multi-process fixture guarantee.

## Validation ledger

The implementation was committed as `b34999c3` and integrated with `origin/main` in `e1bca718` before acceptance. The initial enhancement pass completed with no actionable core or adversarial findings and stopped before its four-cycle checkpoint. Standard QA made no source changes.

The planned endpoint and sample test extensions were split into focused companion files:
[persona-selection endpoint contracts](../../Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth.Tests/AppSurfaceDevAuthPersonaSelectionTests.cs)
and [fixture activation HTTP contracts](../../examples/auth-aspnetcore-dev-auth.tests/PersonaScopedFixtureActivationHttpTests.cs).
Existing admission cases remain in the original endpoint file. The sample's rendering lives in
[LocalCandidatePages](../../examples/auth-aspnetcore-dev-auth/LocalCandidatePages.cs), and the actual
[unreleased entry](../../releases/unreleased.entries/2026-10-01-dev-auth-persona-activation.md) supplies the approved release guidance.

| Command or check | Observed result |
| --- | --- |
| DevAuth package xUnit suite | 156 passed, including activation, cancellation, request scope, admission and marker browser contracts |
| DevAuth sample xUnit suite | 90 passed, including 60 verifier contracts and deterministic role overlap/failure recovery |
| `./examples/auth-aspnetcore-dev-auth/verify.sh` | Exit 0; real socket, shared candidate and preserved independent work |
| MarkdownSnippets `generate` and `verify --document Auth/ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth/README.md` | Exit 0 for both; source-backed setup, handler and store |
| PackageIndex `generate`, `verify` and `gate` | Exit 0 after changing one planning phrase to the repository's accepted brand wording |
| `dotnet format` and `dotnet format --verify-no-changes` | Exit 0 for both |
| `dotnet build` | Exit 0, zero warnings and errors; 2m51s |
| PackageIndex `verify-packages --package-version 0.0.0-ci.local` | Exit 0 after the fixture-root repair; all 49 package artifacts validated |
| Standard browser QA | No actionable finding; full results and measurement limits in the [execution record](issue-772-browser-qa.md#execution-record-2026-10-01) |
| `./scripts/coverage-solution.sh` | Exit 0 on `16aee8e1`; 54 suites, 14,969 passed, zero failed and four existing skips; aggregate 95.11% line/89.04% branch and patch 99.51% line/97.67% branch |

### Full validation history

The initial parallel `dotnet test --no-build` run encountered startup deadlines and a Playwright browser-install failure, then stalled with test hosts still active. Its confirmed process tree was stopped after fifteen minutes. The complete serial retry on a non-sandboxed host, `dotnet test --no-build -m:1`, finished with 14,961 passed, eight failed and four existing skips across 54 suites. Six failures were unchanged Durable prerequisite-script tests timing out; rerunning their complete class passed all 11 tests in two seconds with the same timeouts. The other two failures were release-page screenshot comparisons: the new release entry adds the DevAuth outline item in the bottom-right corner. Five release snapshots were deliberately refreshed and visually inspected; six incidental home/search rasterization changes were restored. The full coverage run on `16aee8e1` subsequently passed all 54 suites, including fresh strict screenshot comparisons and the prerequisite-script tests. No tests, timeouts or coverage thresholds were removed.

The first PackageIndex `verify-packages` attempt produced 49 packages and passed the Docs consumer proof, but its synthetic Coverage CLI patch-target proof failed. The reports existed; their uncovered-target content did not match because automatic repository-root detection selected the outer checkout while the synthetic diff used consumer-relative paths. The narrow fixture repair explicitly passes the consumer directory as `--repository-root`; its rebuilt command-contract regression passed. The complete package proof then exited 0 and validated all 49 artifacts.

The unchanged hard coverage gate remains `./scripts/coverage-solution.sh`: 95% line and 85% branch for aggregate and patch coverage against `origin/main`, with the repository's existing tolerance. The exact command passed on `16aee8e1` with all tracked input bytes matching its pre-run snapshot. Final ship reviews, documentation audit and frozen-candidate verification remain required before a draft PR is created.

## Final ship follow-through

The table above records the passing acceptance baseline on `16aee8e1`, rather than a claim that later edits inherit its coverage result. A fresh full ship run then exited 1 with six unchanged timeout/process-cleanup test failures across the Durable PostgreSQL example and AdoptionMetrics suites. Its aggregate report was 95.10% line and 89.04% branch. Passing coverage percentages alone do not satisfy the gate when tests fail; the exact command must pass again on the final candidate. No failures or thresholds are waived.

The ship security review reproduced HTTP 200 from the sample control page with an unapproved Host header. The sample now installs ASP.NET Core host filtering before identity and fixture mutations, with explicit `localhost`, `127.0.0.1`, and `[::1]` authorities and empty hosts disabled. Nine HTTP regression cases first produced three failures and six passes before the fix, then all nine passed. They cover ports, matching Origin on an unapproved authority, both empty and existing fixture state, and rejected control/status/selection/clear/candidate requests without a cookie or state change. The [sample admission guidance](../../examples/auth-aspnetcore-dev-auth/README.md#persona-fixture-activation) describes configuration and middleware ordering. The reusable package contract is unchanged.

The final draft PR's verification section will supply fresh results for the exact full coverage command, generated documentation and final host behavior. Ship reviews, the ship-owned documentation audit and frozen-candidate verification must complete before publication.
