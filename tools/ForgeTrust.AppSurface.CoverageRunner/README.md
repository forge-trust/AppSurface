# AppSurface Coverage Runner

`ForgeTrust.AppSurface.CoverageRunner` is the implementation behind `scripts/coverage-solution.sh`.
Call the script from local shells and CI so the entrypoint stays stable while the runner owns test
project discovery, scheduling, coverage artifact layout, and summary generation.

## Usage

```bash
scripts/coverage-solution.sh [solution] [output]
scripts/coverage-solution.sh --group <name> [--output <dir>] [--solution <path>]
scripts/coverage-solution.sh --list-groups
scripts/coverage-solution.sh --merge-only <source-dir> [--output <dir>]
```

Supported groups are `all`, `core`, `tools`, `web`, `docs`, `razorwire`, and `integration`.
`all` builds the solution before running tests by default. Named groups skip the solution build by
default so they can be used as focused local probes.

## Environment

- `TEST_GROUP`: group selected when `--group` is not supplied. Valid values are `all`, `core`,
  `tools`, `web`, `docs`, `razorwire`, and `integration`. Defaults to `all`; this runs every
  discovered test project and uses the default solution-build path.
- `BUILD_CONFIGURATION`: `dotnet build` and `dotnet test` configuration. Defaults to `Debug`.
- `BUILD_SOLUTION`: `true` or `false`. Overrides the default build behavior.
- `BUILD_NO_RESTORE`: when `true`, forwards `--no-restore` to build and test commands.
- `COVERAGE_PARALLELISM`: positive integer for non-exclusive project concurrency. Defaults to `1`.
- `INCLUDE_FILTER`: coverlet include filter. Defaults to `[ForgeTrust.AppSurface.*]*`.
- `EXCLUDE_FILTER`: coverlet exclude filter. Defaults to `[*.Tests]*,[*.IntegrationTests]*`.

## Scheduling

The runner keeps all work inside one process tree and one GitHub Actions job. Non-exclusive projects
run through a bounded queue controlled by `COVERAGE_PARALLELISM`. Integration-test projects and
projects that reference Playwright run exclusively: the runner waits for active work to finish, runs
that project alone, then resumes the queue.

This keeps the first parallelism rollout focused on reducing wall-clock time without multiplying
runner jobs or hiding capacity cost in a matrix fan-out.

## Artifacts

Each project writes to `TestResults/coverage-merged/projects/<project>/` by default. The runner then
merges Cobertura files once with ReportGenerator and writes:

- `coverage.cobertura.xml`
- `summary.txt`
- `timings.json`
- `junit-*.xml`
- per-project `dotnet-test.log` files

Detailed `dotnet test` output is captured per project and replayed in solution order before the final
summary so CI logs stay readable even when projects run concurrently.
