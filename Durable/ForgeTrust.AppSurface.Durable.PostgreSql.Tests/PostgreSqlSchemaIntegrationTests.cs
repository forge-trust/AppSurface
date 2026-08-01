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
    private const string RoleRecipeApplicationName = "durable-role-recipe";
    private const string DispatcherRoleTestPassword = "durable-dispatcher-test-password";
    private const string RuntimeRoleTestPassword = "durable-runtime-test-password";

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
        Assert.Equal([1, 2, 3, 4], first.AppliedVersions);
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
    public async Task ScheduleHistoryPartitionMaintenance_RestoresTheUpcomingPartitionWithForcedRls()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        await ExecuteNonQueryAsync(
            database.DataSource,
            """
            DO $$
            DECLARE
                next_child text := format('schedule_history_%s', to_char(date_trunc('month', CURRENT_DATE) + interval '1 month', 'YYYYMM'));
            BEGIN
                EXECUTE format('DROP TABLE appsurface_durable.%I', next_child);
            END $$;
            """);

        await ExecuteNonQueryAsync(
            database.DataSource,
            "SELECT appsurface_durable.ensure_schedule_history_partitions();");
        await using var verify = database.DataSource.CreateCommand(
            """
            WITH next_child AS
            (
                SELECT to_regclass(format(
                    'appsurface_durable.schedule_history_%s',
                    to_char(date_trunc('month', CURRENT_DATE) + interval '1 month', 'YYYYMM'))) AS relation_id
            )
            SELECT relation.relrowsecurity
                   AND relation.relforcerowsecurity
                   AND EXISTS
                   (
                       SELECT 1
                       FROM pg_catalog.pg_policy AS policy
                       WHERE policy.polrelid = next_child.relation_id
                         AND policy.polname = 'schedule_history_scope_isolation'
                   )
            FROM next_child
            JOIN pg_catalog.pg_class AS relation ON relation.oid = next_child.relation_id;
            """);

        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
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
            3,
            "forced_failure",
            "CREATE TABLE appsurface_durable.rollback_probe (id integer); SELECT 1 / 0;",
            new string('0', 64));
        var failingManager = new PostgreSqlDurableRuntimeSchemaManager(
            database.DataSource,
            [embedded[0], embedded[1], failingMigration]);

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
        Assert.Equal(2, afterFailure.InstalledVersion);
        Assert.Equal([1, 2], afterFailure.AppliedVersions);

        var retry = await retryManager.ApplyAsync();
        var compatible = await retryManager.GetStatusAsync();
        Assert.Equal([3, 4], retry.AppliedVersions);
        Assert.True(compatible.IsCompatible);
        Assert.Equal([1, 2, 3, 4], compatible.AppliedVersions);
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
            $"""
            CREATE ROLE durable_owner NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE durable_dispatcher LOGIN PASSWORD '{DispatcherRoleTestPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE durable_runtime LOGIN PASSWORD '{RuntimeRoleTestPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE bypass_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;
            CREATE ROLE createdb_dispatcher LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE nologin_dispatcher NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE owner_inheriting_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE member_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE member_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE direct_privilege_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE direct_privilege_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE column_privilege_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE sequence_privilege_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE grant_option_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE schema_grant_option_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE database_owner_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE inherited_privilege_dispatcher LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE inherited_privilege_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE dispatcher_privilege_parent NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE runtime_privilege_parent NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE downstream_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE downstream_login LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            GRANT durable_owner TO owner_inheriting_runtime;
            GRANT member_dispatcher TO member_runtime;
            GRANT TRUNCATE ON appsurface_durable.work TO direct_privilege_dispatcher;
            GRANT CREATE ON SCHEMA appsurface_durable TO direct_privilege_runtime;
            GRANT UPDATE (work_name) ON appsurface_durable.work TO column_privilege_runtime;
            GRANT UPDATE ON SEQUENCE appsurface_durable.scope_history_event_id_seq TO sequence_privilege_dispatcher;
            GRANT SELECT ON appsurface_durable.dispatch TO grant_option_dispatcher WITH GRANT OPTION;
            GRANT USAGE ON SCHEMA appsurface_durable TO schema_grant_option_runtime WITH GRANT OPTION;
            GRANT CREATE ON SCHEMA appsurface_durable TO dispatcher_privilege_parent;
            GRANT TRIGGER ON appsurface_durable.work TO runtime_privilege_parent;
            GRANT dispatcher_privilege_parent TO inherited_privilege_dispatcher WITH INHERIT FALSE, SET TRUE;
            GRANT runtime_privilege_parent TO inherited_privilege_runtime WITH INHERIT FALSE, SET TRUE;
            GRANT downstream_dispatcher TO downstream_login;
            ALTER TABLE appsurface_durable.work OWNER TO durable_runtime;
            """))
        {
            await roles.ExecuteNonQueryAsync();
        }

        var rejected = new[]
        {
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "durable_dispatcher", Expected: "must be distinct"),
            (Owner: "durable_owner", Dispatcher: "bypass_dispatcher", Runtime: "durable_runtime", Expected: "must be LOGIN roles without SUPERUSER"),
            (Owner: "durable_owner", Dispatcher: "createdb_dispatcher", Runtime: "durable_runtime", Expected: "must be LOGIN roles without SUPERUSER"),
            (Owner: "durable_owner", Dispatcher: "nologin_dispatcher", Runtime: "durable_runtime", Expected: "must be LOGIN roles without SUPERUSER"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "owner_inheriting_runtime", Expected: "exact login leaves with no role memberships"),
            (Owner: "durable_owner", Dispatcher: "member_dispatcher", Runtime: "member_runtime", Expected: "exact login leaves with no role memberships"),
            (Owner: "durable_owner", Dispatcher: "direct_privilege_dispatcher", Runtime: "durable_runtime", Expected: "effective durable-table privilege outside the package allowlist"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "direct_privilege_runtime", Expected: "schema CREATE or grant options"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "column_privilege_runtime", Expected: "effective durable-column privilege outside the package allowlist"),
            (Owner: "durable_owner", Dispatcher: "sequence_privilege_dispatcher", Runtime: "durable_runtime", Expected: "effective durable-sequence privilege outside the package allowlist"),
            (Owner: "durable_owner", Dispatcher: "grant_option_dispatcher", Runtime: "durable_runtime", Expected: "effective durable-table privilege outside the package allowlist"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "schema_grant_option_runtime", Expected: "schema CREATE or grant options"),
            (Owner: "durable_owner", Dispatcher: "inherited_privilege_dispatcher", Runtime: "durable_runtime", Expected: "exact login leaves with no role memberships"),
            (Owner: "durable_owner", Dispatcher: "durable_dispatcher", Runtime: "inherited_privilege_runtime", Expected: "exact login leaves with no role memberships"),
            (Owner: "durable_owner", Dispatcher: "downstream_dispatcher", Runtime: "durable_runtime", Expected: "exact login leaves with no role memberships"),
        };
        foreach (var item in rejected)
        {
            var result = await RunRoleRecipeAsync(container, containerRecipePath, item.Owner, item.Dispatcher, item.Runtime);
            Assert.NotEqual(0, result.ExitCode);
            var output = $"{result.Stdout}\n{result.Stderr}";
            Assert.True(
                output.Contains(item.Expected, StringComparison.Ordinal),
                $"Expected role recipe case ({item.Owner}, {item.Dispatcher}, {item.Runtime}) to contain '{item.Expected}'. Output: {output}");
        }

        await ExecuteNonQueryAsync(
            dataSource,
            "ALTER TABLE appsurface_durable.work DISABLE ROW LEVEL SECURITY;");
        var disabledRls = await RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "durable_runtime");
        Assert.NotEqual(0, disabledRls.ExitCode);
        Assert.Contains(
            "row-level security flags must exactly match",
            $"{disabledRls.Stdout}\n{disabledRls.Stderr}",
            StringComparison.Ordinal);
        await ExecuteNonQueryAsync(
            dataSource,
            "ALTER TABLE appsurface_durable.work ENABLE ROW LEVEL SECURITY;");

        await ExecuteNonQueryAsync(
            dataSource,
            "CREATE POLICY work_permissive_bypass ON appsurface_durable.work USING (true) WITH CHECK (true);");
        var permissivePolicy = await RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "durable_runtime");
        Assert.NotEqual(0, permissivePolicy.ExitCode);
        Assert.Contains(
            "row-level security policies must exactly match",
            $"{permissivePolicy.Stdout}\n{permissivePolicy.Stderr}",
            StringComparison.Ordinal);
        await ExecuteNonQueryAsync(
            dataSource,
            "DROP POLICY work_permissive_bypass ON appsurface_durable.work;");

        await ExecuteNonQueryAsync(
            dataSource,
            "GRANT CREATE ON SCHEMA appsurface_durable TO PUBLIC;");
        var publicPrivilege = await RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "durable_runtime");
        Assert.NotEqual(0, publicPrivilege.ExitCode);
        Assert.Contains(
            "schema CREATE or grant options",
            $"{publicPrivilege.Stdout}\n{publicPrivilege.Stderr}",
            StringComparison.Ordinal);
        await ExecuteNonQueryAsync(
            dataSource,
            "REVOKE CREATE ON SCHEMA appsurface_durable FROM PUBLIC;");

        await ExecuteNonQueryAsync(
            dataSource,
            "ALTER DATABASE appsurface_durable OWNER TO database_owner_runtime;");
        var databaseOwner = await RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "database_owner_runtime");
        Assert.NotEqual(0, databaseOwner.ExitCode);
        Assert.Contains(
            "must not own any database",
            $"{databaseOwner.Stdout}\n{databaseOwner.Stderr}",
            StringComparison.Ordinal);
        await ExecuteNonQueryAsync(
            dataSource,
            $"ALTER DATABASE appsurface_durable OWNER TO {RoleRecipeUsername};");

        await using (var noRejectedGrant = dataSource.CreateCommand(
            """
            SELECT
                NOT has_schema_privilege('durable_dispatcher', 'appsurface_durable', 'USAGE')
                AND NOT has_table_privilege(
                    'direct_privilege_dispatcher',
                    'appsurface_durable.dispatch',
                    'SELECT')
                AND
                (
                    SELECT owner_role.rolname = 'durable_runtime'
                    FROM pg_catalog.pg_class AS object
                    JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
                    JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = object.relowner
                    WHERE namespace.nspname = 'appsurface_durable'
                      AND object.relname = 'work'
                );
            """))
        {
            Assert.True((bool)(await noRejectedGrant.ExecuteScalarAsync())!);
        }

        await using var lockConnection = await dataSource.OpenConnectionAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var holdRuntimeFence = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock_shared(@lock_id);",
            lockConnection,
            lockTransaction))
        {
            holdRuntimeFence.Parameters.AddWithValue("lock_id", MigrationAdvisoryLock);
            await holdRuntimeFence.ExecuteNonQueryAsync();
        }

        var acceptedTask = RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "durable_runtime");
        await WaitForBackendAsync(dataSource, RoleRecipeApplicationName);
        Assert.False(acceptedTask.IsCompleted);
        await lockTransaction.CommitAsync();
        var accepted = await acceptedTask;
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
            WITH service_role(role_name) AS
            (
                VALUES ('durable_dispatcher'), ('durable_runtime')
            ),
            durable_relation AS
            (
                SELECT object.oid, object.relname
                FROM pg_catalog.pg_class AS object
                JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = 'appsurface_durable'
                  AND object.relkind IN ('r', 'p', 'v', 'm', 'f')
            ),
            relation_privilege(privilege_name) AS
            (
                VALUES
                    ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE'), ('TRUNCATE'), ('REFERENCES'), ('TRIGGER'), ('MAINTAIN'),
                    ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION'),
                    ('DELETE WITH GRANT OPTION'), ('TRUNCATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION'),
                    ('TRIGGER WITH GRANT OPTION'), ('MAINTAIN WITH GRANT OPTION')
            ),
            durable_column AS
            (
                SELECT object.oid, object.relname, attribute.attnum, attribute.attname
                FROM durable_relation AS relation
                JOIN pg_catalog.pg_class AS object ON object.oid = relation.oid
                JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid = object.oid
                WHERE attribute.attnum > 0
                  AND NOT attribute.attisdropped
            ),
            column_privilege(privilege_name) AS
            (
                VALUES
                    ('SELECT'), ('INSERT'), ('UPDATE'), ('REFERENCES'),
                    ('SELECT WITH GRANT OPTION'), ('INSERT WITH GRANT OPTION'),
                    ('UPDATE WITH GRANT OPTION'), ('REFERENCES WITH GRANT OPTION')
            ),
            durable_sequence AS
            (
                SELECT object.oid
                FROM pg_catalog.pg_class AS object
                JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = object.relnamespace
                WHERE namespace.nspname = 'appsurface_durable'
                  AND object.relkind = 'S'
            ),
            sequence_privilege(privilege_name) AS
            (
                VALUES
                    ('USAGE'), ('SELECT'), ('UPDATE'),
                    ('USAGE WITH GRANT OPTION'), ('SELECT WITH GRANT OPTION'), ('UPDATE WITH GRANT OPTION')
            )
            SELECT
                has_schema_privilege('durable_dispatcher', 'appsurface_durable', 'USAGE')
                AND NOT has_schema_privilege('durable_dispatcher', 'appsurface_durable', 'CREATE')
                AND NOT has_schema_privilege(
                    'durable_dispatcher',
                    'appsurface_durable',
                    'USAGE WITH GRANT OPTION')
                AND has_schema_privilege('durable_runtime', 'appsurface_durable', 'USAGE')
                AND NOT has_schema_privilege('durable_runtime', 'appsurface_durable', 'CREATE')
                AND NOT has_schema_privilege(
                    'durable_runtime',
                    'appsurface_durable',
                    'USAGE WITH GRANT OPTION')
                AND NOT EXISTS
                (
                    SELECT 1
                    FROM service_role AS service
                    CROSS JOIN durable_relation AS relation
                    CROSS JOIN relation_privilege AS privilege
                    WHERE has_table_privilege(
                        service.role_name,
                        relation.oid,
                        privilege.privilege_name)
                      <>
                        (
                            service.role_name = 'durable_dispatcher'
                            AND relation.relname IN ('dispatch', 'flow_dispatch')
                            AND privilege.privilege_name = 'SELECT'
                            OR service.role_name = 'durable_runtime'
                            AND
                            (
                                privilege.privilege_name = 'SELECT'
                                AND relation.relname IN
                                (
                                    'store_metadata', 'schema_migration', 'scope', 'work', 'dispatch',
                                    'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
                                    'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer',
                                    'flow_dispatch', 'schedule_definition', 'schedule_generation', 'schedule_command',
                                    'schedule_occurrence', 'schedule_dispatch', 'schedule_history'
                                )
                                OR privilege.privilege_name = 'INSERT'
                                AND relation.relname IN
                                (
                                    'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
                                    'scope_history', 'work_history', 'flow_instance', 'flow_command',
                                    'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'schedule_definition',
                                    'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
                                    'schedule_history'
                                )
                            )
                        )
                )
                AND NOT EXISTS
                (
                    SELECT 1
                    FROM service_role AS service
                    CROSS JOIN durable_column AS column_value
                    CROSS JOIN column_privilege AS privilege
                    WHERE has_column_privilege(
                        service.role_name,
                        column_value.oid,
                        column_value.attnum,
                        privilege.privilege_name)
                      <>
                        (
                            service.role_name = 'durable_dispatcher'
                            AND column_value.relname IN ('dispatch', 'flow_dispatch')
                            AND privilege.privilege_name = 'SELECT'
                            OR service.role_name = 'durable_runtime'
                            AND
                            (
                                privilege.privilege_name = 'SELECT'
                                AND column_value.relname IN
                                (
                                    'store_metadata', 'schema_migration', 'scope', 'work', 'dispatch',
                                    'work_operator_command', 'effect_permit', 'scope_history', 'work_history',
                                    'flow_instance', 'flow_command', 'flow_history', 'flow_wait', 'flow_timer',
                                    'flow_dispatch', 'schedule_definition', 'schedule_generation', 'schedule_command',
                                    'schedule_occurrence', 'schedule_dispatch', 'schedule_history'
                                )
                                OR privilege.privilege_name = 'INSERT'
                                AND column_value.relname IN
                                (
                                    'scope', 'work', 'dispatch', 'work_operator_command', 'effect_permit',
                                    'scope_history', 'work_history', 'flow_instance', 'flow_command',
                                    'flow_history', 'flow_wait', 'flow_timer', 'flow_dispatch', 'schedule_definition',
                                    'schedule_generation', 'schedule_command', 'schedule_occurrence', 'schedule_dispatch',
                                    'schedule_history'
                                )
                                OR privilege.privilege_name = 'UPDATE'
                                AND
                                (
                                    column_value.relname = 'scope'
                                    AND column_value.attname IN ('generation', 'state', 'updated_at')
                                    OR column_value.relname = 'work'
                                    AND column_value.attname IN
                                    (
                                        'state', 'due_at', 'updated_at', 'terminal_at', 'cancellation_requested_at',
                                        'attempt_number', 'lease_generation', 'lease_owner', 'lease_started_at',
                                        'lease_expires_at', 'runtime_epoch', 'revision', 'result_contract_id',
                                        'result_schema_version', 'result_codec_id', 'result_classification',
                                        'result_retention_policy_id', 'result_payload', 'result_sha256', 'terminal_code'
                                    )
                                    OR column_value.relname = 'dispatch'
                                    AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
                                    OR column_value.relname = 'work_operator_command'
                                    AND column_value.attname IN
                                        ('status', 'resulting_state', 'resulting_revision', 'completed_at')
                                    OR column_value.relname = 'effect_permit'
                                    AND column_value.attname IN ('status', 'observed_at', 'details', 'runtime_epoch')
                                    OR column_value.relname = 'flow_instance'
                                    AND column_value.attname IN
                                    (
                                        'state', 'current_node_id', 'context_contract_id', 'context_schema_version',
                                        'context_codec_id', 'context_payload', 'context_sha256', 'context_classification',
                                        'context_retention', 'resume_event_name', 'resume_event_is_timeout',
                                        'resume_event_contract_id', 'resume_event_schema_version', 'resume_event_codec_id',
                                        'resume_event_payload', 'resume_event_sha256', 'resume_event_classification',
                                        'resume_event_retention', 'activity_callsite_id', 'activity_result_contract_id',
                                        'activity_result_schema_version', 'activity_result_codec_id', 'activity_result_payload',
                                        'activity_result_sha256', 'activity_result_classification',
                                        'activity_result_retention', 'lease_generation', 'lease_owner',
                                        'lease_started_at', 'lease_expires_at', 'updated_at',
                                        'cancellation_requested_at', 'terminal_at', 'terminal_code',
                                        'suspension_descriptor', 'suspended_from_state', 'revision',
                                        'scope_generation', 'runtime_epoch'
                                    )
                                    OR column_value.relname = 'flow_wait'
                                    AND column_value.attname IN
                                        ('state', 'resolved_revision', 'resolved_at', 'suspension_descriptor', 'updated_at')
                                    OR column_value.relname = 'flow_timer'
                                    AND column_value.attname IN ('state', 'resolved_at', 'updated_at')
                                    OR column_value.relname = 'flow_dispatch'
                                    AND column_value.attname IN ('due_at', 'state', 'expected_revision', 'updated_at')
                                    OR column_value.relname = 'schedule_definition'
                                    AND column_value.attname IN
                                    (
                                        'display_name', 'state', 'active_generation', 'revision', 'accepted_at_utc',
                                        'cursor_utc', 'next_due_utc', 'scope_generation', 'runtime_epoch',
                                        'suspension_code', 'updated_at'
                                    )
                                    OR column_value.relname = 'schedule_occurrence'
                                    AND column_value.attname IN
                                    (
                                        'last_nominal_utc', 'state', 'target_kind', 'target_id', 'target_command_id',
                                        'target_idempotency_key', 'claimed_by', 'lease_expires_at', 'updated_at'
                                    )
                                    OR column_value.relname = 'schedule_dispatch'
                                    AND column_value.attname IN
                                    (
                                        'dispatch_revision', 'due_at', 'state', 'lease_owner', 'lease_generation',
                                        'lease_expires_at', 'updated_at'
                                    )
                                )
                            )
                        )
                )
                AND NOT EXISTS
                (
                    SELECT 1
                    FROM service_role AS service
                    CROSS JOIN durable_sequence AS sequence_value
                    CROSS JOIN sequence_privilege AS privilege
                    WHERE has_sequence_privilege(
                        service.role_name,
                        sequence_value.oid,
                        privilege.privilege_name)
                      <>
                        (
                            service.role_name = 'durable_runtime'
                            AND privilege.privilege_name IN ('USAGE', 'SELECT')
                        )
                );
            """))
        {
            Assert.True((bool)(await effectivePrivileges.ExecuteScalarAsync())!);
        }

        var reapplied = await RunRoleRecipeAsync(
            container,
            containerRecipePath,
            "durable_owner",
            "durable_dispatcher",
            "durable_runtime");
        Assert.True(
            reapplied.ExitCode == 0,
            $"Role recipe reapply failed with exit {reapplied.ExitCode}. stdout: {reapplied.Stdout} stderr: {reapplied.Stderr}");

        await using (var flowDispatchPolicies = dataSource.CreateCommand(
            """
            SELECT policyname, roles, qual
            FROM pg_policies
            WHERE schemaname = 'appsurface_durable'
              AND tablename = 'flow_dispatch'
              AND policyname IN ('flow_dispatch_global_discovery', 'flow_dispatch_runtime_scope_select')
            ORDER BY policyname;
            """))
        {
            await using var reader = await flowDispatchPolicies.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("flow_dispatch_global_discovery", reader.GetString(0));
            Assert.Equal(["durable_dispatcher"], reader.GetFieldValue<string[]>(1));
            Assert.Equal("true", reader.GetString(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("flow_dispatch_runtime_scope_select", reader.GetString(0));
            Assert.Equal(["durable_runtime"], reader.GetFieldValue<string[]>(1));
            Assert.Contains("appsurface_durable.scope_id", reader.GetString(2), StringComparison.Ordinal);
            Assert.False(await reader.ReadAsync());
        }

        await using (var seedFlowDispatch = dataSource.CreateCommand(
            """
            INSERT INTO appsurface_durable.scope (scope_id)
            VALUES ('flow-rls-scope-a'), ('flow-rls-scope-b');

            INSERT INTO appsurface_durable.flow_instance
            (
                scope_id, flow_instance_id, flow_id, flow_version, manifest_id, authoring_model,
                definition_fingerprint_schema, definition_fingerprint_sha256, current_node_id,
                state, scope_generation, runtime_epoch
            )
            VALUES
            (
                'flow-rls-scope-a', 'flow-rls-instance-a', 'tests.flow', 'v1', 'tests.manifest', 'tests',
                'sha256', repeat('0', 64), 'start', 'ready', 1, @runtime_epoch
            ),
            (
                'flow-rls-scope-b', 'flow-rls-instance-b', 'tests.flow', 'v1', 'tests.manifest', 'tests',
                'sha256', repeat('0', 64), 'start', 'ready', 1, @runtime_epoch
            );

            INSERT INTO appsurface_durable.flow_dispatch
            (
                dispatch_id, scope_id, kind, flow_instance_id, due_at, state, expected_revision
            )
            VALUES
            (@dispatch_a, 'flow-rls-scope-a', 'flow', 'flow-rls-instance-a', clock_timestamp(), 'available', 1),
            (@dispatch_b, 'flow-rls-scope-b', 'flow', 'flow-rls-instance-b', clock_timestamp(), 'available', 1);

            INSERT INTO appsurface_durable.schedule_definition
            (
                scope_id, schedule_id, state, active_generation, revision, accepted_at_utc, cursor_utc,
                next_due_utc, scope_generation, runtime_epoch
            )
            VALUES
            (
                'flow-rls-scope-a', 'role-claim-schedule', 'active', 1, 1,
                clock_timestamp(), clock_timestamp() - interval '1 second', clock_timestamp(), 1, @runtime_epoch
            );

            INSERT INTO appsurface_durable.schedule_generation
            (
                scope_id, schedule_id, generation, accepted_at_utc, schedule_kind, at_utc,
                overlap_kind, overlap_limit, misfire_kind, misfire_limit,
                target_kind, target_name, target_version, target_contract_id, target_schema_version,
                target_classification, target_retention, target_payload, target_sha256, target_provider_safety
            )
            VALUES
            (
                'flow-rls-scope-a', 'role-claim-schedule', 1, clock_timestamp(), 'at', clock_timestamp(),
                'queue_one', 1, 'run_once', 0,
                'work', 'tests.role-claim', 'v1', 'tests.role-claim-payload', 'v1',
                'approved_application', 'default', decode('00', 'hex'), repeat('0', 64), 'idempotent'
            );

            INSERT INTO appsurface_durable.schedule_dispatch
                (scope_id, schedule_id, dispatch_revision, due_at, state)
            VALUES
                ('flow-rls-scope-a', 'role-claim-schedule', 1, clock_timestamp(), 'available');
            """))
        {
            seedFlowDispatch.Parameters.AddWithValue("runtime_epoch", Guid.NewGuid());
            seedFlowDispatch.Parameters.AddWithValue("dispatch_a", Guid.NewGuid());
            seedFlowDispatch.Parameters.AddWithValue("dispatch_b", Guid.NewGuid());
            Assert.Equal(9, await seedFlowDispatch.ExecuteNonQueryAsync());
        }

        var dispatcherConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "durable_dispatcher",
            Password = DispatcherRoleTestPassword,
        }.ConnectionString;
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnectionString);
        await using var dispatcherConnection = await dispatcherDataSource.OpenConnectionAsync();
        await using (var dispatcherRead = dispatcherConnection.CreateCommand())
        {
            dispatcherRead.CommandText = "SELECT count(*) FROM appsurface_durable.flow_dispatch;";
            Assert.Equal(2L, (long)(await dispatcherRead.ExecuteScalarAsync())!);
        }

        var dueAtDisclosure = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var read = dispatcherConnection.CreateCommand();
            read.CommandText = "SELECT due_at FROM appsurface_durable.schedule_dispatch;";
            await read.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, dueAtDisclosure.SqlState);
        var invalidOwner = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var claim = dispatcherConnection.CreateCommand();
            claim.CommandText =
                "SELECT * FROM appsurface_durable.claim_schedule_dispatch(' ', interval '1 second');";
            await claim.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, invalidOwner.SqlState);
        var invalidDuration = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var claim = dispatcherConnection.CreateCommand();
            claim.CommandText =
                "SELECT * FROM appsurface_durable.claim_schedule_dispatch('role-test-dispatcher', interval '0 seconds');";
            await claim.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, invalidDuration.SqlState);
        var excessiveDuration = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var claim = dispatcherConnection.CreateCommand();
            claim.CommandText =
                "SELECT * FROM appsurface_durable.claim_schedule_dispatch('role-test-dispatcher', interval '10 minutes 1 second');";
            await claim.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, excessiveDuration.SqlState);
        await using (var claim = dispatcherConnection.CreateCommand())
        {
            claim.CommandText =
                "SELECT scope_id, schedule_id, dispatch_revision FROM appsurface_durable.claim_schedule_dispatch('role-test-dispatcher', interval '1 second');";
            await using var reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("flow-rls-scope-a", reader.GetString(0));
            Assert.Equal("role-claim-schedule", reader.GetString(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.False(await reader.ReadAsync());
        }

        var runtimeConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "durable_runtime",
            Password = RuntimeRoleTestPassword,
        }.ConnectionString;
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
        await using var runtimeConnection = await runtimeDataSource.OpenConnectionAsync();
        var runtimeClaim = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var claim = runtimeConnection.CreateCommand();
            claim.CommandText =
                "SELECT * FROM appsurface_durable.claim_schedule_dispatch('role-test-runtime', interval '1 second');";
            await claim.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, runtimeClaim.SqlState);
        await using (var allowedRead = runtimeConnection.CreateCommand())
        {
            allowedRead.CommandText =
                "SELECT current_user = 'durable_runtime' AND EXISTS (SELECT 1 FROM appsurface_durable.store_metadata);";
            Assert.True((bool)(await allowedRead.ExecuteScalarAsync())!);
        }

        await using (var unscopedRead = runtimeConnection.CreateCommand())
        {
            unscopedRead.CommandText = "SELECT count(*) FROM appsurface_durable.flow_dispatch;";
            Assert.Equal(0L, (long)(await unscopedRead.ExecuteScalarAsync())!);
        }

        await using (var scopedTransaction = await runtimeConnection.BeginTransactionAsync())
        {
            await using (var setScope = new NpgsqlCommand(
                "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
                runtimeConnection,
                scopedTransaction))
            {
                setScope.Parameters.AddWithValue("scope_id", "flow-rls-scope-a");
                await setScope.ExecuteNonQueryAsync();
            }

            await using (var scopedRead = new NpgsqlCommand(
                "SELECT scope_id FROM appsurface_durable.flow_dispatch ORDER BY scope_id;",
                runtimeConnection,
                scopedTransaction))
            await using (var reader = await scopedRead.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("flow-rls-scope-a", reader.GetString(0));
                Assert.False(await reader.ReadAsync());
            }

            await using (var scopedUpdate = new NpgsqlCommand(
                "UPDATE appsurface_durable.flow_dispatch SET due_at = due_at + interval '1 second' WHERE scope_id = @scope_id;",
                runtimeConnection,
                scopedTransaction))
            {
                scopedUpdate.Parameters.AddWithValue("scope_id", "flow-rls-scope-a");
                Assert.Equal(1, await scopedUpdate.ExecuteNonQueryAsync());
            }

            await using (var crossScopeUpdate = new NpgsqlCommand(
                "UPDATE appsurface_durable.flow_dispatch SET due_at = due_at + interval '1 second' WHERE scope_id = @scope_id;",
                runtimeConnection,
                scopedTransaction))
            {
                crossScopeUpdate.Parameters.AddWithValue("scope_id", "flow-rls-scope-b");
                Assert.Equal(0, await crossScopeUpdate.ExecuteNonQueryAsync());
            }

            await scopedTransaction.CommitAsync();
        }

        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var disableRls = runtimeConnection.CreateCommand();
            disableRls.CommandText = "ALTER TABLE appsurface_durable.work DISABLE ROW LEVEL SECURITY;";
            await disableRls.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
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
           SET schema_version = 4,
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
           DELETE FROM appsurface_durable.schema_migration WHERE version IN (3, 4);
           UPDATE appsurface_durable.store_metadata
           SET schema_version = 2,
               minimum_reader_version = 1,
               maximum_reader_version = 2,
               minimum_writer_version = 1,
               maximum_writer_version = 2
           WHERE singleton;
           """);
        var upgrade = await manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, upgrade.Compatibility);
        Assert.Equal([1, 2], upgrade.AppliedVersions);
        Assert.Equal([3, 4], upgrade.PendingVersions);
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

        Assert.Equal([1, 2, 3, 4], results.SelectMany(result => result.AppliedVersions).Order().ToArray());
        Assert.Contains(results, result => result.AppliedVersions.SequenceEqual([1, 2, 3, 4]));
        Assert.Contains(results, result => result.AppliedVersions.Count == 0);
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.schema_migration;");
        Assert.Equal(4, (long)(await count.ExecuteScalarAsync())!);
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
        Assert.Equal([1, 2, 3, 4], applied.AppliedVersions);
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
        Assert.Equal([1, 2, 3, 4], applied.AppliedVersions);
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
                "env",
                $"PGAPPNAME={RoleRecipeApplicationName}",
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
