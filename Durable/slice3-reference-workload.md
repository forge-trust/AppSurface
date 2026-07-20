# Durable slice 3 reference workload

This is the first-success and conformance workload for the source-only PostgreSQL Work provider. It proves atomic
acceptance, process-loss recovery, exact effect permission, stable provider identity, and safety-class handling.

> Slice 3 starts no worker, polling loop, scheduler, hosted service, or automatic migration. The test harness manually
> drives each operation. Production activation remains slice 6.

## Success target and prerequisites

- At most 5 minutes with PostgreSQL ready; at most 10 minutes cold with Docker.
- .NET 10 SDK and either Docker or a dedicated PostgreSQL 17.5 connection in
  `APPSURFACE_POSTGRES_TEST_CONNECTION`.
- One filtered test command against real PostgreSQL. A skipped or zero-test run is not success.

The Docker path uses the immutable multi-platform image
`postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302`.
Refreshing that digest is an explicit reviewed dependency change. The version check still requires
`server_version_num=170005` after startup.

Use a disposable database. The workload applies forward-only schema and supplies no destructive down migration.

## Run the proof

From the repository root:

```bash
dotnet restore ForgeTrust.AppSurface.slnx --locked-mode
dotnet test \
  Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj \
  --configuration Release \
  --no-restore \
  --filter FullyQualifiedName~DurableSlice3ReferenceWorkloadTests \
  --logger "console;verbosity=normal"
```

The fixture uses the connection environment variable when set; otherwise it starts the pinned Testcontainers image.
`APPSURFACE_POSTGRES_TEST_ALLOW_SKIP=true` is a local-only escape hatch and is rejected when `CI=true`.

To record classified Docker evidence, run cold before the pinned image exists locally, then warm after the cold run has
cached it:

```bash
./Durable/verify-postgresql.sh --quick \
  --evidence-mode cold \
  --evidence-output Durable/evidence/postgresql-slice3/cold
./Durable/verify-postgresql.sh --quick \
  --evidence-mode warm \
  --evidence-output Durable/evidence/postgresql-slice3/warm
```

The wrapper verifies the image-cache precondition instead of trusting the label, rejects external-database runs as
warm/cold evidence, enforces the 10-minute cold and 5-minute warm targets, and writes `run.json`. Each successful test
writes a separate privacy-safe JSON document with monotonic timing plus ordered application, database-transaction,
crash-recovery, provider-invocation, and operator-action checkpoints. Evidence never contains a connection string,
credential, SQL parameter, payload, actor-provided value, or provider response. Exact log wording is not API;
committed-state assertions are.

The checked-in [cold](evidence/postgresql-slice3/cold/run.json) and
[warm](evidence/postgresql-slice3/warm/run.json) manifests record the July 20, 2026 Apple arm64 proof. Both discovered
and passed six workload cases. The cold run, including the immutable image pull, completed in 32 seconds against the
600-second target; the warm run completed in 23 seconds against the 300-second target. Both manifests identify the
Linux arm64 image and Darwin arm64 host, share the same SHA-256 source fingerprint, and bind the exact six freshly
written scenario documents with a second SHA-256 fingerprint. Their scenario documents are the operation-level
evidence; the base commit is lineage, not a claim that the tested working tree was already committed.

## What the workload proves

1. A migration owner checks status, applies two migrations, reads StoreId, and explicitly initializes the epoch.
2. One caller-owned transaction changes a domain row and accepts registered Work. Rollback removes both; commit keeps both.
3. A parent launches a helper process and force-terminates it after committed acceptance, claim, and effect permit, but
   before provider I/O or a terminal fact. The parent reconnects to the same store and treats the post-permit window as
   unknown rather than inferring that no external effect occurred.
4. The helper drives acceptance, discovery, claim, and the first permit. After reconnecting, the parent prepares safely
   reclaimable Work before a new permit, invokes it only after that permit commits, and records terminal completion;
   provider invocation holds no database transaction or connection.
5. ActivityId/ProviderKey remains stable while attempt and lease generations advance.
6. `Idempotent` and `ProviderKeyed` safely reclaim expired permitted attempts. `ReconcileBeforeRetry` and
   `ManualResolution` suspend until evidence/authorized resolution.
7. Final truth is an immutable exact-fence terminal fact or required safety suspension. Unknown post-permit outcomes are
   never failed terminal.
8. An explicit scope-disable operator action commits its audit truth and blocks later acceptance for that scope.

## Application sequence

The compiled test is canonical because it tracks exact constructors. The required sequence is:

1. Construct `PostgreSqlDurableRuntimeSchemaManager` with a migration-owner data source.
2. Call `GetStatusAsync`, `ApplyAsync`, and the one-time `InitializeRuntimeEpochAsync`; capture non-empty StoreId and epoch.
3. Construct validated `PostgreSqlDurableWorkOptions` with `ExpectedStoreId`, `RuntimeEpoch`, and notifications disabled.
4. Construct `PostgreSqlDurableWorkTransactionWriter` with the runtime data source, landed Work registry, and options.
5. Call `EnqueueAsync` with the application's exact active `NpgsqlTransaction`, then let the application commit/rollback.

`PostgreSqlDurableWorkClient` takes the same data source, registry, and options for a provider-owned short transaction.
Neither construction starts a worker. Writers never initialize/rotate epochs or apply schema.

## Failure interpretation

- Preflight/domain outcomes preserve caller transaction usability as specified by the
  [Work protocol](work-protocol-v1.md#caller-owned-transaction-contract).
- PostgreSQL/connection failures may abort the transaction. Roll it back; never guess it remains usable.
- `ASDUR102` means acceptance identities conflict; `ASDUR107` is a disabled permanent tombstone; `ASDUR109` means
  historical execution material is unavailable.
- `ASDUR400`-`ASDUR403` require deployment correction, not runtime DDL.
- Ambiguous post-permit outcome is evidence to reconcile, not permission to retry.

See the [`diagnostics catalog`](../troubleshooting/durable-diagnostics.md),
[`Work protocol`](work-protocol-v1.md), and [`reconstruction ledger`](slice3-reconstruction.md).

## Slice 6 comparison gate

Slice 6 must run this workload through hosted activation with the same safety evidence and fewer manual steps. If that
path is not materially simpler or safer than established durable-work alternatives, pause for an explicit go/no-go
decision before publication or further scope expansion.
