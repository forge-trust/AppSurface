# Durable slice 4 reconstruction ledger

This ledger is the audit boundary for reconstructing the PostgreSQL Flow engine on current `main`. Commit
`456dbfa3` is evidence, not merge-ready history or an independent specification. The current
[`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md) and
[`ForgeTrust.AppSurface.Flow`](../Flow/README.md) contracts, the
[`Flow protocol v1`](flow-protocol-v1.md), and the
[`slice 4 reference workload`](slice4-reference-workload.md) are authoritative.

The semantic delta comprises the PostgreSQL Flow tables, flow instance store, client API, request fingerprints, reference workload, diagnostics, and verification test flags.

Every row first identifies the current requirement or risk, then records how the old artifact contributes. Allowed
dispositions are `retained`, `adapted`, `replaced-by-landed-contract`, `deferred-to-slice-N`, and
`removed-with-rationale`.

## Source and target facts

| Fact | Value |
| --- | --- |
| Audit source | `456dbfa3` (`feat(durable): add PostgreSQL Flow persistence`) |
| Audit source parent | `226346bc` |
| Reconstruction base | current `main` |
| Old semantic delta | 9 paths, about 8,300 added lines |
| Package | `ForgeTrust.AppSurface.Durable.PostgreSql` |
| Publication posture | source-only public preview; no hosted worker; machine-held from publish plans |

## Requirements-first inventory

Paths beginning with `PostgreSql/` or `PostgreSql.Tests/` are relative to `Durable/ForgeTrust.AppSurface.Durable.`.

| # | Current requirement or risk | Old path / symbol | Disposition | New path / symbol | Rationale and proof |
| ---: | --- | --- | --- | --- | --- |
| 1 | Flow tables versioned in schema migration 0003. | `PostgreSql/Migrations/0003_durable_flow_protocol.sql` | adapted | `PostgreSql/Migrations/0003_flow_protocol.sql` | Adds six focused Flow relations, constraints, indexes, and RLS policies. |
| 2 | Flow instance state transitions execute under atomic locks. | `PostgreSql/PostgreSqlDurableFlowStore.cs` | adapted | Internal Flow store | Implements state machine (`ready`, `evaluating`, `waiting_event`, `waiting_timer`, `waiting_activity`, `cancel_pending`, `completed`, `faulted`, `canceled`, `suspended`), canonical lock order, and aggregate revision CAS. |
| 3 | Non-ambient callers start and interact with Flows via client API. | `PostgreSql/PostgreSqlDurableFlowClient.cs` | adapted | Public Flow client | Implements `IDurableFlowClient` over `PostgreSqlDurableFlowStore` using caller transactions or short database transactions. |
| 4 | Request fingerprinting validates definition and payload identity. | `PostgreSql/DurableFlowRequestFingerprint.cs` | adapted | Internal fingerprint helper | Computes SHA-256 fingerprints for definitions, start requests, command requests, and external events. |
| 5 | Request fingerprinting tested against collision and divergence. | `PostgreSql.Tests/DurableFlowRequestFingerprintTests.cs` | adapted | Flow fingerprint unit tests | Verifies collision handling (`Duplicate`), mismatch rejections (`ASDUR206`, `ASDUR207`), and payload hashing. |
| 6 | Flow store integration verified against real PostgreSQL 17.5. | `PostgreSql.Tests/PostgreSqlDurableFlowStoreTests.cs` | adapted | Flow store integration tests | Tests recovery invariants, single-use event delivery, timer expiry with a forced post-commit child-process kill, child activity completion/cancellation, and RLS isolation. |
| 7 | Flow reference workload proves end-to-end execution. | `slice4-reference-workload.md` | adapted | Same path | Defines standard reference workload driving start, step evaluation, event wait, timer expiry, child activity, suspension, release, and scope disable. |
| 8 | Test harness supports `--quick --flow` and `--ci --flow` verification. | `verify-postgresql.sh` | adapted | Same script | Added `--flow` flag to `--quick` and `--ci` modes to target `DurableSlice4ReferenceWorkloadTests`. |
| 9 | Flow diagnostics mapped to standard `ASDURxxx` codes. | `troubleshooting/durable-diagnostics.md` | adapted | Diagnostics catalog | Maps `ASDUR200`-`ASDUR211` to concrete Flow engine state and failure conditions. |
| 10 | Security recipe grants least privilege for Flow tables. | `configure-postgresql-roles.sql` | adapted | Role configuration script | Updates permissions for runtime role on `flow_instance`, `flow_command`, `flow_history`, `flow_wait`, `flow_timer`, and `flow_dispatch`. |
| 11 | Flow and Work options share common store configuration. | `PostgreSqlDurableWorkOptions` | retained | Same options type | Shared `ExpectedStoreId`, `RuntimeEpoch`, `WakeNotificationMode`, and schema compatibility manager. |
| 12 | Schedule protocol deferred to slice 5. | `PostgreSql/Migrations/0004_schedule_protocol.sql` | deferred-to-slice-5 | Slice 5 migration | Standalone schedule persistence deferred to slice 5. |
| 13 | Hosted worker fleet protocol deferred to slice 6. | `PostgreSql/Migrations/0005_runtime_health.sql` | deferred-to-slice-6 | Slice 6 migration | Worker heartbeat and process lease protocol deferred to slice 6. |

## Completion rule

Every retained/adapted row needs a test, generated check, or documentation proof in the pull request; each deferred row
names its owning slice; and every replaced/removed artifact must be absent. A passing build alone does not close a row
whose risk requires real PostgreSQL or child-process crash proof.
