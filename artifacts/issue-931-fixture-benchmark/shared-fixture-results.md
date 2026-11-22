# Durable PostgreSQL benchmark with shared database leases

Durable used the shared fixture for fresh databases and template clones. Each clone used PostgreSQL's `FILE_COPY` strategy. The two-worker Release suite ran on Darwin 25.6.0 arm64 (14 logical processors), .NET SDK 10.0.402 and PostgreSQL 17.12. The runner verified all 73 fixture acceptance cases and the same four Work and thirty Flow cases per arm. All 408 warmup and measured case executions passed. The post-run catalog matched the pre-run snapshot for all three databases, 16 roles and three role memberships, including their OIDs. The Release test assembly SHA-256 was `7fb2fd3f37b5e1fd4ec732caf5e523133f77d8c8a8f4cf812d1f95d42f01d88e`. [Measured source revision](https://github.com/forge-trust/AppSurface/commit/cffdc3b8a7178eb8448a51a4fac66a4f5b8298be)

**Keep fresh as the default.** Median combined Work and Flow time was 13.599 s fresh and 12.569 s with templates. Across the five pairs the median gain was 6.56%, against a 5% minimum and a variability threshold of 6.86% (twice the median absolute deviation of 3.43 percentage points). Work slowed by at least 5% in 2/5 pairs and Flow in 1/5. The runner recorded `NO_GO`. [Three earlier samples](results.md)

| Pair | First arm | Fresh (s) | Template (s) | Saved (s) | Gain | Work gain | Flow gain |
|---:|---|---:|---:|---:|---:|---:|---:|
| 1 | fresh | 13.452 | 12.569 | 0.883 | 6.56% | 5.26% | 7.31% |
| 2 | template | 13.599 | 16.345 | -2.746 | -20.19% | 15.30% | -41.53% |
| 3 | fresh | 19.397 | 13.116 | 6.280 | 32.38% | -15.46% | 46.86% |
| 4 | template | 12.852 | 12.088 | 0.763 | 5.94% | -14.68% | 16.21% |
| 5 | fresh | 13.936 | 12.543 | 1.393 | 10.00% | 0.86% | 14.91% |

The runner excluded an earlier warmup after PostgreSQL reported insufficient disk space in fresh Flow. The five measured pairs above and both warmup arms below come from a complete retry on the same server. The independent acceptance TRX SHA-256 was `0ad618bc048fab99767427844cabaeccb02915cb0531079d35b4b83ce2118a5d`. The frozen inventory SHA-256 was `a0744765a334e4787c33893e0df9728bd3f0542803ae9cfcf10eac624aac9634`. [Runner](https://github.com/forge-trust/AppSurface/blob/cffdc3b8a7178eb8448a51a4fac66a4f5b8298be/scripts/postgresql-fixture-benchmark.py) · [Test inventory](https://github.com/forge-trust/AppSurface/blob/cffdc3b8a7178eb8448a51a4fac66a4f5b8298be/artifacts/issue-931-fixture-benchmark/inventory/manifest.json)

## Process and fixture timings

The phase columns record operation count and total milliseconds. Process time includes launch, setup, tests, coverage and teardown.

| Window | Arm | Cohort | Process (s) | Template prepare | Database setup | Database teardown | Template teardown |
|---|---|---|---:|---:|---:|---:|---:|
| warmup | fresh | work | 4.390348084 | 0 / 0.000 | 4 / 587.029 | 4 / 84.610 | 0 / 0.000 |
| warmup | fresh | flow | 9.135321709 | 0 / 0.000 | 30 / 3689.754 | 30 / 566.426 | 0 / 0.000 |
| warmup | template | work | 5.040996625 | 1 / 249.886 | 4 / 304.234 | 4 / 69.210 | 1 / 22.127 |
| warmup | template | flow | 7.998626833 | 1 / 248.749 | 30 / 2012.614 | 30 / 493.666 | 1 / 21.343 |
| pair-01 | fresh | work | 4.901960459 | 0 / 0.000 | 4 / 583.098 | 4 / 85.024 | 0 / 0.000 |
| pair-01 | fresh | flow | 8.549745375 | 0 / 0.000 | 30 / 3537.003 | 30 / 549.703 | 0 / 0.000 |
| pair-01 | template | work | 4.644084583 | 1 / 329.961 | 4 / 341.059 | 4 / 77.819 | 1 / 23.880 |
| pair-01 | template | flow | 7.924792333 | 1 / 253.429 | 30 / 2119.009 | 30 / 475.678 | 1 / 21.243 |
| pair-02 | template | work | 4.324017167 | 1 / 254.160 | 4 / 304.295 | 4 / 64.236 | 1 / 20.077 |
| pair-02 | template | flow | 12.021100875 | 1 / 253.644 | 30 / 5101.732 | 30 / 1271.292 | 1 / 105.107 |
| pair-02 | fresh | work | 5.105381708 | 0 / 0.000 | 4 / 654.277 | 4 / 84.722 | 0 / 0.000 |
| pair-02 | fresh | flow | 8.493803584 | 0 / 0.000 | 30 / 3515.173 | 30 / 555.291 | 0 / 0.000 |
| pair-03 | fresh | work | 4.507202583 | 0 / 0.000 | 4 / 543.770 | 4 / 76.300 | 0 / 0.000 |
| pair-03 | fresh | flow | 14.889475083 | 0 / 0.000 | 30 / 7305.741 | 30 / 1498.029 | 0 / 0.000 |
| pair-03 | template | work | 5.204216625 | 1 / 255.487 | 4 / 331.633 | 4 / 72.104 | 1 / 21.197 |
| pair-03 | template | flow | 7.912254750 | 1 / 260.933 | 30 / 2090.386 | 30 / 524.608 | 1 / 22.571 |
| pair-04 | template | work | 4.900503583 | 1 / 262.442 | 4 / 310.677 | 4 / 67.683 | 1 / 19.999 |
| pair-04 | template | flow | 7.187807250 | 1 / 250.029 | 30 / 2063.859 | 30 / 530.506 | 1 / 20.415 |
| pair-04 | fresh | work | 4.273308709 | 0 / 0.000 | 4 / 596.382 | 4 / 86.372 | 0 / 0.000 |
| pair-04 | fresh | flow | 8.578425917 | 0 / 0.000 | 30 / 3489.515 | 30 / 587.572 | 0 / 0.000 |
| pair-05 | fresh | work | 4.873630875 | 0 / 0.000 | 4 / 590.181 | 4 / 82.006 | 0 / 0.000 |
| pair-05 | fresh | flow | 9.062046250 | 0 / 0.000 | 30 / 3480.512 | 30 / 588.394 | 0 / 0.000 |
| pair-05 | template | work | 4.831526334 | 1 / 252.943 | 4 / 337.318 | 4 / 68.264 | 1 / 21.240 |
| pair-05 | template | flow | 7.711246416 | 1 / 254.903 | 30 / 2009.970 | 30 / 535.246 | 1 / 22.371 |

## Load at each arm boundary

| Window | Arm | UTC start | UTC end | Start load (1m) | End load (1m) |
|---|---|---|---|---:|---:|
| warmup | fresh | 2026-11-22T20:00:41+00:00 | 2026-11-22T20:00:55+00:00 | 18.44 | 17.33 |
| warmup | template | 2026-11-22T20:00:55+00:00 | 2026-11-22T20:01:08+00:00 | 17.33 | 16.23 |
| pair-01 | fresh | 2026-11-22T20:01:08+00:00 | 2026-11-22T20:01:21+00:00 | 16.23 | 14.43 |
| pair-01 | template | 2026-11-22T20:01:21+00:00 | 2026-11-22T20:01:34+00:00 | 14.43 | 14.30 |
| pair-02 | template | 2026-11-22T20:01:34+00:00 | 2026-11-22T20:01:51+00:00 | 14.30 | 15.45 |
| pair-02 | fresh | 2026-11-22T20:01:51+00:00 | 2026-11-22T20:02:04+00:00 | 15.45 | 13.79 |
| pair-03 | fresh | 2026-11-22T20:02:04+00:00 | 2026-11-22T20:02:24+00:00 | 13.79 | 14.79 |
| pair-03 | template | 2026-11-22T20:02:24+00:00 | 2026-11-22T20:02:37+00:00 | 14.79 | 13.67 |
| pair-04 | template | 2026-11-22T20:02:37+00:00 | 2026-11-22T20:02:49+00:00 | 13.67 | 12.27 |
| pair-04 | fresh | 2026-11-22T20:02:49+00:00 | 2026-11-22T20:03:02+00:00 | 12.27 | 11.08 |
| pair-05 | fresh | 2026-11-22T20:03:02+00:00 | 2026-11-22T20:03:16+00:00 | 11.08 | 10.02 |
| pair-05 | template | 2026-11-22T20:03:16+00:00 | 2026-11-22T20:03:29+00:00 | 10.02 | 8.99 |
