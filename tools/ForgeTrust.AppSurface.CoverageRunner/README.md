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
- `slow-test-diagnostics.md`
- `slow-test-diagnostics.json`
- `junit-*.xml`
- per-project `dotnet-test.log` files

Detailed `dotnet test` output is captured per project and replayed in solution order before the final
summary so CI logs stay readable even when projects run concurrently.

## Slow-Test Diagnostics

Every successful test scheduling attempt writes slow-test diagnostics from the recorded JUnit paths.
The report is best effort: malformed, missing, or incomplete JUnit files add warnings to the
diagnostic artifacts, but they do not change the coverage runner exit code. Test failures still take
precedence, followed by coverage merge failures and summary parsing failures.

`slow-test-diagnostics.md` is the human-readable report. It includes:

- diagnostic aggregation overhead in seconds and as a percent of total runner time
- project timing metadata when a normal run produced it
- the slowest parsed JUnit test cases
- evidence categories such as `browser-or-integration`, `process-startup`,
  `filesystem-artifacts`, and `coverage-tooling`
- parser and metadata warnings

`slow-test-diagnostics.json` is the machine-readable companion. Its top-level shape is:

- `schemaVersion`: current value is `1`.
- `generatedAtUtc`: UTC timestamp for traceability.
- `group`: selected coverage group.
- `metadataComplete`: `true` for normal runs with project metadata; `false` for merge-only
  diagnostics that only have copied JUnit files.
- `overhead`: `aggregationSeconds` and `aggregationPercent`.
- `artifacts`: absolute paths to the Markdown and JSON diagnostic artifacts.
- `totals`: project, JUnit, test case, failure, skipped, and warning counts.
- `topProjects`: project, group, exclusivity, duration, exit code, JUnit path, and log path.
- `topTestCases`: class, test name, duration, status, project metadata when known, JUnit path, and
  evidence categories.
- `categories`: aggregate category counts, maximum observed seconds, confidence, and evidence.
- `warnings`: bounded parser and metadata warnings.

The diagnostics use JUnit XML attributes rather than per-project `dotnet-test.log` content. This
keeps the report bounded and means evidence categories are triage hints, not root-cause claims.
Merge-only runs copy available `junit-*.xml` files into the output directory and write a partial
diagnostic report with `metadataComplete=false` because project run duration and exclusivity data are
not available from copied XML alone.
