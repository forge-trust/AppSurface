using DotNet.Testcontainers.Builders;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlSchemaIntegrationTests
{
    private const long MigrationAdvisoryLock = 4_707_181_168_775_217_740;
    private const string RoleRecipeDatabase = "appsurface_durable";
    private const string RoleRecipeUsername = "appsurface";
    private const string RoleRecipePassword = "appsurface-test-password";

    [Fact]
    public async Task ApplyStatusInitializeRotate_AreExplicitAndIdempotent()
    {
        await using var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase("appsurface_durable")
            .WithUsername("appsurface")
            .WithPassword("appsurface-test-password")
            .Build();
        await container.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);

        var missing = await manager.GetStatusAsync();
        var missingEpoch = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () =>
            await manager.InitializeRuntimeEpochAsync(Guid.NewGuid(), "tests", "initial"));
        var first = await manager.ApplyAsync();
        var compatible = await manager.GetStatusAsync();
        await manager.ValidateAsync();
        var second = await manager.ApplyAsync();
        var initialEpoch = Guid.NewGuid();
        var activation = await manager.InitializeRuntimeEpochAsync(initialEpoch, "tests", "initial");
        var afterActivation = await manager.GetStatusAsync();
        var nextEpoch = Guid.NewGuid();
        var rotation = await manager.RotateRuntimeEpochAsync(initialEpoch, nextEpoch, "tests", "restore");
        var afterRotation = await manager.GetStatusAsync();
        var staleRotation = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.RotateRuntimeEpochAsync(initialEpoch, Guid.NewGuid(), "tests", "stale-restore"));
        var afterStaleRotation = await manager.GetStatusAsync();

        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, missing.Compatibility);
        Assert.Equal(DurableRuntimeSchemaCompatibility.Missing, missingEpoch.Status.Compatibility);
        Assert.Equal([1, 2], first.AppliedVersions);
        Assert.Empty(second.AppliedVersions);
        Assert.True(compatible.IsCompatible);
        Assert.NotEqual(Guid.Empty, compatible.StoreId);
        Assert.Null(compatible.ActiveRuntimeEpoch);
        Assert.Equal(initialEpoch, activation.ActiveEpoch);
        Assert.Equal(initialEpoch, afterActivation.ActiveRuntimeEpoch);
        Assert.Equal(nextEpoch, rotation.ActiveEpoch);
        Assert.Equal(nextEpoch, afterRotation.ActiveRuntimeEpoch);
        Assert.StartsWith(DurableProblemCodes.RecoveryEpochRequired, staleRotation.Message, StringComparison.Ordinal);
        Assert.Equal(nextEpoch, afterStaleRotation.ActiveRuntimeEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.InitializeRuntimeEpochAsync(Guid.NewGuid(), "tests", "duplicate"));
    }

    [Fact]
    public async Task ModifiedMigrationHistory_FailsClosed()
    {
        await using var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase("appsurface_durable")
            .WithUsername("appsurface")
            .WithPassword("appsurface-test-password")
            .Build();
        await container.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var manager = new PostgreSqlDurableRuntimeSchemaManager(dataSource);
        await manager.ApplyAsync();
        await using (var command = dataSource.CreateCommand(
            "UPDATE appsurface_durable.schema_migration SET sha256 = repeat('0', 64) WHERE version = 1;"))
        {
            await command.ExecuteNonQueryAsync();
        }

        var status = await manager.GetStatusAsync();
        var exception = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () => await manager.ValidateAsync());

        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, status.Compatibility);
        Assert.Equal(status.Compatibility, exception.Status.Compatibility);
        Assert.Equal(status.InstalledVersion, exception.Status.InstalledVersion);
        Assert.Equal(status.Problem, exception.Status.Problem);
    }

    [Fact]
    public async Task FailedMigration_RollsBackPartialDdlAndRetriesFromLastCommittedVersion()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var embedded = DurablePostgreSqlMigrationCatalog.Load();
        var failingMigration = new DurablePostgreSqlMigration(
            2,
            "forced_failure",
            "CREATE TABLE appsurface_durable.rollback_probe (id integer); SELECT 1 / 0;",
            new string('0', 64));
        var failingManager = new PostgreSqlDurableRuntimeSchemaManager(
            database.DataSource,
            [embedded[0], failingMigration]);

        var exception = await Assert.ThrowsAsync<PostgresException>(async () => await failingManager.ApplyAsync());

        Assert.Equal(PostgresErrorCodes.DivisionByZero, exception.SqlState);
        await using (var partialDdl = database.DataSource.CreateCommand(
            "SELECT to_regclass('appsurface_durable.rollback_probe') IS NOT NULL;"))
        {
            Assert.False((bool)(await partialDdl.ExecuteScalarAsync())!);
        }

        var retryManager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        var afterFailure = await retryManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, afterFailure.Compatibility);
        Assert.Equal(1, afterFailure.InstalledVersion);
        Assert.Equal([1], afterFailure.AppliedVersions);

        var retry = await retryManager.ApplyAsync();
        var compatible = await retryManager.GetStatusAsync();
        Assert.Equal([2], retry.AppliedVersions);
        Assert.True(compatible.IsCompatible);
        Assert.Equal([1, 2], compatible.AppliedVersions);
    }

    [Fact]
    public async Task RoleRecipe_RejectsUnsafeRolesAndPrivilegesAndRemovesPreexistingRuntimeOwnership()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var recipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable/configure-postgresql-roles.sql");
        const string containerRecipePath = "/tmp/configure-postgresql-roles.sql";
        await using var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase(RoleRecipeDatabase)
            .WithUsername(RoleRecipeUsername)
            .WithPassword(RoleRecipePassword)
            .WithResourceMapping(File.ReadAllBytes(recipePath), containerRecipePath)
            .Build();
        await container.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await new PostgreSqlDurableRuntimeSchemaManager(dataSource).ApplyAsync();
        await using (var roles = dataSource.CreateCommand(
            """
            CREATE ROLE durable_owner NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE durable_dispatcher NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE durable_runtime NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE bypass_dispatcher NOLOGIN NOSUPERUSER BYPASSRLS;
            CREATE ROLE owner_inheriting_runtime NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE member_dispatcher NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE member_runtime NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE direct_privilege_dispatcher NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE direct_privilege_runtime NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE inherited_privilege_dispatcher NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE inherited_privilege_runtime NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE dispatcher_privilege_parent NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE runtime_privilege_parent NOLOGIN NOSUPERUSER NOBYPASSRLS;
            GRANT durable_owner TO owner_inheriting_runtime;
            GRANT member_dispatcher TO member_runtime;
            GRANT TRUNCATE ON appsurface_durable.work TO direct_privilege_dispatcher;
            GRANT CREATE ON SCHEMA appsurface_durable TO direct_privilege_runtime;
            GRANT CREATE ON SCHEMA appsurface_durable TO dispatcher_privilege_parent;
            GRANT TRIGGER ON appsurface_durable.work TO runtime_privilege_parent;
            GRANT dispatcher_privilege_parent TO inherited_privilege_dispatcher;
            GRANT runtime_privilege_parent TO inherited_privilege_runtime;
            ALTER TABLE appsurface_durable.work OWNER TO durable_runtime;
            """))
        {
            await roles.ExecuteNonQueryAsync();
        }

        var rejected = new[]
        {
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "durable_dispatcher", Expected: "must be distinct"),
            (Owner: "durable_owner", Dispatcher: "bypass_dispatcher", Runtime: "durable_runtime", Expected: "must not inherit SUPERUSER or BYPASSRLS"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "owner_inheriting_runtime", Expected: "must not inherit each other or the migration owner"),
            (Owner: "durable_owner", Dispatcher: "member_dispatcher", Runtime: "member_runtime", Expected: "must not inherit each other or the migration owner"),
            (Owner: "durable_owner", Dispatcher: "direct_privilege_dispatcher", Runtime: "durable_runtime", Expected: "effective durable-table privilege outside the package allowlist"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "direct_privilege_runtime", Expected: "must not inherit CREATE on the durable schema"),
            (Owner: "durable_owner", Dispatcher: "inherited_privilege_dispatcher", Runtime: "durable_runtime", Expected: "must not inherit CREATE on the durable schema"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "inherited_privilege_runtime", Expected: "effective durable-table privilege outside the package allowlist"),
        };
        foreach (var item in rejected)
        {
            var result = await RunRoleRecipeAsync(container, containerRecipePath, item.Owner, item.Dispatcher, item.Runtime);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(item.Expected, $"{result.Stdout}\n{result.Stderr}", StringComparison.Ordinal);
        }

        await using (var noRejectedGrant = dataSource.CreateCommand(
            "SELECT has_schema_privilege('durable_dispatcher', 'appsurface_durable', 'USAGE');"))
        {
            Assert.False((bool)(await noRejectedGrant.ExecuteScalarAsync())!);
        }

        var accepted = await RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "durable_runtime");
        Assert.True(
            accepted.ExitCode == 0,
            $"Role recipe failed with exit {accepted.ExitCode}. stdout: {accepted.Stdout} stderr: {accepted.Stderr}");
        await using (var ownership = dataSource.CreateCommand(
            """
            SELECT count(*) = 0
            FROM pg_catalog.pg_class AS object
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
            JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = object.relowner
            WHERE namespace.nspname = 'appsurface_durable'
              AND object.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
              AND owner_role.rolname <> 'durable_owner';
            """))
        {
            Assert.True((bool)(await ownership.ExecuteScalarAsync())!);
        }

        await using (var effectivePrivileges = dataSource.CreateCommand(
            """
            SELECT
                has_schema_privilege('durable_dispatcher', 'appsurface_durable', 'USAGE')
                AND NOT has_schema_privilege('durable_dispatcher', 'appsurface_durable', 'CREATE')
                AND has_table_privilege('durable_dispatcher', 'appsurface_durable.dispatch', 'SELECT')
                AND NOT has_table_privilege(
                    'durable_dispatcher',
                    'appsurface_durable.work',
                    'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
                AND NOT has_sequence_privilege(
                    'durable_dispatcher',
                    'appsurface_durable.scope_history_event_id_seq',
                    'USAGE,SELECT,UPDATE')
                AND has_schema_privilege('durable_runtime', 'appsurface_durable', 'USAGE')
                AND NOT has_schema_privilege('durable_runtime', 'appsurface_durable', 'CREATE')
                AND has_table_privilege('durable_runtime', 'appsurface_durable.work', 'SELECT,INSERT')
                AND NOT has_table_privilege(
                    'durable_runtime',
                    'appsurface_durable.work',
                    'UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,MAINTAIN')
                AND has_column_privilege('durable_runtime', 'appsurface_durable.work', 'state', 'UPDATE')
                AND NOT has_column_privilege('durable_runtime', 'appsurface_durable.work', 'work_name', 'UPDATE')
                AND has_sequence_privilege(
                    'durable_runtime',
                    'appsurface_durable.scope_history_event_id_seq',
                    'USAGE,SELECT')
                AND NOT has_sequence_privilege(
                    'durable_runtime',
                    'appsurface_durable.scope_history_event_id_seq',
                    'UPDATE');
            """))
        {
            Assert.True((bool)(await effectivePrivileges.ExecuteScalarAsync())!);
        }

        await using var runtimeConnection = await dataSource.OpenConnectionAsync();
        await using (var assumeRuntime = runtimeConnection.CreateCommand())
        {
            assumeRuntime.CommandText = "SET ROLE durable_runtime;";
            await assumeRuntime.ExecuteNonQueryAsync();
        }

        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var disableRls = runtimeConnection.CreateCommand();
            disableRls.CommandText = "ALTER TABLE appsurface_durable.work DISABLE ROW LEVEL SECURITY;";
            await disableRls.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await using (var resetRole = runtimeConnection.CreateCommand())
        {
            resetRole.CommandText = "RESET ROLE;";
            await resetRole.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RenamedMigrationHistory_FailsClosed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await ExecuteNonQueryAsync(
            database.DataSource,
            "UPDATE appsurface_durable.schema_migration SET name = 'renamed' WHERE version = 1;");

        var status = await manager.GetStatusAsync();

        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, status.Compatibility);
        Assert.Contains("does not match", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonContiguousMigrationHistory_FailsClosed()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await ExecuteNonQueryAsync(
            database.DataSource,
            "DELETE FROM appsurface_durable.schema_migration WHERE version = 1;");

        var status = await manager.GetStatusAsync();
        var exception = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () =>
            await manager.ValidateAsync());

        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, status.Compatibility);
        Assert.Contains("not contiguous at version 1", status.Problem, StringComparison.Ordinal);
        Assert.Equal(status.Problem, exception.Status.Problem);
    }

    [Fact]
    public async Task SchemaStatus_ClassifiesIncompleteInvalidUpgradeAndUnsupportedStores()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);

        await ExecuteNonQueryAsync(database.DataSource, "CREATE SCHEMA appsurface_durable;");
        var incomplete = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, incomplete.Compatibility);
        Assert.Contains("incomplete", incomplete.Problem, StringComparison.Ordinal);
        var incompleteApply = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await manager.ApplyAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, incompleteApply.Status.Compatibility);

        await ExecuteNonQueryAsync(database.DataSource, "DROP SCHEMA appsurface_durable CASCADE;");
        await manager.ApplyAsync();

        await ExecuteNonQueryAsync(
            database.DataSource,
            "UPDATE appsurface_durable.store_metadata SET schema_version = 1 WHERE singleton;");
        var invalid = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, invalid.Compatibility);
        Assert.Contains("invalid", invalid.Problem, StringComparison.Ordinal);
        var invalidApply = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await manager.ApplyAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, invalidApply.Status.Compatibility);

        await ExecuteNonQueryAsync(
            database.DataSource,
            """
            UPDATE appsurface_durable.store_metadata
            SET schema_version = 2,
                minimum_reader_version = 1,
                maximum_reader_version = 1,
                minimum_writer_version = 1,
                maximum_writer_version = 1
            WHERE singleton;
            """);
        var unsupported = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, unsupported.Compatibility);
        Assert.Equal(1, unsupported.MaximumReaderVersion);
        Assert.Equal(1, unsupported.MaximumWriterVersion);
        var unsupportedValidation = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await manager.ValidateAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, unsupportedValidation.Status.Compatibility);
        var unsupportedApply = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await manager.ApplyAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, unsupportedApply.Status.Compatibility);

        await ExecuteNonQueryAsync(
            database.DataSource,
            """
            DELETE FROM appsurface_durable.schema_migration WHERE version = 2;
            UPDATE appsurface_durable.store_metadata
            SET schema_version = 1,
                minimum_reader_version = 1,
                maximum_reader_version = 1,
                minimum_writer_version = 1,
                maximum_writer_version = 1
            WHERE singleton;
            """);
        var upgrade = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, upgrade.Compatibility);
        Assert.Equal([1], upgrade.AppliedVersions);
        Assert.Equal([2], upgrade.PendingVersions);
        var upgradeValidation = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await manager.ValidateAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, upgradeValidation.Status.Compatibility);

        await ExecuteNonQueryAsync(database.DataSource, "DELETE FROM appsurface_durable.store_metadata WHERE singleton;");
        var missingMetadata = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Inconsistent, missingMetadata.Compatibility);
        Assert.Contains("metadata is missing", missingMetadata.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentApply_SerializesAndRecordsEachMigrationOnce()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var first = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        var second = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);

        var results = await Task.WhenAll(first.ApplyAsync().AsTask(), second.ApplyAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([1, 2], results.SelectMany(result => result.AppliedVersions).Order().ToArray());
        Assert.Contains(results, result => result.AppliedVersions.SequenceEqual([1, 2]));
        Assert.Contains(results, result => result.AppliedVersions.Count == 0);
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.schema_migration;");
        Assert.Equal(2, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ApplyCancellationWhileWaitingForLock_DoesNotLeakLockAndCanRetry()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@lock_id);", blocker))
        {
            acquire.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await acquire.ExecuteNonQueryAsync();
        }

        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.ApplyAsync(cancellation.Token));

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_id);", blocker))
        {
            release.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await release.ExecuteNonQueryAsync();
        }

        var applied = await manager.ApplyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([1, 2], applied.AppliedVersions);
    }

    [Fact]
    public async Task EpochActivation_WaitsForTheSchemaAdvisoryLock()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@lock_id);", blocker))
        {
            acquire.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await acquire.ExecuteNonQueryAsync();
        }

        var epoch = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.InitializeRuntimeEpochAsync(epoch, "tests", "initial", cancellation.Token));
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_id);", blocker))
        {
            release.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await release.ExecuteNonQueryAsync();
        }

        var activated = await manager.InitializeRuntimeEpochAsync(epoch, "tests", "initial");
        Assert.Equal(epoch, activated.ActiveEpoch);
    }

    [Fact]
    public async Task ApplyConnectionLossWhileWaitingForLock_CanRecoverOnANewSession()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@lock_id);", blocker))
        {
            acquire.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await acquire.ExecuteNonQueryAsync();
        }

        var applicationName = $"slice3-loss-{Guid.NewGuid():N}";
        var managerConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = applicationName,
        };
        await using var managerDataSource = NpgsqlDataSource.Create(managerConnection.ConnectionString);
        var manager = new PostgreSqlDurableRuntimeSchemaManager(managerDataSource);
        var apply = manager.ApplyAsync().AsTask();
        var backendPid = await WaitForBackendAsync(database.DataSource, applicationName);
        await using (var terminate = database.DataSource.CreateCommand("SELECT pg_terminate_backend(@pid);"))
        {
            terminate.Parameters.AddWithValue("pid", backendPid);
            Assert.True((bool)(await terminate.ExecuteScalarAsync())!);
        }

        await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
            await apply.WaitAsync(TimeSpan.FromSeconds(30)));
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_id);", blocker))
        {
            release.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await release.ExecuteNonQueryAsync();
        }

        var applied = await manager.ApplyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([1, 2], applied.AppliedVersions);
    }

    [Fact]
    public async Task ApplyConnectionLossAfterAcquiringLock_PreservesFailureAndCanRetry()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var initialManager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await initialManager.ApplyAsync();
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand(
            "LOCK TABLE appsurface_durable.schema_migration IN ACCESS EXCLUSIVE MODE;",
            blocker,
            blockerTransaction))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        var applicationName = $"slice3-post-lock-loss-{Guid.NewGuid():N}";
        var managerConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = applicationName,
        };
        await using var managerDataSource = NpgsqlDataSource.Create(managerConnection.ConnectionString);
        var manager = new PostgreSqlDurableRuntimeSchemaManager(managerDataSource);
        var apply = manager.ApplyAsync().AsTask();
        var backendPid = await WaitForBackendAsync(database.DataSource, applicationName, "relation");
        await using (var terminate = database.DataSource.CreateCommand("SELECT pg_terminate_backend(@pid);"))
        {
            terminate.Parameters.AddWithValue("pid", backendPid);
            Assert.True((bool)(await terminate.ExecuteScalarAsync())!);
        }

        await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
            await apply.WaitAsync(TimeSpan.FromSeconds(30)));
        await blockerTransaction.RollbackAsync();

        var retried = await manager.ApplyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(retried.AppliedVersions);
    }

    [Fact]
    public async Task RoleIdentifierFormatting_RoundTripsExactHostileNames()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var roleNames = new[]
        {
            $"Durable Mixed {Guid.NewGuid():N}",
            $"durable-role.{Guid.NewGuid():N}",
            $"durable\"quote-{Guid.NewGuid():N}",
            $"durable\";drop role x;--{Guid.NewGuid():N}",
        };

        foreach (var roleName in roleNames)
        {
            await using var format = database.DataSource.CreateCommand("SELECT format('%I', @role_name);");
            format.Parameters.AddWithValue("role_name", roleName);
            var identifier = (string)(await format.ExecuteScalarAsync())!;
            await using var create = database.DataSource.CreateCommand($"CREATE ROLE {identifier};");
            await create.ExecuteNonQueryAsync();
            try
            {
                await using var exists = database.DataSource.CreateCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role_name);");
                exists.Parameters.AddWithValue("role_name", roleName);
                Assert.True((bool)(await exists.ExecuteScalarAsync())!);
            }
            finally
            {
                await using var drop = database.DataSource.CreateCommand($"DROP ROLE {identifier};");
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static ValueTask<int> WaitForBackendAsync(NpgsqlDataSource dataSource, string applicationName)
        => WaitForBackendAsync(dataSource, applicationName, "advisory");

    private static Task<DotNet.Testcontainers.Containers.ExecResult> RunRoleRecipeAsync(
        PostgreSqlContainer container,
        string recipePath,
        string owner,
        string dispatcher,
        string runtime) =>
        container.ExecAsync(
            [
                "psql",
                "-U", RoleRecipeUsername,
                "-d", RoleRecipeDatabase,
                "-v", $"migration_owner_role={owner}",
                "-v", $"dispatcher_role={dispatcher}",
                "-v", $"runtime_role={runtime}",
                "-f", recipePath,
            ]);

    private static async ValueTask ExecuteNonQueryAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<int> WaitForBackendAsync(
        NpgsqlDataSource dataSource,
        string applicationName,
        string waitEvent)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT pid
                FROM pg_catalog.pg_stat_activity
                WHERE application_name = @application_name
                  AND wait_event = @wait_event
                LIMIT 1;
                """);
            command.Parameters.AddWithValue("application_name", applicationName);
            command.Parameters.AddWithValue("wait_event", waitEvent);
            if (await command.ExecuteScalarAsync() is int backendPid)
            {
                return backendPid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"The migration session did not begin waiting for the {waitEvent} lock.");
    }
}
