using System.Data;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>Proves the two committed orders for native scope, Work, epoch, and lease transitions.</summary>
public sealed class PostgreSqlDurableHandoffRaceTests
{
    [Fact]
    public async Task R1R2_ScopeDisableFirstRefuses_AndHandoffFirstHoldsLocksThroughCallerCommit()
    {
        await ScopeDisableWinsAsync();
        await ScopeDisableHandoffWinsAsync();
    }

    [Fact]
    public async Task R3R4_WorkCancellationFirstRefuses_AndHandoffFirstHoldsLocksThroughCallerCommit()
    {
        await WorkCancellationWinsAsync();
        await WorkCancellationHandoffWinsAsync();
    }

    [Fact]
    public async Task R5R6_EpochRotationFirstRefuses_AndHandoffFirstHoldsSharedFenceThroughCallerCommit()
    {
        await EpochRotationWinsAsync();
        await EpochRotationHandoffWinsAsync();
    }

    [Fact]
    public async Task R7R8_LeaseReclaimFirstRefuses_AndHandoffFirstHoldsWorkLockThroughCallerCommit()
    {
        await LeaseReclaimWinsAsync();
        await LeaseReclaimHandoffWinsAsync();
    }

    private static async Task ScopeDisableWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-r1-disable");
        var disabled = await native.ScopeControl.DisableAsync(new DurableScopeDisableRequest(
            lab.Permit.Claim.ScopeId, "issue-869-test", "scope-disable-r1", lab.Permit.Claim.ScopeGeneration));
        Assert.True(disabled.IsSuccess, disabled.Problem?.Problem);
        Assert.Equal(DurableScopeDisableOutcome.Applied, disabled.Value!.Outcome);

        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbacks = 0;
        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult(true);
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ScopeDisabled, result.Problem!.Code);
        Assert.Equal(0, callbacks);
        await transaction.RollbackAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    private static async Task ScopeDisableHandoffWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-r2-disable");
        var disabled = await CompleteHandoffBeforeTransitionAsync(
            lab,
            transitionApplicationName: native.ApplicationName,
            startTransition: token => native.ScopeControl.DisableAsync(new DurableScopeDisableRequest(
                lab.Permit.Claim.ScopeId, "issue-869-test", "scope-disable-r2", lab.Permit.Claim.ScopeGeneration), token).AsTask(),
            timeout: TimeSpan.FromSeconds(8));

        Assert.True(disabled.IsSuccess, disabled.Problem?.Problem);
        Assert.Equal(DurableScopeDisableOutcome.Applied, disabled.Value!.Outcome);
    }

    private static async Task WorkCancellationWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-r3-cancel");
        var canceled = await native.WorkControl.CancelAsync(new DurableWorkCancelRequest(
            lab.Permit.Claim.ScopeId,
            lab.Permit.Claim.WorkId,
            "issue-869-test",
            "work-cancel-r3",
            lab.Permit.Claim.Revision));
        Assert.True(canceled.IsSuccess, canceled.Problem?.Problem);
        Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value!.Outcome);

        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbacks = 0;
        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult(true);
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ClaimLost, result.Problem!.Code);
        Assert.Equal(0, callbacks);
        await transaction.RollbackAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    private static async Task WorkCancellationHandoffWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-r4-cancel");
        var canceled = await CompleteHandoffBeforeTransitionAsync(
            lab,
            transitionApplicationName: native.ApplicationName,
            startTransition: token => native.WorkControl.CancelAsync(new DurableWorkCancelRequest(
                lab.Permit.Claim.ScopeId,
                lab.Permit.Claim.WorkId,
                "issue-869-test",
                "work-cancel-r4",
                lab.Permit.Claim.Revision), token).AsTask(),
            timeout: TimeSpan.FromSeconds(8));

        Assert.True(canceled.IsSuccess, canceled.Problem?.Problem);
        Assert.Equal(DurableWorkCancelOutcome.Applied, canceled.Value!.Outcome);
    }

    private static async Task EpochRotationWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-r5-epoch");
        var newEpoch = Guid.NewGuid();
        var rotated = await native.SchemaManager.RotateRuntimeEpochAsync(
            lab.Options.RuntimeEpoch, newEpoch, "issue-869-test", "epoch-rotate-r5");
        Assert.Equal(newEpoch, rotated.ActiveEpoch);

        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbacks = 0;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, (_, _) =>
            {
                callbacks++;
                return ValueTask.FromResult(true);
            }));

        Assert.Contains(DurableProblemCodes.RecoveryEpochRequired, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, callbacks);
        await transaction.RollbackAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    private static async Task EpochRotationHandoffWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using var native = PostgreSqlDurableHandoffNativeApi.Create(lab, "869-r6-epoch");

        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var ownerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(connection, transaction);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var handoff = await RecordHandoffAsync(lab, transaction, "r6");
        Assert.True(handoff.IsSuccess, handoff.Problem?.Problem);
        Assert.True(handoff.Value!.HandoffPrepared);

        var lockAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new PostgreSqlDurableRuntimeSchemaManager(
            native.RuntimeDataSource,
            DurablePostgreSqlMigrationCatalog.Load(),
            migrationLockAcquireTimeout: TimeSpan.FromSeconds(8),
            sampleMigrationLockRetryDelayMilliseconds: static () => PostgreSqlDurableRuntimeSchemaManager.MigrationLockRetryMinimumMilliseconds,
            delayMigrationLockRetry: static (delay, token) => Task.Delay(delay, token),
            observeMigrationLockAttempt: acquired =>
            {
                if (acquired)
                {
                    lockAcquired.TrySetResult();
                }
                else
                {
                    lockAttempt.TrySetResult();
                }
            });
        var newEpoch = Guid.NewGuid();
        var rotation = manager.RotateRuntimeEpochAsync(
            lab.Options.RuntimeEpoch, newEpoch, "issue-869-test", "epoch-rotate-r6").AsTask();

        await lockAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rotation.IsCompleted);
        Assert.Equal(lab.Options.RuntimeEpoch, await PostgreSqlDurableHandoffTestSupport.ReadActiveEpochAsync(lab));
        await transaction.CommitAsync();

        var rotated = await rotation.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(newEpoch, rotated.ActiveEpoch);
        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    private static async Task LeaseReclaimWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await lab.Database.SetExecutionTimeAsync(lab.Permit.Claim.LeaseExpiresAtUtc.AddSeconds(1));
        var reclaimed = await PostgreSqlDurableHandoffTestSupport.ReclaimAsync(
            lab.PolicyLab.Store, lab.Permit.Claim.WorkId, "issue-869-r7-reclaimer");
        Assert.NotNull(reclaimed);
        Assert.True(reclaimed!.LeaseGeneration > lab.Permit.Claim.LeaseGeneration);

        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbacks = 0;
        var result = await lab.Fence.ExecuteAsync(transaction, (_, _) =>
        {
            callbacks++;
            return ValueTask.FromResult(true);
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(DurableProblemCodes.ClaimLost, result.Problem!.Code);
        Assert.Equal(0, callbacks);
        await transaction.RollbackAsync();
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
    }

    private static async Task LeaseReclaimHandoffWinsAsync()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        var applicationName = "869-r8-reclaimer";
        await using var reclaimerDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(lab, applicationName);
        var reclaimer = new PostgreSqlDurableWorkStore(reclaimerDataSource, lab.PolicyLab.Epoch);

        var reclaimed = await CompleteHandoffBeforeTransitionAsync(
            lab,
            transitionApplicationName: applicationName,
            startTransition: async _ =>
            {
                await lab.Database.SetExecutionTimeAsync(lab.Permit.Claim.LeaseExpiresAtUtc.AddSeconds(1));
                return await PostgreSqlDurableHandoffTestSupport.ReclaimAsync(
                    reclaimer, lab.Permit.Claim.WorkId, "issue-869-r8-reclaimer");
            },
            timeout: TimeSpan.FromSeconds(8));

        Assert.NotNull(reclaimed);
        Assert.True(reclaimed!.LeaseGeneration > lab.Permit.Claim.LeaseGeneration);
        Assert.Equal(lab.Permit.Claim.ActivityId, reclaimed.ActivityId);
    }

    private static async Task<T> CompleteHandoffBeforeTransitionAsync<T>(
        PostgreSqlDurableHandoffTestLab lab,
        string transitionApplicationName,
        Func<CancellationToken, Task<T>> startTransition,
        TimeSpan timeout)
    {
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var ownerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(connection, transaction);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var handoff = await RecordHandoffAsync(lab, transaction, transitionApplicationName);
        Assert.True(handoff.IsSuccess, handoff.Problem?.Problem);
        Assert.True(handoff.Value!.HandoffPrepared);

        using var transitionCancellation = new CancellationTokenSource(timeout + timeout);
        var transition = startTransition(transitionCancellation.Token);
        Exception? observationFailure = null;
        try
        {
            await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
                lab.Database.DataSource, transitionApplicationName, ownerPid, timeout);
            Assert.False(transition.IsCompleted);
        }
        catch (Exception exception)
        {
            observationFailure = exception;
        }

        await transaction.CommitAsync();
        T result;
        try
        {
            result = await transition.WaitAsync(timeout);
        }
        catch (Exception transitionFailure) when (observationFailure is not null)
        {
            throw new AggregateException("The native transition and its blocking observation both failed.",
                observationFailure, transitionFailure);
        }

        if (observationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(observationFailure).Throw();
        }

        Assert.Equal(1, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab));
        return result;
    }

    private static async Task<DurableOperationResult<DurableWorkHandoffResult>> RecordHandoffAsync(
        PostgreSqlDurableHandoffTestLab lab,
        NpgsqlTransaction transaction,
        string key)
    {
        var callbacks = 0;
        var result = await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
        {
            callbacks++;
            Assert.Same(transaction, supplied);
            await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, key, token);
            return true;
        });

        Assert.Equal(1, callbacks);
        return result;
    }
}

/// <summary>Small explicit PostgreSQL probes shared by the two #869 native-boundary test files.</summary>
internal static class PostgreSqlDurableHandoffTestSupport
{
    internal const string LedgerTable = "public.issue869_handoff_ledger";
    internal const string AuthorityTable = "public.issue869_handoff_authority";
    internal static readonly TimeSpan TerminalWait = TimeSpan.FromSeconds(10);

    internal static NpgsqlDataSource CreateDataSource(
        PostgreSqlDurableHandoffTestLab lab,
        string applicationName,
        int lockTimeoutMilliseconds = 12_000,
        int statementTimeoutMilliseconds = 20_000,
        int idleTimeoutMilliseconds = 30_000)
    {
        var builder = new NpgsqlConnectionStringBuilder(lab.Database.ConnectionString)
        {
            ApplicationName = applicationName,
            Options = $"-c lock_timeout={lockTimeoutMilliseconds}ms -c statement_timeout={statementTimeoutMilliseconds}ms "
                + $"-c idle_in_transaction_session_timeout={idleTimeoutMilliseconds}ms",
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    internal static async Task CreateLedgerAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            $"CREATE TABLE {LedgerTable} (handoff_key text PRIMARY KEY);");
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task CreateAuthorityAndLedgerAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using (var authority = lab.Database.DataSource.CreateCommand(
            $"CREATE TABLE {AuthorityTable} (authority_id integer PRIMARY KEY, enabled boolean NOT NULL, revision integer NOT NULL);"))
        {
            await authority.ExecuteNonQueryAsync();
        }

        await using (var seed = lab.Database.DataSource.CreateCommand(
            $"INSERT INTO {AuthorityTable} (authority_id, enabled, revision) VALUES (1, true, 0);"))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await CreateLedgerAsync(lab);
    }

    internal static async Task InsertLedgerAsync(
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {LedgerTable} (handoff_key) VALUES (@key);",
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<long> CountLedgerAsync(PostgreSqlDurableHandoffTestLab lab, string? key = null)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            $"SELECT count(*) FROM {LedgerTable} WHERE (@key IS NULL OR handoff_key = @key);");
        command.Parameters.AddWithValue("key", NpgsqlTypes.NpgsqlDbType.Text, key is null ? DBNull.Value : key);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<long> CountLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string? key = null)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {LedgerTable} WHERE (@key IS NULL OR handoff_key = @key);",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", NpgsqlTypes.NpgsqlDbType.Text, key is null ? DBNull.Value : key);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<int> GetBackendPidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid();", connection, transaction);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task WaitForBlockingPidAsync(
        NpgsqlDataSource observerDataSource,
        string waitingApplicationName,
        int expectedBlockingPid,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await using var connection = await observerDataSource.OpenConnectionAsync(deadline.Token);
            await using var command = new NpgsqlCommand("""
                SELECT pid, pg_catalog.pg_blocking_pids(pid)
                FROM pg_catalog.pg_stat_activity
                WHERE datname = current_database() AND application_name = @application_name;
                """, connection);
            command.Parameters.AddWithValue("application_name", waitingApplicationName);

            while (true)
            {
                await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
                {
                    while (await reader.ReadAsync(deadline.Token))
                    {
                        var blockingPids = reader.GetFieldValue<int[]>(1);
                        if (blockingPids.Contains(expectedBlockingPid)) return;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"PostgreSQL did not report application '{waitingApplicationName}' blocked by backend {expectedBlockingPid} within {timeout}.");
        }
    }

    internal static async Task<PostgreSqlDurableWorkClaim?> ReclaimAsync(
        PostgreSqlDurableWorkStore store,
        DurableWorkId workId,
        string workerId)
    {
        var candidates = await store.DiscoverAsync(10);
        var candidate = Assert.Single(candidates, item => item.WorkId == workId);
        return await store.TryClaimAsync(candidate, workerId);
    }

    internal static async Task<Guid> ReadActiveEpochAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var command = lab.Database.DataSource.CreateCommand(
            "SELECT active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;");
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>Creates real public control APIs on separately named PostgreSQL sessions for race observations.</summary>
internal sealed class PostgreSqlDurableHandoffNativeApi : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly NpgsqlDataSource _dispatcherDataSource;
    private readonly NpgsqlDataSource _runtimeDataSource;

    private PostgreSqlDurableHandoffNativeApi(
        ServiceProvider services,
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        string applicationName)
    {
        _services = services;
        _dispatcherDataSource = dispatcherDataSource;
        _runtimeDataSource = runtimeDataSource;
        ApplicationName = applicationName;
        ScopeControl = services.GetRequiredService<IDurableScopeControlClient>();
        WorkControl = services.GetRequiredService<IDurableWorkControlClient>();
        SchemaManager = services.GetRequiredService<IDurableRuntimeSchemaManager>();
    }

    internal string ApplicationName { get; }
    internal IDurableScopeControlClient ScopeControl { get; }
    internal IDurableWorkControlClient WorkControl { get; }
    internal IDurableRuntimeSchemaManager SchemaManager { get; }
    internal NpgsqlDataSource RuntimeDataSource => _runtimeDataSource;

    internal static PostgreSqlDurableHandoffNativeApi Create(
        PostgreSqlDurableHandoffTestLab lab,
        string applicationName,
        int lockTimeoutMilliseconds = 12_000,
        int statementTimeoutMilliseconds = 20_000,
        int idleTimeoutMilliseconds = 30_000)
    {
        var dispatcherDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, applicationName + "-dispatch", lockTimeoutMilliseconds,
            statementTimeoutMilliseconds, idleTimeoutMilliseconds);
        var runtimeDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, applicationName, lockTimeoutMilliseconds,
            statementTimeoutMilliseconds, idleTimeoutMilliseconds);
        try
        {
            var services = new ServiceCollection();
            services.AddAppSurfaceDurablePostgreSql(
                dispatcherDataSource,
                runtimeDataSource,
                lab.Options,
                new PostgreSqlDurableScheduleOptions("issue-869-test-runtime"),
                options => options.SendWakeNotifications = false);
            return new(services.BuildServiceProvider(), dispatcherDataSource, runtimeDataSource, applicationName);
        }
        catch
        {
            dispatcherDataSource.Dispose();
            runtimeDataSource.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _dispatcherDataSource.DisposeAsync();
        await _runtimeDataSource.DisposeAsync();
    }
}
