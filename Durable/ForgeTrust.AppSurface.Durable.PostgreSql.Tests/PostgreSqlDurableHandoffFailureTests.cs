using System.Data;
using ForgeTrust.AppSurface.Durable;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableHandoffFailureTests
{
    private const string PartitionedWorkTable = "public.issue869_b5_partitioned_work";
    private const string DeadlockRowsTable = "public.issue869_b5_deadlock_rows";
    private const string CallerMarkerTable = "public.issue869_b5_caller_marker";

    [Fact]
    public async Task ConcurrentPartitionRowMovementSerializationFailurePropagatesAfterCallbackCleanup()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            command: "handoff-failure-partition-serialization");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await CreateFailureTablesAsync(lab);

        const string moverApplicationName = "869-b5-partition-mover";
        const string callbackApplicationName = "869-b5-partition-callback";
        const string handoffKey = "partition-row-movement";
        await using var moverDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, moverApplicationName, lockTimeoutMilliseconds: 10_000, statementTimeoutMilliseconds: 20_000);
        await using var moverConnection = await moverDataSource.OpenConnectionAsync();
        await using var moverTransaction = await moverConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await ExecuteNonQueryAsync(moverConnection, moverTransaction,
            $"UPDATE {PartitionedWorkTable} SET work_bucket = 11 WHERE work_id = 1;");
        var moverPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(moverConnection, moverTransaction);

        await using var callbackDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, callbackApplicationName, lockTimeoutMilliseconds: 10_000, statementTimeoutMilliseconds: 20_000);
        await using var callerConnection = await callbackDataSource.OpenConnectionAsync();
        await using var callerTransaction = await callerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await SetScopeAsync(callerConnection, callerTransaction, "caller-scope");
        await InsertCallerMarkerAsync(callerConnection, callerTransaction, handoffKey);
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var handoff = lab.Fence.ExecuteAsync(callerTransaction, async (suppliedTransaction, token) =>
        {
            callbackCount++;
            Assert.Same(callerTransaction, suppliedTransaction);
            await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(suppliedTransaction, handoffKey, token);
            await using var update = new NpgsqlCommand(
                $"UPDATE {PartitionedWorkTable} SET revision = revision + 1 WHERE work_id = 1;",
                callerConnection,
                suppliedTransaction);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(token));
            return true;
        }).AsTask();

        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource,
            callbackApplicationName,
            moverPid,
            TimeSpan.FromSeconds(8));
        await moverTransaction.CommitAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await handoff.WaitAsync(TimeSpan.FromSeconds(8)));

        Assert.Equal(PostgresErrorCodes.SerializationFailure, failure.SqlState);
        Assert.Equal(1, callbackCount);
        Assert.Same(callerConnection, callerTransaction.Connection);
        Assert.Equal(1, await CountCallerMarkerAsync(callerConnection, callerTransaction, handoffKey));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(
            callerConnection, callerTransaction, handoffKey));
        Assert.Equal("caller-scope", await ReadScopeAsync(callerConnection, callerTransaction));

        await callerTransaction.CommitAsync();

        Assert.Equal(1, await CountCallerMarkerAsync(lab.Database.DataSource, handoffKey));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, handoffKey));
        Assert.Equal(11, await ReadMovedBucketAsync(lab.Database.DataSource));
    }

    [Fact]
    public async Task CallbackDeadlockPropagatesAsPostgresDeadlockAfterSavepointCleanup()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            command: "handoff-failure-callback-deadlock");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await CreateCallerMarkerTableAsync(lab);
        await CreateDeadlockRowsAsync(lab);

        const string callbackApplicationName = "869-b5-deadlock-callback";
        const string competingApplicationName = "869-b5-deadlock-competing";
        const string handoffKey = "deadlock-callback";
        await using var competingDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, competingApplicationName, lockTimeoutMilliseconds: 35_000, statementTimeoutMilliseconds: 45_000);
        await using var competingConnection = await competingDataSource.OpenConnectionAsync();
        await using var callbackDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, callbackApplicationName, lockTimeoutMilliseconds: 15_000, statementTimeoutMilliseconds: 25_000);
        await using var callerConnection = await callbackDataSource.OpenConnectionAsync();
        await SetDeadlockTimeoutAsync(competingConnection, "30s", 30_000);
        await SetDeadlockTimeoutAsync(callerConnection, "10s", 10_000);
        await using var callerTransaction = await callerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await SetScopeAsync(callerConnection, callerTransaction, "caller-scope");
        await InsertCallerMarkerAsync(callerConnection, callerTransaction, handoffKey);
        Assert.Equal(1, await ExecuteNonQueryAsync(callerConnection, callerTransaction,
            $"UPDATE {DeadlockRowsTable} SET row_value = row_value + 1 WHERE row_id = 1;"));
        var callerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(
            callerConnection, callerTransaction);

        await using var competingTransaction = await competingConnection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted);
        Assert.Equal(1, await ExecuteNonQueryAsync(competingConnection, competingTransaction,
            $"UPDATE {DeadlockRowsTable} SET row_value = row_value + 1 WHERE row_id = 2;"));
        var competingPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(
            competingConnection, competingTransaction);
        await using var competingUpdate = new NpgsqlCommand(
            $"UPDATE {DeadlockRowsTable} SET row_value = row_value + 1 WHERE row_id = 1;",
            competingConnection,
            competingTransaction);
        var competingUpdateTask = competingUpdate.ExecuteNonQueryAsync();
        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource,
            competingApplicationName,
            callerPid,
            TimeSpan.FromSeconds(8));

        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;
        PostgresException? callbackDeadlock = null;

        var handoff = lab.Fence.ExecuteAsync(callerTransaction, async (suppliedTransaction, token) =>
        {
            callbackCount++;
            Assert.Same(callerTransaction, suppliedTransaction);
            await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(suppliedTransaction, handoffKey, token);
            try
            {
                await using var secondRow = new NpgsqlCommand(
                    $"UPDATE {DeadlockRowsTable} SET row_value = row_value + 1 WHERE row_id = 2;",
                    callerConnection,
                    suppliedTransaction);
                Assert.Equal(1, await secondRow.ExecuteNonQueryAsync(token));
            }
            catch (PostgresException exception)
            {
                callbackDeadlock = exception;
                throw;
            }

            return true;
        }).AsTask();

        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource,
            callbackApplicationName,
            competingPid,
            TimeSpan.FromSeconds(8));

        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await handoff.WaitAsync(TimeSpan.FromSeconds(25)));

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, failure.SqlState);
        Assert.Same(callbackDeadlock, failure);
        Assert.Equal(1, callbackCount);
        Assert.Same(callerConnection, callerTransaction.Connection);
        Assert.Equal(1, await CountCallerMarkerAsync(callerConnection, callerTransaction, handoffKey));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(
            callerConnection, callerTransaction, handoffKey));
        Assert.Equal("caller-scope", await ReadScopeAsync(callerConnection, callerTransaction));
        await PostgreSqlDurableHandoffTestSupport.WaitForBlockingPidAsync(
            lab.Database.DataSource,
            competingApplicationName,
            callerPid,
            TimeSpan.FromSeconds(6));
        Assert.False(competingUpdateTask.IsCompleted);

        await callerTransaction.CommitAsync();
        Assert.Equal(1, await competingUpdateTask.WaitAsync(TimeSpan.FromSeconds(6)));
        await competingTransaction.CommitAsync();

        Assert.Equal(1, await CountCallerMarkerAsync(lab.Database.DataSource, handoffKey));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, handoffKey));
        Assert.Equal(2, await ReadDeadlockRowValueAsync(lab.Database.DataSource, 1));
        Assert.Equal(1, await ReadDeadlockRowValueAsync(lab.Database.DataSource, 2));
    }

    [Fact]
    public Task CallbackAndScopeRestoreFailuresAreBothPreservedAndRequireOuterRollback() =>
        AssertCleanupFailureAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRestore);

    [Fact]
    public Task CallbackAndSavepointReleaseFailuresAreBothPreservedAndRequireOuterRollback() =>
        AssertCleanupFailureAsync(PostgreSqlDurableHandoffCheckpoint.BeforeRelease);

    private static async Task AssertCleanupFailureAsync(PostgreSqlDurableHandoffCheckpoint checkpoint)
    {
        var callbackFailure = new InvalidOperationException("injected callback failure");
        var cleanupFailure = new InvalidOperationException($"injected {checkpoint} failure");
        var callbackToken = default(CancellationToken);
        var cleanupToken = default(CancellationToken);
        var checkpoints = new PostgreSqlDurableHandoffCheckpointHook((observed, token) =>
        {
            if (observed != checkpoint) return ValueTask.CompletedTask;
            cleanupToken = token;
            return ValueTask.FromException(cleanupFailure);
        });
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            checkpoints: checkpoints,
            command: $"handoff-failure-cleanup-{checkpoint}");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await CreateCallerMarkerTableAsync(lab);
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await SetScopeAsync(connection, transaction, "caller-scope");
        await InsertCallerMarkerAsync(connection, transaction, checkpoint.ToString());
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var callbackCount = 0;

        var aggregate = await Assert.ThrowsAsync<AggregateException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, async (suppliedTransaction, token) =>
            {
                callbackCount++;
                callbackToken = token;
                Assert.Same(transaction, suppliedTransaction);
                await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(
                    suppliedTransaction, checkpoint.ToString(), token);
                throw callbackFailure;
            }));

        var causes = aggregate.Flatten().InnerExceptions;
        Assert.Contains(causes, exception => ReferenceEquals(exception, callbackFailure));
        Assert.Contains(causes, exception => ReferenceEquals(exception, cleanupFailure));
        Assert.Equal(1, callbackCount);
        Assert.True(cleanupToken.CanBeCanceled);
        Assert.NotEqual(callbackToken, cleanupToken);

        await transaction.RollbackAsync();

        Assert.Equal(0, await CountCallerMarkerAsync(lab.Database.DataSource, checkpoint.ToString()));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, checkpoint.ToString()));
    }

    private static async Task CreateFailureTablesAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await ExecuteNonQueryAsync(lab.Database.DataSource, $"""
            CREATE TABLE {PartitionedWorkTable} (
                work_id integer NOT NULL,
                work_bucket integer NOT NULL,
                revision integer NOT NULL
            ) PARTITION BY RANGE (work_bucket);
            """);
        await ExecuteNonQueryAsync(lab.Database.DataSource, $"""
            CREATE TABLE {PartitionedWorkTable}_low PARTITION OF {PartitionedWorkTable}
                FOR VALUES FROM (0) TO (10);
            """);
        await ExecuteNonQueryAsync(lab.Database.DataSource, $"""
            CREATE TABLE {PartitionedWorkTable}_high PARTITION OF {PartitionedWorkTable}
                FOR VALUES FROM (10) TO (20);
            """);
        await ExecuteNonQueryAsync(lab.Database.DataSource,
            $"INSERT INTO {PartitionedWorkTable} (work_id, work_bucket, revision) VALUES (1, 1, 0);");
        await CreateCallerMarkerTableAsync(lab);
    }

    private static async Task CreateDeadlockRowsAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await ExecuteNonQueryAsync(lab.Database.DataSource, $"""
            CREATE TABLE {DeadlockRowsTable} (
                row_id integer PRIMARY KEY,
                row_value integer NOT NULL
            );
            """);
        await ExecuteNonQueryAsync(lab.Database.DataSource,
            $"INSERT INTO {DeadlockRowsTable} (row_id, row_value) VALUES (1, 0), (2, 0);");
    }

    private static async Task CreateCallerMarkerTableAsync(PostgreSqlDurableHandoffTestLab lab) =>
        await ExecuteNonQueryAsync(lab.Database.DataSource,
            $"CREATE TABLE {CallerMarkerTable} (marker text PRIMARY KEY);");

    private static async Task SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scope)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope, true);", connection, transaction);
        command.Parameters.AddWithValue("scope", scope);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('appsurface_durable.scope_id', true);", connection, transaction);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task InsertCallerMarkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string marker)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {CallerMarkerTable} (marker) VALUES (@marker);", connection, transaction);
        command.Parameters.AddWithValue("marker", marker);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountCallerMarkerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string marker)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {CallerMarkerTable} WHERE marker = @marker;", connection, transaction);
        command.Parameters.AddWithValue("marker", marker);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountCallerMarkerAsync(NpgsqlDataSource dataSource, string marker)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT count(*) FROM {CallerMarkerTable} WHERE marker = @marker;");
        command.Parameters.AddWithValue("marker", marker);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ReadMovedBucketAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT work_bucket FROM {PartitionedWorkTable} WHERE work_id = 1;");
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ReadDeadlockRowValueAsync(NpgsqlDataSource dataSource, int rowId)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT row_value FROM {DeadlockRowsTable} WHERE row_id = @row_id;");
        command.Parameters.AddWithValue("row_id", rowId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetDeadlockTimeoutAsync(
        NpgsqlConnection connection,
        string timeout,
        int expectedMilliseconds)
    {
        await using var set = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('deadlock_timeout', @timeout, false);", connection);
        set.Parameters.AddWithValue("timeout", timeout);
        await set.ExecuteScalarAsync();

        await using var verify = new NpgsqlCommand(
            "SELECT setting::integer FROM pg_catalog.pg_settings WHERE name = 'deadlock_timeout';", connection);
        Assert.Equal(expectedMilliseconds, (int)(await verify.ExecuteScalarAsync())!);
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync();
    }
}
