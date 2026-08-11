using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlIntegrationTestDatabaseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateFromConnectionStringAsync_RejectsMissingConnectionStrings(string? connectionString)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await PostgreSqlIntegrationTestDatabase.CreateFromConnectionStringAsync(connectionString!));
    }

    [Fact]
    public async Task TryCreateAsync_UsesOneServerAndCreatesIsolatedDatabasesConcurrently()
    {
        var databases = await Task.WhenAll(
            PostgreSqlIntegrationTestDatabase.TryCreateAsync().AsTask(),
            PostgreSqlIntegrationTestDatabase.TryCreateAsync().AsTask());
        await using var first = databases[0];
        await using var second = databases[1];

        AssertSameServerWithDistinctDatabases(first.ConnectionString, second.ConnectionString);

        await using (var create = first.DataSource.CreateCommand("CREATE TABLE shared_server_probe (id integer NOT NULL);"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var probe = second.DataSource.CreateCommand(
            "SELECT to_regclass('public.shared_server_probe') IS NOT NULL;");
        Assert.False((bool)(await probe.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CreateFromConnectionStringAsync_UsesConfiguredServerAndCreatesAnIsolatedDatabase()
    {
        await using var source = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var configured = await PostgreSqlIntegrationTestDatabase.CreateFromConnectionStringAsync(source.ConnectionString);

        AssertSameServerWithDistinctDatabases(source.ConnectionString, configured.ConnectionString);
    }

    [Fact]
    public async Task TryCreateAsync_KeepsExistingDatabaseAvailableWhileAnotherIsDisposed()
    {
        var databases = await Task.WhenAll(
            PostgreSqlIntegrationTestDatabase.TryCreateAsync().AsTask(),
            PostgreSqlIntegrationTestDatabase.TryCreateAsync().AsTask());
        var disposing = databases[0];
        await using var surviving = databases[1];
        var disposed = false;
        try
        {
            var replacementTask = PostgreSqlIntegrationTestDatabase.TryCreateAsync().AsTask();
            await disposing.DisposeAsync();
            disposed = true;
            await using var replacement = await replacementTask;

            AssertSameServerWithDistinctDatabases(surviving.ConnectionString, replacement.ConnectionString);
            await using var command = surviving.DataSource.CreateCommand("SELECT 1;");
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        finally
        {
            if (!disposed)
            {
                await disposing.DisposeAsync();
            }
        }
    }

    private static void AssertSameServerWithDistinctDatabases(string firstConnectionString, string secondConnectionString)
    {
        var firstConnection = new NpgsqlConnectionStringBuilder(firstConnectionString);
        var secondConnection = new NpgsqlConnectionStringBuilder(secondConnectionString);

        Assert.Equal(firstConnection.Host, secondConnection.Host);
        Assert.Equal(firstConnection.Port, secondConnection.Port);
        Assert.Equal(firstConnection.Username, secondConnection.Username);
        Assert.NotEqual(firstConnection.Database, secondConnection.Database);
    }
}
