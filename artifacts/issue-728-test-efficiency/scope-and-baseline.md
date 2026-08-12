# Issue #728 coverage-efficiency scope and baseline

Complete this committed record before changing any sharing or scheduler boundary. Raw local `TestResults/coverage-merged/` output remains uncommitted; each row below links the workflow run, commit SHA, command, and retained `coverage-efficiency-evidence` artifact that produced it.

## Measurement declaration

| Field | Value |
| --- | --- |
| Primary outcome | Exact wall-clock duration of the `Capture coverage efficiency evidence` step in `.github/workflows/coverage-efficiency.yml`. |
| Supporting attribution | Resolved schedule and per-project end-to-end `seconds` from `timings.json`; these include launch through coverage normalization and are never reported as test-process time. |
| Authoritative CI command | The manual workflow runs `BUILD_CONFIGURATION=Release BUILD_NO_RESTORE=true COVERAGE_PARALLELISM=2 COVERAGE_GATE_DIFF_BASE= ./scripts/coverage-solution.sh` with the wrapper's non-sandbox guard enabled. |
| CI-equivalent local screening command | `BUILD_CONFIGURATION=Release BUILD_NO_RESTORE=true COVERAGE_PARALLELISM=2 COVERAGE_GATE_DIFF_BASE= COVERAGE_REQUIRE_NON_SANDBOX=false ./scripts/coverage-solution.sh`; use the override only when a restricted local automation environment cannot satisfy the guard. This is screening-only, never CI or issue-claim evidence. |
| Comparable CI environment | The manual workflow records the commit, run URL, runner image, .NET SDK, Docker/PostgreSQL evidence, Node/pnpm, and Playwright browser inventory. A runner/runtime mismatch makes samples non-comparable. |
| Local evidence status | Screening-only unless the recorded environment fingerprint matches the manual workflow. |
| Cold state | Clear NuGet global packages and prune pnpm’s store before locked restore; Docker images and Playwright browsers remain pinned prerequisites, not reusable test fixture state. |
| Warm state | Use the runner’s declared dependency caches; no test container, database, process, browser context, profile, or coverage output is reused. |

## Metric reconciliation

Record the source of the issue’s reported 401 seconds before treating it as an acceptance threshold.

| Reported value | Source URL / artifact | Metric definition | Reconciled with exact step wall time? | Notes |
| --- | --- | --- | --- | --- |
| 401 seconds | [Issue #728](https://github.com/forge-trust/AppSurface/issues/728) | Pending evidence reconciliation | No | Context only until this table is complete. |
| 240 seconds | [Issue #728](https://github.com/forge-trust/AppSurface/issues/728) | Requested outcome | No | May be claimed only with the comparable evidence required by `results.md`. |

## Resolved serial-set scope

Populate this table from `timings.json` and the artifact’s derived `resolved-serial-set.json` for each comparable run. The actual serial set is authoritative. The issue-named and explicit-barrier views are subsets and must not be substituted for it.

| Project path | Issue-named | Explicit barrier | Automatic classification | Actual serial set | Exclusivity source | Preceding parallel batch | Barrier-critical-path rationale | Project-run seconds | Evidence run URL |
| --- | --- | --- | --- | --- | --- | --- | --- | ---: | --- |
| `Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj` | yes | yes | pending | pending | pending | pending | pending | pending | pending |
| `examples/auth-web-razorwire-proof.tests/AuthWebRazorWireProofExample.Tests.csproj` | yes | yes | pending | pending | pending | pending | pending | pending | pending |
| `Web/ForgeTrust.AppSurface.Web.Tailwind.Tests/ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj` | yes | yes | pending | pending | pending | pending | pending | pending | pending |
| `examples/auth-aspnetcore-dev-auth.tests/AuthAspNetCoreDevAuthExample.Tests.csproj` | yes | yes | pending | pending | pending | pending | pending | pending | pending |
| `Config/ForgeTrust.AppSurface.Config.Tests/ForgeTrust.AppSurface.Config.Tests.csproj` | no | yes | pending | pending | pending | pending | pending | pending | pending |
| `Web/ForgeTrust.RazorWire.Cli.Tests/ForgeTrust.RazorWire.Cli.Tests.csproj` | no | yes | pending | pending | pending | pending | pending | pending | pending |
| Additional integration/Playwright project from the emitted schedule | no | no | pending | pending | pending | pending | pending | pending | pending |

## Sample worksheet

Capture five cold and five warm local screening samples. A class is comparable when its relative spread, `(max - min) / median`, is at most 10%. If it is noisier, capture exactly one additional five-sample set. If that retry is also noisy, record no time claim for that class.

| State | Sample | Commit SHA | Command / cache declaration | Exact step seconds | Run URL / artifact | Comparable? | Notes |
| --- | ---: | --- | --- | ---: | --- | --- | --- |
| cold | 1 | pending | pending | pending | pending | pending | pending |
| warm | 1 | pending | pending | pending | pending | pending | pending |

## Baseline decision

- Selected candidate: pending inventory.
- Rejected candidates and reason: pending inventory.
- Baseline owner: pending.
- Baseline completion date: pending.
