# Durable slice 3 reconstruction ledger

This ledger is the audit boundary for reconstructing the PostgreSQL Work provider on current `main`. Commit
`226346bc` is evidence, not merge-ready history or an independent specification. The current
[`ForgeTrust.AppSurface.Durable`](ForgeTrust.AppSurface.Durable/README.md) and
[`ForgeTrust.AppSurface.Durable.Provider`](ForgeTrust.AppSurface.Durable.Provider/README.md) contracts, the
[`Work protocol v1`](work-protocol-v1.md), and the
[`slice 3 reference workload`](slice3-reference-workload.md) are authoritative.

The old semantic delta is exactly the 42 paths reported by:

```bash
git diff --name-status 01ce0084 226346bc
```

Every row first identifies the current requirement or risk, then records how the old artifact contributes. Allowed
dispositions are `retained`, `adapted`, `replaced-by-landed-contract`, `deferred-to-slice-N`, and
`removed-with-rationale`.

## Source and target facts

| Fact | Value |
| --- | --- |
| Audit source | `226346bc` (`feat(durable): add PostgreSQL schema and Work engine`) |
| Audit source parent | `01ce0084` |
| Reconstruction base | current `main`; record its exact SHA in the implementing pull request |
| Old semantic delta | 42 paths, about 8,300 added lines |
| New package | `ForgeTrust.AppSurface.Durable.PostgreSql` |
| Publication posture | source-only public preview; no hosted worker; machine-held from publish plans |

## Requirements-first inventory

Paths beginning with `PostgreSql/` or `PostgreSql.Tests/` are relative to `Durable/ForgeTrust.AppSurface.Durable.`.

| # | Current requirement or risk | Old path / symbol | Disposition | New path / symbol | Rationale and proof |
| ---: | --- | --- | --- | --- | --- |
| 1 | Pin one Npgsql and Testcontainers version. | `Directory.Packages.props` | adapted | Same path | Add approved dependencies and prove locked restore. |
| 2 | Provider identity is activity identity, not a second hash. | `PostgreSql.Tests/DurableProviderKeyTests.cs` | replaced-by-landed-contract | Provider adaptation tests | Prove `ProviderKey == ActivityId` across attempt, lease, generation, and epoch changes. |
| 3 | Schema lifecycle must fail closed. | `PostgreSql.Tests/DurableSchemaManagerTests.cs` | adapted | Schema-manager tests | Cover two migrations, StoreId, session lock, explicit epoch initialization, and CAS rotation. |
| 4 | Acceptance uses the landed canonical fingerprint. | `PostgreSql.Tests/DurableWorkRequestFingerprintTests.cs` | replaced-by-landed-contract | Acceptance/fingerprint integration tests | Cover command/idempotency-key collisions through the landed matcher. |
| 5 | Real PostgreSQL tests participate in solution coverage. | `PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj` | adapted | Same logical project | Reference both landed packages, Npgsql, and Testcontainers; retain locked restore. |
| 6 | Retry is deterministic and transaction-clock based. | `PostgreSql.Tests/PostgreSqlDurableRetryDelayCalculatorTests.cs` | adapted | Retry policy tests | Prove jitter-free `exponential-v1`, cap-first overflow handling, and unknown-algorithm rejection. |
| 7 | Work survives contention, cancellation, stale fences, and ambiguous effects. | `PostgreSql.Tests/PostgreSqlDurableWorkStoreTests.cs` | adapted | Work conformance tests | Add crash-process, recovery, lock-order, and scale proof. |
| 8 | CI uses real PostgreSQL 17.5 and fails closed. | `PostgreSql.Tests/PostgreSqlIntegrationTestDatabase.cs` | adapted | PostgreSQL fixture | Prefer the connection environment variable; otherwise use pinned Testcontainers; reject CI skip. |
| 9 | Tests need immutable registered Work and codecs. | `PostgreSql.Tests/PostgreSqlTestWorkContracts.cs` | adapted | PostgreSQL test contracts | Adapt to landed registries, prepared invocation, and provider adapter. |
| 10 | Test dependencies are reproducible. | `PostgreSql.Tests/packages.lock.json` | adapted | Regenerated lock file | Generate from the current graph; do not copy the obsolete lock. |
| 11 | Internal test seams are deliberate. | `PostgreSql/AssemblyInfo.cs` | adapted | Same logical metadata | Expose internals only to package tests; never use reflection or cross-package friend access. |
| 12 | Migrations are ordered and checksummed. | `PostgreSql/DurablePostgreSqlMigrationCatalog.cs` | retained | Migration catalog | Rebuild for exactly `0001` and `0002`; verify order and SHA-256. |
| 13 | Provider keys cannot diverge from activity identity. | `PostgreSql/DurableProviderKey.cs` | replaced-by-landed-contract | `DurableWorkerExecutionIdentity` | Delete the hash helper and independent persisted value. |
| 14 | Epoch rotation reports compare-and-swap truth. | `PostgreSql/DurableRuntimeEpochRotationResult.cs` | adapted | PostgreSQL schema API | Preserve expected/current/new epoch semantics; no implicit initialization. |
| 15 | Migration apply reports deployment evidence. | `PostgreSql/DurableRuntimeSchemaApplyResult.cs` | adapted | PostgreSQL schema API | Return immutable before/after and applied migration evidence. |
| 16 | Readers/writers classify missing, inconsistent, old, and new stores. | `PostgreSql/DurableRuntimeSchemaCompatibility.cs` | retained | PostgreSQL schema API | Prove every state from migration history/range metadata. |
| 17 | Schema failures need stable codes and safe context. | `PostgreSql/DurableRuntimeSchemaException.cs` | adapted | PostgreSQL schema API | Map `ASDUR400`-`ASDUR403`; retain inner exception/SQLSTATE and exclude secrets. |
| 18 | Deployment pins durable StoreId and explicit epoch. | `PostgreSql/DurableRuntimeSchemaStatus.cs` | adapted | PostgreSQL schema API | Add non-empty StoreId and nullable active epoch; writers never initialize or rotate. |
| 19 | Only one request fingerprint contract may exist. | `PostgreSql/DurableWorkRequestFingerprint.cs` | replaced-by-landed-contract | `DurableWorkRequest.Fingerprint` | Persist/compare the landed schema id/hash; fail closed for unknown schemas. |
| 20 | PostgreSQL depends toward adopter/provider contracts. | `PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj` | adapted | Same logical project | Use `PostgreSql -> Provider -> Durable` plus direct Durable use where required. |
| 21 | Schema/epoch changes are explicit deployment operations. | `PostgreSql/IDurableRuntimeSchemaManager.cs` | adapted | Same public interface | Add one-time initialization; retain script/status/apply/CAS rotate; no startup DDL. |
| 22 | Domain state and Work acceptance share one caller transaction. | `PostgreSql/IDurableWorkTransactionWriter.cs` | retained | Same public interface | Caller owns commit, rollback, disposal, connection, and transaction. |
| 23 | Slice 3 owns Work/shared schema only. | `PostgreSql/Migrations/0001_initial_work_protocol.sql` | adapted | `Migrations/0001_...sql` | Keep metadata/scope/dispatch/Work/history/permit/epoch; remove duplicate provider-key storage and obsolete retry fields. |
| 24 | Runtime roles need forced RLS and least privilege. | `PostgreSql/Migrations/0002_row_level_security.sql` | adapted | `Migrations/0002_...sql` plus role script | Revoke `PUBLIC`; separate migration owner, dispatcher, and scoped runtime. |
| 25 | Flow persistence belongs with Flow behavior. | `PostgreSql/Migrations/0003_durable_flow_protocol.sql` | deferred-to-slice-4 | Slice 4 migration | Do not version unused Flow tables; re-author with the Flow engine. |
| 26 | Schedule persistence belongs with schedule behavior. | `PostgreSql/Migrations/0004_schedule_protocol.sql` | deferred-to-slice-5 | Slice 5 migration | Do not version unused schedule/timer tables; re-author with execution. |
| 27 | Heartbeat/ownership belongs with hosting. | `PostgreSql/Migrations/0005_runtime_health.sql` | deferred-to-slice-6 | Slice 6 migration | Slice 3 starts no worker and has no fleet protocol. |
| 28 | Every mutation validates all applicable fences. | `PostgreSql/PostgreSqlDurableEpochFence.cs` | adapted | Internal fence validation | Validate StoreId, epoch, scope generation, revision, attempt, and lease. |
| 29 | Retry supports one known algorithm. | `PostgreSql/PostgreSqlDurableRetryDelayCalculator.cs` | adapted | Internal retry calculator | Use capped exponential v1, no jitter/Retry-After, and PostgreSQL clock. |
| 30 | Migration serialization spans final validation. | `PostgreSql/PostgreSqlDurableRuntimeSchemaManager.cs` | adapted | Public manager implementation | Hold one session advisory lock across validation, migration transactions, and final status. |
| 31 | Non-ambient callers need a short transaction path. | `PostgreSql/PostgreSqlDurableWorkClient.cs` | retained | Public client | Compose the writer, commit success, roll back failure; never start processing. |
| 32 | Work transitions are short, exact-fence operations. | `PostgreSql/PostgreSqlDurableWorkStore.cs` | adapted | Internal Work store | Follow the [protocol manifest](work-protocol-v1.md), landed claimed-work adapter, recovery matrix, tombstones, and lock order. |
| 33 | Ambient acceptance validates target/StoreId without a second connection. | `PostgreSql/PostgreSqlDurableWorkTransactionWriter.cs` | adapted | Public writer | Use the exact active transaction; never manage caller transaction lifetime. |
| 34 | Evaluators need truthful selection/deployment/safety guidance. | `PostgreSql/README.md` | adapted | PostgreSQL package README | Replace five-migration, implicit epoch, and provider-key claims; link canonical guides. |
| 35 | Runtime dependencies are reproducible. | `PostgreSql/packages.lock.json` | adapted | Regenerated lock file | Generate from the reconstructed graph; do not copy the old lock. |
| 36 | Adopters find the provider without believing it is hosted/published. | `Durable/ForgeTrust.AppSurface.Durable/README.md` | adapted | Same path | Link source preview and exact slices 4-6 release gate; retain passive registration. |
| 37 | The Durable hub describes the three-package direction. | `Durable/README.md` | adapted | Same path | Link this ledger, protocol, workload, and coordinated release gate. |
| 38 | New projects participate in build/coverage. | `ForgeTrust.AppSurface.slnx` | retained | Same path | Add runtime/test projects once; coverage must discover real PostgreSQL tests. |
| 39 | README policy recognizes package docs. | `Web/ForgeTrust.AppSurface.Docs.Tests/RepositoryReadmePolicyTests.cs` | adapted | Same suite | Extend the established policy seam, not a bypass. |
| 40 | Generated chooser exposes held source preview. | `packages/README.md` | adapted | Generated from package index | Never hand-edit; regenerate after source metadata is complete. |
| 41 | Package index is publication truth. | `packages/package-index.yml` | adapted | Same path | Add PostgreSQL and exact slices 4-6 gate for all three packages. |
| 42 | Generated readiness shows the same hold. | `packages/readiness.md` | adapted | Generated from package index | Never hand-edit; regenerate and verify. |

## Completion rule

Every retained/adapted row needs a test, generated check, or documentation proof in the pull request; each deferred row
names its owning slice; and every replaced/removed artifact must be absent. A passing build alone does not close a row
whose risk requires real PostgreSQL or child-process crash proof.
