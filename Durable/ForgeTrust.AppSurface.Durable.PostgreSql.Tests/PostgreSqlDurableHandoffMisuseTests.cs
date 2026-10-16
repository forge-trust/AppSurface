using System.Data;
using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>Documents the limits of the handoff fence when application callbacks violate their contract.</summary>
public sealed class PostgreSqlDurableHandoffMisuseTests
{
    private const string SideEffectTable = "public.issue869_handoff_misuse_effects";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackSideEffectsOutsideSuppliedTransactionSurviveDenialOrException(bool throwAfterSideEffects)
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync(
            command: $"handoff-callback-misuse-{throwAfterSideEffects}");
        await PostgreSqlDurableHandoffTestSupport.CreateLedgerAsync(lab);
        await using (var create = lab.Database.DataSource.CreateCommand(
            $"CREATE TABLE {SideEffectTable} (effect_key text PRIMARY KEY);"))
        {
            await create.ExecuteNonQueryAsync();
        }

        const string callerWrite = "before-callback";
        const string secondConnectionWrite = "second-connection";
        var callbackWrite = $"callback-{throwAfterSideEffects}";
        await using var secondDataSource = PostgreSqlDurableHandoffTestSupport.CreateDataSource(
            lab, "869-callback-misuse-second-connection");
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var callerPid = await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(connection, transaction);
        await InsertEffectAsync(connection, transaction, callerWrite);

        var fakeProvider = new LocalFakeProvider();
        var callbackFailure = new InvalidOperationException("intentional callback misuse failure");
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);

        // The callback deliberately escapes the supplied transaction. The fence can roll back its savepoint,
        // but it cannot sandbox arbitrary code or undo a committed write on another connection/provider.
        if (throwAfterSideEffects)
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
                {
                    await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, callbackWrite, token);
                    await WriteThroughSecondConnectionAsync(secondDataSource, callerPid, secondConnectionWrite, token);
                    fakeProvider.Invoke();
                    throw callbackFailure;
                }));

            Assert.Same(callbackFailure, thrown);
        }
        else
        {
            var result = await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
            {
                await PostgreSqlDurableHandoffTestSupport.InsertLedgerAsync(supplied, callbackWrite, token);
                await WriteThroughSecondConnectionAsync(secondDataSource, callerPid, secondConnectionWrite, token);
                fakeProvider.Invoke();
                return false;
            });

            Assert.True(result.IsSuccess, result.Problem?.Code);
            Assert.False(result.Value!.HandoffPrepared);
        }

        Assert.Equal(1, fakeProvider.Calls);
        Assert.Equal(1, await CountEffectAsync(connection, transaction, callerWrite));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(
            connection, transaction, callbackWrite));
        Assert.Equal(1, await CountEffectAsync(lab.Database.DataSource, secondConnectionWrite));

        await transaction.CommitAsync();

        Assert.Equal(1, await CountEffectAsync(lab.Database.DataSource, callerWrite));
        Assert.Equal(0, await PostgreSqlDurableHandoffTestSupport.CountLedgerAsync(lab, callbackWrite));
        Assert.Equal(1, await CountEffectAsync(lab.Database.DataSource, secondConnectionWrite));
        Assert.Equal(1, fakeProvider.Calls);
    }

    private static async Task WriteThroughSecondConnectionAsync(
        NpgsqlDataSource dataSource,
        int callerPid,
        string key,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Assert.NotEqual(callerPid, await PostgreSqlDurableHandoffTestSupport.GetBackendPidAsync(connection));
        await InsertEffectAsync(connection, transaction: null, key, cancellationToken);
    }

    private static async Task InsertEffectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {SideEffectTable} (effect_key) VALUES (@key);", connection, transaction);
        insert.Parameters.AddWithValue("key", key);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountEffectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string key)
    {
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM {SideEffectTable} WHERE effect_key = @key;", connection, transaction);
        count.Parameters.AddWithValue("key", key);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountEffectAsync(NpgsqlDataSource dataSource, string key)
    {
        await using var count = dataSource.CreateCommand(
            $"SELECT count(*) FROM {SideEffectTable} WHERE effect_key = @key;");
        count.Parameters.AddWithValue("key", key);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private sealed class LocalFakeProvider
    {
        internal int Calls { get; private set; }

        internal void Invoke() => Calls++;
    }
}
