using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

public sealed class PostgreSqlCatalogTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[2001:db8::1]")]
    public void ServerEndpoint_MatchesInlineAndSeparatePortSyntax(string host)
    {
        var inlinePort = PostgreSqlCatalog.ServerEndpoint($"Host={host}:55432;Database=postgres");
        var separatePort = PostgreSqlCatalog.ServerEndpoint($"Host={host};Port=55432;Database=postgres");
        var differentPort = PostgreSqlCatalog.ServerEndpoint($"Host={host};Port=55433;Database=postgres");

        Assert.True(inlinePort.Matches(separatePort));
        Assert.False(inlinePort.Matches(differentPort));
    }

    [Fact]
    public void MaintenanceConnectionString_DisablesMultiplexingAndPreservesPooling()
    {
        var input = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "application",
            Pooling = true,
            Multiplexing = true,
        }.ConnectionString;

        var maintenance = new NpgsqlConnectionStringBuilder(PostgreSqlCatalog.MaintenanceConnectionString(input));

        Assert.Equal("postgres", maintenance.Database);
        Assert.True(maintenance.Pooling);
        Assert.False(maintenance.Multiplexing);

        var target = new NpgsqlConnectionStringBuilder(PostgreSqlCatalog.Target(input, "fixture", disablePooling: true));
        Assert.Equal("fixture", target.Database);
        Assert.False(target.Pooling);
        Assert.False(target.Multiplexing);

        var ordinaryTarget = new NpgsqlConnectionStringBuilder(PostgreSqlCatalog.Target(input, "fixture"));
        Assert.True(ordinaryTarget.Pooling);
        Assert.True(ordinaryTarget.Multiplexing);
    }

    [Fact]
    public void MaintenanceConnectionString_PreservesConfiguredUnixSocketPath()
    {
        const string socketPath = "/var/run/postgresql";
        var input = new NpgsqlConnectionStringBuilder
        {
            Host = socketPath,
            Port = 55432,
            Database = "application",
            Pooling = true,
            Multiplexing = true,
        }.ConnectionString;

        var maintenance = new NpgsqlConnectionStringBuilder(PostgreSqlCatalog.MaintenanceConnectionString(input));
        var target = new NpgsqlConnectionStringBuilder(PostgreSqlCatalog.Target(input, "fixture", disablePooling: true));

        Assert.Equal(socketPath, maintenance.Host);
        Assert.Equal(55432, maintenance.Port);
        Assert.Equal(socketPath, target.Host);
        Assert.Equal(55432, target.Port);
        Assert.True(PostgreSqlCatalog.ServerEndpoint(input).Matches(
            PostgreSqlCatalog.ServerEndpoint(PostgreSqlCatalog.MaintenanceConnectionString(input))));
        Assert.True(PostgreSqlCatalog.ServerEndpoint(input).Matches(
            PostgreSqlCatalog.ServerEndpoint(PostgreSqlCatalog.Target(input, "fixture", disablePooling: true))));
    }

    [SkippableFact]
    public async Task CatalogLock_PreservesSqlErrorWhenAbortedTransactionRejectsUnlock()
    {
        var settings = PostgreSqlCatalog.MaintenanceConnectionString(await PostgreSqlTestServer.GetRequiredConnectionStringAsync());
        await using var admin = new NpgsqlConnection(settings);
        await admin.OpenAsync();
        await using var transaction = await admin.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => PostgreSqlCatalog.WithDatabaseCatalogLockAsync(
            admin,
            async () =>
            {
                await using var command = new NpgsqlCommand("SELECT 1 / 0;", admin, transaction);
                await command.ExecuteScalarAsync();
            },
            CancellationToken.None));

        Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
    }

    [SkippableFact]
    public async Task WithDatabaseCatalogLockAsync_PreservesOperationFailureWhenUnlockReturnsFalse()
    {
        var connectionString = PostgreSqlCatalog.MaintenanceConnectionString(
            await PostgreSqlTestServer.GetRequiredConnectionStringAsync());
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        var operationFailure = new PostgreSqlDatabaseOwnershipException(
            "Database fixture_target has uncertain identity after CREATE. Preserve the target and reconcile it on the disposable server.");

        var observedFailure = await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() =>
            PostgreSqlCatalog.WithDatabaseCatalogLockAsync(admin, async () =>
            {
                await using var releaseAll = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock_all();", admin);
                await releaseAll.ExecuteNonQueryAsync();
                throw operationFailure;
            }, CancellationToken.None));

        Assert.Same(operationFailure, observedFailure);
    }

    [SkippableFact]
    public async Task Lease_DropsDatabaseUsingConfiguredConnectionStringAndInlinePortWhenTcp()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var leaseConnectionString = connectionString;
        await using (var endpointConnection = new NpgsqlConnection(connectionString))
        {
            await endpointConnection.OpenAsync();
            if (endpointConnection.DataSource.StartsWith("tcp://", StringComparison.Ordinal))
            {
                var host = endpointConnection.Host!;
                if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
                {
                    host = $"[{host}]";
                }

                leaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Host = $"{host}:{endpointConnection.Port}",
                    Port = NpgsqlConnection.DefaultPort,
                }.ConnectionString;
            }
        }

        await using var lease = await PostgreSqlDatabaseLease.CreateAsync(
            leaseConnectionString,
            PostgreSqlTestServer.NewPrefix("inline_port_lease"));
        await lease.DropAsync();

        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(leaseConnectionString, lease.Name));
    }
}
