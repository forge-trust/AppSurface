using System.Data;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Npgsql;
using PolicyTests = ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>Native PostgreSQL timing, invocation, savepoint, and host-boundary proofs for handoff.</summary>
public sealed class HandoffBoundaryTests
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData("scope", "lease")]
    [InlineData("work", "lease")]
    [InlineData("dispatch", "lease")]
    [InlineData("permit", "lease")]
    [InlineData("scope", "deadline")]
    [InlineData("work", "deadline")]
    [InlineData("dispatch", "deadline")]
    [InlineData("permit", "deadline")]
    public async Task B1B2_ResampleExpiryAfterEachObservedNativeRowLockWait(string row, string expiry)
    {
        var deadline = expiry == "deadline" ? PolicyTests.Anchor.AddMinutes(1) : (DateTimeOffset?)null;
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            deadline: deadline, command: $"handoff-wait-{row}-{expiry}");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        if (expiry == "deadline")
        {
            await ExtendLeasePastDeadlineAsync(lab);
        }

        var appName = $"869-b12-{row}-{expiry}";
        await using var handoffDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, appName, lockTimeoutMilliseconds: 12_000, statementTimeoutMilliseconds: 20_000,
            idleTimeoutMilliseconds: 30_000);
        await using var blockerConnection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await LockNativeRowAsync(blockerConnection, blockerTransaction, lab.Permit, row);
        var blockerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(blockerConnection, blockerTransaction);

        var fence = new PostgreSqlDurableWorkHandoffFence(handoffDataSource, lab.Options);
        await using var handoffConnection = await handoffDataSource.OpenConnectionAsync();
        await using var handoffTransaction = await handoffConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;
        var operation = fence.ExecuteAsync(handoffTransaction, (_, _) =>
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        }).AsTask();
        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource, appName, blockerPid, BarrierTimeout);

        if (expiry == "deadline")
        {
            await lab.Database.SetExecutionTimeAsync(deadline!.Value);
        }
        else
        {
            await lab.Database.SetExecutionTimeAsync(lab.Permit.Claim.LeaseExpiresAtUtc);
        }

        await blockerTransaction.CommitAsync();
        var result = await operation.WaitAsync(BarrierTimeout);
        Assert.False(result.IsSuccess);
        Assert.Equal(expiry == "lease" ? DurableProblemCodes.LeaseLost : DurableProblemCodes.ExecutionDeadlineReached,
            result.Problem!.Code);
        Assert.Equal(0, callbackCount);
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
        await handoffTransaction.CommitAsync();
    }

    [Theory]
    [InlineData("lease", false)]
    [InlineData("lease", true)]
    [InlineData("deadline", false)]
    public async Task B1B2_ExpiryDuringCallbackRemovesCallbackLedgerWrite(string expiry, bool legacy)
    {
        var deadline = expiry == "deadline" ? PolicyTests.Anchor.AddMinutes(1) : (DateTimeOffset?)null;
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            legacy: legacy, deadline: deadline, command: $"handoff-callback-{expiry}-{legacy}");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        if (expiry == "deadline")
        {
            await ExtendLeasePastDeadlineAsync(lab);
        }
        else if (legacy)
        {
            await SetLegacyLeaseExpiryAsync(lab, TimeSpan.FromSeconds(2));
        }
        else
        {
            await SetOptedInLeaseExpiryAsync(lab, TimeSpan.FromSeconds(2));
        }

        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        var operation = lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
        {
            callbackCount++;
            Assert.Same(transaction, supplied);
            await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, "callback-expiry", token);
            callbackEntered.TrySetResult();
            await releaseCallback.Task.WaitAsync(token);
            return true;
        }).AsTask();

        await callbackEntered.Task.WaitAsync(BarrierTimeout);
        if (expiry == "deadline")
        {
            await lab.Database.SetExecutionTimeAsync(deadline!.Value);
        }
        else if (legacy)
        {
            await WaitForLegacyLeaseExpiryAsync(lab, BarrierTimeout);
        }
        else
        {
            await lab.Database.SetExecutionTimeAsync(lab.Permit.Claim.LeaseExpiresAtUtc.AddSeconds(1));
        }

        releaseCallback.TrySetResult();
        var result = await operation.WaitAsync(BarrierTimeout);
        Assert.False(result.IsSuccess);
        Assert.Equal(expiry == "deadline" ? DurableProblemCodes.ExecutionDeadlineReached : DurableProblemCodes.LeaseLost,
            result.Problem!.Code);
        Assert.Equal(1, callbackCount);
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(connection, transaction, "callback-expiry"));
        await transaction.CommitAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, "callback-expiry"));
    }

    [Fact]
    public async Task B2_DeadlineAlreadyReachedRefusesBeforeCallback()
    {
        var deadline = PolicyTests.Anchor.AddMinutes(1);
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            deadline: deadline, command: "handoff-before-callback-deadline");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await ExtendLeasePastDeadlineAsync(lab);
        await lab.Database.SetExecutionTimeAsync(deadline);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbackCount++;
            return ValueTask.FromResult(true);
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ExecutionDeadlineReached, result.Problem!.Code);
        Assert.Equal(0, callbackCount);
        await transaction.RollbackAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    [Fact]
    public async Task B3_CallerCancellationAtBeforeFinalCheckRollsBackOnlyOperationSavepoint()
    {
        using var callerCancellation = new CancellationTokenSource();
        var finalCheckReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new PostgreSqlDurableHandoffCheckpointHook(async (checkpoint, _) =>
        {
            if (checkpoint == PostgreSqlDurableHandoffCheckpoint.BeforeFinalCheck)
            {
                finalCheckReached.TrySetResult();
                await releaseFinalCheck.Task.WaitAsync(BarrierTimeout);
            }
        });
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            checkpoints: checkpoints, command: "handoff-cancel-before-final");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await CreateCallerProbeAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await InsertCallerProbeAsync(connection, transaction, "before-final");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;
        var operation = lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
        {
            callbackCount++;
            await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, "cancel-before-final", token);
            return true;
        }, callerCancellation.Token).AsTask();

        await finalCheckReached.Task.WaitAsync(BarrierTimeout);
        callerCancellation.Cancel();
        releaseFinalCheck.TrySetResult();
        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await operation.WaitAsync(BarrierTimeout));

        Assert.Equal(callerCancellation.Token, thrown.CancellationToken);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, await CountCallerProbeAsync(connection, transaction, "before-final"));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(connection, transaction));
        // Cancellation rolls back the operation savepoint, releasing its native locks before outer completion.
        // Unlike B4's active-token callback error, a native transition can finish while caller writes stay open.
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-b3-cancel-after-operation-rollback");
        using var transitionDeadline = new CancellationTokenSource(BarrierTimeout);
        var canceled = await native.WorkControl.CancelAsync(new DurableWorkCancelRequest(
            lab.Permit.Claim.ScopeId, lab.Permit.Claim.WorkId, "issue-869-test", "operation-rollback-releases-locks",
            lab.Permit.Claim.Revision), transitionDeadline.Token);
        Assert.True(canceled.IsSuccess, canceled.Problem?.Problem);
        Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value!.Outcome);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(1, await CountCallerProbeAsync(connection, transaction, "before-final"));
        await transaction.CommitAsync();
        Assert.Equal(1, await CountCallerProbeFreshAsync(lab, "before-final"));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    [Fact]
    public async Task B7_InvocationExitDuringCallbackRefusesAtFinalCheck()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-exit-in-callback");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;

        try
        {
            var operation = lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
            {
                callbackCount++;
                await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, "invocation-exit", token);
                callbackEntered.TrySetResult();
                await releaseCallback.Task.WaitAsync(token);
                return true;
            }).AsTask();

            await callbackEntered.Task.WaitAsync(BarrierTimeout);
            invocation.Dispose();
            releaseCallback.TrySetResult();
            var result = await operation.WaitAsync(BarrierTimeout);
            Assert.False(result.IsSuccess);
            Assert.Equal(DurableProblemCodes.ClaimLost, result.Problem!.Code);
            Assert.Equal(1, callbackCount);
            Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(connection, transaction));
            await transaction.CommitAsync();
        }
        finally
        {
            releaseCallback.TrySetResult();
        }

        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    [Fact]
    public async Task B4_CallbackExceptionRetainsNativeWorkLocksUntilCallerEndsOuterTransaction()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-callback-error-native-lock");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-b4-callback-error");
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var ownerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(connection, transaction);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackException = new InvalidOperationException("handoff callback failed after its ledger write");
        var callbackCount = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
            {
                callbackCount++;
                await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, "callback-throws", token);
                throw callbackException;
            }));
        Assert.Same(callbackException, thrown);
        Assert.Equal(1, callbackCount);
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(connection, transaction, "callback-throws"));

        using var transitionDeadline = new CancellationTokenSource(BarrierTimeout + BarrierTimeout);
        var cancellation = native.WorkControl.CancelAsync(new DurableWorkCancelRequest(
            lab.Permit.Claim.ScopeId, lab.Permit.Claim.WorkId, "issue-869-test", "callback-error-b4",
            lab.Permit.Claim.Revision), transitionDeadline.Token).AsTask();
        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource, native.ApplicationName, ownerPid, BarrierTimeout);
        Assert.False(cancellation.IsCompleted);

        await transaction.RollbackAsync();
        var canceled = await cancellation.WaitAsync(BarrierTimeout);
        Assert.True(canceled.IsSuccess, canceled.Problem?.Problem);
        Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value!.Outcome);
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    [Fact]
    public async Task NpgsqlSaveAsyncIsQueuedUntilNextCommandAndRollbackProvesServerBoundary()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-queued-savepoint");
        await CreateCallerProbeAsync(lab);
        const string appName = "869-saveasync-proof";
        await using var dataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(lab, appName);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await InsertCallerProbeAsync(connection, transaction, "before-savepoint");
        var ownerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(connection, transaction);
        var priorQuery = "SELECT 'issue869_before_save_async';";
        await using (var marker = new NpgsqlCommand(priorQuery, connection, transaction))
        {
            Assert.Equal("issue869_before_save_async", await marker.ExecuteScalarAsync());
        }

        var stateBeforeSaveAsync = await ReadBackendStateAsync(lab.Database.DataSource, ownerPid);
        var queuedSavepoint = transaction.SaveAsync("issue869_queued_savepoint");
        Assert.True(queuedSavepoint.IsCompletedSuccessfully,
            "The repository-pinned Npgsql 10.0.3 SaveAsync queues SAVEPOINT synchronously without a server round trip.");
        await queuedSavepoint;
        var stateAfterSaveAsync = await ReadBackendStateAsync(lab.Database.DataSource, ownerPid);
        Assert.Equal(stateBeforeSaveAsync.StateChange, stateAfterSaveAsync.StateChange);
        Assert.Contains("issue869_before_save_async", stateAfterSaveAsync.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("SAVEPOINT", stateAfterSaveAsync.Query, StringComparison.OrdinalIgnoreCase);

        await InsertCallerProbeAsync(connection, transaction, "after-savepoint");
        await transaction.RollbackAsync("issue869_queued_savepoint");
        await using (var flush = new NpgsqlCommand("SELECT 1;", connection, transaction))
        {
            Assert.Equal(1, await flush.ExecuteScalarAsync());
        }

        Assert.Equal(1, await CountCallerProbeAsync(connection, transaction, "before-savepoint"));
        Assert.Equal(0, await CountCallerProbeAsync(connection, transaction, "after-savepoint"));
        await transaction.CommitAsync();
        Assert.Equal(1, await CountCallerProbeFreshAsync(lab, "before-savepoint"));
        Assert.Equal(0, await CountCallerProbeFreshAsync(lab, "after-savepoint"));
    }

    [Fact]
    public async Task HostLockAndStatementTimeoutsAreFiniteOrderedAndPropagateFromPostgreSql()
    {
        await using (var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-host-lock-timeout"))
        {
            await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
            const string appName = "869-host-lock-timeout";
            await using var dataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
                lab, appName, lockTimeoutMilliseconds: 3_000, statementTimeoutMilliseconds: 6_000,
                idleTimeoutMilliseconds: 8_000);
            await using var owner = await lab.Database.DataSource.OpenConnectionAsync();
            await using var ownerTransaction = await owner.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await LockNativeRowAsync(owner, ownerTransaction, lab.Permit, "scope");
            var ownerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(owner, ownerTransaction);
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            AssertHostTimeouts(await ReadHostTimeoutsAsync(connection, transaction), appName);
            var fence = new PostgreSqlDurableWorkHandoffFence(dataSource, lab.Options);
            using var invocation = fence.EnterInvocation(lab.Permit);
            var callbacks = 0;
            var operation = fence.ExecuteAsync(transaction, (_, _) =>
            {
                callbacks++;
                return ValueTask.FromResult(true);
            }).AsTask();
            await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
                lab.Database.DataSource, appName, ownerPid, TimeSpan.FromMilliseconds(2_500));
            var failure = await Assert.ThrowsAsync<PostgresException>(async () => await operation.WaitAsync(BarrierTimeout));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);
            Assert.Equal(0, callbacks);
            await transaction.CommitAsync();
            await ownerTransaction.RollbackAsync();
        }

        await using (var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-host-statement-timeout"))
        {
            await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
            const string appName = "869-host-statement-timeout";
            await using var dataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
                lab, appName, lockTimeoutMilliseconds: 100, statementTimeoutMilliseconds: 350,
                idleTimeoutMilliseconds: 8_000);
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            AssertHostTimeouts(await ReadHostTimeoutsAsync(connection, transaction), appName);
            var fence = new PostgreSqlDurableWorkHandoffFence(dataSource, lab.Options);
            using var invocation = fence.EnterInvocation(lab.Permit);
            var callbacks = 0;
            var operation = fence.ExecuteAsync(
                transaction, async (supplied, token) =>
                {
                    callbacks++;
                    await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, "statement-timeout", token);
                    await using var sleep = new NpgsqlCommand("SELECT pg_catalog.pg_sleep(3);", supplied.Connection, supplied);
                    await sleep.ExecuteNonQueryAsync(token);
                    return true;
                }).AsTask();
            var failure = await Assert.ThrowsAsync<PostgresException>(async () => await operation.WaitAsync(BarrierTimeout));
            Assert.Equal(PostgresErrorCodes.QueryCanceled, failure.SqlState);
            Assert.Equal(1, callbacks);
            Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(connection, transaction));
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task HostIdleAbandonmentRollsBackHandoffAndReleasesEpochWorkAndApplicationLocks()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-idle-abandonment");
        await PostgreSqlDurableHandoffTestSupport.CreateAuthorityAndLedgerAsync(lab);
        const string ownerApplicationName = "869-host-idle-owner";
        await using var ownerDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, ownerApplicationName, lockTimeoutMilliseconds: 1_000,
            statementTimeoutMilliseconds: 20_000, idleTimeoutMilliseconds: 5_000);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(
            lab, "869-host-idle-native", lockTimeoutMilliseconds: 12_000,
            statementTimeoutMilliseconds: 20_000, idleTimeoutMilliseconds: 30_000);
        const string epochApplicationName = "869-host-idle-epoch";
        await using var epochDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, epochApplicationName, lockTimeoutMilliseconds: 12_000,
            statementTimeoutMilliseconds: 20_000, idleTimeoutMilliseconds: 30_000);
        var migrationLockRefused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var epochManager = new PostgreSqlDurableRuntimeSchemaManager(
            epochDataSource,
            DurablePostgreSqlMigrationCatalog.Load(),
            migrationLockAcquireTimeout: TimeSpan.FromSeconds(14),
            sampleMigrationLockRetryDelayMilliseconds: static () => PostgreSqlDurableRuntimeSchemaManager.MigrationLockRetryMinimumMilliseconds,
            delayMigrationLockRetry: static (delay, token) => Task.Delay(delay, token),
            observeMigrationLockAttempt: acquired =>
            {
                if (!acquired)
                {
                    migrationLockRefused.TrySetResult();
                }
            });
        await using var appWriterDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, "869-host-idle-app-writer", lockTimeoutMilliseconds: 12_000,
            statementTimeoutMilliseconds: 20_000, idleTimeoutMilliseconds: 30_000);
        await using var ownerConnection = await ownerDataSource.OpenConnectionAsync();
        await using var ownerTransaction = await ownerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var ownerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(ownerConnection, ownerTransaction);
        var ownerTimeouts = await ReadHostTimeoutsAsync(ownerConnection, ownerTransaction);
        AssertHostTimeouts(ownerTimeouts, ownerApplicationName);
        Assert.Equal(5_000, ownerTimeouts.IdleMilliseconds);
        var fence = new PostgreSqlDurableWorkHandoffFence(ownerDataSource, lab.Options);
        using var invocation = fence.EnterInvocation(lab.Permit);
        var prepared = await fence.ExecuteAsync(ownerTransaction, async (supplied, token) =>
        {
            await using var authorityLock = new NpgsqlCommand(
                $"SELECT authority_id FROM {PostgreSqlDurableHandoffTestSupport.AuthorityTable} WHERE authority_id=1 FOR UPDATE;",
                supplied.Connection, supplied);
            Assert.Equal(1, (int)(await authorityLock.ExecuteScalarAsync(token))!);
            await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, "idle-owner", token);
            return true;
        });
        Assert.True(prepared.IsSuccess, prepared.Problem?.Problem);
        Assert.True(prepared.Value!.HandoffPrepared);

        using var transitionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(24));
        var cancellation = native.WorkControl.CancelAsync(new DurableWorkCancelRequest(
            lab.Permit.Claim.ScopeId, lab.Permit.Claim.WorkId, "issue-869-test", "idle-abandonment",
            lab.Permit.Claim.Revision), transitionDeadline.Token).AsTask();
        var newEpoch = Guid.NewGuid();
        var rotation = epochManager.RotateRuntimeEpochAsync(
            lab.Options.RuntimeEpoch, newEpoch, "issue-869-test", "idle-abandonment-epoch").AsTask();
        var appUpdate = UpdateAuthorityAsync(appWriterDataSource);

        await Task.WhenAll(
            PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
                lab.Database.DataSource, native.ApplicationName, ownerPid, BarrierTimeout),
            migrationLockRefused.Task.WaitAsync(BarrierTimeout),
            PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
                lab.Database.DataSource, "869-host-idle-app-writer", ownerPid, BarrierTimeout));
        Assert.False(cancellation.IsCompleted);
        Assert.False(rotation.IsCompleted);
        Assert.False(appUpdate.IsCompleted);
        Assert.True(await HasSharedMigrationFenceAsync(lab, ownerPid));

        // Observe idle rollback through the blocked operations and the server's timeout error first.
        // Check process teardown after consuming that error and disposing the caller resources.
        await Task.WhenAll(cancellation, rotation, appUpdate).WaitAsync(BarrierTimeout);
        Assert.False(await HasSharedMigrationFenceAsync(lab, ownerPid));
        var canceled = await cancellation;
        Assert.True(canceled.IsSuccess, canceled.Problem?.Problem);
        Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value!.Outcome);
        var rotated = await rotation;
        Assert.Equal(newEpoch, rotated.ActiveEpoch);

        await using (var staleConnection = new NpgsqlCommand("SELECT 1;", ownerConnection))
        {
            var timeout = await Assert.ThrowsAsync<PostgresException>(async () => await staleConnection.ExecuteScalarAsync());
            Assert.Equal("25P03", timeout.SqlState); // idle_in_transaction_session_timeout
            Assert.True(ownerConnection.State is ConnectionState.Broken or ConnectionState.Closed);
        }

        await ownerTransaction.DisposeAsync();
        await ownerConnection.DisposeAsync();
        await WaitForBackendGoneAsync(lab.Database.DataSource, ownerPid, BarrierTimeout);
        await using (var replacement = await ownerDataSource.OpenConnectionAsync())
        {
            Assert.NotEqual(ownerPid, await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(replacement));
            await using var health = new NpgsqlCommand("SELECT 1;", replacement);
            Assert.Equal(1, await health.ExecuteScalarAsync());
        }

        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, "idle-owner"));
        Assert.Equal(1, await ReadAuthorityRevisionAsync(lab));
        Assert.Equal(newEpoch, await PostgreSqlDurableHandoffTestSupport.ReadActiveEpochAsync(lab));
    }

    [Fact]
    public async Task LegacyHandoffUsesPostgreSqlClockInsteadOfFutureOptedInTestClock()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(legacy: true, command: "handoff-legacy-clock");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddYears(1));
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbacks = 0;

        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult(true);
        });

        Assert.True(result.IsSuccess, result.Problem?.Problem);
        Assert.Equal(1, callbacks);
        await transaction.RollbackAsync();
    }

    private static async Task ExtendLeasePastDeadlineAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand("""
            UPDATE appsurface_durable.work
            SET lease_expires_at = execution_not_after + interval '1 minute'
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """);
        command.Parameters.AddWithValue("scope_id", lab.Permit.Claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", lab.Permit.Claim.WorkId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SetOptedInLeaseExpiryAsync(PostgreSqlDurableHandoffTestLab lab, TimeSpan fromNow)
    {
        await using var command = lab.Database.DataSource.CreateCommand("""
            UPDATE appsurface_durable.work
            SET lease_expires_at = appsurface_durable.work_execution_now() + @duration
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """);
        command.Parameters.AddWithValue("duration", fromNow);
        command.Parameters.AddWithValue("scope_id", lab.Permit.Claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", lab.Permit.Claim.WorkId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SetLegacyLeaseExpiryAsync(PostgreSqlDurableHandoffTestLab lab, TimeSpan fromNow)
    {
        await using var command = lab.Database.DataSource.CreateCommand("""
            UPDATE appsurface_durable.work
            SET lease_expires_at = pg_catalog.clock_timestamp() + @duration
            WHERE scope_id = @scope_id AND work_id = @work_id;
            """);
        command.Parameters.AddWithValue("duration", fromNow);
        command.Parameters.AddWithValue("scope_id", lab.Permit.Claim.ScopeId.Value);
        command.Parameters.AddWithValue("work_id", lab.Permit.Claim.WorkId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task WaitForLegacyLeaseExpiryAsync(PostgreSqlDurableHandoffTestLab lab, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            await using var command = lab.Database.DataSource.CreateCommand("""
                SELECT lease_expires_at <= pg_catalog.clock_timestamp()
                FROM appsurface_durable.work WHERE scope_id = @scope_id AND work_id = @work_id;
                """);
            command.Parameters.AddWithValue("scope_id", lab.Permit.Claim.ScopeId.Value);
            command.Parameters.AddWithValue("work_id", lab.Permit.Claim.WorkId.Value);
            if ((bool)(await command.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private static async Task LockNativeRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PostgreSqlEffectPermit permit, string row)
    {
        const string scopeSql = "SELECT scope_id FROM appsurface_durable.scope WHERE scope_id=@scope_id FOR UPDATE;";
        const string workSql = "SELECT work_id FROM appsurface_durable.work WHERE scope_id=@scope_id AND work_id=@work_id FOR UPDATE;";
        const string dispatchSql = "SELECT dispatch_id FROM appsurface_durable.dispatch WHERE dispatch_id=@dispatch_id FOR UPDATE;";
        const string permitSql = "SELECT permit_id FROM appsurface_durable.effect_permit WHERE permit_id=@permit_id FOR UPDATE;";
        var sql = row switch
        {
            "scope" => scopeSql,
            "work" => workSql,
            "dispatch" => dispatchSql,
            "permit" => permitSql,
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (row is "scope" or "work") command.Parameters.AddWithValue("scope_id", permit.Claim.ScopeId.Value);
        if (row == "work") command.Parameters.AddWithValue("work_id", permit.Claim.WorkId.Value);
        if (row == "dispatch") command.Parameters.AddWithValue("dispatch_id", permit.Claim.DispatchId);
        if (row == "permit") command.Parameters.AddWithValue("permit_id", permit.PermitId);
        Assert.NotNull(await command.ExecuteScalarAsync());
    }

    private static async Task CreateCallerProbeAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            "CREATE TABLE public.issue869_handoff_boundary_probe (probe_key text PRIMARY KEY);");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertCallerProbeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO public.issue869_handoff_boundary_probe (probe_key) VALUES (@key);", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountCallerProbeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM public.issue869_handoff_boundary_probe WHERE probe_key=@key;", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountCallerProbeFreshAsync(PostgreSqlDurableHandoffTestLab lab, string key)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            "SELECT count(*) FROM public.issue869_handoff_boundary_probe WHERE probe_key=@key;");
        command.Parameters.AddWithValue("key", key);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record HostTimeouts(long LockMilliseconds, long StatementMilliseconds, long IdleMilliseconds, string ApplicationName);

    private static async Task<HostTimeouts> ReadHostTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand("""
            SELECT round(extract(epoch FROM current_setting('lock_timeout')::interval) * 1000)::bigint,
                   round(extract(epoch FROM current_setting('statement_timeout')::interval) * 1000)::bigint,
                   round(extract(epoch FROM current_setting('idle_in_transaction_session_timeout')::interval) * 1000)::bigint,
                   current_setting('application_name');
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3));
    }

    private static void AssertHostTimeouts(HostTimeouts timeouts, string applicationName)
    {
        Assert.Equal(applicationName, timeouts.ApplicationName);
        Assert.True(timeouts.LockMilliseconds > 0, "lock_timeout must be enabled and finite.");
        Assert.True(timeouts.StatementMilliseconds > 0, "statement_timeout must be enabled and finite.");
        Assert.True(timeouts.IdleMilliseconds > 0, "idle_in_transaction_session_timeout must be enabled and finite.");
        Assert.True(timeouts.LockMilliseconds < timeouts.StatementMilliseconds,
            "The host lock timeout must be shorter than its statement timeout.");
    }

    private sealed record BackendState(DateTime StateChange, string Query);

    private static async Task<BackendState> ReadBackendStateAsync(NpgsqlDataSource observer, int pid)
    {
        await using var command = observer.CreateCommand(
            "SELECT state_change, query FROM pg_catalog.pg_stat_activity WHERE pid=@pid;");
        command.Parameters.AddWithValue("pid", pid);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetDateTime(0), reader.GetString(1));
    }

    private static async Task WaitForBackendGoneAsync(NpgsqlDataSource observer, int pid, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            await using var command = observer.CreateCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE pid=@pid);");
            command.Parameters.AddWithValue("pid", pid);
            if (!(bool)(await command.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private static async Task UpdateAuthorityAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE {PostgreSqlDurableHandoffTestSupport.AuthorityTable}
            SET revision=revision+1, enabled=false WHERE authority_id=1;
            """);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<bool> HasSharedMigrationFenceAsync(PostgreSqlDurableHandoffTestLab lab, int pid)
    {
        await using var command = lab.Database.DataSource.CreateCommand("""
            SELECT EXISTS
            (
                SELECT 1
                FROM pg_catalog.pg_locks
                WHERE pid = @pid
                  AND locktype = 'advisory'
                  AND granted
                  AND mode = 'ShareLock'
                  AND classid = ((@lock_id >> 32)::oid)
                  AND objid = ((@lock_id & 4294967295)::oid)
                  AND objsubid = 1
            );
            """);
        command.Parameters.AddWithValue("pid", pid);
        command.Parameters.AddWithValue("lock_id", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ReadAuthorityRevisionAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            $"SELECT revision FROM {PostgreSqlDurableHandoffTestSupport.AuthorityTable} WHERE authority_id=1;");
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
