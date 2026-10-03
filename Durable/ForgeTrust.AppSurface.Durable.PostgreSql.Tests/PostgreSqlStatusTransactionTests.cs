using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlStatusTransactionTests
{
    [Theory]
    [InlineData(false, "", DurableRuntimeSchemaCompatibility.Missing)]
    [InlineData(false, "CREATE SCHEMA appsurface_durable;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "DELETE FROM appsurface_durable.store_metadata;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "ALTER TABLE appsurface_durable.store_metadata DROP CONSTRAINT store_metadata_store_id_check; UPDATE appsurface_durable.store_metadata SET store_id = '00000000-0000-0000-0000-000000000000';", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "UPDATE appsurface_durable.store_metadata SET schema_version = 1;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "UPDATE appsurface_durable.store_metadata SET minimum_reader_version = 12, maximum_reader_version = 12;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "UPDATE appsurface_durable.schema_migration SET name = 'renamed' WHERE version = 1;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('0', 64) WHERE version = 11;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "DELETE FROM appsurface_durable.schema_migration WHERE version = 1;", DurableRuntimeSchemaCompatibility.Inconsistent)]
    [InlineData(true, "DELETE FROM appsurface_durable.schema_migration WHERE version = 11; UPDATE appsurface_durable.store_metadata SET schema_version = 10, minimum_reader_version = 10, maximum_reader_version = 10, minimum_writer_version = 10, maximum_writer_version = 10;", DurableRuntimeSchemaCompatibility.UpgradeRequired)]
    [InlineData(true, "UPDATE appsurface_durable.store_metadata SET minimum_reader_version = 1, maximum_reader_version = 1, minimum_writer_version = 1, maximum_writer_version = 1;", DurableRuntimeSchemaCompatibility.StoreTooNew)]
    public async Task ReadStatusInTransactionAsync_PreservesEveryCompatibilityClassification(
        bool install, string mutation, DurableRuntimeSchemaCompatibility expected)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        if (install)
        {
            await manager.ApplyAsync();
        }
        if (mutation.Length != 0)
        {
            await using var mutate = database.DataSource.CreateCommand(mutation);
            await mutate.ExecuteNonQueryAsync();
        }

        var publicStatus = await manager.GetStatusAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY;", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync();
        }
        var snapshotStatus = await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        Assert.Equal(expected, snapshotStatus.Compatibility);
        AssertStatusEqual(publicStatus, snapshotStatus);
        Assert.Same(connection, transaction.Connection);
        if (expected == DurableRuntimeSchemaCompatibility.UpgradeRequired)
        {
            Assert.Equal([11], snapshotStatus.PendingVersions);
            Assert.Equal(10, snapshotStatus.InstalledVersion);
        }
        await transaction.CommitAsync();
        AssertStatusEqual(publicStatus, await manager.GetStatusAsync());
    }

    [Fact]
    public async Task ReadStatusInTransactionAsync_RejectsDuplicateMetadataWithoutEndingCallerTransaction()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using (var duplicate = database.DataSource.CreateCommand(
            "ALTER TABLE appsurface_durable.store_metadata DROP CONSTRAINT store_metadata_pkey; INSERT INTO appsurface_durable.store_metadata SELECT * FROM appsurface_durable.store_metadata;"))
        {
            await duplicate.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await manager.GetStatusAsync());
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None));
        Assert.Same(connection, transaction.Connection);
        await using var stillActive = new NpgsqlCommand("SELECT 1;", connection, transaction);
        Assert.Equal(1, await stillActive.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadStatusInTransactionAsync_MatchesPublicStatusAndLeavesTransactionForCaller()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();

        var publicStatus = await manager.GetStatusAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var transactionStatus = await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        AssertStatusEqual(publicStatus, transactionStatus);
        Assert.Same(connection, transaction.Connection);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);

        var changedEpoch = Guid.NewGuid();
        await using (var updateEpoch = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.store_metadata SET active_runtime_epoch = @epoch WHERE singleton;"))
        {
            updateEpoch.Parameters.AddWithValue("epoch", changedEpoch);
            Assert.Equal(1, await updateEpoch.ExecuteNonQueryAsync());
        }

        var repeatedSnapshotStatus = await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None);
        AssertStatusEqual(transactionStatus, repeatedSnapshotStatus);
        Assert.Null(repeatedSnapshotStatus.ActiveRuntimeEpoch);

        await using (var createProbe = new NpgsqlCommand(
            "CREATE TEMP TABLE status_transaction_ownership_probe (value integer NOT NULL);",
            connection,
            transaction))
        {
            await createProbe.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
        Assert.Same(connection, transaction.Connection);

        await using var verifyRollback = new NpgsqlCommand(
            "SELECT to_regclass('pg_temp.status_transaction_ownership_probe') IS NULL;",
            connection);
        Assert.True((bool)(await verifyRollback.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReadStatusInTransactionAsync_UsesTheSameMigrationHistoryChecksumValidation()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using (var corruptHistory = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('0', 64) WHERE version = 1;"))
        {
            Assert.Equal(1, await corruptHistory.ExecuteNonQueryAsync());
        }

        var publicStatus = await manager.GetStatusAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var transactionStatus = await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        AssertStatusEqual(publicStatus, transactionStatus);
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, transactionStatus.Compatibility);
        Assert.Contains("does not match the package resource", transactionStatus.Problem, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadStatusInTransactionAsync_RejectsMissingClosedForeignAndCompletedInputs()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var otherConnection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await manager.ReadStatusInTransactionAsync(null!, transaction, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await manager.ReadStatusInTransactionAsync(connection, null!, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadStatusInTransactionAsync(new NpgsqlConnection(), transaction, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.ReadStatusInTransactionAsync(otherConnection, transaction, CancellationToken.None));

        await transaction.CommitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None));
    }

    [Fact]
    public async Task ReadStatusInTransactionAsync_PropagatesCancellationWithoutTakingTransactionOwnership()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.ReadStatusInTransactionAsync(connection, transaction, cancellation.Token));

        Assert.Same(connection, transaction.Connection);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await transaction.RollbackAsync();
    }

    private static void AssertStatusEqual(DurableRuntimeSchemaStatus expected, DurableRuntimeSchemaStatus actual)
    {
        Assert.Equal(expected.Compatibility, actual.Compatibility);
        Assert.Equal(expected.StoreId, actual.StoreId);
        Assert.Equal(expected.ActiveRuntimeEpoch, actual.ActiveRuntimeEpoch);
        Assert.Equal(expected.InstalledVersion, actual.InstalledVersion);
        Assert.Equal(expected.RequiredVersion, actual.RequiredVersion);
        Assert.Equal(expected.MinimumReaderVersion, actual.MinimumReaderVersion);
        Assert.Equal(expected.MaximumReaderVersion, actual.MaximumReaderVersion);
        Assert.Equal(expected.MinimumWriterVersion, actual.MinimumWriterVersion);
        Assert.Equal(expected.MaximumWriterVersion, actual.MaximumWriterVersion);
        Assert.Equal(expected.AppliedVersions, actual.AppliedVersions);
        Assert.Equal(expected.PendingVersions, actual.PendingVersions);
        Assert.Equal(expected.Problem, actual.Problem);
    }
}
