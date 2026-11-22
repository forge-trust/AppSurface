using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>Runs fixture DDL against the maintenance database. The two keys also coordinate with existing Skoolit hosts.</summary>
internal static class PostgreSqlCatalog
{
    private const long DatabaseCatalogLockKey = 5_300_219_492_281_001;
    private const long ClusterInitializationLockKey = 5_300_219_492_281_002;
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    internal static string Target(string connectionString, string name, bool disablePooling = false)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = name };
        if (disablePooling)
        {
            builder.Pooling = false;
        }

        return builder.ConnectionString;
    }

    internal static async Task WithClusterInitializationLockAsync(string connectionString, Func<CancellationToken, Task> action)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        await using var admin = new NpgsqlConnection(Target(connectionString, "postgres", disablePooling: true));
        await admin.OpenAsync(deadline.Token);
        await AcquireAsync(admin, ClusterInitializationLockKey, deadline.Token);
        await action(deadline.Token);
        // Closing this dedicated physical session releases the lock on every exit.
    }

    internal static async Task CreateAsync(string connectionString, string name, string? template, Action onCreateStarted, CancellationToken token)
    {
        await using var admin = new NpgsqlConnection(Target(connectionString, "postgres"));
        await admin.OpenAsync(token);
        if (template != null)
        {
            await TerminateSessionsAsync(admin, template, clientsOnly: false, token);
        }

        var command = template == null
            ? $"""CREATE DATABASE "{name}";"""
            : $"""CREATE DATABASE "{name}" TEMPLATE "{template}" STRATEGY FILE_COPY;""";
        await ExecuteLockedAsync(admin, command, token, onCreateStarted);
    }

    internal static async Task DropAsync(string connectionString, string name, CancellationToken token)
    {
        // A canceled CREATE may have broken its connection. Teardown checks out its own session.
        await using var admin = new NpgsqlConnection(Target(connectionString, "postgres"));
        await admin.OpenAsync(token);
        await TerminateSessionsAsync(admin, name, clientsOnly: true, token);
        await ExecuteLockedAsync(admin, $"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE);""", token);
    }

    private static async Task TerminateSessionsAsync(NpgsqlConnection admin, string name, bool clientsOnly, CancellationToken token)
    {
        const string predicate = "datname = CAST(@database AS name) AND pid <> pg_backend_pid()";
        var command = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE " + predicate
            + (clientsOnly ? " AND backend_type = 'client backend'" : "");
        await using var terminate = new NpgsqlCommand(command, admin);
        terminate.Parameters.AddWithValue("database", name);
        await terminate.ExecuteNonQueryAsync(token);
    }

    private static async Task AcquireAsync(NpgsqlConnection admin, long key, CancellationToken token)
    {
        await using var acquire = new NpgsqlCommand($"SELECT pg_advisory_lock({key});", admin)
        {
            CommandTimeout = (int)Timeout.TotalSeconds,
        };
        await acquire.ExecuteNonQueryAsync(token);
    }

    private static async Task ExecuteLockedAsync(NpgsqlConnection admin, string sql, CancellationToken token, Action? onCommandStarting = null)
    {
        var acquired = false;
        try
        {
            await AcquireAsync(admin, DatabaseCatalogLockKey, token);
            acquired = true;
            await using var command = new NpgsqlCommand(sql, admin);
            onCommandStarting?.Invoke();
            await command.ExecuteNonQueryAsync(token);
        }
        finally
        {
            // Cancellation can arrive after the server grants the lock. Always release before returning to the pool.
            if (admin.State == System.Data.ConnectionState.Open)
            {
                using var deadline = new CancellationTokenSource(Timeout);
                await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({DatabaseCatalogLockKey});", admin);
                if (await release.ExecuteScalarAsync(deadline.Token) is not true && acquired)
                {
                    throw new InvalidOperationException("The PostgreSQL fixture lost its catalog advisory lock.");
                }
            }
        }
    }
}
