# Unreleased

This is the living release note for the next coordinated AppSurface version after `0.1.0-rc.3`. It stays provisional until the next tag is cut.

## What is taking shape

- AppSurface CI can now prove the default full-solution coverage lane through the `appsurface coverage run` command, running from source via `dotnet run --project`, without waiting on matrix fan-in workflows.
- Reader-intent relevance for AppSurface Docs search.
- More trustworthy AppSurface Docs search typing for multi-word queries.

## Included in the next coordinated version

### Release and docs surface

- AppSurface CI coverage now dogfoods the `appsurface coverage run` command, running from source via `dotnet run --project`, for the default full-solution lane. The lane preserves the existing merged Cobertura, managed JUnit, slow-test diagnostics, Codecov, and `coverage gate` evidence paths while `scripts/coverage-solution.sh` keeps legacy compatibility for grouped runs, group listing, merge-only runs, `TEST_GROUP`, and `BUILD_SOLUTION=false`.
- `appsurface coverage run` now supports `--test-results junit` for AppSurface-managed top-level JUnit artifacts. `--slow-test-diagnostics` implies managed JUnit results and writes diagnostics from those files; `junit` is the only managed result format in this release, with TRX/TUnit compatibility reserved for #491.
- Coverage runs now emit `slow-test-diagnostics.md` and `slow-test-diagnostics.json` next to the merged coverage artifacts. The diagnostics rank project and JUnit test-case timings, preserve best-effort parser warnings without changing coverage exit codes, record metadata completeness, and report diagnostic aggregation overhead in seconds and as a percent of elapsed runner time at diagnostics generation.
- AppSurface Docs search now hydrates MiniSearch candidates from the normalized docs payload and applies deterministic reader-intent ranking before both sidebar and full-page rendering. Exact title, path, source, alias, keyword, and entry-point matches stay protected; broad task queries prefer reader-facing guides; explicit API/internal filters override broad-task boosts; and contributor/internal docs are demoted unless the query asks for them directly.
- AppSurface Docs search now preserves multi-word spacing while readers type, so pausing after a separator in either the full-page search workspace or sidebar search no longer joins words together.
- RazorWire now includes a hybrid-hosting guide for split-origin deployments that serve exported static pages from one origin while Cloud Run or another container host serves RazorWire streams, islands, and lazy anti-forgery forms from a live origin.

## Migration watch

- Record breaking or behavior-changing guidance here before it moves into the tagged release note.
