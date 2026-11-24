using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace ForgeTrust.AppSurface.PostgreSql.Testing;

/// <summary>Runs fixture database catalog operations under the shared PostgreSQL advisory locks.</summary>
internal static class PostgreSqlCatalog
{
    private const long DatabaseCatalogLockKey = 5_300_219_492_281_001;
    private const long ClusterInitializationLockKey = 5_300_219_492_281_002;
    private const int CommandTimeoutSeconds = 300;
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    internal static string Target(string connectionString, string name, bool disablePooling = false)
    {
        var builder = ParseConnectionString(connectionString);
        builder.Database = name;
        if (disablePooling)
        {
            builder.Pooling = false;
            builder.Multiplexing = false;
        }

        return builder.ConnectionString;
    }

    internal static string MaintenanceConnectionString(string connectionString)
    {
        var builder = ParseConnectionString(connectionString);
        builder.Database = "postgres";
        builder.Multiplexing = false;
        return builder.ConnectionString;
    }

    internal static void ValidateSingleServer(string connectionString)
    {
        var builder = ParseConnectionString(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new ArgumentException("PostgreSQL database leases require an explicit host.", nameof(connectionString));
        }

        if (builder.Host.Contains(',') || builder.LoadBalanceHosts)
        {
            throw new ArgumentException(
                "PostgreSQL database leases require a single-host connection without host load balancing.",
                nameof(connectionString));
        }
    }

    internal static async Task WithClusterInitializationLockAsync(string connectionString, Func<CancellationToken, Task> action)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        await using var admin = new NpgsqlConnection(Target(connectionString, "postgres", disablePooling: true));
        await admin.OpenAsync(deadline.Token);
        await AcquireAsync(admin, ClusterInitializationLockKey, deadline.Token);
        await action(deadline.Token);
        // Disposing this dedicated non-pooled session releases the cluster initialization lock.
    }

    internal static async Task<DatabaseCatalogIdentity> ReadDatabaseIdentityAsync(
        NpgsqlConnection connection,
        string name,
        uint oid,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT (SELECT oid FROM pg_catalog.pg_database WHERE datname = @name), " +
            "(SELECT datname FROM pg_catalog.pg_database WHERE oid = @oid);",
            connection,
            transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, oid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oidAtName = reader.IsDBNull(0) ? (uint?)null : reader.GetFieldValue<uint>(0);
        var nameAtOid = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new DatabaseCatalogIdentity(oidAtName, nameAtOid);
    }

    internal static async Task TerminateSessionsAsync(
        NpgsqlConnection admin,
        uint databaseOid,
        bool clientsOnly,
        CancellationToken cancellationToken)
    {
        var commandText = "SELECT pg_catalog.pg_terminate_backend(pid) " +
            "FROM pg_catalog.pg_stat_activity " +
            "WHERE datid = @oid AND pid <> pg_catalog.pg_backend_pid()" +
            (clientsOnly ? " AND backend_type = 'client backend'" : "") + ";";
        await using var command = new NpgsqlCommand(commandText, admin)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue("oid", NpgsqlDbType.Oid, databaseOid);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string QuoteIdentifier(string identifier)
    {
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(identifier);
    }

    internal static async Task WithDatabaseCatalogLockAsync(
        NpgsqlConnection admin,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        var operationCompleted = false;
        try
        {
            await AcquireAsync(admin, DatabaseCatalogLockKey, cancellationToken);
            await action();
            operationCompleted = true;
        }
        finally
        {
            if (admin.State == ConnectionState.Open)
            {
                using var deadline = new CancellationTokenSource(Timeout);
                try
                {
                    await using var release = new NpgsqlCommand($"SELECT pg_catalog.pg_advisory_unlock({DatabaseCatalogLockKey});", admin)
                    {
                        CommandTimeout = CommandTimeoutSeconds,
                    };
                    var unlocked = await release.ExecuteScalarAsync(deadline.Token) is true;
                    if (!unlocked && operationCompleted)
                    {
                        throw new InvalidOperationException("The PostgreSQL fixture lost its catalog advisory lock.");
                    }
                }
                catch when (!operationCompleted)
                {
                    // Preserve the operation error when releasing the lock also fails.
                }
            }
        }
    }

    private static async Task AcquireAsync(NpgsqlConnection admin, long key, CancellationToken cancellationToken)
    {
        await using var acquire = new NpgsqlCommand($"SELECT pg_catalog.pg_advisory_lock({key});", admin)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        await acquire.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlConnectionStringBuilder ParseConnectionString(string connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("The PostgreSQL connection string is invalid.", nameof(connectionString));
        }
        catch (FormatException)
        {
            throw new ArgumentException("The PostgreSQL connection string is invalid.", nameof(connectionString));
        }
    }

    internal static ServerIdentity ServerEndpoint(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        // Npgsql normalizes inline Host:Port syntax and retains Unix socket endpoints as paths.
        return new ServerIdentity(connection.DataSource);
    }

    internal readonly record struct DatabaseCatalogIdentity(uint? OidAtName, string? NameAtOid);

    internal readonly record struct ServerIdentity(string Endpoint)
    {
        internal bool Matches(ServerIdentity other) =>
            string.Equals(Endpoint, other.Endpoint, StringComparison.Ordinal);
    }
}
