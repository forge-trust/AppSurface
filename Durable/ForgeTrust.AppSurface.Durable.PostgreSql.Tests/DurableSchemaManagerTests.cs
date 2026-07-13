using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableSchemaManagerTests
{
    [Fact]
    public void MigrationCatalog_LoadsContiguousNumberedResources()
    {
        var migrations = DurablePostgreSqlMigrationCatalog.Load();

        Assert.Equal(
            Enumerable.Range(1, migrations.Count),
            migrations.Select(migration => migration.Version));
        Assert.All(migrations, migration => Assert.Equal(64, migration.Sha256.Length));
        Assert.Equal("initial_work_protocol", migrations[0].Name);
        Assert.Equal("row_level_security", migrations[1].Name);
        Assert.Equal("durable_flow_protocol", migrations[2].Name);
        Assert.Equal("schedule_protocol", migrations[3].Name);
        Assert.Equal("runtime_health", migrations[4].Name);
    }

    [Fact]
    public void MigrationFive_DefinesPayloadFreeGlobalWorkerHeartbeat()
    {
        var migration = Assert.Single(
            DurablePostgreSqlMigrationCatalog.Load(),
            item => item.Version == 5);

        Assert.Contains("CREATE TABLE appsurface_durable.runtime_heartbeat", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("last_successful_sweep_at", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("draining boolean", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("pass_active boolean", migration.Sql, StringComparison.Ordinal);
        Assert.Contains("runtime_epoch uuid", migration.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("scope_id", migration.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", migration.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateScript_RendersExplicitOrderedTransactionsAndRls()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);

        var script = manager.GenerateScript();

        Assert.Contains("-- Migration 0001_initial_work_protocol", script, StringComparison.Ordinal);
        Assert.Contains("-- Migration 0002_row_level_security", script, StringComparison.Ordinal);
        Assert.Contains("-- Migration 0003_durable_flow_protocol", script, StringComparison.Ordinal);
        Assert.Contains("-- Migration 0004_schedule_protocol", script, StringComparison.Ordinal);
        Assert.Contains("-- Migration 0005_runtime_health", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("0001_initial_work_protocol", StringComparison.Ordinal) <
            script.IndexOf("0002_row_level_security", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("0003_durable_flow_protocol", StringComparison.Ordinal) <
            script.IndexOf("0004_schedule_protocol", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("0004_schedule_protocol", StringComparison.Ordinal) <
            script.IndexOf("0005_runtime_health", StringComparison.Ordinal));
        Assert.Contains("pg_advisory_lock", script, StringComparison.Ordinal);
        Assert.Contains("FORCE ROW LEVEL SECURITY", script, StringComparison.Ordinal);
        Assert.Contains("current_setting('appsurface_durable.scope_id', true)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateScript_FromInstalledVersion_OnlyIncludesPendingMigrations()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);

        var script = manager.GenerateScript(1);
        var emptyScript = manager.GenerateScript(PostgreSqlDurableRuntimeSchemaManager.RequiredVersion);

        Assert.DoesNotContain("0001_initial_work_protocol", script, StringComparison.Ordinal);
        Assert.Contains("0002_row_level_security", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-- Migration", emptyScript, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => manager.GenerateScript(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => manager.GenerateScript(PostgreSqlDurableRuntimeSchemaManager.RequiredVersion + 1));
    }

    [Fact]
    public async Task ApplyAsync_InstallsSchemaAndIsIdempotent_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();

        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);

        var missing = await manager.GetStatusAsync();
        var first = await manager.ApplyAsync();
        var compatible = await manager.GetStatusAsync();
        var second = await manager.ApplyAsync();
        await manager.ValidateAsync();

        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, missing.Compatibility);
        Assert.Equal(
            Enumerable.Range(1, PostgreSqlDurableRuntimeSchemaManager.RequiredVersion),
            first.AppliedVersions);
        Assert.Equal(PostgreSqlDurableRuntimeSchemaManager.RequiredVersion, compatible.InstalledVersion);
        Assert.Equal(1, compatible.MinimumReaderVersion);
        Assert.Equal(compatible.InstalledVersion, compatible.MaximumReaderVersion);
        Assert.Equal(1, compatible.MinimumWriterVersion);
        Assert.Equal(compatible.InstalledVersion, compatible.MaximumWriterVersion);
        Assert.True(compatible.IsCompatible);
        Assert.Empty(second.AppliedVersions);
    }

    [Fact]
    public async Task GetStatusAsync_AllowsOlderRuntimeInsideNewerAdditiveStoreRanges_AndRejectsExcludedRuntime()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var runtimeVersion = PostgreSqlDurableRuntimeSchemaManager.RequiredVersion;
        var additiveVersion = runtimeVersion + 1;
        await using (var command = database.DataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.schema_migration (version, name, sha256)
            VALUES (@version, 'future_additive', repeat('f', 64));
            UPDATE appsurface_durable.store_metadata
            SET schema_version = @version,
                minimum_reader_version = 1,
                maximum_reader_version = @version,
                minimum_writer_version = 1,
                maximum_writer_version = @version;
            """))
        {
            command.Parameters.AddWithValue("version", additiveVersion);
            await command.ExecuteNonQueryAsync();
        }

        var compatible = await manager.GetStatusAsync();
        await manager.ValidateAsync();
        var writer = new PostgreSqlDurableWorkTransactionWriter(
            database.DataSource,
            Guid.NewGuid(),
            PostgreSqlTestWorkContracts.CreateRegistry(new PostgreSqlTestWorkContract(
                "tests.schema-range",
                "v1",
                DurableProviderSafety.Idempotent,
                "tests.schema-range",
                "v1",
                DurableDataClassification.Operational)),
            sendWakeNotification: false);
        var client = new PostgreSqlDurableWorkClient(database.DataSource, writer);
        var acceptance = await client.EnqueueAsync(CreateSchemaRequest());

        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, compatible.Compatibility);
        Assert.Equal(additiveVersion, compatible.InstalledVersion);
        Assert.Equal(additiveVersion, compatible.MaximumReaderVersion);
        Assert.True(acceptance.IsSuccess);

        await using (var command = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.store_metadata
            SET minimum_reader_version = @excluded_version,
                minimum_writer_version = @excluded_version;
            """))
        {
            command.Parameters.AddWithValue("excluded_version", additiveVersion);
            await command.ExecuteNonQueryAsync();
        }

        var excluded = await manager.GetStatusAsync();
        await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () => await manager.ValidateAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, excluded.Compatibility);
    }

    [Fact]
    public async Task GetStatusAsync_RequiresPendingKnownMigrationsForOlderStore()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var first = DurablePostgreSqlMigrationCatalog.Load()[0];
        await using (var transaction = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = transaction.CreateCommand();
            command.CommandText = $"""
                {first.Sql}
                INSERT INTO appsurface_durable.schema_migration (version, name, sha256)
                VALUES (1, @name, @sha256);
                UPDATE appsurface_durable.store_metadata
                SET schema_version = 1,
                    minimum_reader_version = 1,
                    maximum_reader_version = 1,
                    minimum_writer_version = 1,
                    maximum_writer_version = 1;
                """;
            command.Parameters.AddWithValue("name", first.Name);
            command.Parameters.AddWithValue("sha256", first.Sha256);
            await command.ExecuteNonQueryAsync();
        }

        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        var status = await manager.GetStatusAsync();

        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, status.Compatibility);
        Assert.Equal(1, status.InstalledVersion);
        Assert.Equal(
            Enumerable.Range(2, PostgreSqlDurableRuntimeSchemaManager.RequiredVersion - 1),
            status.PendingVersions);
    }

    [Fact]
    public async Task GetStatusAsync_DetectsModifiedMigrationHistory_WhenPostgreSqlIsConfigured()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();

        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using (var command = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('0', 64) WHERE version = 1;"))
        {
            await command.ExecuteNonQueryAsync();
        }

        var status = await manager.GetStatusAsync();
        var exception = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await manager.ValidateAsync());

        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, status.Compatibility);
        Assert.Contains("does not match", status.Problem, StringComparison.Ordinal);
        Assert.Equal(status.Compatibility, exception.Status.Compatibility);
        Assert.StartsWith(DurableProblemCodes.SchemaInconsistent, exception.Message, StringComparison.Ordinal);
    }

    private static DurableWorkRequest CreateSchemaRequest() =>
        new(
            new DurableScopeId("schema-range-scope"),
            new DurableCommandId("schema-range-command"),
            "schema-range-retry",
            "tests.schema-range",
            "v1",
            new DurableEncodedPayload(
                "tests.schema-range",
                "v1",
                DurableDataClassification.Operational,
                System.Text.Encoding.UTF8.GetBytes("payload")),
            DurableProviderSafety.Idempotent);
}
