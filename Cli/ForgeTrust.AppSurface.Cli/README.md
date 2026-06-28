# AppSurface CLI

The **AppSurface CLI** is the command-line home for repository-level AppSurface workflows. It is packaged as a .NET tool with the command name `appsurface`.

The first public verb family is `docs`, which replaces the earlier standalone `appsurfacedocs preview --repo .` idea with AppSurface-owned preview and export commands:

```bash
appsurface docs --repo .
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3
```

`appsurface docs` runs the same AppSurface Docs standalone host used by CI and integration tests. It forwards AppSurface Docs configuration into that host instead of duplicating harvesting, routing, static web asset, or MVC setup in the CLI. `appsurface docs export` starts that same host in-process, binds an internal loopback listener, and delegates static crawling plus CDN validation to the RazorWire export engine. `appsurface docs verify-archive` checks one catalog-pinned exact release tree locally before deploy.

The CLI also includes public coverage commands for private-by-default CI coverage enforcement. `appsurface coverage run` discovers or accepts instrumented .NET test projects, writes local coverage artifacts, and merges Cobertura through the package-owned ReportGenerator dependency in the same command. `appsurface coverage merge` fans in existing Cobertura shards from matrix or custom test workflows without reading a consumer tool manifest. `appsurface coverage gate` evaluates the merged local Cobertura XML, writes JSON and Markdown reports, and can append the same Markdown to GitHub Actions step summaries without uploading coverage data to a hosted coverage service.

`appsurface secrets` manages the first-secret workflow for `ForgeTrust.AppSurface.Config.LocalSecrets`: initialize a local namespace, set one key, verify presence without printing the value, list names, delete keys, and run doctor diagnostics for platform availability.

Future CLI authentication is design-only today. The [authenticated command design](docs/authenticated-command-design.md) keeps auth centered on protected command execution, uses `appsurface docs publish --archive ./dist/docs --site <site>` as the first protected command wedge, and requires browser/loopback PKCE, RFC 8628 device flow, CI no-prompt behavior, secure token-cache boundaries, `ASCLI1xx` diagnostics, and packed-tool readiness proof before auth commands ship.

## Release Guidance

AppSurface ships as a coordinated package family. Before installing this package from a prerelease feed, check the [package chooser](../../packages/README.md) and [release hub](../../releases/README.md) for current release risk, migration guidance, and readiness.

## Install

Install the published tool from NuGet:

```bash
dotnet tool install --global ForgeTrust.AppSurface.Cli --prerelease
```

Update an existing global install with the same package id:

```bash
dotnet tool update --global ForgeTrust.AppSurface.Cli --prerelease
```

Verify the installed global or tool-path command reports the package SemVer exactly:

```bash
appsurface --version
```

Prerelease installs print values such as `0.1.0-rc.1`, without a leading `v` or build metadata. Use the package artifact manifest, publish ledger, or release note when you need build provenance beyond the package version.

Use a local tool manifest when you want the command version pinned per repository:

```bash
dotnet new tool-manifest
dotnet tool install ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface --version
dotnet tool run appsurface docs --repo .
dotnet tool run appsurface coverage run --solution ./MyApp.slnx --dry-run
dotnet tool run appsurface coverage run --solution ./MyApp.slnx
dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 95 --min-branch 85 --diff-base origin/main --min-patch-line 95 --min-patch-branch 85
```

Update the repo-scoped tool version with:

```bash
dotnet tool update ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface --version
```

## Commands

### `appsurface secrets`

Manage AppSurface local development secrets before a remote vault exists.

```bash
appsurface secrets init --app MyApp --environment Development
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
appsurface secrets doctor --app MyApp --environment Development
appsurface secrets list --names-only --app MyApp --environment Development
appsurface secrets get Stripe:ApiKey --app MyApp --environment Development
```

`get` verifies presence and source without printing the secret value. `list` prints currently retrievable names only:
platform-backed stores validate indexed names against live values and silently remove stale names when validation and
repair succeed. If the platform store is locked, unavailable, or the index is corrupt, `list` fails with a paste-safe
diagnostic instead of hiding names it could not verify. `delete KEY` also repairs stale indexed names when the value is
already gone, while keys that never existed still report `local-secret-missing`.

`doctor` reports `Problem`, `Cause`, `Fix`, `Docs`, and `Retryable` so unsupported platforms, locked stores, and
headless sessions fail closed instead of falling through to file secrets. Use `--store-file <path>` only for
deterministic examples and tests; normal local development should use the OS-backed store when the platform adapter is
available. Use environment variables, key-per-file, or a remote vault for CI, containers, team environments, and
production.

For explicit file fallback, `doctor` can render these value-safe posture codes:

```text
local-secret-store-ready
local-secret-file-posture-repaired
local-secret-file-posture-degraded
local-secret-file-posture-unsupported
```

`ready`, `repaired`, and `degraded` are doctor-style readiness results and exit successfully so setup scripts can keep
moving. `degraded` still means the file fallback is weaker than the OS-backed store; on Windows it is expected because
v1 does not claim Windows ACL hardening, and on Unix it is reserved for paths AppSurface can open but cannot fully
prove. `unsupported` fails the command and points to a normal per-user file path or OS-backed storage. On Unix, unsafe
path shapes, loose existing directories, loose file mode bits, and writable non-sticky ancestors use `unsupported`
rather than `degraded`. The file fallback creates missing directories with `0700` mode bits and tightens JSON file mode
bits to `0600`; existing loose parent directories are rejected rather than modified in place. v1 does not claim
universal POSIX ACL proof.

On Linux, the OS-backed store runs Secret Service through `secret-tool`, but AppSurface does not execute `secret-tool`
from `PATH`. It uses `/usr/bin/secret-tool`, then `/bin/secret-tool`, unless you pass an explicit trusted absolute path:

```bash
SECRET_TOOL=/absolute/path/to/secret-tool
test -x "$SECRET_TOOL"
appsurface secrets doctor --app MyApp --environment Development --secret-tool-path "$SECRET_TOOL"
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --secret-tool-path "$SECRET_TOOL" --stdin
```

Use `--secret-tool-path` for Nix, Linuxbrew, Guix, or custom prefixes after verifying the binary. The flag applies only
to the current CLI invocation; configure `AppSurfaceLocalSecretsOptions.LinuxSecretToolPath` in the app for runtime use.
`--secret-tool-path` and `--store-file` are mutually exclusive so `doctor` cannot report file-store readiness when you
meant to verify the Linux platform store.

### `appsurface coverage run`

Run instrumented .NET test projects and merge private Cobertura artifacts.

```bash
appsurface coverage run \
  --solution ./MyApp.slnx \
  --output ./TestResults/coverage-merged
```

`coverage run` is the public package-consumer coverage orchestrator for private .NET repositories. It supports `.sln` and `.slnx` discovery, repeated `--test-project` selection, a default output directory that matches `coverage gate`, bounded parallel scheduling, per-project logs, stable per-project artifact directories, safe cleanup of AppSurface-owned outputs, managed JUnit test-result artifacts, optional slow-test diagnostics, and a package-owned ReportGenerator merge. Package consumers do not need a separate merge step: the command finishes by writing the merged `coverage.cobertura.xml` artifact. It does not mutate consumer projects, install tools into the consumer repo, read the consumer `.config/dotnet-tools.json`, upload coverage, call GitHub APIs, or store trends.

The v1 contract assumes selected test projects are already instrumented with Coverlet. No managed test result export happens by default. Use `--test-results junit` when AppSurface should own top-level JUnit artifacts, and make sure every selected test project references `JunitXml.TestLogger`. `junit` is the only managed test-result format supported in this release; `trx` and TUnit-compatible parsing are reserved for follow-up work. `--logger` remains raw `dotnet test` pass-through and does not create AppSurface-managed artifacts.

#### Already Has Coverlet

```bash
dotnet new tool-manifest
dotnet tool install ForgeTrust.AppSurface.Cli --prerelease
dotnet tool run appsurface coverage run --solution ./MyApp.slnx --dry-run
dotnet tool run appsurface coverage run --solution ./MyApp.slnx
dotnet tool run appsurface coverage gate --min-line 85 --min-branch 75
```

Use `--dry-run` before the first real CI run to confirm project discovery, exclusive scheduling, and artifact paths without running tests.

#### Add Coverlet First

```bash
dotnet add tests/MyApp.Tests/MyApp.Tests.csproj package coverlet.msbuild
dotnet tool run appsurface coverage run --test-project tests/MyApp.Tests/MyApp.Tests.csproj
dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 85 --min-branch 75
```

Add `coverlet.msbuild` to every selected test project that should contribute coverage. `coverage run` passes Coverlet MSBuild properties to `dotnet test`, but it intentionally does not edit project files or add packages on the consumer's behalf.

Options:

- `--solution`: Solution file used for discovery. Supports `.sln` and `.slnx`. When omitted, the current directory must contain exactly one `.sln` or `.slnx`.
- `--test-project`: Repeatable explicit test project path. Supplying one or more values skips solution discovery.
- `--output`: Coverage output directory. Defaults to `TestResults/coverage-merged`.
- `--configuration`: Build/test configuration. Defaults to `Debug`.
- `--parallelism`: Positive integer for non-exclusive test project concurrency. Defaults to `1`.
- `--no-restore`: Passes `--no-restore` to build and test commands.
- `--build`: Builds the solution once before tests, including explicit-project runs.
- `--no-build`: Skips the solution build before tests.
- `--include`: Coverlet include filter. Omit it to use Coverlet's project defaults.
- `--exclude`: Coverlet exclude filter. Defaults to `[*.Tests]*,[*.IntegrationTests]*`.
- `--dry-run`: Prints discovery, scheduling, and artifact paths without running tests.
- `--list-projects`: Lists selected and skipped projects without running tests.
- `--no-discover-exclusive`: Disables automatic exclusive classification for integration or Playwright-shaped projects.
- `--exclusive-test-project`: Repeatable project path or file name that should run exclusively.
- `--logger`: Repeatable `dotnet test` logger value forwarded as `--logger:<value>`.
- `--test-argument`: Repeatable extra argument token appended to every `dotnet test` invocation.
- `--test-results`: Managed test-result format. Use `junit` to write AppSurface-owned top-level JUnit files. Other values fail before tests run.
- `--slow-test-diagnostics`: Writes `slow-test-diagnostics.md` and `.json` from managed JUnit results. This implies `--test-results junit`.
- `--no-clean`: Preserves existing AppSurface-owned output instead of cleaning known coverage artifacts first.
- `--verbosity`: `dotnet test` verbosity. Defaults to `minimal`.

Artifacts are local and private by default:

- `coverage.cobertura.xml`: Merged Cobertura file consumed by `coverage gate`.
- `summary.txt`: Human-readable merged line and branch coverage summary.
- `timings.json`: Machine-readable build, test, merge, managed test-result, diagnostics, artifact, log, and exit-code data.
- `reportgenerator-summary.txt`: Text summary from the package-owned ReportGenerator merge when available.
- `junit-coverage-<index>-<project-name-hash>.xml`: AppSurface-managed JUnit test results when `--test-results junit` or `--slow-test-diagnostics` is used.
- `slow-test-diagnostics.md` and `slow-test-diagnostics.json`: Slow-test evidence, parser warnings, metadata completeness, and diagnostic overhead when `--slow-test-diagnostics` is used.
- `projects/<project-name-hash>/coverage.cobertura.xml`: Per-project Coverlet Cobertura output.
- `projects/<project-name-hash>/dotnet-test.log`: Full `dotnet test` output for that project.
- `.appsurface-coverage-output`: Ownership marker that allows future runs to clean only known AppSurface-owned artifacts.

`coverage run` rejects unsafe output paths such as filesystem roots, the current working directory, the user home directory, the solution directory, test project directories, files, and populated directories that do not carry the AppSurface ownership marker. Use a dedicated artifact directory for CI, for example `TestResults/coverage-merged`.

`coverage run`, `coverage merge`, and `coverage gate` are the supported CLI coverage surfaces. The package artifact verifier installs the packed `ForgeTrust.AppSurface.Cli` tool in a clean fixture and proves all three coverage commands, including a deliberately failing gate that must still write reports, before publication. Grouped CLI execution and TRX/TUnit result parsing remain separate follow-up work.

Use this GitHub Actions shape for a private pull request workflow that already has Coverlet instrumentation. GitHub's checkout action fetches one commit by default; `fetch-depth: 2` is enough to keep the default pull request merge checkout and let the patch gate compare against the merge commit's base parent without fetching full history:

```yaml
- uses: actions/checkout@v5
  with:
    fetch-depth: 2
    persist-credentials: false
- uses: actions/setup-dotnet@v5
  with:
    dotnet-version: 10.0.x
- run: dotnet tool restore
- run: dotnet restore ./MyApp.slnx
- run: dotnet tool run appsurface coverage run --solution ./MyApp.slnx --configuration Release --no-restore --test-results junit --slow-test-diagnostics
- run: dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 85 --min-branch 75 --diff-base HEAD^1 --min-patch-line 85 --min-patch-branch 75
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: coverage
    path: |
      TestResults/coverage-merged/coverage.cobertura.xml
      TestResults/coverage-merged/summary.txt
      TestResults/coverage-merged/timings.json
      TestResults/coverage-merged/junit-*.xml
      TestResults/coverage-merged/slow-test-diagnostics.md
      TestResults/coverage-merged/slow-test-diagnostics.json
      TestResults/coverage-merged/coverage-gate.json
      TestResults/coverage-merged/coverage-gate.md
      TestResults/coverage-merged/projects/**/dotnet-test.log
```

GitHub's default `pull_request` checkout is the synthetic merge commit. `fetch-depth: 2` brings in the merge commit and its base parent, so `--diff-base HEAD^1` reports the pull request changes as tested by the job without fetching the full repository. If `fetch-depth: 2` is omitted, `actions/checkout` fetches only `HEAD`, `HEAD^1` is unavailable, and the gate fails closed with `ASCOV010`. If a workflow checks out the pull request head instead, use a head-vs-base source for that same tree; do not reuse merge-ref coverage artifacts with a head diff.

### `appsurface coverage merge`

Merge existing Cobertura shards that another workflow already produced.

```bash
appsurface coverage merge \
  --source ./TestResults/coverage-shards \
  --output ./TestResults/coverage-merged
```

Use `coverage merge` when a CI matrix, custom test harness, or non-AppSurface test producer already writes Cobertura files and you only need AppSurface's package-owned fan-in plus `coverage gate` artifacts. Use `coverage run -> coverage gate` for normal package-consuming .NET repositories where AppSurface should discover projects, invoke `dotnet test`, and merge the Coverlet output. In this repository, keep `./scripts/coverage-solution.sh --merge-only` for the legacy grouped contributor flow until that script is retired by a separate coverage-runner cleanup.

The v1 source contract is intentionally narrow. `--source` must point to an existing directory. The command recursively selects files named exactly `coverage.cobertura.xml`, sorts them by ordinal path, validates that each selected file has a Cobertura `<coverage>` root, and prints the discovered count plus the first few relative paths. A single shard is valid. Files named `Cobertura.xml`, arbitrary `*.xml`, or non-Cobertura XML are not accepted by v1; rename or copy producer artifacts to `coverage.cobertura.xml` before merging.

Options:

- `--source`: Required directory containing one or more `coverage.cobertura.xml` shard files.
- `--output`: Coverage output directory. Defaults to `TestResults/coverage-merged`.

Artifacts are local and private by default:

- `coverage.cobertura.xml`: Merged Cobertura file consumed by `coverage gate`.
- `summary.txt`: Human-readable merged line and branch coverage summary.
- `timings.json`: Machine-readable merge duration, selected shard count, selected input paths, ReportGenerator exit code, and merged Cobertura path.
- `reportgenerator-summary.txt`: Text summary from the package-owned ReportGenerator merge when available.
- `reportgenerator-input/`: AppSurface-owned staged copies of selected inputs, using deterministic sanitized shard directories.
- `.appsurface-coverage-output`: Ownership marker that allows future merges to clean only known AppSurface-owned merge artifacts.

`coverage merge` rejects unsafe output paths such as filesystem roots, the current working directory, the user home directory, files, source/output overlap in either direction, and populated directories that do not carry the AppSurface ownership marker. Use separate shard and output directories, for example `TestResults/coverage-shards` and `TestResults/coverage-merged`.

Use this GitHub Actions fan-in shape when matrix jobs upload Cobertura shard artifacts:

```yaml
jobs:
  test:
    strategy:
      matrix:
        shard: [unit, integration]
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore ./MyApp.slnx
      - run: dotnet test ./tests/${{ matrix.shard }} --collect:"XPlat Code Coverage"
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: coverage-${{ matrix.shard }}
          path: '**/coverage.cobertura.xml'

  coverage:
    needs: test
    steps:
      - uses: actions/checkout@v5
        with:
          fetch-depth: 2
          persist-credentials: false
      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x
      - run: dotnet tool restore
      - uses: actions/download-artifact@v4
        with:
          pattern: coverage-*
          path: ./TestResults/coverage-shards
          merge-multiple: false
      - run: dotnet tool run appsurface coverage merge --source ./TestResults/coverage-shards --output ./TestResults/coverage-merged
      - run: dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 85 --min-branch 75 --diff-base HEAD^1 --min-patch-line 85 --min-patch-branch 75
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: coverage
          path: |
            TestResults/coverage-merged/coverage.cobertura.xml
            TestResults/coverage-merged/summary.txt
            TestResults/coverage-merged/timings.json
            TestResults/coverage-merged/coverage-gate.json
            TestResults/coverage-merged/coverage-gate.md
```

#### Coverage Merge Diagnostics

Every merge diagnostic uses the `ASCOV130` through `ASCOV139` range and includes the problem, likely cause, exact fix, and docs anchor.

| Code | Meaning | Fix |
| --- | --- | --- |
| `ASCOV130` | The required source path is missing, invalid, unreadable, or not a directory. | Pass `--source` with an existing readable directory that contains shard artifacts. |
| `ASCOV131` | No `coverage.cobertura.xml` files were found. | Download or copy shard artifacts under `--source`, keeping the exact file name. |
| `ASCOV132` | A selected input is malformed, unreadable, or not Cobertura XML. | Regenerate the shard or remove non-Cobertura files before rerunning. |
| `ASCOV133` | Source and output overlap or alias each other. | Keep downloaded shards and merged artifacts in separate dedicated directories. |
| `ASCOV134` | The package-owned ReportGenerator dependency was not found. | Restore or reinstall `ForgeTrust.AppSurface.Cli` so its package dependencies are present. |
| `ASCOV135` | ReportGenerator failed, did not produce merged Cobertura, or produced malformed merged Cobertura. | Inspect selected shards and ReportGenerator output, then rerun after fixing the inputs. |
| `ASCOV136` | The output path is unsafe or not AppSurface-owned. | Use a dedicated AppSurface-owned output directory. |
| `ASCOV137` | Staging or artifact writes failed. | Use a writable dedicated output directory and rerun. |
| `ASCOV138` | A source or output path shape could not be normalized safely. | Use ordinary directory paths without invalid characters or unsupported path forms. |
| `ASCOV139` | AppSurface staging did not preserve every selected shard. | Clean the output directory or choose a fresh output path, then rerun. |

#### Coverage Run Diagnostics

Every `ASCOV###` diagnostic includes the problem, likely cause, exact fix, docs anchor, and a log path when a project log is available.

| Code | Meaning | Fix |
| --- | --- | --- |
| `ASCOV101` | A command option or project path is invalid. | Correct the option value, pass an existing test project, or use a valid dedicated output path. |
| `ASCOV102` | Solution discovery or `dotnet sln <solution> list` failed. | Pass a valid `.sln`/`.slnx`, fix the solution file, or use repeated `--test-project`. |
| `ASCOV103` | No Coverlet Cobertura files were produced. | Add `coverlet.msbuild` to each selected test project, then rerun `coverage run`. |
| `ASCOV104` | ReportGenerator merge failed. | Inspect per-project Cobertura files and rerun with `--dry-run` to verify selected projects. |
| `ASCOV105` | Discovery selected no test projects. | Rename test projects to match `*Tests.csproj`, or pass explicit `--test-project` values. |
| `ASCOV106` | The merged Cobertura file is malformed. | Regenerate coverage and inspect ReportGenerator output. |
| `ASCOV109` | The output path is unsafe or not AppSurface-owned. | Use a dedicated artifact directory such as `TestResults/coverage-merged`. |
| `ASCOV110` | `dotnet build`, `dotnet test`, or process startup failed. | Fix the build/test failure and inspect the listed project log. |
| `ASCOV111` | An unsupported managed test-result format was requested. | Use `--test-results junit`, omit `--test-results`, or keep custom loggers on `--logger`. |
| `ASCOV114` | The package-owned ReportGenerator dependency was not found. | Restore or reinstall `ForgeTrust.AppSurface.Cli` so its package dependencies are present. |
| `ASCOV120` | One or more test, merge, or artifact steps failed. | Open `timings.json` and per-project logs listed above, fix failing tests, then rerun. |

### `appsurface coverage gate`

Enforce a private coverage quality gate from an existing Cobertura XML file.

```bash
appsurface coverage gate \
  --coverage ./TestResults/coverage-merged/coverage.cobertura.xml \
  --min-line 95 \
  --min-branch 85 \
  --diff-base HEAD^1 \
  --min-patch-line 95 \
  --min-patch-branch 85
```

`coverage gate` is the stable v1 coverage API. It does not run tests, merge shards, upload coverage, call GitHub APIs, or store trends. It reads one Cobertura file, evaluates line and branch percentages, optionally estimates changed-line and changed-branch coverage from exactly one patch diff source, writes `coverage-gate.json` and `coverage-gate.md`, prints the result, and exits nonzero when any configured threshold fails. When `$GITHUB_STEP_SUMMARY` is set, the Markdown report is appended by default so GitHub Actions logs show the gate result without requiring Codecov or another hosted dashboard. Use `--no-github-summary` when a workflow wants only file artifacts.

Options:

- `--coverage`: Cobertura XML file to evaluate. Defaults to `TestResults/coverage-merged/coverage.cobertura.xml`.
- `--min-line`: Minimum line coverage percentage from `0` through `100`. Defaults to `0`.
- `--min-branch`: Minimum branch coverage percentage from `0` through `100`. Defaults to `0`.
- `--diff-base`: Git ref or commit compared with `HEAD` for patch coverage. When set, the command runs `git diff --unified=0 --no-ext-diff --relative <base>...HEAD --` from the repository root.
- `--diff-file`: Unified diff file for patch coverage. Use this for CI systems that can download a pull request or compare diff without fetching full repository history.
- `--diff-stdin`: Read unified diff text from stdin. Use this with pipes or redirected input; interactive stdin fails fast so local terminals do not appear hung.
- `--diff-label`: Optional display label for the selected patch source in JSON, Markdown, and GitHub summaries.
- `--repository-root`: Repository root used to normalize Cobertura paths and diff paths. Defaults to the Git worktree root when available, otherwise the current directory.
- `--min-patch-line`: Minimum changed-line coverage percentage from `0` through `100`. Requires exactly one patch source: `--diff-base`, `--diff-file`, or `--diff-stdin`. Omit it to report changed-line coverage without gating on it.
- `--min-patch-branch`: Minimum changed-branch coverage percentage from `0` through `100`. Requires exactly one patch source. Omit it to report changed-branch coverage without gating on it.
- `--output`: Directory for `coverage-gate.json` and `coverage-gate.md`. Defaults to the coverage file directory.
- `--github-summary`: Append Markdown to `$GITHUB_STEP_SUMMARY` when it is set. Enabled by default.
- `--no-github-summary`: Suppress GitHub step summary output.

The command accepts Cobertura root attributes such as `line-rate`, `branch-rate`, `lines-covered`, `lines-valid`, `branches-covered`, and `branches-valid`. XML parsing disables DTD processing and external resolution. Coverage counts must be non-negative, covered counts cannot exceed valid counts, rates must be from `0` through `1`, and zero valid line or branch counts fail with `ASCOV006` because a quality gate with no measurable denominator is misleading.

Patch coverage counts added or modified diff lines, intersects those lines with Cobertura `<class filename>` and `<line number hits>` entries, and reports covered/measurable lines. Changed lines that do not appear in the Cobertura line map are ignored for the denominator, which keeps docs, project files, generated artifacts, and other non-coverable edits from failing the patch gate. Changed-branch coverage uses Cobertura line-level `condition-coverage` counts on those same changed measurable lines, so ordinary changed statements without branch conditions do not inflate the branch denominator. When a diff has no measurable changed lines or no measurable changed branches, the corresponding patch metric reports `100%` and says so explicitly in Markdown. Empty external diff files or stdin are valid empty patches. Non-empty malformed external artifacts, such as HTML login pages or JSON API errors, fail before coverage is evaluated.

Reports are private local artifacts:

```json
{
  "passed": false,
  "coverage": "/repo/TestResults/coverage-merged/coverage.cobertura.xml",
  "thresholds": {
    "line": 95,
    "branch": 85,
    "patchLine": 95,
    "patchBranch": 85
  },
  "patchDiffSource": {
    "kind": "git-base",
    "label": "HEAD^1",
    "strictness": "local-git",
    "diffBase": "HEAD^1",
    "path": null,
    "bytes": 1024,
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    "empty": false
  },
  "line": {
    "covered": 80,
    "valid": 100,
    "percent": 80
  },
  "branch": {
    "covered": 30,
    "valid": 50,
    "percent": 60
  },
  "patchLine": {
    "diffBase": "HEAD^1",
    "changed": 28,
    "measurable": 20,
    "covered": 18,
    "percent": 90
  },
  "patchBranch": {
    "diffBase": "HEAD^1",
    "changed": 28,
    "measurable": 8,
    "covered": 7,
    "percent": 87.5
  }
}
```

Use `coverage gate` after `coverage run`, or after any other private coverage workflow that produces a local Cobertura file:

```yaml
- uses: actions/checkout@v5
  with:
    fetch-depth: 2
    persist-credentials: false
- uses: actions/setup-dotnet@v5
  with:
    dotnet-version: 10.0.x
- run: dotnet tool restore
- run: dotnet restore ./MyApp.slnx
- run: dotnet tool run appsurface coverage run --solution ./MyApp.slnx --configuration Release --no-restore
- run: dotnet tool run appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --min-line 95 --min-branch 85 --diff-base HEAD^1 --min-patch-line 95 --min-patch-branch 85
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: coverage
    path: |
      TestResults/coverage-merged/coverage.cobertura.xml
      TestResults/coverage-merged/summary.txt
      TestResults/coverage-merged/timings.json
      TestResults/coverage-merged/coverage-gate.json
      TestResults/coverage-merged/coverage-gate.md
      TestResults/coverage-merged/projects/**/dotnet-test.log
```

For repositories with an existing coverage producer, replace the `coverage run` step and the `--coverage` path with the command and Cobertura file path your test setup actually produces.

Use `--diff-base origin/main` for local development or simple CI jobs that already fetched the base ref. Use `--diff-file` when CI already produced a unified diff artifact, and use `--diff-stdin` when another command streams unified diff text:

```bash
git diff --unified=0 --no-ext-diff --relative origin/main...HEAD -- \
  | appsurface coverage gate --coverage ./TestResults/coverage-merged/coverage.cobertura.xml --diff-stdin --diff-label origin/main...HEAD --min-patch-line 95
```

For GitHub pull requests, prefer the default merge checkout with `fetch-depth: 2` and `--diff-base HEAD^1`. This keeps patch line numbers aligned with the tree that produced coverage while still avoiding full history. If `HEAD^1` cannot be resolved, the checkout is probably still at the default depth of `1`; add `fetch-depth: 2` to the checkout step. If your CI checks out the pull request head instead, generate or download a head-vs-base diff for that same tree and pass it with `--diff-file` or `--diff-stdin`.

Diagnostics use `ASCOV###` codes so CI logs are searchable:

| Code | Meaning | Fix |
| --- | --- | --- |
| `ASCOV001` | The Cobertura file is missing or `--coverage` is blank. | Produce coverage first or pass the correct file path. |
| `ASCOV006` | The Cobertura file is malformed or has unsupported/misleading metrics. | Regenerate coverage and verify counts/rates on the root `<coverage>` element. |
| `ASCOV007` | A threshold is outside the `0` through `100` range. | Correct `--min-line`, `--min-branch`, `--min-patch-line`, or `--min-patch-branch`. |
| `ASCOV008` | GitHub step summary could not be written. | Check `$GITHUB_STEP_SUMMARY` permissions or add `--no-github-summary`. |
| `ASCOV009` | The report output path is unsafe. | Use a dedicated artifact directory, not a filesystem root or working directory. |
| `ASCOV010` | The command could not run or read `git diff` for changed-line coverage. | Fetch the diff base or pass a valid local commit/ref to `--diff-base`. In GitHub pull request workflows using `--diff-base HEAD^1`, set `actions/checkout` to `fetch-depth: 2`; the default depth is `1` and does not include `HEAD^1`. |
| `ASCOV011` | A patch threshold was set without a patch diff source. | Pass exactly one of `--diff-base`, `--diff-file`, or `--diff-stdin`. |
| `ASCOV012` | Multiple patch diff sources were set. | Keep only one of `--diff-base`, `--diff-file`, or `--diff-stdin`. |
| `ASCOV013` | An external diff file/stdin artifact is missing, unreadable, or larger than 20 MiB. | Regenerate the unified diff artifact or pass a smaller diff. |
| `ASCOV014` | `--diff-stdin` was requested from an interactive terminal. | Pipe or redirect unified diff text, or use `--diff-file`. |
| `ASCOV015` | A non-empty external diff artifact is not unified diff text. | Download the diff with `Accept: application/vnd.github.diff`, fix the producer, or use `--diff-base`. |
| `ASCOV016` | Patch source metadata is invalid. | Correct `--diff-label` or `--repository-root`. |
| `ASCOV020` | The gate ran successfully and coverage is below threshold. | Raise coverage or lower the threshold intentionally in source control. |

### `appsurface export`

Export a general AppSurface or RazorWire application through the product-facing CLI.

```bash
appsurface export --mode hybrid \
  --public-origin https://www.example.com \
  --live-origin https://api.example.com \
  --project ./src/MyApp/MyApp.csproj
```

The command shares the RazorWire export engine and accepts the same source choices as `razorwire export`: exactly one of `--url`, `--project`, or `--dll`, plus `--framework`, `--app-args`, and `--no-build` for launched apps. `--public-origin` rewrites same-origin canonical metadata to the public static host; it does not change crawl routing or app links. `--mode hybrid` by itself preserves application-style URLs and can support same-origin backend passthrough for RazorWire endpoints, including lazy anti-forgery token refresh. Hybrid still fails missing browser-delivered static assets with RazorWire `RWEXPORT003` diagnostics; fix missing CSS, image, script, stylesheet, module preload, icon, font, and asset-shaped preload/prefetch references instead of using hybrid as an asset-ignore mode. Exporter-managed artifact fetches handle redirects inside the shared engine before response content is read or written: same-origin redirects are allowed only when they stay inside the configured export origin and app path, while invalid redirects, redirect loops, and cross-origin or cross-path artifact redirects fail with RazorWire `RWEXPORT008`. Adding `--live-origin` enables split-origin rewriting for RazorWire-managed live surfaces. `--hybrid-credentials auto` is the default and includes credentials for managed live calls when a live origin is configured; `omit` is an advanced escape hatch for anonymous split-origin live endpoints. See the RazorWire [Hybrid Hosting With Cloud Run](../../Web/ForgeTrust.RazorWire/Docs/hybrid-hosting.md) guide for the local split-origin proof, Cloud Run live-origin recipe, CORS setup, and first-interaction cold-start tradeoff.

Static website deployment extras follow the same export boundary as `razorwire export`: seeds are app routes, exporter-owned provider artifacts such as `_redirects` are generated by the exporter, and opaque files such as `CNAME` belong in the deployment publish root through `--publish-root-extras ./deploy/export-extras.yml`. The manifest is explicit single-file copy only and fails with RazorWire `RWEXPORT007` when an extra is malformed, symlinked, reserved, collides with generated output, or targets an existing file. See the RazorWire CLI [Static website deployment extras](../../Web/ForgeTrust.RazorWire.Cli/README.md#static-website-deployment-extras) guidance for the provider table, GitHub Pages `CNAME` flow, and migration examples.

### `appsurface docs`

Preview AppSurface Docs for a repository checkout.

```bash
appsurface docs --repo .
```

Options:

- `--repo`, `-r`: Repository root to preview. Defaults to the current directory.
- `--urls`, `-u`: Explicit host URL binding, such as `http://127.0.0.1:5189`.
- `--port`, `-p`: Localhost-only AppSurface Web port shortcut forwarded to the AppSurface Docs host.
- `--all-hosts`: Binds `--port` previews to localhost and the all-hosts wildcard. Use this only when LAN, container, or other non-loopback preview access is intentional.
- `--strict`: Enables `AppSurfaceDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs preview root.
- `--public-origin`: Public origin used for absolute canonical metadata, such as `https://docs.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, or fragment. Do not include the docs route path. When unset, canonical metadata remains app-relative and app routes do not change.
- `--environment`, `-e`: Host environment forwarded to the AppSurface Docs host. Defaults to `Development` so the AppSurface Web deterministic per-workspace localhost URL is used when no endpoint is configured.
- `--startup-timeout-seconds`: Seconds to wait for the web host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

`appsurface docs preview` is an alias for the same behavior, kept so the old deferred shape maps cleanly to the new AppSurface command family.

When no endpoint is configured, the command runs the host in `Development` from the selected repository root and chooses the same stable localhost port for that repository or worktree. The CLI keeps routine ASP.NET Core lifecycle logs quiet, prints the resolved docs URL after Kestrel is listening, and then attempts to open that page in the system browser. If browser launch fails, the preview keeps running and reports the URL to open manually. Pass `--port`, `--urls`, `--environment Production`, or endpoint settings such as `ASPNETCORE_URLS`, HTTP/HTTPS ports, or Kestrel endpoints when you intentionally want to bypass that local preview default. `--port 5189` binds `http://localhost:5189`; add `--all-hosts` only when you intentionally want the wildcard binding `http://localhost:5189;http://*:5189`, which can expose the preview host beyond the local machine.

Packaged .NET tools usually do not carry ASP.NET Core static web asset manifests. The AppSurface CLI disables static web asset manifest loading for the preview host and relies on AppSurface Docs and RazorWire embedded asset fallbacks instead, so a global or local tool install stays self-contained.

### `appsurface docs export`

Export AppSurface Docs for a repository checkout to static files.

```bash
appsurface docs export --repo . --output ./dist/docs --mode cdn --strict
```

Use `cdn` when the output folder will be uploaded to GitHub Pages, Netlify, S3, or a plain CDN. Use `hybrid` only when the exported pages remain behind app-aware routing or live RazorWire frames, forms, streams, or islands. Missing browser assets are never live-route escapes: a CSS reference such as `url('/img/map-image.png')`, an image path with the wrong casing, or a forgotten script file fails with `RWEXPORT003` until the asset is copied, corrected, externalized, or removed.

Options:

- `--repo`, `-r`: Repository root to harvest. Defaults to the current directory.
- `--output`, `-o`: Output directory for exported static docs. Defaults to `dist/docs`; the directory must be missing or empty before export starts. CI should pass this explicitly and create a fresh output location per run.
- `--mode`, `-m`: Export mode. `cdn` is the default and validates plus rewrites managed URLs for plain static hosts. Use `hybrid` only when the output still sits behind application-aware routing. Hybrid tolerates missing live/page routes but still validates browser-delivered static assets.
- `--redirects`: Redirect alias materialization strategy. `html` is the default for GitHub Pages and generic static hosts; it writes tiny alias HTML fallback files. Use `--mode cdn --redirects netlify` for Netlify-compatible CDN publishing; export writes one root `_redirects` file with exact `301!` rules and does not emit alias HTML files. Netlify export rejects self-redirects and conflicting same-source rules after provider path encoding. `--redirects netlify` is rejected with `--mode hybrid`.
- `--seeds`: Optional path to a seed-route file. This is long-only because `-r` means `--repo` in AppSurface CLI commands.
- `--strict`: Enables `AppSurfaceDocs:Harvest:FailOnFailure=true`, which fails startup when every configured harvester fails. This is separate from `--mode cdn`, which validates the emitted static artifact and preserves `RWEXPORT00x` diagnostics.
- `--route-root`: Route-family root for version and archive routes.
- `--docs-root`: Live docs root. When `--seeds` is omitted, export seeds `/` and this resolved docs root, `/docs` by default.
- `--public-origin`: Public origin used for absolute canonical metadata in exported pages, such as `https://docs.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, fragment, or userinfo. The export host still crawls loopback internally; this option keeps public canonical links from using that private listener. When unset, canonical metadata remains app-relative and app routes do not change.
- `--live-origin`: Optional live origin for split-origin hybrid docs export, such as `https://api.example.com`. Use an absolute `http://` or `https://` origin only, with no path, query, fragment, or userinfo.
- `--hybrid-credentials`: Credential behavior for RazorWire-managed live calls in split-origin hybrid export: `auto` (default), `include`, or `omit`. `auto` includes credentials when `--live-origin` is set.
- `--environment`, `-e`: Host environment forwarded to the AppSurface Docs host. Defaults to `Production` for export.
- `--startup-timeout-seconds`: Seconds to wait for the in-process AppSurface Docs host to start before failing fast. Defaults to `10`; use `0` to disable while investigating intentional pre-bind delays.

Export does not expose `--port`, `--urls`, or `--all-hosts`. It binds `http://127.0.0.1:0` internally, resolves the actual Kestrel listener, crawls that URL, then stops the host. Before crawling, it reads the AppSurface Docs route manifest from the in-process host, registers every public canonical docs route as an export seed, registers redirect aliases for source-shaped Markdown URLs and declared aliases, and writes `.appsurface-docs-route-manifest.json` into the export root. After all final files are materialized, export writes `.appsurface-docs-release-manifest.json`, hashes it, and prints a copy-ready `"releaseManifestSha256": "..."` catalog snippet. This keeps unlinked-but-public docs pages exportable, gives each alias a proven canonical target before the selected redirect strategy materializes it, lets exact version archives preserve the route identity that existed when the release was captured, and gives the runtime a catalog-pinned integrity proof for mounted archive HTML, JavaScript, CSS, SVG, and search payloads. Export fails with `ASDOCSARCHIVE005` when unsupported hidden files such as `.nojekyll` or `.well-known/...` are present, so export exact releases to a clean directory before copying the pin. RazorWire `RWEXPORT00x` diagnostics come from the shared export engine: for `RWEXPORT003`, add/copy the missing browser asset, correct path casing, make the URL external/data/hash-only when appropriate, or remove the reference; for `RWEXPORT008`, keep exporter-managed artifact redirects on the same scheme, host, port, and app path, or model the destination as an external reference instead of a static artifact; for `RWEXPORT009`, remove symlinks, junctions, reparse points, or lexical escapes from the generated output tree before retrying. Do not hand-author `_redirects` inside the export output; use `--redirects netlify` so the exporter can validate and own that provider file. Use the generic `razorwire export` command when exporting arbitrary RazorWire apps via `--url`, `--project`, or `--dll`; use `appsurface docs export` when AppSurface owns the AppSurface Docs repository host.

Generated docs export artifacts use the RazorWire [generated export artifact boundary](../../Web/ForgeTrust.RazorWire.Cli/README.md#generated-export-artifact-boundary). The guard covers `.appsurface-docs-route-manifest.json`, exported HTML/CSS/assets, docs partials, redirect alias HTML, `_redirects`, and `.appsurface-docs-release-manifest.json`. `RWEXPORT009` is a local filesystem boundary failure; `RWEXPORT008` is still reserved for unsafe HTTP redirects while fetching exporter-managed artifacts.

For split-origin AppSurface Docs publishing, keep `--public-origin` on the static docs origin and add `--live-origin` for RazorWire-managed live calls:

```bash
appsurface docs export \
  --repo . \
  --output ./dist/docs \
  --mode hybrid \
  --public-origin https://docs.example.com \
  --live-origin https://api.example.com
```

The RazorWire [Hybrid Hosting With Cloud Run](../../Web/ForgeTrust.RazorWire/Docs/hybrid-hosting.md) guide explains the local proof path, Cloud Run live-origin deployment, CORS credentials, lazy anti-forgery refresh, and first-interaction cold starts.

`appsurface docs export` intentionally does not expose `--publish-root-extras`. Exact AppSurface Docs archives are immutable release artifacts: `.appsurface-docs-route-manifest.json` and `.appsurface-docs-release-manifest.json` describe the files that belong to the archive, and deployment-owned files such as `CNAME`, `.nojekyll`, or `/.well-known/security.txt` must live in the surrounding publish root outside the exact release tree. Do not place opaque extras inside immutable exact release archives unless a future archive contract explicitly supports them. If a future docs export path wires extras by mistake, it should reject them before host startup with RazorWire `RWEXPORT007 [release-archive-incompatible]`. For raw provider files, do not copy `/_redirects` or `/_headers`; use `--redirects netlify` for Netlify redirects and wait for structured headers support instead of raw copy-through.

### `appsurface docs verify-archive`

Verify one exact release tree from a version catalog without starting the docs web host.

```bash
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3
appsurface docs verify-archive --catalog ./docs-versions.json --version 1.2.3 --trusted-release-root ./published-docs
```

Options:

- `--catalog`: Path to the AppSurface Docs version catalog JSON file.
- `--version`: Exact version identifier to verify.
- `--trusted-release-root`: Trusted release root used to resolve `exactTreePath` entries. When omitted, paths resolve the same way as runtime defaults: relative to the catalog directory.

The command loads the catalog, resolves the selected `exactTreePath`, and runs the same release archive verification used at runtime. Pass `--trusted-release-root` when the deployment sets `AppSurfaceDocs:Versioning:TrustedReleaseRootPath`; otherwise the local verifier may inspect a different relative tree than the host would mount. It exits nonzero when the version is missing, lacks a `releaseManifestSha256` pin, has a mismatched manifest digest, has missing or changed files, or contains handler-servable files not covered by the manifest. The catalog pin proves local archive integrity relative to trusted host configuration; it is not a signature or build provenance attestation.

Migration map for repo-owned AppSurface Docs export:

| Old path | New path |
| --- | --- |
| `razorwire export --project Web/ForgeTrust.AppSurface.Docs.Standalone/...` | `appsurface docs export --repo .` |
| `AppSurfaceDocs__Harvest__FailOnFailure=true` | `--strict` |
| `--mode cdn` | `--mode cdn` |
| `--seeds <file>` | `--seeds <file>` |
| `--output <dir>` | `--output <dir>` |
| `AppSurfaceDocs__Routing__PublicOrigin=https://docs.example.com` | `--public-origin https://docs.example.com` |
| `--project`, `--dll`, `--url`, `--app-args`, `--no-build`, `--framework` | use `appsurface export` for arbitrary app export |

## Development

Run the tool from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs --repo .
```

Use `--strict` for CI-like validation when an all-failed harvest should stop the preview before the host begins serving.

Run the export command from source while developing:

```bash
dotnet run --project Cli/ForgeTrust.AppSurface.Cli -- docs export --repo . --output ./dist/docs --mode cdn --strict
```
