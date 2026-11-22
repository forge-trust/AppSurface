# Skoolit Persistence fixture extraction

The adoption question is whether Skoolit can delete its PostgreSQL database lifecycle while retaining the speed and behavior of its existing optimized fixture. The control is [Skoolit main at 858cdf74](https://github.com/forge-trust/skoolit/tree/858cdf74bca123d36ce205fc6528a6fad9a566ba). The earlier [Durable Work and Flow experiment](https://github.com/forge-trust/AppSurface/issues/931) compared template clones with fresh databases in a different workload.

## Decision

**Adopt the AppSurface lifecycle for Skoolit Persistence, subject to the normal Linux release checks.** The final consumer removes 523 lines from its shared helper and 400 net lines of downstream C# including tests. The five-pair local trial met both predeclared speed criteria. The median changed from 415.6 to 411.6 seconds, 0.95% faster. Run-to-run variation was much larger than that difference. The reason to adopt is transferring lifecycle maintenance while preserving observed suite performance and database isolation.

## Measured wall time

Each row compares one run of the existing optimized Skoolit fixture with one run of the final AppSurface consumer. Times include the coverage runner and host teardown. Negative values mean the candidate completed sooner.

| Pair | First to run | Skoolit fixture (s) | AppSurface fixture (s) | Change |
| ---: | --- | ---: | ---: | ---: |
| 1 | Skoolit | 830.8 | 497.6 | −40.1% |
| 2 | AppSurface | 463.9 | 411.6 | −11.3% |
| 3 | Skoolit | 415.6 | 370.1 | −10.9% |
| 4 | AppSurface | 370.6 | 412.3 | +11.3% |
| 5 | Skoolit | 355.1 | 358.4 | +0.9% |

The candidate-to-baseline ratio of medians was 0.9905. Four of five paired ratios were at or below 1.05. Across runs, the median absolute deviations were 48.3 seconds for the baseline, 41.5 seconds for the candidate, and 0.1186 for the paired ratios. Every observation was retained. The uncounted warm-ups took 366.6 seconds and 540.2 seconds, respectively. The final series used a dedicated on-disk PostgreSQL instance after a host restart. Earlier attempts, including a disk-full interruption, contributed no samples.

Both warm-ups and all ten measured runs exited successfully with all 2,230 JUnit cases passing and identical case/outcome fingerprints (`8abec7dbef890c058cf0caf248c99df083b27169e3d53d87cc881f05cdadb4f3`). Two host start/completion pairs matched per run. Seven templates were registered and removed in each run. Independent catalog queries found zero residual databases, extra schemas or client sessions. The 39-role inventory and four memberships matched the baseline after initialization. The dedicated server shut down cleanly. The [child-process proof](#child-process-cleanup) ran separately with its fault-injection flags enabled.

## What moves

The [AppSurface package](../../PostgreSql/ForgeTrust.AppSurface.PostgreSql.Testing/README.md) owns database naming, physical cloning, template reuse, catalog and cluster advisory locks, and cleanup that can report partial failure and be retried. Each linked Skoolit test assembly owns one fixture instance. Skoolit supplies its connection configuration, EF model or migration setup and validation, per-case action, and the EvidenceHost start and completion receipts. A fresh-database entry point supports tests that deliberately exercise an empty database.

## Reproducing the trial dependency

The source commit is `1ad8236004a8317aff49768b29e64dbdff4b9efa`. From a checkout of that commit with the AppSurface GitHub origin, use the pinned .NET 10.0.402 SDK and a new output directory:

```sh
dotnet pack PostgreSql/ForgeTrust.AppSurface.PostgreSql.Testing -c Release \
  -p:Version=0.1.0-skoolit.4 -p:ContinuousIntegrationBuild=true \
  -p:DeterministicTimestamp=2026-11-22T07:30:42Z -o ./artifacts/fixture-feed
```

Two independent checkouts produced identical package bytes. The SHA-256 of `ForgeTrust.AppSurface.PostgreSql.Testing.0.1.0-skoolit.4.nupkg` is `46ba0fee50e2c8238abf0a40885a6478fe84c3adf18494791bdae7f69ccc43bf`. Supply the absolute feed directory as `RestoreAdditionalProjectSources` when restoring Skoolit. The package remains a local trial dependency.

## Performance criterion

The predeclared bound permits at most a 5% increase in median elapsed time for the complete Skoolit Persistence test project. At least four of five alternating pairs must also fall within that bound. Each arm gets an uncounted warm-up. The benchmark uses real PostgreSQL, Release binaries, the same case inventory and outcomes, and the same coverage collector. Each run must clean its owned templates and disposable databases.

The comparison uses the [Skoolit benchmark runbook](https://github.com/forge-trust/skoolit/blob/858cdf74bca123d36ce205fc6528a6fad9a566ba/docs/testing/issue1002-coverage-benchmark.md) and AppSurface CLI `0.2.0-preview.14`. After independent Release builds and tool restore in each checkout, run the collector against the full project. Set `SKOOLIT_REQUIRE_POSTGRES_TESTS=1` and point `SKOOLIT_POSTGRES_TEST_CONNECTION` at the dedicated PostgreSQL 17.12 instance. Use a fresh output directory and cleanup-receipt request for every run.

```bash
dotnet tool run appsurface -- coverage run \
  --test-project tests/Skoolit.Persistence.Tests/Skoolit.Persistence.Tests.csproj \
  --coverage-driver collector --configuration Release --no-restore --no-build \
  --test-argument "/p:RunSettingsFilePath=$PWD/coverage.runsettings" \
  --logger 'console;verbosity=normal' \
  --heartbeat-interval 30s --no-progress-timeout 180s --watchdog fail \
  --test-results junit --slow-test-diagnostics --output "$run_directory"
```

## Downstream code removed

The Skoolit consumer commit is `16f9b9522f3a41f83d578e4c40bdff68c832769c` on `codex/appsurface-postgresql-fixture`. The control is Skoolit main at `858cdf74`.

| Scope | Result |
| --- | ---: |
| Skoolit shared PostgreSQL helper | 923 → 400 lines, 523 fewer (56.7%) |
| All changed Skoolit C#, including regression tests | 458 added, 858 removed, 400 fewer |
| Entire Skoolit change, including package references, lock files and instructions | 646 added, 876 removed, 230 fewer |
| AppSurface fixture implementation, excluding tests and documentation | 464 C# lines |
| AppSurface test cases and test support | 1,480 C# lines |

The Skoolit test changes replace access to private implementation details with checks against a real PostgreSQL catalog. All five projects that compile the linked helper reference the trial dependency. The application-specific EF schema helper is unchanged. These are physical line counts from `wc -l` and `git diff --numstat`, including comments and blank lines. They measure how much code moves out of the application and the cost of retaining its regression coverage.

The initial candidate warm-up exposed an argument-name regression in the unsafe-prefix test. The corrected package reports the public parameter name and supports the same validation before Skoolit reads its database configuration. A subsequent warm-up passed all 2,230 cases but exited unsuccessfully because a synthetic receipt test replaced the real test host’s start identity. The final consumer gives that test an independent receipt writer. A focused run with live receipt requests verified two correctly paired host start/completion records. Both failed warm-ups are excluded from timing.

## Package verification

The [AppSurface test project](../../PostgreSql/ForgeTrust.AppSurface.PostgreSql.Testing.Tests/ForgeTrust.AppSurface.PostgreSql.Testing.Tests.csproj) ran 44 cases with zero failures or skips against its owned PostgreSQL 17.5 container. The suite covers independent seed data and DDL, simultaneous clones, owners with identical prefixes, cancellation after acquiring a catalog lock, retry after failed initialization and rollback, and real child-process exit cleanup. Scoped coverage of the fixture package was 271/283 lines (95.75%) and 50/56 branches (89.28% reported). Locked restore, formatting, and package-index verification passed.

## Child-process cleanup

On the final Skoolit revision, the selected Persistence process-proof test passed on macOS arm64 with PostgreSQL 17.12. The parent deliberately exercised success, an exception in the test, an xUnit timeout, missing completion receipts, and failure before host startup. The execution hosts published the expected evidence for each case, including the deliberately missing records. Across all five scenarios the before-and-after PostgreSQL catalogs matched byte for byte. Only `postgres`, `template0`, and `template1` remained. The parent command took 34.432 seconds outside the paired timing series. The [consumer's cleanup guide](https://github.com/forge-trust/skoolit/blob/858cdf74bca123d36ce205fc6528a6fad9a566ba/docs/testing/postgres-template-cleanup.md) specifies the Linux release-acceptance procedure.
