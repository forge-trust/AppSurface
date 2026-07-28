# Durable slice 4 reference workload

This is the first-success and conformance workload for the source-only PostgreSQL Flow engine (slice 4). It proves atomic
Flow instance creation, transition step evaluation, external event delivery, timer expiry, child Work activity integration,
recovery invariants across 11 protocol boundaries, options reuse, RLS scope isolation, and operator release/disable safety.
The timer-winner boundary is certified by force-terminating a separate process after the database commit; the other
Flow boundaries use transaction rollback and fresh processor instances. The existing slice-3 workload separately
certifies child Work process loss after an effect permit.

> Slice 4 starts no background worker, polling loop, scheduler, hosted service, or automatic migration. The test harness manually
> drives each step and operation. Production activation remains slice 6.

## Success target and prerequisites

- At most 5 minutes warm with PostgreSQL ready; at most 10 minutes cold with Docker.
- .NET 10 SDK and either Docker or a dedicated PostgreSQL 17.5 connection in `APPSURFACE_POSTGRES_TEST_CONNECTION`.
- Filtered test run executing `DurableSlice4ReferenceWorkloadTests`. A skipped or zero-test run is not success.

The Docker path uses the immutable multi-platform image
`postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302`.
Use a disposable database. The workload applies forward-only schema (migrations `0001_work_shared`, `0002_forced_rls`, `0003_flow_protocol`) and supplies no destructive down migration.

## Run the proof

From the repository root using .NET:

```bash
dotnet restore ForgeTrust.AppSurface.slnx --locked-mode
dotnet test \
  Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj \
  --configuration Release \
  --no-restore \
  --filter FullyQualifiedName~DurableSlice4ReferenceWorkloadTests \
  --logger "console;verbosity=normal"
```

From the repository root using the verification shell script with the `--flow` flag:

```bash
./Durable/verify-postgresql.sh --quick --flow
```

For strict CI verification across all PostgreSQL integration tests including Flow:

```bash
./Durable/verify-postgresql.sh --ci --flow
```

`APPSURFACE_POSTGRES_TEST_ALLOW_SKIP=true` is a local-only escape hatch and is rejected when `CI=true`.

## What the workload proves

1. **Schema Deployment**: Migration owner checks status, applies migrations `0001_work_shared.sql`, `0002_forced_rls.sql`, and `0003_flow_protocol.sql`, reads `StoreId`, and explicitly initializes the runtime epoch.
2. **Atomic Flow Start**: Flow instance starts atomically within a caller transaction or via `PostgreSqlDurableFlowClient`. Re-using `start_idempotency_key` with identical payload returns `Duplicate`; divergent definition or a new start identity targeting an existing Flow instance returns `ASDUR206`.
3. **Step Evaluation & Determinism**: Step evaluation advances Flow state machine (`ready` -> `evaluating`) and verifies definition fingerprint SHA-256 against registered code.
4. **Child Work Activity Lifecycle**: Flow enqueues child activity Work item in `appsurface_durable.work` and enters `waiting_activity`. Work engine claims item, acquires effect permit, executes provider I/O, and commits terminal fact. Completion handler resolves `flow_wait` (`activity_completed`) and returns Flow to `ready`.
5. **External Event Delivery**: Incoming event delivers atomically via `flow_command`, resolves active wait (`event_won`), supersedes scheduled timer (`timer_won` lost), and updates state to `ready`. Single-use `event_id` reuse returns `ASDUR204`.
6. **Timer Expiry**: Scheduled timer fires when `due_at <= clock_timestamp()`, resolves wait (`timer_won`), supersedes event wait, and updates state to `ready`.
7. **11 Recovery Boundaries** (boundary 9 is a forced child-process termination; the others are transactional/fresh-processor proofs):
   - Boundary 1 (Start pre-commit): Transaction rollback leaves no state; safe caller retry.
   - Boundary 2 (Start post-commit): `flow_instance` durable in `ready` state at revision 1.
   - Boundary 3 (Evaluation in-flight): Crash before child accept rolls back step transaction; state remains `ready`.
   - Boundary 4 (Child activity accepted): Work item durable in `dispatch`; Flow durable in `waiting_activity`.
   - Boundary 5 (Child activity permit acquired): Work effect permit committed; Flow remains in `waiting_activity`.
   - Boundary 6 (Child activity completed, Flow evaluation pending): Work completion committed (`activity_completed`); Flow state `ready` with result payload ready for next step evaluation.
   - Boundary 7 (Event delivery pre-commit): Rollback leaves `event_id` unconsumed and wait active.
   - Boundary 8 (Event delivered, Flow evaluation pending): Event command accepted (`event_won`), Flow state `ready` awaiting step evaluation.
   - Boundary 9 (Timer fired, Flow evaluation pending): Timer updated to `fired` (`timer_won`), Flow state `ready` awaiting step evaluation.
   - Boundary 10 (Flow suspended): Instance durable in `suspended` state with `suspended_from_state` preserved.
   - Boundary 11 (Flow terminal): Instance durable in `completed`, `faulted`, or `canceled` state; subsequent commands fail `already_terminal` / `ASDUR110`.
8. **Safety Suspension & Operator Release**: Non-restorable child activity failure or code mismatch causes safety suspension (`suspended`). Operator release command (`release`) validates authorized epoch, clears suspension, and restores state.
9. **Scope Disabling**: Disabling scope tombstones scope (`state = 'disabled'`) and suspends all non-terminal Flow instances in scope.

## Application sequence

The compiled reference workload requires:

1. Construct `PostgreSqlDurableRuntimeSchemaManager` with a migration-owner data source.
2. Call `GetStatusAsync`, `ApplyAsync` (applying migrations `0001`-`0003`), and `InitializeRuntimeEpochAsync`; capture StoreId and active epoch.
3. Construct `PostgreSqlDurableWorkOptions` with `RuntimeEpoch` and `ExpectedStoreId`.
4. Construct `PostgreSqlDurableFlowClient` with the scoped data source, Flow registry, payload codec registry, and shared PostgreSQL options.
5. Invoke `StartFlowAsync`, `DeliverEventAsync`, or `ExecuteStepAsync`.

## Failure interpretation

- Preflight/domain outcomes preserve caller transaction usability as specified by the [Flow protocol](flow-protocol-v1.md).
- `ASDUR200` means definition unavailable; `ASDUR201` means history/definition mismatch; `ASDUR203` means aggregate revision race lost; `ASDUR204` means duplicate event ID; `ASDUR206` means start conflict; `ASDUR207` means command or event identity conflict.
- `ASDUR400`-`ASDUR403` require deployment correction via schema manager, not runtime DDL.

See the [`diagnostics catalog`](../troubleshooting/durable-diagnostics.md),
[`Flow protocol`](flow-protocol-v1.md), and [`slice 4 reconstruction ledger`](slice4-reconstruction.md).

## Slice 6 comparison gate

Slice 6 must run this workload through hosted activation with identical safety evidence and fewer manual steps. If that path is not materially simpler or safer than established workflow alternatives, pause for an explicit go/no-go decision before publication or further scope expansion.
