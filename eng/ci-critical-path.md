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
