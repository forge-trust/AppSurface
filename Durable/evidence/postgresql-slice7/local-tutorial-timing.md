# Slice 7 local tutorial timing evidence

Measured on 2026-08-07 from the `codex/durable-slice-7-adoption` working tree after a clean source build. The checked-in [`warm/run.json`](warm/run.json) records the immutable head and merge-base revisions alongside the working-tree source fingerprint used for the measurement.

| Checkpoint | Elapsed | Result |
| --- | ---: | --- |
| Offline first evidence: `appsurface durable schema script --from-version 0 --output <temporary-file>` | 4 seconds | Generated deterministic SQL without a database connection or environment-variable value. |
| Full local proof: PostgreSQL readiness, guarded apply, canonical role recipe, one-time epoch bootstrap, and `verify-local` | 14 seconds | Passed all named Work, Flow trace-context, Schedule, bounded host, health, drain, completed hosted-worker pass, and no-DDL catalog checkpoints. |
| Source-bound warm PostgreSQL reference workload | 33 seconds | Passed 6 Work recovery scenarios plus forward migration rollback/retry; see [`warm/run.json`](warm/run.json). |

The final proof ran the published prerequisite path with only .NET 10 and Docker installed. It used PostgreSQL 17's
container-bundled `pg_isready` and `psql`, exactly as the transcript now specifies, against a loopback-only disposable
database. The warm evidence binds the complete Slice 7 adoption manifest to its seven named scenarios.
