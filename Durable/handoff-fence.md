# PostgreSQL application handoff fence

`IDurableWorkHandoffFence` lets a running Durable Work executor make one current database decision from two kinds of authority: Durable's live invocation and permit, plus the application's current authorization and conditional handoff record. The application owns its authority and ledger; Durable continues to own the Work claim and execution lifecycle. A prepared handoff is provisional until the caller commits.

Use this boundary when an already-permitted Work invocation must revalidate domain authority immediately before a possible provider effect. Use [transactional enqueue](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#accept-work) to accept new Work. An application outbox can persist business intent and relay state, but does not by itself validate the live Durable invocation described here.

## Eligibility and registration

Register the PostgreSQL provider through [`AddAppSurfaceDurablePostgreSql`](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#composition-requirement) and inject `IDurableWorkHandoffFence` into the Work executor. The fence is scoped. The pump binds the same scoped instance only around the actual executor invocation, after permit commit and any existing invocation admission. Constructor or executor preparation runs unbound. A retained service loses authority when its invocation exits; an instance borrowed from another invocation cannot authorize the current one. There is no public binder, claim/permit input, or caller-selected scope.

The caller supplies an active `NpgsqlTransaction` on an open connection to the configured physical PostgreSQL target. Its isolation level must already be `ReadCommitted`. The fence does not begin, commit, roll back, dispose, or change the isolation of caller resources.

## Public API

```csharp
namespace ForgeTrust.AppSurface.Durable.PostgreSql;

public interface IDurableWorkHandoffFence
{
    ValueTask<DurableOperationResult<DurableWorkHandoffResult>> ExecuteAsync(
        NpgsqlTransaction transaction,
        Func<NpgsqlTransaction, CancellationToken, ValueTask<bool>> authorizeAndRecordHandoff,
        CancellationToken cancellationToken = default);
}

public sealed record DurableWorkHandoffResult(bool HandoffPrepared);
```

`ExecuteAsync` calls the delegate at most once for that call, passing the exact caller transaction and caller token. The delegate returns `true` only after it has revalidated application authority and made its conditional ledger write in that transaction. Sequential calls within one invocation are supported; the application ledger's conditional write must prevent duplicate logical handoffs. Overlapping or reentrant calls on one scoped instance throw `InvalidOperationException` before a second callback starts. The framework does not queue or retry callbacks.

| Outcome | Meaning and required caller action |
| --- | --- |
| `IsSuccess` and `Value.HandoffPrepared == true` | Application preparation and final native validation succeeded. The write is still uncommitted. Check cancellation, commit promptly, dispose the transaction and release the connection before provider I/O. |
| `IsSuccess` and `Value.HandoffPrepared == false` | The application callback denied the handoff. Its writes were rolled back to the callback savepoint; unrelated caller writes made before the fence remain subject to the caller's outer commit or rollback. Do not send. |
| Failed `DurableOperationResult` | A bounded native refusal. The callback did not run, or callback writes were rolled back if refusal occurred during final validation. Follow the problem code and existing recovery policy; do not send. |
| Exception | Argument/resource errors, observed caller cancellation, callback errors, or database/transport failures propagate after feasible cleanup. A cleanup failure throws with both causes and makes outer rollback/disposal mandatory. |

The result is not a provider-delivery receipt, a durable application commit, or permission transferable to another invocation. Existing [provider-effect safety and reconciliation rules](work-protocol-v1.md#provider-effect-safety) still decide how to handle provider acceptance, rejection, or ambiguity.

## Transaction and lock sequence

The caller begins and owns the outer transaction. The fence establishes an operation savepoint, captures the effective transaction-local Durable scope, acquires the shared migration/epoch fence, validates schema, StoreId and active epoch, binds the native scope, then locks and validates the active scope, exact Work, its dispatch row, and the exact permit. The callback savepoint is established only after those native locks are held.

The lock order is:

```text
shared migration/epoch fence
  -> scope
  -> Work
  -> dispatch
  -> exact effect permit
  -> application authority and ledger rows (inside the callback)
```

After the callback, the fence restores the caller's prior effective Durable scope, samples authoritative database time after lock waits and callback work, and performs its final invocation-lifetime, cancellation, lease, and deadline checks before releasing the operation savepoint. Releasing savepoints leaves their successful effects and acquired locks in the caller's outer transaction. Therefore native locks remain held after `ExecuteAsync` returns until the caller commits, rolls back, or otherwise ends that transaction.

Pre-callback native refusal rolls back the operation savepoint; locks acquired by that operation may be released, while writes the caller made before the savepoint remain outside it. Callback `false` rolls back the callback savepoint, restores the prior scope, performs the final check, and returns a successful false result only if the final check succeeds. A post-callback native/time refusal removes callback writes while retaining the native locks until outer completion. If the callback throws while the caller token remains active, cleanup rolls back the callback savepoint, restores the prior scope, releases the operation savepoint, and propagates the original exception; native locks remain until outer completion. If caller cancellation is observed instead, cleanup rolls back the operation savepoint, so those operation-acquired locks may be released. The fence never completes the outer transaction.

Caller-token cancellation observed before the final success check throws `OperationCanceledException` after cleanup and removes operation writes, including callback writes if its savepoint has already been released. The last decision point is after callback handling and scope restoration, with database time sampled before the invocation/token checks and operation-savepoint release. A cancellation arriving after that point does not retroactively reverse the decision. Check the token before commit; if commit fails or its outcome is uncertain, do not send until the application ledger and existing provider-safety rules resolve the outcome.

The implementation uses a fresh internal five-second cleanup token for savepoint rollback/release and scope restoration. It is not a public option and does not change the host's PostgreSQL statement or transaction policy. Npgsql is pinned by the repository to **10.0.3** in [`Directory.Packages.props`](https://github.com/forge-trust/AppSurface/blob/main/Directory.Packages.props). In that version, `NpgsqlTransaction.SaveAsync` completes synchronously without a database round trip; it queues the `SAVEPOINT` statement with the next command on that same transaction. Therefore, a completed `SaveAsync` alone is not server acknowledgement. The two-savepoint proof must exercise the queued savepoints through subsequent commands on the exact supplied transaction and verify rollback/release visibility and lock behavior. See the [Npgsql transaction API](https://www.npgsql.org/doc/api/Npgsql.NpgsqlTransaction.html). If cleanup fails, the fence throws rather than returning success or a clean refusal; roll back and dispose the outer transaction and connection.

## Native facts and refusal precedence

The validator stops at the first applicable check and does not inspect later rows merely to select a different description. For simultaneously invalid facts, the order is:

| Order | Fact | Observable outcome |
| ---: | --- | --- |
| 1 | Null transaction or callback | `ArgumentNullException`; callback is not run. |
| 2 | Missing, inactive, mismatched, or exited invocation binding | Failed result with `ClaimLost` (`ASDUR104`); callback is not run. |
| 3 | Overlap/reentry, inactive transaction/connection, wrong physical target, or isolation other than `ReadCommitted` | `InvalidOperationException`; callback is not run and caller ownership/isolation is unchanged. |
| 4 | Store schema, StoreId, or active epoch check | Existing safe `DurableRuntimeSchemaException` or coded `InvalidOperationException` (`StoreIdentityMismatch` / `RecoveryEpochRequired`) conventions. |
| 5 | Scope absent, disabled, or generation changed | `ScopeNotFound` (`ASDUR113`), `ScopeDisabled` (`ASDUR107`), or `ScopeGenerationConflict` (`ASDUR114`). |
| 6 | Work absent | `WorkNotFound` (`ASDUR111`). |
| 7 | Activity, attempt, lease generation/owner, scope generation, or epoch identity mismatch | `ClaimLost` (`ASDUR104`). |
| 8 | Work is terminal | `AlreadyTerminal` (`ASDUR110`). |
| 9 | Native cancellation is requested | `ClaimLost` (`ASDUR104`). Native cancellation is distinct from the caller's `CancellationToken`. |
| 10 | Lease is absent or expired | `LeaseLost` (`ASDUR105`). |
| 11 | An opted-in execution deadline is reached | `ExecutionDeadlineReached` (`ASDUR121`). Lease expiry precedes deadline expiry. |
| 12 | Dispatch state/identity/revision, granted permit identity/status, or opted-in admission facts are inconsistent | `ClaimLost` (`ASDUR104`). |

Caller-token cancellation is an exception, not a native refusal. A callback-thrown `OperationCanceledException` propagates even if the caller token is not canceled. PostgreSQL permission, serialization, deadlock, lock-timeout, statement-timeout, and transport errors propagate after feasible cleanup; they are not rewritten as Durable refusals. Safe native codes and user-facing guidance do not include raw server text, application payloads, credentials, recipients, or provider responses. The canonical [Durable diagnostics catalog](../troubleshooting/durable-diagnostics.md) supplies shared Problem/Cause/Fix/Docs wording.

### Refusal and exception cause-to-action checklist

Use the first applicable refusal in the precedence table. A failed `DurableOperationResult` is a stop signal for provider I/O, not an invitation to bypass the fence or retry the callback. The canonical diagnostics catalog above provides shared Problem/Cause/Fix/Docs wording; this table applies it to the handoff boundary.

| Result or exception | Cause at this boundary | Caller action |
| --- | --- | --- |
| `ClaimLost` (`ASDUR104`) | Invocation is absent, inactive, borrowed/mismatched, or exited; native activity/attempt/owner/generation/epoch identity is stale; native cancellation is set; or dispatch, exact permit, or admission facts disagree. | Do not send or reuse the binding. If the call is outside the actual executor invocation, move and await it inside that invocation. Otherwise reload current Work under the authorized scope and let current native state decide the next attempt; never supply claim or permit identities to repair the mismatch. |
| `LeaseLost` (`ASDUR105`) | The current lease is absent or expired, including after a native lock wait or callback. | Stop this attempt and provider I/O. Let the existing reclaim/retry lifecycle establish fresh authority; do not reuse cached lease facts. |
| `ScopeDisabled` (`ASDUR107`) | The scope is a disabled tombstone. | Treat it as permanent; do not recreate or reactivate it to make the handoff pass. |
| `AlreadyTerminal` (`ASDUR110`) | The Work is already terminal. | Return the authoritative terminal outcome; never invoke the provider again for this Work. |
| `WorkNotFound` (`ASDUR111`) | The active invocation does not resolve to the expected Work row. | Verify the trusted scope and opaque Work identity through authorized read surfaces; do not create a row or widen access implicitly. |
| `ScopeNotFound` (`ASDUR113`) | The trusted scope row is absent. | Verify the trusted scope identity; do not create scope state from the callback or runtime path. |
| `ScopeGenerationConflict` (`ASDUR114`) | The scope lifecycle generation advanced. | Reload current scoped truth and use only a newly authorized invocation; do not reuse an old generation. |
| `ExecutionDeadlineReached` (`ASDUR121`) | An opted-in Work item's absolute execution deadline has been reached. | Treat the attempt as timing-closed and stop provider I/O; follow the [execution-policy recovery rules](execution-policies-v1.md), without extending the deadline or inferring provider outcome. |
| Store identity mismatch / `StoreIdentityMismatch` | The caller transaction is connected to a different physical target or configured StoreId. | Use the data source and StoreId validated for this runtime. Do not select a target from callback input or initialize/rotate the store to silence a mismatch. |
| `RecoveryEpochRequired` | The configured runtime epoch differs from the active store epoch. | Compare host configuration with the active epoch. Correct an unintended mismatch; initialize or rotate an epoch only through reviewed recovery before enabling workers. |
| `DurableRuntimeSchemaException` | Required schema is missing, pending, unsupported, or inconsistent. | Keep runtime work stopped; apply reviewed forward-only migrations with migration-owner credentials, then deploy compatible package code. Runtime registration does not apply DDL. |
| `InvalidOperationException` before callback | The transaction/connection is inactive, targets the wrong physical store, uses unsupported isolation, or overlaps/reenters another call on the same scoped instance. | Pass one active transaction on its configured open connection at `ReadCommitted`, and serialize calls on the scoped service. The fence does not take ownership, change isolation, or queue calls. |
| PostgreSQL permission/RLS error | The runtime role lacks a required native/application grant or a row-level policy rejects access. | Review the exact runtime grants and both applicable RLS policies using the [reviewed PostgreSQL role recipe](https://github.com/forge-trust/AppSurface/blob/main/Durable/configure-postgresql-roles.sql). Repair narrowly; never switch to an owner role, disable RLS, or widen grants to force success. |
| PostgreSQL lock/statement timeout, deadlock, or serialization error | A database wait or transaction conflict exceeded a host budget or lost a race. | The original database exception propagates. Check effective host timeout values and the blocking writer/lock order, then roll back the caller transaction. Retry the whole operation only if application policy permits and a fresh active invocation/transaction is available; the fence never retries the callback. |
| Npgsql transport/connection failure | Connectivity or session affinity failed, possibly while cleanup or caller commit acknowledgement was in flight. | Stop provider I/O and discard the failed transaction/connection. If commit may have been sent, resolve the application ledger with a fresh appropriately scoped read: a found row proves a committed database fact, while absence is inconclusive until the original transaction cannot still commit. |
| Callback returns `false` | The application callback denied authorization or its conditional write. | This is successful `HandoffPrepared=false`: callback writes are rolled back. Do not send; earlier unrelated caller writes remain caller-owned. |
| Callback throws while caller token remains active | Application code or callback SQL failed after the callback savepoint was established. | The original exception propagates after the callback savepoint is rolled back and the prior scope restored. Native locks remain held until the caller ends the outer transaction; promptly roll it back or otherwise complete and dispose it. Do not send. |
| Caller token is canceled before the final success check | Cancellation was observed before the fence's last decision point. | `OperationCanceledException` propagates after operation-savepoint rollback; operation-acquired native locks may be released. End/dispose the outer transaction promptly and do not send. A callback-thrown cancellation with an uncanceled caller token follows the callback-exception row instead. |
| Savepoint rollback/release or scope restoration fails | Cleanup could not establish the documented transaction state. | Cleanup throws with the original and cleanup causes retained. Roll back and dispose the outer transaction and connection; never report success, retry on that transaction, or send. |

These outcomes do not transfer responsibility for the outer transaction. After an exception, use the exception and transaction state to decide whether to roll back/dispose; after cleanup failure, rollback/disposal is mandatory.

## Callback, RLS, and application writer obligations

Every authorization read and handoff write in the callback must use the supplied transaction and its exact connection. Do not open another connection, begin or use another transaction, or rely on an ambient transaction for those operations. The callback is database-only: it must not perform provider I/O, commit/roll back/dispose/replace the supplied resources, or leave work running beyond the executor invocation. The fence cannot sandbox application code holding runtime database credentials. An improper callback commit or write on another connection escapes the savepoints and cannot be undone by the framework; detection does not reverse it.

Call the fence before acquiring application authority or ledger locks. The canonical proof may make an unrelated earlier caller write, such as a marker row, only if it has no foreign-key or other lock dependency on authority/ledger rows. Inside the callback, lock and revalidate authority, then use a conditional write keyed by the stable logical operation and unchanged `ActivityId`/`ProviderKey`. Audit every competing writer of those rows, including implicit foreign-key locks and paths using transactional enqueue. All writers must follow native-before-application ordering and deterministic ordering when locking multiple application rows. A writer that takes application locks first and then requests conflicting native locks can deadlock. Do not nest enqueue into the canonical callback.

Use the restrictive runtime principal with separately granted application-table access and the applicable native and application row-level security policies. The fence sets only the bound transaction-local Durable scope and restores the caller's previous effective value, including the no-scope state, on usable outcomes. It does not change role, bypass RLS, or use migration-owner privileges. Prove scope restoration with both an existing scope and no scope, including pooled connection reuse.

The host owns finite, nonzero PostgreSQL `lock_timeout`, `statement_timeout`, and `idle_in_transaction_session_timeout` settings for the relevant runtime/application sessions. `lock_timeout` must be below `statement_timeout`. Record effective values and their owner in release evidence; select budgets from the deployment workload rather than copying universal seconds. The host also owns a bounded cancellation/cleanup policy and total transaction duration. These settings can bound individual waits/statements and terminate an idle abandoned transaction; they do not bound arbitrary callback CPU, an actively extended transaction, or an uncooperative application. The library does not change these settings or own caller completion.

## Local prerequisites and supported proof commands

Run from the repository root. The equipped/ready path requires the .NET 10 SDK, Docker installed and running, and the checkout. The strict PostgreSQL rail uses the repository's pinned PostgreSQL `16.5` Testcontainers image; it can pull that image on first use. An explicitly configured `APPSURFACE_POSTGRES_TEST_CONNECTION` may select an external PostgreSQL 16.0+ server for the ordinary rail, but do not use that override to label Docker readiness evidence. The packed consumer rail uses a fresh coordinated package version and runs the public-only consumer against the strict pinned PostgreSQL path. It accepts no positional test-case selector.

For the public handoff consumer's first run, use the same supported packed command in either cache state. The ready path confirms the pinned image is already present; the cold path leaves it absent so Testcontainers downloads it during the command. Both paths still include isolated NuGet restore and package build work:

```bash
# Ready/equipped: .NET 10 and Docker are ready; the exact PostgreSQL image is cached.
dotnet --version
docker info
docker image inspect 'postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877' >/dev/null
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
```

```bash
# Cold image: .NET 10 and Docker are ready; the exact PostgreSQL image is not cached.
dotnet --version
docker info
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
```

Do not pre-pull the image when measuring the cold path; count its download and all package restore/build waits. These are verifier commands, not a handoff-only case selector.

The supported commands are the existing script interfaces; use the exact package version shown here for the #869 candidate and do not infer success from source presence:

```bash
./Durable/verify-postgresql.sh --ci
CI=true APP_SURFACE_PACKAGE_VERSION=0.2.0-issue869.4 ./Durable/verify-packed-consumers.sh
./scripts/coverage-solution.sh
```

The first command is the full strict PostgreSQL suite, not a handoff-only selection. The packed command packs the coordinated packages and executes the package-only PostgreSQL consumer, including the handoff proof, with required PostgreSQL tests configured to fail on skips. Its reported outcomes and artifact identity must come from that run. The final command is the unchanged solution coverage gate (95% line / 85% branch by default); it accepts no arguments. The first two are issue acceptance rails; run solution coverage only after their source/package state is ready. Record the exact command, source/dirty-tree identity, terminal result, package version and bytes, database image/server identity, and effective host timeout values with their owner. Do not copy generated logs as tracked receipts or describe a pending/failed run as passing.

The approved equipped-machine first-use target is an **unmeasured 2–5 minute human aspiration**, not a test result or release gate. It includes reading this entry point, checking a ready .NET 10/Docker/repository setup with no previous handoff run, restore/build, image-cache state, downloads and waits, then accurately explaining that commit makes preparation visible and callback denial removes only callback writes. The cold-image path uses the same commands on an otherwise equipped machine with the pinned image uncached; image download and setup time are included and must be reported. Installing .NET or Docker and running the full strict suite are separate clocks. No timing or comprehension result is claimed here.

## Prepared, committed, and provider outcomes

```text
active invocation
  -> caller ReadCommitted transaction
  -> native locks, then application authorization + conditional ledger write
  -> final database-time/lifetime/cancellation check
  -> HandoffPrepared (provisional)
  -> caller checks cancellation, commits, disposes transaction, releases connection
  -> same invocation applies existing provider-safety policy
  -> accepted / rejected / ambiguous provider outcome is reconciled under existing rules
```

Give the application ledger a stable key scoped to the logical operation. If commit acknowledgement is lost, a fresh appropriately scoped read that finds the key proves a committed database fact; absence alone is inconclusive while the original transaction could still commit. Keep provider I/O stopped while the result remains unresolved. A later native cancellation does not undo a committed ledger row. The executor may observe cancellation before provider I/O and suppress the call, leaving the ledger for reconciliation; cancellation alone does not prove that no provider effect occurred. A found row alone never creates invocation authority or permission to send outside the current invocation.

| Boundary | What it establishes | What it does not establish |
| --- | --- | --- |
| Transactional Work enqueue | Accepts new Work atomically with caller-owned application data. | Does not revalidate the exact active invocation's live permit for an existing provider effect. |
| Application outbox | Can atomically persist business intent and application-owned delivery/reconciliation state. It can serve as the handoff ledger. | Does not by itself validate Durable's live scope, claim, lease, deadline, dispatch, and permit facts. |
| Invocation-bound handoff fence | Combines current native invocation facts with application authorization and a conditional write in the exact caller transaction. | Does not deliver an effect, guarantee provider acceptance, or replace application ledger/outbox reconciliation. |

## Internal contracts and verification

`PostgreSqlDurableTransactionResources.RequireActive` is the narrow shared resource check for the transaction writer and fence: it requires the supplied transaction's active open connection and configured physical target, preserves the writer's established failure convention, and takes no ownership. `PostgreSqlDurableHandoffFacts.ReadAsync` assumes schema and active epoch were already validated, the bound transaction-local scope was set, and the connection is the transaction's exact connection. It locks scope → exact Work → dispatch → only the supplied permit ID, supports legacy and opted-in Work, samples current database time around relevant lock waits, and returns current lease/deadline facts plus a bounded refusal code. It does not create scope, consume admission, mutate a permit, scan permit history, or complete the caller transaction. `TimeProblem` gives lease expiry precedence over an elapsed opted-in deadline. The shared epoch fence remains in `PostgreSqlDurableWorkStore`; no second admission transaction is run.

The complete executor flow is compiled as part of the public package consumer and published as a canonical snippet:

<!-- appsurface:snippet id="durable-handoff-application-flow" file="Durable/packed-consumers/PostgreSqlPolicyConsumer/HandoffPostgreSqlConsumerTests.cs" marker="durable-handoff-application-flow" lang="csharp" -->
```csharp
internal sealed class HandoffExecutor(
    IDurableWorkHandoffFence handoffFence,
    NpgsqlDataSource runtimeDataSource,
    HandoffExecutionProbe probe,
    HandoffFakeProvider fakeProvider)
    : IDurableWorkExitExecutor<HandoffWork, HandoffResult>
{
    private readonly Task _preparation = ObservePreparationAsync(handoffFence, runtimeDataSource, probe);

    private static async Task ObservePreparationAsync(IDurableWorkHandoffFence fence, NpgsqlDataSource source, HandoffExecutionProbe observation)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var result = await fence.ExecuteAsync(transaction, (_, _) =>
        {
            Interlocked.Increment(ref observation.PreparationCallbackCalls);
            return ValueTask.FromResult(true);
        });
        observation.PreparationProblem.TrySetResult(result.Problem?.Code);
    }

    public async ValueTask<DurableWorkExit<HandoffResult>> ExecuteAsync(
        DurableWorkerEnvelope<HandoffWork> envelope,
        CancellationToken cancellationToken = default)
    {
        try { await _preparation; return await ExecuteCoreAsync(envelope, cancellationToken); }
        catch (Exception exception) { probe.Failure.TrySetResult(exception); throw; }
    }

    private async ValueTask<DurableWorkExit<HandoffResult>> ExecuteCoreAsync(
        DurableWorkerEnvelope<HandoffWork> envelope, CancellationToken cancellationToken)
    {
        var work = envelope.Payload ?? throw new InvalidOperationException("The packed handoff Work payload was missing.");
        var identity = envelope.ExecutionIdentity ?? throw new InvalidOperationException("The native executor received no execution identity.");
        NpgsqlConnection? callerConnection = null;
        NpgsqlTransaction? callerTransaction = null;
        bool handoffPrepared;
        int backendPid;
        string? nativeScopeAfterFence;
        bool callbackUsedExactTransaction = false;
        bool callbackSawRestrictedRole = false;
        bool callbackSawBoundNativeScope = false;
        bool callbackSawOneNativeWorkRow = false;
        bool callbackSawOneApplicationAuthorityRow = false;
        bool callerTransactionDisposed = false;

        await using (var connection = await runtimeDataSource.OpenConnectionAsync(cancellationToken))
        {
            callerConnection = connection;
            await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                callerTransaction = transaction;
                backendPid = await HandoffPostgreSqlConsumerTests.BackendPidAsync(connection, transaction);
                await using (var setPriorScope = new NpgsqlCommand(
                    "SELECT set_config('appsurface_durable.scope_id', 'caller-scope-before-handoff', true);",
                    connection,
                    transaction))
                {
                    await setPriorScope.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var marker = new NpgsqlCommand("""
                    INSERT INTO handoff_consumer.caller_marker (marker_id, scope_id, marker_value)
                    VALUES (@marker_id, @scope_id, 'caller-owned-before-fence');
                    """, connection, transaction))
                {
                    marker.Parameters.AddWithValue("marker_id", work.MarkerId);
                    marker.Parameters.AddWithValue("scope_id", work.ScopeId);
                    await marker.ExecuteNonQueryAsync(cancellationToken);
                }

                probe.RetainedFence = handoffFence;
                probe.BeforeFence.TrySetResult(handoffFence);
                await probe.WaitForFenceAsync(cancellationToken);
                DurableOperationResult<DurableWorkHandoffResult> result;
                try
                {
                    result = await handoffFence.ExecuteAsync(
                    transaction,
                    async (suppliedTransaction, token) =>
                    {
                        Interlocked.Increment(ref probe.CallbackCalls);
                        if (probe.CrossScope is { } otherScope)
                        {
                            await using var crossRead = new NpgsqlCommand("""
                                SELECT (SELECT count(*) FROM appsurface_durable.work WHERE scope_id=@other_scope)
                                     + (SELECT count(*) FROM handoff_consumer.authority WHERE scope_id=@other_scope);
                                """, connection, suppliedTransaction);
                            crossRead.Parameters.AddWithValue("other_scope", otherScope);
                            probe.CrossScopeRowsHidden = (long)(await crossRead.ExecuteScalarAsync(token))! == 0;
                            await using var forbidden = new NpgsqlCommand("""
                                INSERT INTO handoff_consumer.ledger(scope_id, activity_id, logical_operation)
                                VALUES (@other_scope, @activity_id, @operation);
                                """, connection, suppliedTransaction);
                            forbidden.Parameters.AddWithValue("other_scope", otherScope);
                            forbidden.Parameters.AddWithValue("activity_id", identity.ActivityId);
                            forbidden.Parameters.AddWithValue("operation", work.LogicalOperation);
                            await forbidden.ExecuteNonQueryAsync(token);
                        }
                        callbackUsedExactTransaction = ReferenceEquals(suppliedTransaction, transaction)
                            && ReferenceEquals(suppliedTransaction.Connection, connection);
                        if (!callbackUsedExactTransaction)
                        {
                            throw new InvalidOperationException("The handoff callback did not receive the caller's exact transaction and connection.");
                        }

                        await using (var inspect = new NpgsqlCommand("""
                            SELECT current_user,
                                   current_setting('appsurface_durable.scope_id', true),
                                   (SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id),
                                   (SELECT count(*) FROM handoff_consumer.authority WHERE scope_id = @scope_id),
                                   (SELECT rolsuper OR rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user);
                            """, connection, suppliedTransaction))
                        {
                            inspect.Parameters.AddWithValue("scope_id", work.ScopeId);
                            await using var reader = await inspect.ExecuteReaderAsync(token);
                            if (!await reader.ReadAsync(token))
                            {
                                throw new InvalidOperationException("The handoff callback could not inspect the restricted execution context.");
                            }

                            callbackSawRestrictedRole = reader.GetString(0).StartsWith("handoff_runtime_", StringComparison.Ordinal) && !reader.GetBoolean(4);
                            callbackSawBoundNativeScope = reader.GetString(1) == work.ScopeId;
                            callbackSawOneNativeWorkRow = reader.GetInt64(2) == 1;
                            callbackSawOneApplicationAuthorityRow = reader.GetInt64(3) == 1;
                        }

                        probe.CallbackWaitingForAuthority.TrySetResult(new HandoffCallbackObservation(backendPid));
                        bool authorized;
                        await using (var authority = new NpgsqlCommand("""
                            SELECT is_authorized
                            FROM handoff_consumer.authority
                            WHERE scope_id = @scope_id AND operation_key = @authority_key
                            FOR UPDATE;
                            """, connection, suppliedTransaction))
                        {
                            authority.Parameters.AddWithValue("scope_id", work.ScopeId);
                            authority.Parameters.AddWithValue("authority_key", work.AuthorityKey);
                            var value = await authority.ExecuteScalarAsync(token);
                            authorized = value is true;
                        }

                        if (!authorized)
                        {
                            return false;
                        }

                        // The callback writes conditionally on the locked authority row. Both immutable identities are unique.
                        await using var ledger = new NpgsqlCommand("""
                            INSERT INTO handoff_consumer.ledger (scope_id, activity_id, logical_operation)
                            VALUES (@scope_id, @activity_id, @operation)
                            ON CONFLICT DO NOTHING
                            RETURNING activity_id;
                            """, connection, suppliedTransaction);
                        ledger.Parameters.AddWithValue("scope_id", work.ScopeId);
                        ledger.Parameters.AddWithValue("activity_id", identity.ActivityId);
                        ledger.Parameters.AddWithValue("operation", work.LogicalOperation);
                        var inserted = await ledger.ExecuteScalarAsync(token);
                        if (inserted is null)
                        {
                            return false;
                        }

                        // This late test-only denial exercises removal of a staged callback write by the callback savepoint.
                        return !probe.DenyAfterLedgerInsert;
                    },
                    cancellationToken);
                }
                catch
                {
                    await using var scopeAfterFailure = new NpgsqlCommand("SELECT current_setting('appsurface_durable.scope_id', true);", connection, transaction);
                    probe.ScopeAfterFailure = await scopeAfterFailure.ExecuteScalarAsync(CancellationToken.None) as string;
                    throw;
                }

                if (!result.IsSuccess || result.Value is null)
                {
                    throw new InvalidOperationException(result.Problem?.Code ?? "The public handoff fence refused without a problem code.");
                }

                handoffPrepared = result.Value.HandoffPrepared;
                await using (var readRestoredScope = new NpgsqlCommand(
                    "SELECT current_setting('appsurface_durable.scope_id', true);",
                    connection,
                    transaction))
                {
                    nativeScopeAfterFence = await readRestoredScope.ExecuteScalarAsync(cancellationToken) as string;
                }

                probe.Prepared.TrySetResult(new HandoffPreparedObservation(
                    handoffPrepared,
                    callbackUsedExactTransaction,
                    transaction.IsolationLevel,
                    callbackSawRestrictedRole,
                    callbackSawBoundNativeScope,
                    callbackSawOneNativeWorkRow,
                    callbackSawOneApplicationAuthorityRow,
                    nativeScopeAfterFence,
                    backendPid,
                    work.MarkerId));

                await probe.WaitForCallerCommitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (probe.CommitMode == HandoffCommitObservationMode.EstablishedRollback)
                    await transaction.RollbackAsync(cancellationToken);
                else if (probe.CommitMode == HandoffCommitObservationMode.Unresolved)
                {
                    // A fresh scoped reader cannot see this uncommitted row. Absence does not settle its outcome.
                    probe.CommitLedgerVisible.TrySetResult(await ReadLedgerAsync(work, identity.ActivityId));
                    await transaction.RollbackAsync(cancellationToken);
                }
                else
                {
                    try
                    {
                        await transaction.CommitAsync(cancellationToken);
                        if (probe.CommitMode == HandoffCommitObservationMode.AcknowledgementLost)
                            throw new IOException("Test adapter lost acknowledgement after a real PostgreSQL commit.");
                    }
                    catch (IOException failure) when (probe.CommitMode == HandoffCommitObservationMode.AcknowledgementLost)
                    {
                        probe.CommitFailure = failure;
                    }
                }
            }

            callerTransactionDisposed = true;
        }

        probe.ResourcesReleased.TrySetResult(callerTransactionDisposed && callerConnection!.State == ConnectionState.Closed);
        if (probe.CommitMode != HandoffCommitObservationMode.Acknowledged)
        {
            if (probe.CommitMode != HandoffCommitObservationMode.Unresolved)
                probe.CommitLedgerVisible.TrySetResult(await ReadLedgerAsync(work, identity.ActivityId));
            // The test adapter reports acknowledgement loss after a real commit, established rollback, or unresolved
            // outcome. Every uncertain path pauses provider I/O and leaves recovery to ledger/provider policy.
            return DurableWorkExit<HandoffResult>.AmbiguousExternalOutcome("handoff_commit_requires_reconciliation");
        }
        await probe.WaitForProviderAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!handoffPrepared)
        {
            return DurableWorkExit<HandoffResult>.RetryBeforeEffect("handoff_denied");
        }

        await fakeProvider.SendAsync(
            work,
            identity.ActivityId,
            callerTransaction!,
            callerConnection!,
            callerTransactionDisposed);
        return DurableWorkExit<HandoffResult>.Succeeded(new HandoffResult(identity.ActivityId));
    }
    private async Task<bool> ReadLedgerAsync(HandoffWork work, string activityId)
    {
        await using var connection = await runtimeDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var scope = new NpgsqlCommand("SELECT set_config('appsurface_durable.scope_id', @scope, true);", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope", work.ScopeId);
            await scope.ExecuteNonQueryAsync();
        }
        await using var read = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM handoff_consumer.ledger WHERE scope_id=@scope AND activity_id=@activity);", connection, transaction);
        read.Parameters.AddWithValue("scope", work.ScopeId);
        read.Parameters.AddWithValue("activity", activityId);
        return (bool)(await read.ExecuteScalarAsync())!;
    }

}
```
<!-- /appsurface:snippet -->

Start with the [compiled public handoff example above](#internal-contracts-and-verification), its [packed PostgreSQL consumer source](https://github.com/forge-trust/AppSurface/blob/main/Durable/packed-consumers/PostgreSqlPolicyConsumer/HandoffPostgreSqlConsumerTests.cs), and the [#869 proof plan](https://github.com/forge-trust/AppSurface/blob/main/docs/plans/issue-869-handoff-test-plan.md). The [package entry point](ForgeTrust.AppSurface.Durable.PostgreSql/README.md#prepare-an-application-handoff), [Work protocol](work-protocol-v1.md#prepare-application-handoff), [execution-policy reference](execution-policies-v1.md), and [issue progress record](https://github.com/forge-trust/AppSurface/blob/main/docs/issue-869-progress.md) provide the surrounding adoption and verification context. The consumer uses package references only; do not treat source presence as a compiled or passing proof.
