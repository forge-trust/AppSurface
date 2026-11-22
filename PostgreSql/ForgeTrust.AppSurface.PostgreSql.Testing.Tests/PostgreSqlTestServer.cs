using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

internal static class PostgreSqlTestServer
{
    internal const string ConnectionEnvironmentVariable = "APPSURFACE_POSTGRES_TEST_CONNECTION";
    private const int MinimumSupportedServerVersion = 150000;
    private const string PostgreSqlImage =
        "postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302";
    private static readonly SemaphoreSlim SharedContainerGate = new(1, 1);
    private static Task<PostgreSqlContainer>? sharedContainerTask;

    internal static async Task<string> GetRequiredConnectionStringAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            await VerifyServerAsync(connectionString);
            return connectionString;
        }

        var container = await GetSharedContainerAsync();
        return container.GetConnectionString();
    }

    private static async Task VerifyServerAsync(string connectionString)
    {
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 15,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SHOW server_version_num;", connection);
        var value = (string?)await command.ExecuteScalarAsync();
        EnsureSupportedServerVersion(value);
    }

    private static async Task<PostgreSqlContainer> GetSharedContainerAsync()
    {
        Task<PostgreSqlContainer> containerTask;
        await SharedContainerGate.WaitAsync();
        try
        {
            containerTask = sharedContainerTask ??= CreateSharedContainerAsync();
        }
        finally
        {
            SharedContainerGate.Release();
        }

        try
        {
            return await containerTask;
        }
        catch (SkipException)
        {
            throw;
        }
        catch
        {
            await SharedContainerGate.WaitAsync();
            try
            {
                if (ReferenceEquals(sharedContainerTask, containerTask))
                {
                    sharedContainerTask = null;
                }
            }
            finally
            {
                SharedContainerGate.Release();
            }

            throw;
        }
    }

    private static async Task<PostgreSqlContainer> CreateSharedContainerAsync()
    {
        var container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("appsurface_postgresql_testing")
            .WithUsername("appsurface")
            .WithPassword("appsurface-test-password")
            .WithCleanUp(true)
            .Build();

        try
        {
            await container.StartAsync();
            await VerifyServerAsync(container.GetConnectionString());
            return container;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await container.DisposeAsync();
            throw CreateContainerPrerequisiteException(exception);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    private static Exception CreateContainerPrerequisiteException(Exception exception)
    {
        var message =
            $"Real PostgreSQL 17 fixture tests require {ConnectionEnvironmentVariable} or an available Docker daemon.";
        var allowLocalSkip = string.Equals(
            Environment.GetEnvironmentVariable("APPSURFACE_POSTGRES_TEST_ALLOW_SKIP"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var runningInCi = string.Equals(
            Environment.GetEnvironmentVariable("CI"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (allowLocalSkip && !runningInCi)
        {
            return SkipException.ForSkip(message);
        }

        return new InvalidOperationException(
            $"{message} Set APPSURFACE_POSTGRES_TEST_ALLOW_SKIP=true only for an intentional local opt-out.",
            exception);
    }

    internal static void EnsureSupportedServerVersion(string? value)
    {
        if (!int.TryParse(value, out var version) || version < MinimumSupportedServerVersion)
        {
            throw new InvalidOperationException(
                "PostgreSQL fixture tests require server_version_num >= 150000 (PostgreSQL 15 or newer). "
                + $"The configured server reported '{value ?? "<null>"}'.");
        }
    }

    internal static string NewPrefix(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    internal static async Task<bool> DatabaseExistsAsync(string connectionString, string databaseName)
    {
        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @database_name);",
            connection);
        command.Parameters.AddWithValue("database_name", databaseName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<T> ReadScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task ExecuteAsync(string connectionString, string sql, string parameterName, string value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameterName, value);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task ExecuteAsync(string connectionString, string sql, string parameterName, int value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameterName, value);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<TemplateCleanupResult> CleanupAsync(PostgreSqlFixture fixture)
    {
        var result = await fixture.CleanupAllAsync();
        Assert.Equal(result.RegisteredTemplateCount, result.RemovedTemplateCount);
        Assert.Empty(result.Failures);
        Assert.Empty(result.IncompleteOutcomes);
        return result;
    }
}
