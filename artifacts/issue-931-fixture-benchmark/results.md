# Measured no-go for the Durable PostgreSQL fixture

The [shared fixture benchmark](shared-fixture-results.md) records the subsequent Durable run using AppSurface database leases and PostgreSQL’s `FILE_COPY` clone strategy.

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

Measured commit `b8d5dd5ebf2e6570d2b9643f22b4a08d16277f74` with SDK 10.0.402, Darwin 25.6.0 on arm64, 14 logical processors, PostgreSQL 17.12 and two xUnit workers. Databases used the default WAL_LOG create strategy on the dedicated server. Each arm ran Work then Flow in separate Release processes with TRX and XPlat coverage. Times include process launch through teardown and exit. One warmup per mode preceded the five pairs. The [fixture guide](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/README.md) gives the commands and settings.

## Workload and timing records

The first five pairs above are the decision sample. A subsequent change binds the independent acceptance TRX to the measured assembly. The separate verification run exercises that change. Its pairs are reported independently and do not replace or extend the original five.

The independent acceptance run passed 71 cases. The 24 timed processes recorded 408 passing Work/Flow results including warmups. Every Work process ran four cases, every Flow process ran 30, and the empty Flow case ran in both modes. Template preparation and teardown each occurred once per candidate process. All timed processes used the same Release build and settings. The fixture catalog matched before and after the benchmark.

The [process and phase timings](timings.md) cover both warmups and all five pairs in each of the three samples. The [frozen workload inventory](https://github.com/forge-trust/AppSurface/blob/115f1cf0020c77ea848a1c2b96ddb7e10066a8f2/artifacts/issue-931-fixture-benchmark/inventory/manifest.json) records the test identities and the empty-case exception.

## Independent verification after the reporting change

Commit `ce2cc1f16c74e9342871bdf17600fa06077cddfc` also produced a **measured no-go**. The median saving was 0.578 s, or **4.90%**, with a median absolute deviation of 4.64 percentage points. Work slowed by at least 5% once and Flow never did. This run used the same server and protocol. Its own 71-case acceptance receipt matched the SHA-256 recorded by the measured Release assembly. All 24 processes passed and the server catalog returned to its initial state.

| Pair | Fresh (s) | Template (s) | Saved (s) | Gain | Work gain | Flow gain |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 11.862 | 11.832 | 0.031 | 0.26% | −6.84% | 3.71% |
| 2 | 11.300 | 10.165 | 1.135 | 10.04% | 5.55% | 12.48% |
| 3 | 11.090 | 10.793 | 0.297 | 2.68% | −0.10% | 4.13% |
| 4 | 12.580 | 11.233 | 1.347 | 10.71% | 3.99% | 14.50% |
| 5 | 11.797 | 11.220 | 0.578 | 4.90% | −1.64% | 8.29% |

## Final verification after the cleanup and acceptance repairs

Commit `756b2425` passed the rule in its own five pairs. It saved a median **0.970 s** with an **8.43%** median gain and **2.39 percentage points** of median absolute deviation. Neither cohort slowed by 5% in any pair. This run tested the change that retains a case lease after a failed clone drop. The runner required a passing result for every fixture case discovered in the measured Release assembly, including theory rows. All 73 acceptance cases and all 408 timed case executions passed.

| Pair | Fresh (s) | Template (s) | Saved (s) | Gain | Work gain | Flow gain |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 12.169 | 10.520 | 1.650 | 13.56% | 17.17% | 11.39% |
| 2 | 11.510 | 11.341 | 0.169 | 1.47% | −1.65% | 3.06% |
| 3 | 12.251 | 10.925 | 1.325 | 10.82% | 1.12% | 15.33% |
| 4 | 11.527 | 10.577 | 0.949 | 8.24% | −0.20% | 12.59% |
| 5 | 11.508 | 10.539 | 0.970 | 8.43% | 1.37% | 12.09% |

All 166 Release files had identical hashes before and after measurement. The dedicated server had zero fixture databases and zero fixture roles at both boundaries.

The original five pairs determine the experiment’s adoption recommendation. The two verification runs give different answers under the same rule and are reported separately. Fresh remains the default.
