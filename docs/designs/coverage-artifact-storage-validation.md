# Coverage storage implementation validation

This record accompanies the [approved design](coverage-artifact-storage.md) and [consumer adoption guide](../../guides/coverage-storage.md). It distinguishes implemented behavior from release evidence that remains incomplete. All observations below are local to macOS/APFS unless stated otherwise.

## Status

Implementation and validation are in progress. No release-readiness claim is made. The repository's unchanged required gate is `./scripts/coverage-solution.sh`: aggregate line 95%, branch 85%; patch line 95%, branch 85%, Codecov line mode against the tracked base. No coverage threshold or instrumentation exclusion has been relaxed.

A full solution build passed with zero warnings and errors. An earlier full-solution attempt was cancelled after Docker's internal metadata filesystem became read-only; Docker subsequently recovered. The next attempt completed collection but failed three test projects: one browser navigation timeout, an installed-package source-hash guard while development edits overlapped packing, and three new fixture paths violating the existing containment policy. The unchanged navigation reproduction subsequently passed both cases outside the sandbox. The fixture paths now use the shared containment helper; all 15 path-policy tests passed. Final verification requires a frozen source commit and a fresh unchanged solution-gate run. Release-page visual snapshots were deliberately refreshed for changed release content, and both affected visual test methods subsequently passed.

An earlier diagnostic merged report measured 94.9901% line and 88.6246% branch coverage. Its standalone numeric gate had an empty Git patch because the feature was not yet committed; that is **not** evidence of passing patch coverage.

The frozen `a157d56d` full-solution attempt subsequently collected all 55 project reports and merged successfully, but the exact script exited 1 after a PostgreSQL integration test failed with authentication error `28P01`. A separate numeric diagnostic on that fresh full report measured 95.17% aggregate line and 89.02% branch coverage, with **84.52% patch line and 79.20% patch branch** against `origin/main`. The patch gate remains unsatisfied. These observations preserve the existing 95%/85% thresholds and 0.5 percentage-point tolerance; none is a waiver or release-readiness claim. Later storage fixes and tests require renewed final-source verification.

## Consumer evidence

The pinned downstream baseline is Skoolit revision `ceff56681873d9f1c715ec6b88b85e7febc6bad7`, with installed AppSurface `0.2.0-preview.14`. A read-only inventory found 23 reports with 313,041,446 logical XML bytes, including 143,998,050 bytes in eleven exact raw/canonical duplicate pairs. Existing material is current or legacy data; it is not claimed as eligible managed history. Its wrapper, settings, JUnit/log/timing readers and canonical merged-report readers were inspected. The complete unchanged Skoolit workflow has not yet been run against the new installed package.

A separate isolated consumer fixture ran the same collector command with the old installed CLI and the new source binary. Both ran two tests and reported one covered line and two covered branches (100% line and branch). Their existing 95%/85% gates passed.

| Mode | Coverage XML files | XML bytes | Coverage metrics |
| --- | ---: | ---: | --- |
| Old installed CLI | 4 | 4,864 | 100% line / 100% branch |
| New default managed | 2 | 2,432 | Identical |
| Repeated new managed | 2 | 2,432 | Identical |
| New full retention | 4 | 4,864 | Identical |
| New no-clean after full | 6 | 7,296 | Identical; prior duplicates preserved |

The small fixture demonstrates 50% less current XML; it does not establish a universal compression ratio or full downstream compatibility. A compatible stable-output repeat saved verified history. Interleaving the old writer invalidated a prior manifest and correctly produced a missed-history warning rather than claiming recovery.

The documented ordinary-test migration was exercised with actual VSTest 18.0.1. Inherited enabled settings produced a 1,318-byte coverage file; an explicit routine settings variant using `enabled="false"` produced no coverage file, with both tests passing. The original required-coverage settings were left intact. The initially proposed `isEnabled` spelling is rejected by VSTest and has been corrected in the observer, tests and guide. See the [VSTest collector reference](https://github.com/microsoft/vstest/blob/main/docs/analyze.md#enabledisable-a-datacollector).

## Resource evidence

Host: macOS 26.7.1, .NET runtime 10.0.2, SDK 10.0.102, Apple M4 Pro (14 logical CPUs), 48 GiB RAM, internal Apple SSD AP0512Z with FileVault, APFS, physical local machine. The original observation recorded about 29.1 GB free. Resource measurements must be repeated on an otherwise quiet final-source build; later validation encountered host disk pressure.

The streaming gzip prototype verified original-byte round trips for 186 mixed-compression trials plus 93 isolated Fastest trials. The full manager then exercised locking, registration, compression, integrity verification and maintenance with one cold-order and 30 warm trials per deterministic scaled fixture.

| Original size | Warm completed under 2 seconds | Warm p50 ms | Warm p95 ms | Warm maximum ms |
| --- | ---: | ---: | ---: | ---: |
| 35 MiB | 30/30 | 122.2 | 1,233.0 | 1,357.9 |
| 128 MiB | 29/30 | 350.5 | 448.3 | 2,236.1 |
| 256 MiB | 29/30 | 728.5 | 854.5 | 3,246.1 |

This **fails** the original all-warm-trials two-second acceptance bar at 128 and 256 MiB. On October 21, 2026, the user approved validating a five-second cumulative allowance with the existing 256 MiB automatic cap. The implementation and fixed-limit contract now use five seconds. A follow-up isolated manager run on candidate `55288ddb` completed all 93 trials and verified every original SHA-256. Warm maxima were 146.4 ms at 35 MiB, 349.7 ms at 128 MiB, and 564.6 ms at 256 MiB; all 90 warm trials and all three cold-order trials completed within five seconds. This is candidate-specific local evidence; later source changes require renewed verification. The process-memory API returned zero on this host, so peak working set remains unknown rather than being reported as zero memory use. The resource decision does not change code coverage quality.

Reproducible benchmark payloads were removed after preserving raw result records and fixture fingerprints, reclaiming approximately 7.7 GiB of task-owned scratch. Their deletion was development scratch maintenance, not evidence that the product reclaims arbitrary consumer data.

## Focused checks and outstanding proof

Focused checks passed for normalization compatibility, native process leases, settings/JSON safety, archive integrity, restore/no-overwrite, lifecycle, warning throttling and command behavior. The latest broader compatibility suite passed 1,191 tests with no skips; the combined storage suite passed 368 tests with no skips before the latest crash-recovery changes. The exact locked restore used by the native CI matrix also passed. Additional checks passed nine budget/nested-gate cases, 36 command/session boundary cases and 62 store/recovery/session cases. The repetition proof performs 100 registrations across ten outputs, verifies protected current/pinned archives and byte-exact restoration, and exercises bounded contention retries. Its mixed outcomes are fixture metadata, not a claim that 100 real test processes failed or were interrupted. Review corrected eligible-history accounting, archive age, standalone gate inventory integrity, partial-effect reporting and crash-remnant bounds. These are separate focused runs; they are not final-source solution verification.

Outstanding release evidence includes the final unchanged solution gate; installed new-package and packed semantic-consumer proof; full pinned downstream workflow and artifact readers; final resource-policy validation; and native Windows/NTFS and Linux/ext4 execution. The native CI matrix includes all three supported OS families and runs all coverage-artifact contract tests, but adding a lane is not a passing result. Unknown or unsupported capabilities must remain reported rather than silently accepted.
