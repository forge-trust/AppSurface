using Npgsql;

namespace ForgeTrust.AppSurface.PostgreSql.Testing.Tests;

public sealed class PostgreSqlDatabaseLeaseTests
{
    [Fact]
    public async Task CreateAndCloneAsync_VerifiesIdentityCopiesContentsAndDropsByName()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(connectionString);
        var leasePrefix = PostgreSqlTestServer.NewPrefix("lease_parent");
        var clonePrefix = PostgreSqlTestServer.NewPrefix("lease_clone");
        PostgreSqlDatabaseLease? lease = null;
        PostgreSqlDatabaseLease? clone = null;
        string? leaseName = null;
        string? cloneName = null;

        try
        {
            lease = await PostgreSqlDatabaseLease.CreateAsync(connectionString, leasePrefix);
            leaseName = lease.Name;
            Assert.StartsWith(leasePrefix, lease.Name, StringComparison.Ordinal);
            Assert.NotEqual(0u, lease.Oid);
            Assert.Equal(lease.Name, new NpgsqlConnectionStringBuilder(lease.ConnectionString).Database);
            var templateConnection = new NpgsqlConnectionStringBuilder(lease.ConnectionString) { Pooling = false }.ConnectionString;
            await PostgreSqlTestServer.ExecuteAsync(
                templateConnection,
                "CREATE TABLE lease_probe (value integer NOT NULL); INSERT INTO lease_probe VALUES (37);");

            await using (var maintenance = new NpgsqlConnection(maintenanceConnectionString))
            {
                await maintenance.OpenAsync();
                Assert.True(await lease.ExistsAsync(maintenance));
            }

            await using (var connection = new NpgsqlConnection(templateConnection))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await lease.VerifyConnectedIdentityAsync(connection, transaction);
                var rejection = await Assert.ThrowsAsync<PostgresException>(() => lease.CloneAsync(clonePrefix));
                Assert.Equal(PostgresErrorCodes.ObjectInUse, rejection.SqlState);
                Assert.False(lease.HasRegisteredClones);
                await lease.VerifyConnectedIdentityAsync(connection, transaction);
            }

            var wrongDatabase = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = "postgres",
                Pooling = false,
            }.ConnectionString;
            await using (var connection = new NpgsqlConnection(wrongDatabase))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(
                    () => lease.VerifyConnectedIdentityAsync(connection, transaction));
            }

            clone = await lease.CloneAsync(clonePrefix);
            cloneName = clone.Name;
            Assert.StartsWith(clonePrefix, cloneName, StringComparison.Ordinal);
            Assert.NotEqual(lease.Oid, clone.Oid);
            Assert.Equal(37, await PostgreSqlTestServer.ReadScalarAsync<int>(clone.ConnectionString, "SELECT value FROM lease_probe;"));

            await clone.DropAsync();
            await clone.DisposeAsync();
            await clone.DisposeAsync();
            await using (var maintenance = new NpgsqlConnection(maintenanceConnectionString))
            {
                await maintenance.OpenAsync();
                Assert.False(await clone.ExistsAsync(maintenance));
            }

            clone = null;
            await lease.DropAsync();
            await lease.DisposeAsync();
            await using (var maintenance = new NpgsqlConnection(maintenanceConnectionString))
            {
                await maintenance.OpenAsync();
                Assert.False(await lease.ExistsAsync(maintenance));
            }

            lease = null;
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, cloneName));
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, leaseName));
        }
        finally
        {
            await DisposeLeasesAsync(clone, lease);
        }
    }

    [Fact]
    public async Task CreateNamedAsync_ReportsLostCatalogLockAndRemovesOwnedDatabase()
    {
        var connection = MaintenanceConnectionString(await PostgreSqlTestServer.GetRequiredConnectionStringAsync());
        var name = PostgreSqlDatabaseLease.NewName("lost_catalog_lock");
        uint? createdOid = null;
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PostgreSqlDatabaseLease.CreateNamedAsync(connection, name, async (admin, createdName, token) =>
                {
                    createdOid = await ReadDatabaseOidAsync(admin, createdName);
                    await using var release = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock_all();", admin);
                    await release.ExecuteNonQueryAsync(token);
                }, CancellationToken.None));

            Assert.NotNull(createdOid);
            Assert.Equal("The PostgreSQL fixture lost its catalog advisory lock.", failure.Message);
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connection, name));
        }
        finally
        {
            if (createdOid is uint oid && await PostgreSqlTestServer.DatabaseExistsAsync(connection, name))
            {
                await DropDatabaseIfOidMatchesAsync(connection, name, oid);
            }
        }
    }

    [Fact]
    public async Task CloneAsync_RejectsExternallyRemovedTemplate()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(connectionString);
        await using var lease = await PostgreSqlDatabaseLease.CreateAsync(
            connectionString,
            PostgreSqlTestServer.NewPrefix("lease_removed_template"));
        var clonePrefix = PostgreSqlTestServer.NewPrefix("lease_removed_clone");

        await using var maintenance = new NpgsqlConnection(maintenanceConnectionString);
        await maintenance.OpenAsync();
        await DropDatabaseUsingConnectionAsync(maintenance, lease.Name);

        Assert.False(await lease.ExistsAsync(maintenance));
        await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() => lease.CloneAsync(clonePrefix));
        Assert.Equal(0, await CountDatabasesWithPrefixAsync(maintenanceConnectionString, clonePrefix));
        Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, lease.Name));
    }

    [Fact]
    public async Task VerifyConnectedIdentityAsync_RejectsTransactionFromDifferentConnection()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        await using var lease = await PostgreSqlDatabaseLease.CreateAsync(
            connectionString,
            PostgreSqlTestServer.NewPrefix("lease_transaction_owner"));

        var templateConnectionString = new NpgsqlConnectionStringBuilder(lease.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        await using var suppliedConnection = new NpgsqlConnection(templateConnectionString);
        await using var transactionConnection = new NpgsqlConnection(templateConnectionString);
        await suppliedConnection.OpenAsync();
        await transactionConnection.OpenAsync();
        await using var transaction = await transactionConnection.BeginTransactionAsync();

        var rejection = await Assert.ThrowsAsync<ArgumentException>(
            () => lease.VerifyConnectedIdentityAsync(suppliedConnection, transaction));

        Assert.Equal("transaction", rejection.ParamName);
        await lease.VerifyConnectedIdentityAsync(transactionConnection, transaction);
    }

    [Fact]
    public async Task ExistsAsync_RejectsWrongServerBeforeOpeningConnection()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        await using var lease = await PostgreSqlDatabaseLease.CreateAsync(
            connectionString,
            PostgreSqlTestServer.NewPrefix("lease_wrong_server"));

        var wrongServerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "wrong-server.invalid",
            Pooling = false,
        }.ConnectionString;
        await using var wrongServer = new NpgsqlConnection(wrongServerConnectionString);

        await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() => lease.ExistsAsync(wrongServer));

        Assert.Equal(System.Data.ConnectionState.Closed, wrongServer.State);
    }

    [Fact]
    public async Task CreateAndCloneAsync_RejectsInvalidParametersAndMultiHostConnectionStrings()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var databasePrefix = PostgreSqlTestServer.NewPrefix("lease_invalid");
        var multiHostConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "localhost,127.0.0.1",
        }.ConnectionString;

        await Assert.ThrowsAsync<ArgumentException>(() => PostgreSqlDatabaseLease.CreateAsync(connectionString, " "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PostgreSqlDatabaseLease.CreateAsync(null!, databasePrefix));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PostgreSqlDatabaseLease.CreateAsync(multiHostConnectionString, databasePrefix));

        PostgreSqlDatabaseLease? lease = null;
        try
        {
            lease = await PostgreSqlDatabaseLease.CreateAsync(connectionString, databasePrefix);
            await Assert.ThrowsAsync<ArgumentException>(() => lease.CloneAsync(" "));

            var canceledClonePrefix = PostgreSqlTestServer.NewPrefix("cancel_clone");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.CloneAsync(canceledClonePrefix, cancellation.Token));
            Assert.Equal(0, await CountDatabasesWithPrefixAsync(MaintenanceConnectionString(connectionString), canceledClonePrefix));
        }
        finally
        {
            await DisposeLeasesAsync(null, lease);
        }
    }

    [Fact]
    public async Task CreateAsync_CancellationBeforeDispatchDoesNotCreateADatabase()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var prefix = PostgreSqlTestServer.NewPrefix("cancel_create");
        var hookInvoked = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostgreSqlDatabaseLease.CreateAsync(
            connectionString,
            prefix,
            (_, _) =>
            {
                hookInvoked = true;
                return ValueTask.CompletedTask;
            },
            cancellation.Token));

        Assert.False(hookInvoked);
        Assert.Equal(0, await CountDatabasesWithPrefixAsync(
            MaintenanceConnectionString(connectionString), prefix));
    }

    [Fact]
    public async Task ExistsAsync_RejectsRenamedDatabaseWhenItsOidStillExists()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(connectionString);
        var lease = await PostgreSqlDatabaseLease.CreateAsync(connectionString, PostgreSqlTestServer.NewPrefix("lease_renamed"));
        var renamedName = PostgreSqlTestServer.NewPrefix("lease_renamed_target");

        try
        {
            await PostgreSqlTestServer.ExecuteAsync(
                maintenanceConnectionString,
                $"ALTER DATABASE {QuoteIdentifier(lease.Name)} RENAME TO {QuoteIdentifier(renamedName)};");
            Assert.Equal(lease.Oid, await ReadDatabaseOidAsync(maintenanceConnectionString, renamedName));

            await using var maintenance = new NpgsqlConnection(maintenanceConnectionString);
            await maintenance.OpenAsync();
            await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() => lease.ExistsAsync(maintenance));
        }
        finally
        {
            try
            {
                await lease.DisposeAsync();
            }
            catch (PostgreSqlDatabaseOwnershipException)
            {
                await DropDatabaseIfOidMatchesAsync(maintenanceConnectionString, renamedName, lease.Oid);
            }
        }
    }

    [Fact]
    public async Task CloneAsync_StaleSourceLeavesReplacementDatabaseSessionsIntact()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(connectionString);
        var lease = await PostgreSqlDatabaseLease.CreateAsync(connectionString, PostgreSqlTestServer.NewPrefix("lease_stale_source"));
        var replacementName = lease.Name;
        var renamedName = PostgreSqlTestServer.NewPrefix("lease_stale_target");
        var clonePrefix = PostgreSqlTestServer.NewPrefix("lease_stale_clone");
        NpgsqlConnection? replacementSession = null;
        uint? replacementOid = null;

        try
        {
            await PostgreSqlTestServer.ExecuteAsync(
                maintenanceConnectionString,
                $"ALTER DATABASE {QuoteIdentifier(replacementName)} RENAME TO {QuoteIdentifier(renamedName)};");
            await PostgreSqlTestServer.ExecuteAsync(
                maintenanceConnectionString,
                $"CREATE DATABASE {QuoteIdentifier(replacementName)};");
            replacementOid = await ReadDatabaseOidAsync(maintenanceConnectionString, replacementName);

            replacementSession = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = replacementName,
                Pooling = false,
            }.ConnectionString);
            await replacementSession.OpenAsync();
            var backendPid = await ReadScalarAsync<int>(replacementSession, "SELECT pg_backend_pid();");

            await using (var maintenance = new NpgsqlConnection(maintenanceConnectionString))
            {
                await maintenance.OpenAsync();
                await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() => lease.ExistsAsync(maintenance));
            }

            await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() => lease.CloneAsync(clonePrefix));
            Assert.Equal(replacementName, await ReadScalarAsync<string>(replacementSession, "SELECT current_database();"));
            Assert.Equal(backendPid, await ReadScalarAsync<int>(replacementSession, "SELECT pg_backend_pid();"));
            Assert.Equal(1, await ReadScalarAsync<int>(replacementSession, "SELECT 1;"));
        }
        finally
        {
            if (replacementSession is not null)
            {
                await replacementSession.DisposeAsync();
            }

            try
            {
                await lease.DisposeAsync();
            }
            catch (PostgreSqlDatabaseOwnershipException)
            {
                if (replacementOid is uint oid)
                {
                    await DropDatabaseIfOidMatchesAsync(maintenanceConnectionString, replacementName, oid);
                }

                await DropDatabaseIfOidMatchesAsync(maintenanceConnectionString, renamedName, lease.Oid);
            }
        }
    }

    [Fact]
    public async Task CreateAsync_UnknownAcknowledgementPreservesTargetAndSanitizesDiagnostics()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(connectionString);
        const string sentinelPassword = "lease-diagnostic-secret";
        var diagnosticConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = sentinelPassword,
        }.ConnectionString;
        var prefix = PostgreSqlTestServer.NewPrefix("lease_unknown_ack");
        string? databaseName = null;
        uint? databaseOid = null;

        try
        {
            var failure = await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() =>
                PostgreSqlDatabaseLease.CreateAsync(connectionString, prefix, (name, _) =>
                {
                    databaseName = name;
                    return ValueTask.FromException(new NpgsqlException(
                        $"Simulated lost CREATE acknowledgement for {diagnosticConnectionString}."));
                }, CancellationToken.None));

            Assert.NotNull(databaseName);
            Assert.Contains(databaseName, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelPassword, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(diagnosticConnectionString, failure.ToString(), StringComparison.Ordinal);
            databaseOid = await ReadDatabaseOidAsync(maintenanceConnectionString, databaseName);
            Assert.NotEqual(0u, databaseOid);
        }
        finally
        {
            if (databaseName is not null && databaseOid is uint oid)
            {
                await DropDatabaseIfOidMatchesAsync(maintenanceConnectionString, databaseName, oid);
            }
        }
    }

    [Fact]
    public async Task CreateAsync_CancellationAfterDispatchCapturesAndDropsCreatedDatabase()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(connectionString);
        var prefix = PostgreSqlTestServer.NewPrefix("lease_cancel_after_dispatch");
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? databaseName = null;
        uint? databaseOid = null;
        Task<PostgreSqlDatabaseLease>? createTask = null;
        PostgreSqlDatabaseLease? createdLease = null;
        using var cancellation = new CancellationTokenSource();

        try
        {
            createTask = PostgreSqlDatabaseLease.CreateAsync(connectionString, prefix, async (name, _) =>
            {
                databaseName = name;
                databaseOid = await ReadDatabaseOidAsync(maintenanceConnectionString, name);
                dispatched.TrySetResult();
                await releaseHook.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }, cancellation.Token);

            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            releaseHook.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await createTask.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.NotNull(databaseName);
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, databaseName));
        }
        finally
        {
            releaseHook.TrySetResult();
            try
            {
                if (createTask is not null)
                {
                    try
                    {
                        createdLease = await createTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (createdLease is not null)
                {
                    await createdLease.DisposeAsync();
                }
            }
            finally
            {
                if (databaseName is not null && databaseOid is uint oid)
                {
                    await DropDatabaseIfOidMatchesAsync(maintenanceConnectionString, databaseName, oid);
                }
            }
        }
    }

    [Fact]
    public async Task DropAsync_FailedDropRetainsCloneRegistrationAndConcurrentRetryIsIdempotent()
    {
        var connectionString = await PostgreSqlTestServer.GetRequiredConnectionStringAsync();
        var lease = await PostgreSqlDatabaseLease.CreateAsync(connectionString, PostgreSqlTestServer.NewPrefix("lease_drop_parent"));
        PostgreSqlDatabaseLease? clone = null;
        var dropEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            clone = await lease.CloneAsync(PostgreSqlTestServer.NewPrefix("lease_drop_clone"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => clone.DropAsync(
                static _ => ValueTask.FromException(new InvalidOperationException("Injected drop failure.")),
                CancellationToken.None).AsTask());
            var parentBlocked = await Assert.ThrowsAsync<PostgreSqlDatabaseOwnershipException>(() => lease.DropAsync().AsTask());
            Assert.Contains("still has registered clones", parentBlocked.Message, StringComparison.OrdinalIgnoreCase);

            var cloneName = clone.Name;
            var leaseName = lease.Name;
            var retry = clone.DropAsync(async connection =>
            {
                dropEntered.TrySetResult();
                await releaseDrop.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await DropDatabaseUsingConnectionAsync(connection, cloneName);
            }, CancellationToken.None).AsTask();
            await dropEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var concurrentDrop = clone.DropAsync().AsTask();
            releaseDrop.TrySetResult();
            await Task.WhenAll(retry, concurrentDrop).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, cloneName));
            await lease.DropAsync();
            Assert.False(await PostgreSqlTestServer.DatabaseExistsAsync(connectionString, leaseName));
            clone = null;
        }
        finally
        {
            releaseDrop.TrySetResult();
            await DisposeLeasesAsync(clone, lease);
        }
    }

    private static string MaintenanceConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString;

    private static string QuoteIdentifier(string identifier)
    {
        using var commandBuilder = new NpgsqlCommandBuilder();
        return commandBuilder.QuoteIdentifier(identifier);
    }

    private static async Task<int> CountDatabasesWithPrefixAsync(string connectionString, string prefix)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM pg_catalog.pg_database WHERE left(datname, length(@prefix)) = @prefix;",
            connection);
        command.Parameters.AddWithValue("prefix", prefix);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<uint> ReadDatabaseOidAsync(string connectionString, string databaseName) =>
        await TryReadDatabaseOidAsync(connectionString, databaseName)
        ?? throw new InvalidOperationException($"Expected PostgreSQL database '{databaseName}' was absent.");

    private static async Task<uint?> TryReadDatabaseOidAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await ReadDatabaseOidAsync(connection, databaseName);
    }

    private static async Task<uint?> ReadDatabaseOidAsync(NpgsqlConnection connection, string databaseName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_database WHERE datname = @database_name;",
            connection);
        command.Parameters.AddWithValue("database_name", databaseName);
        var value = await command.ExecuteScalarAsync();
        return value is uint oid ? oid : null;
    }

    private static async Task DropDatabaseIfOidMatchesAsync(string connectionString, string databaseName, uint expectedOid)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var lockAcquired = false;
        try
        {
            await using (var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_lock(5300219492281001);", connection))
            {
                await acquire.ExecuteNonQueryAsync();
            }

            lockAcquired = true;
            if (await ReadDatabaseOidAsync(connection, databaseName) == expectedOid)
            {
                await DropDatabaseUsingConnectionAsync(connection, databaseName);
            }
        }
        finally
        {
            if (lockAcquired)
            {
                await using var release = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_unlock(5300219492281001);", connection);
                await release.ExecuteScalarAsync();
            }
        }
    }

    private static async ValueTask DropDatabaseUsingConnectionAsync(NpgsqlConnection connection, string databaseName)
    {
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE);",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ReadScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DisposeLeasesAsync(PostgreSqlDatabaseLease? child, PostgreSqlDatabaseLease? parent)
    {
        try
        {
            if (child is not null)
            {
                await child.DisposeAsync();
            }
        }
        finally
        {
            if (parent is not null)
            {
                await parent.DisposeAsync();
            }
        }
    }
}
