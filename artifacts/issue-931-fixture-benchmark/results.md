# Measured no-go for the Durable PostgreSQL fixture

The median combined Work and Flow time was **11.838 s fresh** and **10.872 s from templates**. Across five pairs, the median saving was 0.966 s and the median gain was **8.16%**. The median absolute deviation was 4.49 percentage points, giving a required gain above **8.98%**. The observed saving does not justify maintaining a second setup path for routine use in these cohorts.

| Pair | First arm | Fresh (s) | Template (s) | Saved (s) | Gain | Work gain | Flow gain |
|---:|---|---:|---:|---:|---:|---:|---:|
| 1 | Fresh | 11.838 | 10.872 | 0.966 | 8.16% | 1.96% | 11.47% |
| 2 | Template | 11.738 | 11.414 | 0.324 | 2.76% | -4.44% | 6.51% |
| 3 | Fresh | 11.360 | 10.195 | 1.165 | 10.26% | -3.83% | 17.22% |
| 4 | Template | 12.268 | 10.696 | 1.571 | 12.81% | 0.19% | 18.87% |
| 5 | Fresh | 12.108 | 11.664 | 0.444 | 3.67% | -0.19% | 5.62% |

Work slowed by at least 5% in 0/5 pairs and Flow in 0/5. The predeclared rule requires a median gain of at least 5%, a gain above twice the median absolute deviation, and fewer than three slowdowns in each cohort. The experiment does not meet that rule. Keep fresh setup as the default.

## Conditions and reproduction

Measured commit `b8d5dd5ebf2e6570d2b9643f22b4a08d16277f74` with SDK 10.0.402, Darwin 25.6.0 on arm64, 14 logical processors, PostgreSQL 17.12 and two xUnit workers. Databases used the default WAL_LOG create strategy on the dedicated server. Each arm ran Work then Flow in separate Release processes with TRX and XPlat coverage. Times include process launch through teardown and exit. One warmup per mode preceded the five pairs. The [fixture guide](../../Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/README.md) gives the commands and settings.

## Evidence

The first five pairs above are the decision sample. A subsequent change binds the independent acceptance TRX to the measured assembly and links it from the index. The separate verification run exercises that change. Its pairs are reported independently and do not replace or extend the original five.

The independent acceptance run passed 71 cases. The 24 timed processes recorded 408 passing Work/Flow results including warmups. Every Work process ran four cases, every Flow process ran 30, and the empty Flow case ran in both modes. Template preparation and teardown each occurred once per candidate process. All timed processes used the same Release build and settings. The server catalogs before and after the benchmark are included.

Download and extract the [redacted process evidence](process-evidence.tar.xz), then open `index.md` to inspect the per-arm and per-cohort commands, TRX, coverage, timings, exit codes and load readings. The archive also includes the acceptance TRX, before/after build hashes, catalog observations, and hashes of the original and redacted files. The [frozen workload inventory](inventory/manifest.json) records the test identities and the empty-case exception.
