using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableHandoffCommitTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CT1CT2_PreparationBecomesVisibleOnlyThroughCallerCommit(bool commit)
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using (var create = lab.Database.DataSource.CreateCommand("CREATE TABLE issue869_commit_ledger(value text PRIMARY KEY);"))
            await create.ExecuteNonQueryAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var calls = 0;
        var result = await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
        {
            Assert.Same(transaction, supplied);
            calls++;
            await using var insert = new NpgsqlCommand("INSERT INTO issue869_commit_ledger VALUES ('prepared');", supplied.Connection, supplied);
            await insert.ExecuteNonQueryAsync(token);
            return true;
        });
        Assert.True(result.IsSuccess, result.Problem?.Code);
        Assert.True(result.Value!.HandoffPrepared);
        Assert.Equal(1, calls);
        Assert.Equal(0, await CountAsync(lab));
        if (commit) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
        Assert.Equal(commit ? 1 : 0, await CountAsync(lab));
    }

    [Fact]
    public async Task ExpiryAfterFinalValidationDoesNotRetroactivelyUndoCallerCommit()
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using (var create = lab.Database.DataSource.CreateCommand("CREATE TABLE issue869_commit_ledger(value text PRIMARY KEY);"))
            await create.ExecuteNonQueryAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var result = await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
        {
            await using var insert = new NpgsqlCommand("INSERT INTO issue869_commit_ledger VALUES ('prepared');", supplied.Connection, supplied);
            await insert.ExecuteNonQueryAsync(token);
            return true;
        });
        Assert.True(result.Value!.HandoffPrepared);
        await lab.Database.SetExecutionTimeAsync(lab.Permit.Claim.LeaseExpiresAtUtc.AddMinutes(1));
        Assert.Equal(0, await CountAsync(lab));
        await transaction.CommitAsync();
        Assert.Equal(1, await CountAsync(lab));
        // This proves database visibility only, never fresh invocation authority or permission to send after expiry.
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("rollback")]
    [InlineData("dispose")]
    public async Task CallbackCompletingCallerResourcesIsDetectedWithoutClaimingToUndoImproperCommit(string completion)
    {
        await using var lab = await PostgreSqlDurableHandoffTestLab.CreateAsync();
        await using (var create = lab.Database.DataSource.CreateCommand("CREATE TABLE issue869_commit_ledger(value text PRIMARY KEY);"))
            await create.ExecuteNonQueryAsync();
        await using var connection = await lab.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var invocation = lab.Fence.EnterInvocation(lab.Permit);
        var calls = 0;
        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            await lab.Fence.ExecuteAsync(transaction, async (supplied, token) =>
            {
                calls++;
                await using (var insert = new NpgsqlCommand("INSERT INTO issue869_commit_ledger VALUES ('prepared');", supplied.Connection, supplied))
                    await insert.ExecuteNonQueryAsync(token);
                switch (completion)
                {
                    case "commit": await supplied.CommitAsync(token); break;
                    case "rollback": await supplied.RollbackAsync(token); break;
                    case "dispose": await supplied.DisposeAsync(); break;
                    default: throw new ArgumentOutOfRangeException(nameof(completion));
                }
                return true;
            }));
        Assert.Contains(exception.InnerExceptions, static cause => cause is InvalidOperationException);
        Assert.Equal(1, calls);
        Assert.Equal(completion == "commit" ? 1 : 0, await CountAsync(lab));
    }

    private static async Task<long> CountAsync(PostgreSqlDurableHandoffTestLab lab)
    {
        await using var reader = lab.Database.DataSource.CreateCommand("SELECT count(*) FROM issue869_commit_ledger;");
        return (long)(await reader.ExecuteScalarAsync())!;
    }
}
