using ForgeTrust.AppSurface.Durable;
using Npgsql;
using PolicyTests = ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlDurableWorkExecutionPolicyTests;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableHandoffCleanupTests
{
    [Theory]
    [InlineData("prepared")]
    [InlineData("denied")]
    [InlineData("exception")]
    [InlineData("unrelated-cancellation")]
    public async Task CallbackOutcomesPreserveCallerWritesScopeSavepointAndTransaction(string outcome)
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: $"handoff-cleanup-{outcome}");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, "caller-scope");
        await InsertAsync(connection, transaction, "caller-before");
        await transaction.SaveAsync("caller_checkpoint");
        await InsertAsync(connection, transaction, "caller-between");
        using var callerCancellation = new CancellationTokenSource();
        using var unrelatedCancellation = new CancellationTokenSource();
        unrelatedCancellation.Cancel();
        var callbackCount = 0;
        var callbackFailure = new InvalidOperationException("injected callback failure");
        var callerToken = callerCancellation.Token;

        async ValueTask<bool> Callback(NpgsqlTransaction suppliedTransaction, CancellationToken suppliedToken)
        {
            callbackCount++;
            Assert.Same(transaction, suppliedTransaction);
            Assert.Equal(callerToken, suppliedToken);
            Assert.Equal("execution-tests", await ReadScopeAsync(connection, transaction));
            await InsertAsync(connection, transaction, "callback-write");
            await SetScopeAsync(connection, transaction, "callback-scope");
            return outcome switch
            {
                "prepared" => true,
                "denied" => false,
                "exception" => throw callbackFailure,
                "unrelated-cancellation" => throw new OperationCanceledException(
                    "callback cancellation unrelated to the caller token", null, unrelatedCancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
        }

        if (outcome is "prepared" or "denied")
        {
            var result = await ExecuteAsync(lab.Fence, transaction, Callback, callerToken);
            Assert.True(result.IsSuccess);
            Assert.Equal(outcome == "prepared", result.Value!.HandoffPrepared);
        }
        else if (outcome == "exception")
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ExecuteAsync(lab.Fence, transaction, Callback, callerToken));
            Assert.Same(callbackFailure, thrown);
        }
        else
        {
            var thrown = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await ExecuteAsync(lab.Fence, transaction, Callback, callerToken));
            Assert.Equal(unrelatedCancellation.Token, thrown.CancellationToken);
            Assert.False(callerCancellation.IsCancellationRequested);
        }

        Assert.Equal(1, callbackCount);
        Assert.Equal("caller-scope", await ReadScopeAsync(connection, transaction));
        Assert.Equal(1, await CountAsync(connection, transaction, "caller-before"));
        Assert.Equal(1, await CountAsync(connection, transaction, "caller-between"));
        Assert.Equal(outcome == "prepared" ? 1 : 0,
            await CountAsync(connection, transaction, "callback-write"));
        Assert.False(await CanTakeExclusiveMigrationFenceAsync(lab));

        await transaction.RollbackAsync("caller_checkpoint");
        Assert.Equal("caller-scope", await ReadScopeAsync(connection, transaction));
        Assert.Equal(1, await CountAsync(connection, transaction, "caller-before"));
        Assert.Equal(0, await CountAsync(connection, transaction, "caller-between"));
        Assert.Equal(0, await CountAsync(connection, transaction, "callback-write"));
        await InsertAsync(connection, transaction, "caller-after-rollback");
        await transaction.CommitAsync();

        Assert.Equal(1, await lab.CountProbeRowsAsync("caller-before"));
        Assert.Equal(1, await lab.CountProbeRowsAsync("caller-after-rollback"));
        Assert.Equal(0, await lab.CountProbeRowsAsync("callback-write"));
        await AssertPooledScopeIsEmptyAsync(lab);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("denied")]
    [InlineData("exception")]
    public async Task MissingPriorScopeIsRestoredAsEmptyAfterEveryCallbackOutcomeAndPoolReturn(string outcome)
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: $"handoff-empty-scope-{outcome}");
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        if (outcome == "exception")
        {
            var callbackFailure = new InvalidOperationException("empty-scope callback failure");
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, _) =>
                {
                    callbackCount++;
                    Assert.Same(transaction, suppliedTransaction);
                    await SetScopeAsync(connection, transaction, "callback-scope");
                    throw callbackFailure;
                }));
            Assert.Same(callbackFailure, thrown);
        }
        else
        {
            var result = await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, _) =>
            {
                callbackCount++;
                Assert.Same(transaction, suppliedTransaction);
                await SetScopeAsync(connection, transaction, "callback-scope");
                return outcome == "prepared";
            });

            Assert.True(result.IsSuccess);
            Assert.Equal(outcome == "prepared", result.Value!.HandoffPrepared);
        }

        Assert.Equal(1, callbackCount);
        Assert.True(await IsEmptyScopeAsync(connection, transaction));
        await transaction.CommitAsync();
        await AssertPooledScopeIsEmptyAsync(lab);
    }

    [Fact]
    public async Task CallerCancellationAndRollbackHookFailureRetainBothCausesAndRequireOuterRollback()
    {
        var cleanupFailure = new InvalidOperationException("injected rollback cleanup failure");
        var checkpoints = new PostgreSqlDurableHandoffCheckpointHook((checkpoint, _) =>
            checkpoint == PostgreSqlDurableHandoffCheckpoint.BeforeRollback
                ? ValueTask.FromException(cleanupFailure)
                : ValueTask.CompletedTask);
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            checkpoints: checkpoints, command: "handoff-caller-cancel-cleanup-failure");
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(connection, transaction, "caller-before");
        using var callerCancellation = new CancellationTokenSource();
        var callerToken = callerCancellation.Token;
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, suppliedToken) =>
            {
                callbackCount++;
                Assert.Same(transaction, suppliedTransaction);
                Assert.Equal(callerToken, suppliedToken);
                await InsertAsync(connection, transaction, "callback-write");
                callerCancellation.Cancel();
                throw new OperationCanceledException("caller cancellation inside callback", callerToken);
            }, callerToken));

        Assert.Contains(aggregate.Flatten().InnerExceptions,
            exception => exception is OperationCanceledException canceled
                && canceled.CancellationToken == callerToken);
        Assert.Contains(aggregate.Flatten().InnerExceptions,
            exception => ReferenceEquals(exception, cleanupFailure));
        Assert.Equal(1, callbackCount);

        // Cleanup failed before the fence could restore the caller transaction; the caller owns final rollback.
        await transaction.RollbackAsync();
        Assert.Equal(0, await lab.CountProbeRowsAsync("caller-before"));
        Assert.Equal(0, await lab.CountProbeRowsAsync("callback-write"));
    }

    [Fact]
    public async Task InvalidSqlInsideCallbackRollsBackItsSavepointAndPreservesCallerTransaction()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-callback-sql-failure");
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, "caller-scope");
        await InsertAsync(connection, transaction, "caller-before");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, _) =>
            {
                callbackCount++;
                Assert.Same(transaction, suppliedTransaction);
                await InsertAsync(connection, transaction, "callback-write");
                await using var invalid = new NpgsqlCommand(
                    "SELECT missing_column FROM handoff_contract_probe;", connection, transaction);
                await invalid.ExecuteNonQueryAsync();
                return true;
            }));

        Assert.Equal(PostgresErrorCodes.UndefinedColumn, failure.SqlState);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, await CountAsync(connection, transaction, "caller-before"));
        Assert.Equal(0, await CountAsync(connection, transaction, "callback-write"));
        Assert.Equal("caller-scope", await ReadScopeAsync(connection, transaction));
        Assert.False(await CanTakeExclusiveMigrationFenceAsync(lab));
        await InsertAsync(connection, transaction, "caller-after");
        await transaction.CommitAsync();

        Assert.Equal(1, await lab.CountProbeRowsAsync("caller-before"));
        Assert.Equal(1, await lab.CountProbeRowsAsync("caller-after"));
        Assert.Equal(0, await lab.CountProbeRowsAsync("callback-write"));
    }

    [Fact]
    public async Task TerminatedCallbackBackendAggregatesOriginalAndCleanupFailuresAndLeavesNoHandoffRows()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-terminated-callback");
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(connection, transaction, "caller-before");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, _) =>
            {
                callbackCount++;
                Assert.Same(transaction, suppliedTransaction);
                await InsertAsync(connection, transaction, "callback-write");
                await using var backendCommand = new NpgsqlCommand("SELECT pg_backend_pid();", connection, transaction);
                var backendPid = (int)(await backendCommand.ExecuteScalarAsync())!;
                await using var terminator = await lab.Database.DataSource.OpenConnectionAsync();
                await using var terminateCommand = new NpgsqlCommand(
                    "SELECT pg_terminate_backend(@pid, @timeout_ms);", terminator);
                terminateCommand.Parameters.AddWithValue("pid", backendPid);
                terminateCommand.Parameters.AddWithValue("timeout_ms", 5_000L);
                Assert.True((bool)(await terminateCommand.ExecuteScalarAsync())!);
                return true;
            }));

        Assert.True(aggregate.Flatten().InnerExceptions.Count >= 2);
        Assert.Contains(aggregate.Flatten().InnerExceptions,
            exception => exception is InvalidOperationException or NpgsqlException);
        Assert.Equal(1, callbackCount);

        // The backend termination forces rollback of the caller transaction; check from a fresh pooled connection.
        Assert.Equal(0, await lab.CountProbeRowsAsync("caller-before"));
        Assert.Equal(0, await lab.CountProbeRowsAsync("callback-write"));
    }

    [Fact]
    public async Task SequentialCallsUseIndependentSavepointsAndApplicationCompareAndSetRunsOnce()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(command: "handoff-sequential-cas");
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.SaveAsync("ash_op_caller_owned");
        await transaction.SaveAsync("ash_cb_caller_owned");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        async ValueTask<bool> CompareAndSet(NpgsqlTransaction suppliedTransaction, CancellationToken _)
        {
            callbackCount++;
            Assert.Same(transaction, suppliedTransaction);
            await using var command = new NpgsqlCommand(
                "INSERT INTO handoff_contract_probe(value) VALUES ('cas-once') ON CONFLICT DO NOTHING;",
                connection, transaction);
            return await command.ExecuteNonQueryAsync() == 1;
        }

        var first = await lab.Fence.ExecuteAsync(transaction, CompareAndSet);
        var second = await lab.Fence.ExecuteAsync(transaction, CompareAndSet);

        Assert.True(first.IsSuccess);
        Assert.True(first.Value!.HandoffPrepared);
        Assert.True(second.IsSuccess);
        Assert.False(second.Value!.HandoffPrepared);
        Assert.Equal(2, callbackCount);
        Assert.Equal(1, await CountAsync(connection, transaction, "cas-once"));
        await transaction.RollbackAsync("ash_cb_caller_owned");
        Assert.Equal(0, await CountAsync(connection, transaction, "cas-once"));
        await transaction.RollbackAsync("ash_op_caller_owned");
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PostCallbackDeadlineOrLifetimeRefusalRestoresScopeAndRetainsNativeFence(
        bool callbackPrepared, bool revokeInvocation)
    {
        var finalCheckReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalCheck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new PostgreSqlDurableHandoffCheckpointHook(async (checkpoint, token) =>
        {
            if (checkpoint == PostgreSqlDurableHandoffCheckpoint.BeforeFinalCheck)
            {
                finalCheckReached.TrySetResult(true);
                await releaseFinalCheck.Task.WaitAsync(token);
            }
        });
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            checkpoints: checkpoints, deadline: PolicyTests.Anchor.AddMinutes(1),
            command: $"handoff-final-refusal-{callbackPrepared}-{revokeInvocation}");
        // Native leases normally cap at the deadline. Extend this fixture lease to isolate deadline precedence;
        // a simultaneous lease/deadline expiry is separately required to return LeaseLost.
        await using (var extendLease = lab.Database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET lease_expires_at = execution_not_after + interval '1 minute';"))
            await extendLease.ExecuteNonQueryAsync();
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, "caller-scope");
        await InsertAsync(connection, transaction, "caller-before");
        var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;
        var execution = lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, _) =>
        {
            callbackCount++;
            Assert.Same(transaction, suppliedTransaction);
            await InsertAsync(connection, transaction, "callback-write");
            return callbackPrepared;
        }).AsTask();

        await finalCheckReached.Task;
        long callbackRowsAtFinalCheck;
        try
        {
            callbackRowsAtFinalCheck = await CountAsync(connection, transaction, "callback-write");
            if (revokeInvocation)
            {
                invocation.Dispose();
            }
            else
            {
                await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(1));
            }
        }
        finally
        {
            releaseFinalCheck.TrySetResult(true);
        }

        var result = await execution;
        invocation.Dispose();
        Assert.False(result.IsSuccess);
        Assert.Equal(revokeInvocation ? DurableProblemCodes.ClaimLost : DurableProblemCodes.ExecutionDeadlineReached,
            result.Problem!.Code);
        Assert.Equal(callbackPrepared ? 1 : 0, callbackRowsAtFinalCheck);
        Assert.Equal(1, callbackCount);
        Assert.Equal(0, await CountAsync(connection, transaction, "callback-write"));
        Assert.Equal(1, await CountAsync(connection, transaction, "caller-before"));
        Assert.Equal("caller-scope", await ReadScopeAsync(connection, transaction));
        Assert.False(await CanTakeExclusiveMigrationFenceAsync(lab));
        await InsertAsync(connection, transaction, "caller-after");
        await transaction.CommitAsync();

        Assert.Equal(1, await lab.CountProbeRowsAsync("caller-before"));
        Assert.Equal(1, await lab.CountProbeRowsAsync("caller-after"));
        Assert.Equal(0, await lab.CountProbeRowsAsync("callback-write"));
    }

    [Fact]
    public async Task CancellationAfterFinalCheckUsesCleanupTokenAndStillRequiresCallerCommit()
    {
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken checkpointToken = default;
        var hookCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new PostgreSqlDurableHandoffCheckpointHook((checkpoint, token) =>
        {
            if (checkpoint == PostgreSqlDurableHandoffCheckpoint.AfterFinalCheck)
            {
                checkpointToken = token;
                callerCancellation.Cancel();
                hookCalled.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        });
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            checkpoints: checkpoints, command: "handoff-late-cancellation");
        await CreateProbeTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(connection, transaction, "caller-before");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var result = await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, suppliedToken) =>
        {
            callbackCount++;
            Assert.Same(transaction, suppliedTransaction);
            Assert.Equal(callerCancellation.Token, suppliedToken);
            await InsertAsync(connection, transaction, "callback-write");
            return true;
        }, callerCancellation.Token);

        Assert.True(hookCalled.Task.IsCompletedSuccessfully);
        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.NotEqual(callerCancellation.Token, checkpointToken);
        Assert.False(checkpointToken.IsCancellationRequested);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.HandoffPrepared);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, await CountAsync(connection, transaction, "callback-write"));

        await transaction.RollbackAsync();
        Assert.Equal(0, await lab.CountProbeRowsAsync("caller-before"));
        Assert.Equal(0, await lab.CountProbeRowsAsync("callback-write"));
    }

    [Fact]
    public async Task CleanupCheckpointFailureEscapesInsteadOfReturningARefusalResult()
    {
        var finalCheckReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalCheck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new InvalidOperationException("injected cleanup checkpoint failure");
        var checkpoints = new PostgreSqlDurableHandoffCheckpointHook(async (checkpoint, token) =>
        {
            if (checkpoint == PostgreSqlDurableHandoffCheckpoint.BeforeFinalCheck)
            {
                finalCheckReached.TrySetResult(true);
                await releaseFinalCheck.Task.WaitAsync(token);
            }
            else if (checkpoint == PostgreSqlDurableHandoffCheckpoint.BeforeRollback)
            {
                throw cleanupFailure;
            }
        });
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            checkpoints: checkpoints, deadline: PolicyTests.Anchor.AddMinutes(1), command: "handoff-cleanup-hook-failure");
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var execution = lab.Fence.ExecuteAsync(transaction, static (_, _) => ValueTask.FromResult(true)).AsTask();

        await finalCheckReached.Task;
        try
        {
            await lab.Database.SetExecutionTimeAsync(PolicyTests.Anchor.AddMinutes(1));
        }
        finally
        {
            releaseFinalCheck.TrySetResult(true);
        }

        var thrown = await Assert.ThrowsAsync<AggregateException>(async () => await execution);
        Assert.Contains(thrown.Flatten().InnerExceptions, exception => ReferenceEquals(exception, cleanupFailure));
        await transaction.RollbackAsync();
    }

    private static ValueTask<DurableOperationResult<DurableWorkHandoffResult>> ExecuteAsync(
        PostgreSqlDurableWorkHandoffFence fence, NpgsqlTransaction transaction,
        Func<NpgsqlTransaction, CancellationToken, ValueTask<bool>> callback, CancellationToken token) =>
        fence.ExecuteAsync(transaction, callback, token);

    private static async Task CreateProbeTableAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            "CREATE TABLE handoff_contract_probe (value text PRIMARY KEY);");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string value)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO handoff_contract_probe(value) VALUES (@value);", connection, transaction);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string value)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM handoff_contract_probe WHERE value = @value;", connection, transaction);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string? scope)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope, true);", connection, transaction);
        command.Parameters.AddWithValue("scope", scope ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('appsurface_durable.scope_id', true);", connection, transaction);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<bool> IsEmptyScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var scope = await ReadScopeAsync(connection, transaction);
        return string.IsNullOrEmpty(scope);
    }

    private static async Task<bool> CanTakeExclusiveMigrationFenceAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_xact_lock(@lock_id);", connection, transaction);
        command.Parameters.AddWithValue("lock_id", PostgreSqlDurableRuntimeSchemaManager.MigrationAdvisoryLock);
        var acquired = (bool)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return acquired;
    }

    private static async Task AssertPooledScopeIsEmptyAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('appsurface_durable.scope_id', true);", connection);
        var value = await command.ExecuteScalarAsync();
        Assert.True(value is null or DBNull || string.IsNullOrEmpty((string)value));
    }
}
