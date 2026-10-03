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
    [InlineData(true, "DELETE FROM appsurface_durable.schema_migration;", DurableRuntimeSchemaCompatibility.Inconsistent)]
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
        var doctorStatus = await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        Assert.Equal(expected, snapshotStatus.Compatibility);
        AssertStatusEqual(publicStatus, snapshotStatus);
        AssertStatusEqual(snapshotStatus, doctorStatus);
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
        AssertStatusEqual(
            transactionStatus,
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));
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
        AssertStatusEqual(
            repeatedSnapshotStatus,
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));

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
        var doctorStatus = await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        AssertStatusEqual(publicStatus, transactionStatus);
        AssertStatusEqual(transactionStatus, doctorStatus);
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
            await manager.ReadDoctorStatusInTransactionAsync(null!, transaction, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await manager.ReadStatusInTransactionAsync(connection, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, null!, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadStatusInTransactionAsync(new NpgsqlConnection(), transaction, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(new NpgsqlConnection(), transaction, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.ReadStatusInTransactionAsync(otherConnection, transaction, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(otherConnection, transaction, CancellationToken.None));

        await transaction.CommitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadStatusInTransactionAsync(connection, transaction, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));
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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, cancellation.Token));

        Assert.Same(connection, transaction.Connection);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_AcceptsSixtyFourRowsAndPreservesAuthoritativeClassification()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await SeedHistoryThroughVersionAsync(database, 64);

        var publicStatus = await manager.GetStatusAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var doctorStatus = await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        AssertStatusEqual(publicStatus, doctorStatus);
        Assert.Equal(64, doctorStatus.AppliedVersions.Count);
        Assert.Equal(64, doctorStatus.InstalledVersion);
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, doctorStatus.Compatibility);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_BoundsExecutingCatalogAndPendingVersionsToSixtyFour()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var atLimitManager = new PostgreSqlDurableRuntimeSchemaManager(
            database.DataSource,
            CreateMigrations(64));
        var missingStatus = await atLimitManager.ReadDoctorStatusInTransactionAsync(
            connection,
            transaction,
            CancellationToken.None);

        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, missingStatus.Compatibility);
        Assert.Equal(64, missingStatus.PendingVersions.Count);

        var overLimitManager = new PostgreSqlDurableRuntimeSchemaManager(
            database.DataSource,
            CreateMigrations(65));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await overLimitManager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));

        Assert.Equal("The durable doctor schema status exceeded its bounded contract.", exception.Message);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    // Value: protects=doctor rejects a malformed executing catalog before database reads; fails_when=empty or noncanonical names are accepted; why_new=the existing bound test covers only catalog length; seam=PostgreSqlDurableRuntimeSchemaManager
    [Theory]
    [InlineData("")]
    [InlineData("not-canonical")]
    [InlineData("UPPERCASE")]
    [InlineData("migratiön")]
    public async Task ReadDoctorStatusInTransactionAsync_RejectsNoncanonicalExecutingCatalog(string name)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var catalog = CreateMigrations(1);
        var original = catalog[0];
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource,
            [original with { Name = name }]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));
        Assert.Equal("The durable doctor schema status exceeded its bounded contract.", exception.Message);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_RejectsEmptyExecutingCatalog()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));
        Assert.Equal("The durable doctor schema status exceeded its bounded contract.", exception.Message);
        Assert.Same(connection, transaction.Connection);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_RejectsTheSixtyFifthRowBeforeRetainingIt()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await SeedHistoryThroughVersionAsync(database, 65);

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));

        Assert.Equal("The durable doctor schema status exceeded its bounded contract.", exception.Message);
        Assert.Same(connection, transaction.Connection);
        await using var stillActive = new NpgsqlCommand("SELECT 1;", connection, transaction);
        Assert.Equal(1, await stillActive.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("UPDATE appsurface_durable.schema_migration SET name = repeat('x', 100000) WHERE version = 1;")]
    [InlineData("UPDATE appsurface_durable.schema_migration SET name = 'not-canonical-name' WHERE version = 1;")]
    [InlineData("UPDATE appsurface_durable.schema_migration SET name = 'migratiön' WHERE version = 1;")]
    public async Task ReadDoctorStatusInTransactionAsync_RejectsNamesOutsideTheCanonicalBound(string mutation)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using (var update = database.DataSource.CreateCommand(mutation))
        {
            await update.ExecuteNonQueryAsync();
        }

        await AssertDoctorContractFailureAsync(database, manager);
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_AcceptsTheMaximumCanonicalCatalogNameLength()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var maximumCanonicalNameLength = DurablePostgreSqlMigrationCatalog.Load()
            .Max(static migration => migration.Name.Length);
        await using (var update = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.schema_migration SET name = @name WHERE version = 1;"))
        {
            update.Parameters.AddWithValue("name", new string('x', maximumCanonicalNameLength));
            await update.ExecuteNonQueryAsync();
        }

        var publicStatus = await manager.GetStatusAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var doctorStatus = await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None);

        AssertStatusEqual(publicStatus, doctorStatus);
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, doctorStatus.Compatibility);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_RejectsAnOversizedDigestWithoutMaterializingIt()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using (var changeShape = database.DataSource.CreateCommand(
            "ALTER TABLE appsurface_durable.schema_migration DROP CONSTRAINT schema_migration_sha256_check; ALTER TABLE appsurface_durable.schema_migration ALTER COLUMN sha256 TYPE text;"))
        {
            await changeShape.ExecuteNonQueryAsync();
        }
        await using (var update = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('a', 100000) WHERE version = 1;"))
        {
            await update.ExecuteNonQueryAsync();
        }

        await AssertDoctorContractFailureAsync(database, manager);
    }

    [Fact]
    public async Task ReadDoctorStatusInTransactionAsync_RejectsNonLowercaseDigestText()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using (var changeShape = database.DataSource.CreateCommand(
            "ALTER TABLE appsurface_durable.schema_migration DROP CONSTRAINT schema_migration_sha256_check; ALTER TABLE appsurface_durable.schema_migration ALTER COLUMN sha256 TYPE text;"))
        {
            await changeShape.ExecuteNonQueryAsync();
        }
        await using (var update = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('A', 64) WHERE version = 1;"))
        {
            await update.ExecuteNonQueryAsync();
        }

        await AssertDoctorContractFailureAsync(database, manager);
    }

    private static async Task SeedHistoryThroughVersionAsync(PostgreSqlIntegrationTestDatabase database, int targetVersion)
    {
        await using (var insert = database.DataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.schema_migration (version, name, sha256)
            SELECT version, 'fixture_' || version::text, repeat('a', 64)
            FROM generate_series(12, @target_version) AS generated(version);
            """))
        {
            insert.Parameters.AddWithValue("target_version", targetVersion);
            await insert.ExecuteNonQueryAsync();
        }

        await using (var metadata = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.store_metadata
            SET schema_version = @target_version,
                minimum_reader_version = 12,
                maximum_reader_version = @target_version,
                minimum_writer_version = 12,
                maximum_writer_version = @target_version;
            """))
        {
            metadata.Parameters.AddWithValue("target_version", targetVersion);
            await metadata.ExecuteNonQueryAsync();
        }
    }

    private static IReadOnlyList<DurablePostgreSqlMigration> CreateMigrations(int count) =>
        Enumerable.Range(1, count)
            .Select(static version => new DurablePostgreSqlMigration(
                version,
                $"migration_{version}",
                string.Empty,
                new string('a', 64)))
            .ToArray();

    private static async Task AssertDoctorContractFailureAsync(
        PostgreSqlIntegrationTestDatabase database,
        PostgreSqlDurableRuntimeSchemaManager manager)
    {
        var existingStatus = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, existingStatus.Compatibility);

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.ReadDoctorStatusInTransactionAsync(connection, transaction, CancellationToken.None));
        Assert.Equal("The durable doctor schema status exceeded its bounded contract.", exception.Message);
        Assert.Same(connection, transaction.Connection);
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
