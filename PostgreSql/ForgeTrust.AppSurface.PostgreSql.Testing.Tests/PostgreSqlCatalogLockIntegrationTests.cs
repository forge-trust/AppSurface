using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

public sealed class PostgreSqlCatalogLockIntegrationTests
{
    [Fact]
    public async Task RunFreshAsync_ReportsLostCatalogLockAfterCreateAndCleansDatabase()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
            SearchPath = "pg_catalog,public",
        }.ConnectionString;
        var schemaName = PostgreSqlTestServer.NewPrefix("catalog_lock_fault");
        var quotedSchemaName = PostgreSqlCatalog.QuoteIdentifier(schemaName);
        var databaseNamePrefix = PostgreSqlTestServer.NewPrefix("catalog_lock_db");
        var ownedDatabasePrefix = databaseNamePrefix[..Math.Min(databaseNamePrefix.Length, 46)];

        try
        {
            await PostgreSqlTestServer.ExecuteAsync(maintenanceConnectionString, $"""
                CREATE SCHEMA {quotedSchemaName};
                CREATE TABLE {quotedSchemaName}.unlock_observations (
                    observation_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    owned_database_count integer NOT NULL);
                CREATE FUNCTION {quotedSchemaName}.pg_advisory_lock(bigint)
                    RETURNS void
                    LANGUAGE plpgsql
                    AS $function$
                    BEGIN
                        RETURN;
                    END;
                    $function$;
                CREATE FUNCTION {quotedSchemaName}.pg_advisory_unlock(bigint)
                    RETURNS boolean
                    LANGUAGE plpgsql
                    AS $function$
                    DECLARE observed_count integer;
                    BEGIN
                        SELECT count(*)::integer
                        INTO observed_count
                        FROM pg_catalog.pg_database
                        WHERE left(datname, length('{ownedDatabasePrefix}')) = '{ownedDatabasePrefix}';
                        INSERT INTO {quotedSchemaName}.unlock_observations (owned_database_count)
                        VALUES (observed_count);
                        RETURN false;
                    END;
                    $function$;
                """);

            var injectedConnectionString = new NpgsqlConnectionStringBuilder(maintenanceConnectionString)
            {
                SearchPath = $"{schemaName},pg_catalog",
            }.ConnectionString;
            var actionInvoked = false;
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgreSqlFixture().RunFreshAsync(
                injectedConnectionString,
                databaseNamePrefix,
                (_, _) =>
                {
                    actionInvoked = true;
                    return Task.CompletedTask;
                }));

            Assert.Equal("The PostgreSQL fixture lost its catalog advisory lock.", failure.Message);
            Assert.False(actionInvoked);
            Assert.Empty(await GetDatabaseNamesWithPrefixAsync(maintenanceConnectionString, ownedDatabasePrefix));

            var databaseCountsAtUnlock = await PostgreSqlTestServer.ReadScalarAsync<int[]>(
                maintenanceConnectionString,
                $"SELECT array_agg(owned_database_count ORDER BY observation_id) FROM {quotedSchemaName}.unlock_observations;");
            Assert.Equal(new[] { 1, 0 }, databaseCountsAtUnlock);
        }
        finally
        {
            foreach (var databaseName in await GetDatabaseNamesWithPrefixAsync(maintenanceConnectionString, ownedDatabasePrefix))
            {
                await DropDatabaseAsync(maintenanceConnectionString, databaseName);
            }

            await PostgreSqlTestServer.ExecuteAsync(
                maintenanceConnectionString,
                $"DROP SCHEMA IF EXISTS {quotedSchemaName} CASCADE;");
        }
    }

    private static async Task<IReadOnlyList<string>> GetDatabaseNamesWithPrefixAsync(string connectionString, string prefix)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT datname FROM pg_catalog.pg_database WHERE left(datname, length(@prefix)) = @prefix ORDER BY datname;",
            connection);
        command.Parameters.AddWithValue("prefix", prefix);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var quotedDatabaseName = PostgreSqlCatalog.QuoteIdentifier(databaseName);
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {quotedDatabaseName} WITH (FORCE);",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}
