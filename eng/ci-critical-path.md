# AppSurface CI Critical Path

This note is the CI-000 baseline for build-time work. It records the current PR decision path before coverage artifact changes or test-group experiments are judged by timing data.

## Current PR validation path

AppSurface currently runs these workflows for ordinary pull requests to `main`:

- `build.yml`: generated package chooser verification, Markdown snippet verification, web asset verification, solution tests with coverage, Codecov upload, and AppSurface Docs export.
- `package-gate.yml`: isolated NuGet restore, release package build, package manifest gate, and package packing.
- `package-artifacts.yml`: package artifact verification and upload.
- `code-quality.yml`: locked restore, release build, generated verification, and `dotnet format --verify-no-changes`.
- `release-contract.yml`: PR title and unreleased-entry contract.
- `release-prep.yml`: release-tooling or release-artifact review only for release-related paths.
- `vcs-ignore-parity.yml`: docs VCS ignore parity tests only for docs/package-lock related paths.

GitHub returned `Branch not protected` for `main` on 2026-06-02, so there is no GitHub-enforced required-check set at this snapshot. The trusted PR decision path is therefore the practical all-green convention across the workflows above, not a branch-protection rule.

## Baseline timing sample

Recent PR runs from 2026-06-02 showed this shape:

- `build.yml` was the long tail at roughly 9-10 minutes end to end when docs export was included; the old serialized `Run tests` step was roughly 6m13s to 6m20s.
- `package-gate.yml`, `package-artifacts.yml`, and `code-quality.yml` generally completed in about 2-4 minutes.
- `release-contract.yml`, `release-prep.yml`, and `vcs-ignore-parity.yml` were short checks when they ran, but they still contribute to the all-green moment.
- The docs export job repeated generated package chooser, Markdown snippet, and web asset verification after `build` already completed those checks, costing roughly 41-55 seconds in the sampled runs.

Future CI performance changes should compare:

- p50 and p95 time from push to all trusted PR checks green.
- `build.yml` total time and test lane time.
- Codecov coverage upload and test-result upload success.
- Total runner minutes, because matrix fan-out can trade wall-clock time for capacity.
- Stale-run cancellation behavior for superseded commits on the same PR branch.

## v1 policy

- Keep all current PR workflows active.
- Keep coverage as PR confidence unless a measured follow-up proves Codecov coverage and test-result reporting still work under a different policy.
- Use `concurrency.cancel-in-progress` on long PR workflows so superseded commits stop consuming runner capacity.
- Keep PR tests in a single coverage-bearing lane until measured group runs prove they reduce total GitHub Actions minutes, not just wall-clock time.
- Keep experimental test groups bounded and readable: `core`, `tools`, `web`, `docs`, `razorwire`, and `integration`.
- Preserve a single merged Cobertura output at `TestResults/coverage-merged/coverage.cobertura.xml` for Codecov and local contributors.

## NuGet cache policy

Issue #479 treats NuGet package caching as a measured CI hygiene change, not as the primary fix for the solution coverage long pole. Caching is enabled only for jobs that repeatedly restore the shared locked dependency graph:

- `build.yml` / `build`
- `build.yml` / `export-appsurface-docs`
- `code-quality.yml` / `dotnet-format`

Each cache-enabled job sets `NUGET_PACKAGES: ${{ github.workspace }}/.nuget/packages` at the job level before `actions/setup-dotnet`, then uses `setup-dotnet` with `cache: true` and `cache-dependency-path: '**/packages.lock.json'`. The broad lock-file path is intentional for this first rollout because the selected solution-shaped jobs restore project graphs spread across the repository. A dependency or lock-file change anywhere in that graph can invalidate the cache; that is expected and safer than silently omitting a restored project from the cache key. If future timing data shows lock-file churn dominates cache misses, split the cache dependency paths by job/project scope in a follow-up.

Cache-enabled build jobs should restore explicitly before later .NET commands. When `build.yml` runs `scripts/coverage-solution.sh` after that restore, it sets `BUILD_NO_RESTORE=true` so the script's solution build does not hide additional restore work inside the coverage timing.

Package-sensitive workflows stay uncached unless a separate issue evaluates their trust boundary. In particular, `package-gate.yml` must keep its isolated `${{ runner.temp }}/nuget-packages` restore and `NuGet.package-gate.config` source policy. Publish, smoke-restore, trusted-publishing, and package validation workflows should not inherit the shared NuGet cache by convention.

Cache misses are normal after dependency updates, lock-file updates, or cache eviction. The cache is only useful if warm runs reduce selected workflow time or runner minutes without regressing the all-green decision path. For cache experiments, record at least:

| Run | Cache state | Setup .NET | Locked restore | Build/tests/docs | Workflow total | Notes |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Baseline after #476 | none | | | | | |
| First #479 run | miss/populate | | | | | |
| Warm #479 run | hit | | | | | |

Use the GitHub UI for action step duration and the job summary for cache-hit, package path, package folder size, and locked restore duration. Merge cache changes only when warm-cache evidence shows at least a 30 second net improvement in one selected workflow or at least a 5% total runner-minute/all-green improvement without regression. If the threshold is not met, remove the cache workflow changes and keep only the measurement notes.

Common restore failures:

- A locked restore failure usually means package references or central package versions changed without updating `packages.lock.json`. Run `dotnet restore --force-evaluate`, commit the updated lock files, and rerun CI.
- `NU1403` after enabling setup-dotnet caching can happen because the action restores only the global packages folder. If CI hits this, add or test `DisableImplicitNuGetFallbackFolder` centrally before widening the cache rollout.

Rollback is per job: remove `cache: true`, remove `cache-dependency-path`, and remove job-level `NUGET_PACKAGES` from the affected cache-enabled job. Leave package-gate's isolated temp `NUGET_PACKAGES` in place.
