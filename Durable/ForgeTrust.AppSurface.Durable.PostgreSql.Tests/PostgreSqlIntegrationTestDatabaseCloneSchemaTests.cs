using ForgeTrust.AppSurface.Durable.PostgreSql;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlIntegrationTestDatabaseCloneSchemaTests
{
    [Fact]
    public async Task PrepareTemplateAndActivateClones_RekeysAndIsolatesSiblingWrites()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var firstClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        await using var secondClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        var firstEpoch = Guid.NewGuid();
        var secondEpoch = Guid.NewGuid();

        var firstStatus = await firstClone.ActivateCloneAsync(templateStoreId, firstEpoch, "schema-tests", "sibling-isolation");
        var secondStatus = await secondClone.ActivateCloneAsync(templateStoreId, secondEpoch, "schema-tests", "sibling-isolation");

        Assert.NotEqual(Guid.Empty, templateStoreId);
        Assert.NotEqual(Guid.Empty, firstStatus.StoreId);
        Assert.NotEqual(Guid.Empty, secondStatus.StoreId);
        Assert.Equal(3, new[] { templateStoreId, firstStatus.StoreId, secondStatus.StoreId }.Distinct().Count());
        Assert.Equal(firstEpoch, firstStatus.ActiveRuntimeEpoch);
        Assert.Equal(secondEpoch, secondStatus.ActiveRuntimeEpoch);
        Assert.NotEqual(firstEpoch, secondEpoch);
        Assert.Null((await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).GetStatusAsync()).ActiveRuntimeEpoch);

        const string scopeId = "clone-isolation-only";
        await InsertScopeAsync(firstClone.DataSource, scopeId);

        Assert.Equal(1, await CountVisibleScopeAsync(firstClone.DataSource, scopeId));
        Assert.Equal(0, await CountVisibleScopeAsync(secondClone.DataSource, scopeId));
        Assert.Equal(0, await CountVisibleScopeAsync(template.DataSource, scopeId));
    }

    [Fact]
    public async Task CloneGuards_RejectWrongKindsAndCallerSuppliedTemplateIdentity()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, isTemplate: true);
        var templateStoreId = await template.PrepareTemplateAsync();
        await using var clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);

        var templateActivation = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await template.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "wrong-kind"));
        var clonePreparation = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await clone.PrepareTemplateAsync());
        var mismatchedIdentity = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await clone.ActivateCloneAsync(Guid.NewGuid(), Guid.NewGuid(), "schema-tests", "wrong-template-id"));

        Assert.Contains("registered clone", templateActivation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registered template", clonePreparation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source identity", mismatchedIdentity.Message, StringComparison.OrdinalIgnoreCase);
        var metadata = await ReadMetadataAsync(clone.DataSource);
        Assert.Equal(1, metadata.Count);
        Assert.Equal(templateStoreId, metadata.StoreId);
        Assert.Null(metadata.ActiveRuntimeEpoch);
    }

    [Fact]
    public async Task PrepareTemplate_RejectsActiveEpoch()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, isTemplate: true);
        var manager = new PostgreSqlDurableRuntimeSchemaManager(template.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        await manager.InitializeRuntimeEpochAsync(epoch, "schema-tests", "preexisting-template-epoch");

        var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await template.PrepareTemplateAsync());

        Assert.Contains("active runtime epoch", error.Message, StringComparison.OrdinalIgnoreCase);
        var status = await manager.GetStatusAsync();
        Assert.NotEqual(Guid.Empty, status.StoreId);
        Assert.Equal(epoch, status.ActiveRuntimeEpoch);
    }

    [Fact]
    public async Task CloneGuard_RejectsChecksumAndNonSingletonMetadataBeforeRekey()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var checksumClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        await using var metadataClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);

        await ExecuteNonQueryAsync(
            checksumClone.DataSource,
            "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('0', 64) WHERE version = 1;");
        var checksumError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await checksumClone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "checksum-mismatch"));
        Assert.Contains("compatible version", checksumError.Message, StringComparison.OrdinalIgnoreCase);
        var checksumMetadata = await ReadMetadataAsync(checksumClone.DataSource);
        Assert.Equal(templateStoreId, checksumMetadata.StoreId);
        Assert.Null(checksumMetadata.ActiveRuntimeEpoch);
        Assert.Equal(0, await ReadEpochHistoryCountAsync(checksumClone.DataSource));

        await ExecuteNonQueryAsync(
            metadataClone.DataSource,
            "ALTER TABLE appsurface_durable.store_metadata DROP CONSTRAINT store_metadata_pkey; " +
            "INSERT INTO appsurface_durable.store_metadata SELECT * FROM appsurface_durable.store_metadata WHERE singleton;");
        var metadataError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await metadataClone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "duplicate-metadata"));
        Assert.Contains("metadata", metadataError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await ReadMetadataAsync(metadataClone.DataSource)).Count);
        Assert.Equal(0, await ReadEpochHistoryCountAsync(metadataClone.DataSource));
    }

    [Fact]
    public async Task CloneGuard_RejectsActiveEpochAndVisibleRuntimeRowsWithoutChangingMetadata()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var activeClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        await using var populatedClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);

        var activeEpoch = Guid.NewGuid();
        await new PostgreSqlDurableRuntimeSchemaManager(activeClone.DataSource)
            .InitializeRuntimeEpochAsync(activeEpoch, "schema-tests", "preexisting-clone-epoch");
        var activeError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await activeClone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "active-epoch"));
        Assert.Contains("active runtime epoch", activeError.Message, StringComparison.OrdinalIgnoreCase);
        var activeMetadata = await ReadMetadataAsync(activeClone.DataSource);
        Assert.Equal(templateStoreId, activeMetadata.StoreId);
        Assert.Equal(activeEpoch, activeMetadata.ActiveRuntimeEpoch);
        Assert.Equal(1, await ReadEpochHistoryCountAsync(activeClone.DataSource));

        await ExecuteNonQueryAsync(
            populatedClone.DataSource,
            "INSERT INTO appsurface_durable.runtime_heartbeat " +
            "(worker_id, worker_instance_id, runtime_epoch, hosted_surfaces) " +
            "VALUES ('guard-runtime-row', gen_random_uuid(), gen_random_uuid(), 1);");
        var populatedError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await populatedClone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "runtime-row"));
        Assert.Contains("runtime rows", populatedError.Message, StringComparison.OrdinalIgnoreCase);
        var populatedMetadata = await ReadMetadataAsync(populatedClone.DataSource);
        Assert.Equal(templateStoreId, populatedMetadata.StoreId);
        Assert.Null(populatedMetadata.ActiveRuntimeEpoch);
        Assert.Equal(0, await ReadEpochHistoryCountAsync(populatedClone.DataSource));
    }

    [Fact]
    public async Task CloneGuard_RejectsRlsHiddenRowsWithRestrictedCredentialAndRedactsRelationNames()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var roleName = $"schema_guard_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder();
        var quotedRole = builder.QuoteIdentifier(roleName);
        await ExecuteNonQueryAsync(
            server.DataSource,
            $"CREATE ROLE {quotedRole} LOGIN PASSWORD '{password}' CREATEDB NOSUPERUSER NOCREATEROLE NOREPLICATION NOBYPASSRLS;");

        try
        {
            await using var template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, isTemplate: true);
            await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).ApplyAsync();
            var sentinelRelation = builder.QuoteIdentifier($"credential_{password}");
            var templateStoreId = await template.PrepareTemplateAsync();
            await ExecuteNonQueryAsync(
                template.DataSource,
                $"CREATE TABLE appsurface_durable.{sentinelRelation} (id integer PRIMARY KEY); " +
                $"GRANT USAGE ON SCHEMA appsurface_durable TO {quotedRole}; " +
                $"GRANT SELECT, INSERT ON appsurface_durable.scope, appsurface_durable.{sentinelRelation} TO {quotedRole}; " +
                $"GRANT SELECT ON ALL TABLES IN SCHEMA appsurface_durable TO {quotedRole};");

            await using var hiddenClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
            await using var namedClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
            await using var populatedClone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);

            var restrictedCloneConnectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString)
            {
                Database = new NpgsqlConnectionStringBuilder(hiddenClone.ConnectionString).Database,
                Username = roleName,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            await using var restrictedCloneDataSource = NpgsqlDataSource.Create(restrictedCloneConnectionString);
            await InsertScopeAsync(restrictedCloneDataSource, "restricted-hidden-row");
            Assert.Equal(0, await CountVisibleScopeAsync(restrictedCloneDataSource, "restricted-hidden-row", "other-scope"));
            var roleIdentity = await ReadRoleIdentityAsync(restrictedCloneDataSource);
            Assert.False(roleIdentity.IsSuperuser);
            Assert.False(roleIdentity.BypassesRowSecurity);

            var hiddenError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
                await hiddenClone.ActivateCloneAsync(
                    templateStoreId,
                    Guid.NewGuid(),
                    "schema-tests",
                    "hidden-row",
                    activationDataSource: restrictedCloneDataSource));
            Assert.Contains("runtime rows", hiddenError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(password, hiddenError.Message, StringComparison.Ordinal);
            var hiddenMetadata = await ReadMetadataAsync(hiddenClone.DataSource);
            Assert.Equal(templateStoreId, hiddenMetadata.StoreId);
            Assert.Null(hiddenMetadata.ActiveRuntimeEpoch);

            await ExecuteNonQueryAsync(
                namedClone.DataSource,
                $"INSERT INTO appsurface_durable.{sentinelRelation} (id) VALUES (1);");
            var namedError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
                await namedClone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "catalog-name-redaction"));
            Assert.DoesNotContain(password, namedError.Message, StringComparison.Ordinal);
            Assert.Contains("runtime rows", namedError.Message, StringComparison.OrdinalIgnoreCase);

            await ExecuteNonQueryAsync(
                populatedClone.DataSource,
                "INSERT INTO appsurface_durable.runtime_heartbeat " +
                "(worker_id, worker_instance_id, runtime_epoch, hosted_surfaces) " +
                "VALUES ('restricted-visible-row', gen_random_uuid(), gen_random_uuid(), 1);");
            var populatedError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
                await populatedClone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "visible-row"));
            Assert.Contains("runtime rows", populatedError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ExecuteNonQueryAsync(server.DataSource, $"DROP ROLE IF EXISTS {quotedRole};");
        }
    }

    [Fact]
    public async Task CloneGuard_SerializesConcurrentRekeyAndRejectsTheDuplicate()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        var firstEpoch = Guid.NewGuid();
        var secondEpoch = Guid.NewGuid();

        var attempts = await Task.WhenAll(
            CaptureActivationAsync(clone, templateStoreId, firstEpoch),
            CaptureActivationAsync(clone, templateStoreId, secondEpoch));

        var winner = Assert.Single(attempts, attempt => attempt.Status is not null);
        var rejected = Assert.Single(attempts, attempt => attempt.Error is not null);
        Assert.IsType<PostgreSqlFixtureException>(rejected.Error);
        Assert.Contains(winner.Status!.ActiveRuntimeEpoch, new Guid?[] { firstEpoch, secondEpoch });
        var metadata = await ReadMetadataAsync(clone.DataSource);
        Assert.Equal(1, metadata.Count);
        Assert.NotEqual(templateStoreId, metadata.StoreId);
        Assert.Equal(winner.Status.ActiveRuntimeEpoch, metadata.ActiveRuntimeEpoch);
        Assert.Equal(1, await ReadEpochHistoryCountAsync(clone.DataSource));

        var duplicate = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
            await clone.ActivateCloneAsync(templateStoreId, Guid.NewGuid(), "schema-tests", "duplicate-rekey"));
        Assert.Contains("clone metadata", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ReadEpochHistoryCountAsync(clone.DataSource));
    }

    [Fact]
    public async Task ActivateClone_PostRekeyFailureDropsOnlyTheOwnedClone()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        var cloneName = new NpgsqlConnectionStringBuilder(clone.ConnectionString).Database!;
        const string injectedDiagnostic = "credential-sentinel-post-rekey";

        var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() =>
            clone.ActivateCloneAsync(
                    templateStoreId,
                    Guid.NewGuid(),
                    "schema-tests",
                    "post-rekey-failure",
                    _ => ValueTask.FromException(new InvalidOperationException(injectedDiagnostic)))
                .AsTask());

        Assert.Contains("InvalidOperationException", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(injectedDiagnostic, error.Message, StringComparison.Ordinal);
        Assert.False(await DatabaseExistsAsync(server.ConnectionString, cloneName));
        var templateStatus = await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).GetStatusAsync();
        Assert.Equal(templateStoreId, templateStatus.StoreId);
        Assert.Null(templateStatus.ActiveRuntimeEpoch);
    }

    [Fact]
    public async Task ActivateClone_CommitAcknowledgementLost_CleansRekeyedOwnedClone()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);
        var cloneName = new NpgsqlConnectionStringBuilder(clone.ConnectionString).Database!;
        var cloneOid = await ReadDatabaseOidAsync(server.ConnectionString, cloneName);
        Assert.NotNull(cloneOid);

        var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() =>
            clone.ActivateCloneAsync(
                templateStoreId,
                Guid.NewGuid(),
                "schema-tests",
                "lost-rekey-commit-acknowledgement",
                commitRekey: async (transaction, cancellationToken) =>
                {
                    await transaction.CommitAsync(cancellationToken);
                    throw new InvalidOperationException("Injected lost rekey commit acknowledgement.");
                }).AsTask());

        Assert.Contains("after rekey", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InvalidOperationException", error.Message, StringComparison.Ordinal);
        Assert.Contains("removed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await DatabaseExistsAsync(server.ConnectionString, cloneName));
        var templateStatus = await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).GetStatusAsync();
        Assert.Equal(templateStoreId, templateStatus.StoreId);
        Assert.Null(templateStatus.ActiveRuntimeEpoch);
    }

    [Fact]
    public async Task PrepareTemplate_RejectsARegisteredNameReplacedByAnUnrelatedEmptyDatabase()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var maintenanceConnectionString = MaintenanceConnectionString(server.ConnectionString);
        var template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(maintenanceConnectionString, isTemplate: true);
        var templateName = new NpgsqlConnectionStringBuilder(template.ConnectionString).Database!;
        using var builder = new NpgsqlCommandBuilder();
        var quotedName = builder.QuoteIdentifier(templateName);
        uint? replacementOid = null;

        try
        {
            await ExecuteMaintenanceAsync(maintenanceConnectionString, $"DROP DATABASE {quotedName} WITH (FORCE);");
            await ExecuteMaintenanceAsync(maintenanceConnectionString, $"CREATE DATABASE {quotedName};");
            replacementOid = await ReadDatabaseOidAsync(maintenanceConnectionString, templateName);
            Assert.NotNull(replacementOid);

            var prepareError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(async () =>
                await template.PrepareTemplateAsync());
            Assert.Contains("ownership mismatch", prepareError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(await HasDurableMigrationSchemaAsync(maintenanceConnectionString, templateName));
            Assert.False(await HasPublicTablesAsync(maintenanceConnectionString, templateName));

            var disposalError = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() => template.DisposeAsync().AsTask());
            Assert.Contains("ownership mismatch", disposalError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(replacementOid, await ReadDatabaseOidAsync(maintenanceConnectionString, templateName));
            Assert.False(await HasDurableMigrationSchemaAsync(maintenanceConnectionString, templateName));
        }
        finally
        {
            if (replacementOid is { } expectedOid
                && await ReadDatabaseOidAsync(maintenanceConnectionString, templateName) == expectedOid)
            {
                await ExecuteMaintenanceAsync(maintenanceConnectionString, $"DROP DATABASE {quotedName} WITH (FORCE);");
            }

            await template.DisposeAsync();
        }
    }

    [Fact]
    public async Task TemplateDisposal_RejectsLiveClonesUntilTheyAreReleased()
    {
        await using var server = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var (template, templateStoreId) = await CreatePreparedTemplateAsync(server.ConnectionString);
        await using var templateScope = template;
        await using var clone = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(server.ConnectionString, template);

        var error = await Assert.ThrowsAsync<PostgreSqlFixtureException>(() => template.DisposeAsync().AsTask());
        Assert.Contains("registered clones", error.Message, StringComparison.OrdinalIgnoreCase);

        await clone.DisposeAsync();
        Assert.Equal(templateStoreId, (await new PostgreSqlDurableRuntimeSchemaManager(template.DataSource).GetStatusAsync()).StoreId);
        await template.DisposeAsync();
    }

    private static async ValueTask<(PostgreSqlIntegrationTestDatabase Template, Guid StoreId)> CreatePreparedTemplateAsync(
        string connectionString)
    {
        var template = await PostgreSqlIntegrationTestDatabase.CreateOwnedAsync(
            MaintenanceConnectionString(connectionString),
            isTemplate: true);
        try
        {
            var storeId = await template.PrepareTemplateAsync();
            return (template, storeId);
        }
        catch
        {
            await template.DisposeAsync();
            throw;
        }
    }

    private static string MaintenanceConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;

    private static async ValueTask ExecuteNonQueryAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask ExecuteNonQueryAsync(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<MetadataSnapshot> ReadMetadataAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT store_id, active_runtime_epoch FROM appsurface_durable.store_metadata WHERE singleton;");
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        Guid? storeId = null;
        Guid? activeEpoch = null;
        while (await reader.ReadAsync())
        {
            count++;
            if (count == 1)
            {
                storeId = reader.GetGuid(0);
                activeEpoch = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }
        }

        return new MetadataSnapshot(count, storeId, activeEpoch);
    }

    private static async ValueTask<long> ReadEpochHistoryCountAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM appsurface_durable.runtime_epoch_history;");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask InsertScopeAsync(NpgsqlDataSource dataSource, string scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setting = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            setting.Parameters.AddWithValue("scope_id", scopeId);
            await setting.ExecuteScalarAsync();
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO appsurface_durable.scope (scope_id) VALUES (@scope_id);",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("scope_id", scopeId);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask<long> CountVisibleScopeAsync(
        NpgsqlDataSource dataSource,
        string scopeId,
        string? activeScopeId = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setting = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction))
        {
            setting.Parameters.AddWithValue("scope_id", activeScopeId ?? scopeId);
            await setting.ExecuteScalarAsync();
        }

        await using var query = new NpgsqlCommand(
            "SELECT count(*) FROM appsurface_durable.scope WHERE scope_id = @scope_id;",
            connection,
            transaction);
        query.Parameters.AddWithValue("scope_id", scopeId);
        var count = (long)(await query.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async ValueTask<(bool IsSuperuser, bool BypassesRowSecurity)> ReadRoleIdentityAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT rolsuper, rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user;");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task<ActivationAttempt> CaptureActivationAsync(
        PostgreSqlIntegrationTestDatabase clone,
        Guid templateStoreId,
        Guid epoch)
    {
        try
        {
            return new ActivationAttempt(
                await clone.ActivateCloneAsync(templateStoreId, epoch, "schema-tests", "concurrent-rekey"),
                null);
        }
        catch (Exception error)
        {
            return new ActivationAttempt(null, error);
        }
    }

    private static async ValueTask<uint?> ReadDatabaseOidAsync(string maintenanceConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT oid FROM pg_catalog.pg_database WHERE datname = @name;",
            connection);
        command.Parameters.AddWithValue("name", databaseName);
        return (uint?)await command.ExecuteScalarAsync();
    }

    private static async ValueTask ExecuteMaintenanceAsync(string maintenanceConnectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<bool> DatabaseExistsAsync(string connectionString, string databaseName)
    {
        var maintenance = MaintenanceConnectionString(connectionString);
        await using var connection = new NpgsqlConnection(maintenance);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = @name);",
            connection);
        command.Parameters.AddWithValue("name", databaseName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<bool> HasDurableMigrationSchemaAsync(string connectionString, string databaseName)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT pg_catalog.to_regclass('appsurface_durable.schema_migration') IS NOT NULL;");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async ValueTask<bool> HasPublicTablesAsync(string connectionString, string databaseName)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = databaseName,
            Pooling = false,
        };
        await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r', 'p'));");
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private sealed record MetadataSnapshot(int Count, Guid? StoreId, Guid? ActiveRuntimeEpoch);

    private sealed record ActivationAttempt(DurableRuntimeSchemaStatus? Status, Exception? Error);
}
