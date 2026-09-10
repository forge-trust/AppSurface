# Issue #794 adoption and timing evidence

This artifact records only measurements actually executed for the
[Durable operational-assessment adoption guide](../operational-assessments.md). Targets from the approved design are
listed separately from observations so an unrun journey cannot be mistaken for evidence.

## Measurement boundaries

| Journey | Start | Stop | Target |
| --- | --- | --- | --- |
| Cold local proof | Invocation of `bash examples/durable-postgresql/run-local-proof.sh` in a clean prepared checkout | Final `[ok] local operational-assessment proof completed` checkpoint after container cleanup is armed | Five runs; p50 under five minutes; no run above seven minutes |
| Prepared existing-host adoption | Developer opens a restored schema-10 checkout and begins replacing handwritten readiness/admission logic | Exhaustive switch compiles and its focused tests pass | Five runs; median under two minutes; no run above three minutes |
| Migration-inclusive upgrade | Migration-owner credentials and canonical role recipe are available | Schema `9 -> 10`, role reconciliation, old/new smoke checks, and admission proof pass | Under five minutes |
| Findability | Unfamiliar developer opens the repository root | Developer locates the first-run command, direct-admission example, and distinct `ASDUR103` permission remedy | Under two minutes |

Run automated command timings with:

```bash
bash Durable/evidence/measure-issue-794-adoption.sh LABEL RUNS -- COMMAND [ARG ...]
```

The harness emits Markdown rows and the command output; it does not mutate this file. Copy only rows from completed
runs and record the exact runner profile. Human adoption and findability journeys must be timed by a participant and
must not be replaced with build duration. Each command run has a 420-second deadline; set
`APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS` to another positive whole-second bound when the documented journey
has a different target. A timed-out command and its process group are terminated and recorded as `timeout`.

## Runner profile

- Date: 2026-09-10.
- Host: macOS 26.6.2 (25G83), arm64.
- .NET SDK: 10.0.102.
- Docker Engine: 29.7.2.
- PostgreSQL image:
  `postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877`.
- Source base: `024e294f49a1b354411051680b51798d80512666` plus the uncommitted shared #794 working tree.
- Cache state: the Docker image and NuGet/source build caches were present. Each local-proof run used a new container
  and empty database. Each packed-consumer run used a new temporary package feed and consumer package cache.

## Observed measurements

These are automated command durations, not human adoption or findability timings:

| Journey | Invocation/run | Started (UTC) | Elapsed seconds | Result |
| --- | --- | --- | ---: | --- |
| `packed-consumer` | initial/1 | `2026-09-10T09:19:34Z` | 19 | fail: two invalid `ArgumentOutOfRangeException` overloads |
| `packed-consumer` | fixed/1 | `2026-09-10T09:20:17Z` | 18 | pass |
| `local-proof` | initial/1 | `2026-09-10T09:20:45Z` | 23 | fail: `-m:1` was forwarded by `dotnet run` to the CLI |
| `local-proof` | fixed/1 | `2026-09-10T09:22:22Z` | 15 | pass |
| `packed-consumer` | controlled-build/1 | `2026-09-10T09:24:11Z` | 20 | pass |
| `local-proof` | repeat/1 | `2026-09-10T09:28:27Z` | 10 | pass |
| `local-proof` | repeat/2 | `2026-09-10T09:28:37Z` | 11 | pass |
| `local-proof` | repeat/3 | `2026-09-10T09:28:48Z` | 11 | pass |
| `local-proof` | repeat/4 | `2026-09-10T09:28:59Z` | 10 | pass |
| `packed-consumer-final` | final/1 | `2026-09-10T09:35:59Z` | 15 | pass |

The passing packed-consumer run packed all six source packages into a disposable feed, restored fresh consumer
projects, and compiled/ran the adopter, Provider, and PostgreSQL consumers. Its PostgreSQL consumer covered all four
attempt kinds, asserted zero execution calls for every returned non-completed outcome, and verified one shared custom
pump singleton behind both public interfaces. The explicitly labeled contract fakes also propagated pre-call
cancellation without entering execution and preserved the same exception instance; these fake checks are API-consumer
evidence, not PostgreSQL execution evidence.

The passing local proof used a fresh PostgreSQL 16.5 database. It applied migrations `0001` through `0010`, reran the
canonical role recipe, initialized a runtime epoch, accepted Work/Flow/Schedule inputs, resolved the legacy and
admission APIs to one PostgreSQL singleton, returned `Completed` from the real admission call, observed `Healthy`,
exercised drain/resume and the hosted worker, and verified startup performed no DDL. It was a prepared-cache
engineering run. Five consecutive fresh-container/fresh-database passes measured 15, 10, 11, 11, and 10 seconds
(p50 11 seconds; maximum 15 seconds). They establish repeatability and are comfortably below the cold-run target, but
they do not replace five clean-checkout cold measurements because source, package, and image caches were present.

## Required evidence still unrun

- Five cold local-proof journeys.
- Five prepared existing-host adoption journeys.
- One migration-inclusive `9 -> 10` journey including role reconciliation and the actual
  `v0.2.0-preview.8` rollback smoke.
- One unfamiliar-developer findability journey.
