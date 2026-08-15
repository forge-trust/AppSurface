using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

/// <summary>Verifies the tutorial commands against the same restricted PostgreSQL roles they document.</summary>
[Collection(DurablePostgreSqlLocalExampleCollection.Name)]
public sealed class DurablePostgreSqlLocalExampleIntegrationTests
{
    private const string DatabaseName = "appsurface_durable";
    private const string AdministratorUser = "appsurface";
    private const string AdministratorPassword = "appsurface-test-password";
    private const string MigrationOwnerRole = "appsurface_durable_owner";
    private const string DispatcherRole = "appsurface_durable_dispatcher";
    private const string RuntimeRole = "appsurface_durable_runtime";
    private const string RetentionOperatorRole = "appsurface_durable_retention";
    private const string MigrationOwnerPassword = "durable-owner-test-password";
    private const string DispatcherPassword = "durable-dispatcher-test-password";
    private const string RuntimePassword = "durable-runtime-test-password";
    private const string RetentionOperatorPassword = "durable-retention-test-password";
    private const string RoleRecipeContainerPath = "/tmp/configure-postgresql-roles.sql";
    private const string PostgreSqlImage =
        "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877";

    [Fact]
    public async Task Commands_bootstrap_and_verify_the_restricted_local_postgresql_proof()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var roleRecipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable", "configure-postgresql-roles.sql");
        await using var container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase(DatabaseName)
            .WithUsername(AdministratorUser)
            .WithPassword(AdministratorPassword)
            .WithResourceMapping(File.ReadAllBytes(roleRecipePath), RoleRecipeContainerPath)
            .Build();
        await container.StartAsync();

        await using var administratorDataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await CreateTutorialRolesAsync(administratorDataSource);

        var runtimeEpoch = Guid.NewGuid().ToString("D");
        using var development = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Development");
        using var confirmation = new EnvironmentVariableScope("APPSURFACE_DURABLE_LOCAL_PROOF", "1");
        using var migrationConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_MIGRATION_CONNECTION",
            ConnectionStringForRole(container.GetConnectionString(), MigrationOwnerRole, MigrationOwnerPassword));
        using var dispatcherConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_DISPATCHER_CONNECTION",
            ConnectionStringForRole(container.GetConnectionString(), DispatcherRole, DispatcherPassword));
        using var runtimeConnection = new EnvironmentVariableScope(
            "APPSURFACE_DURABLE_RUNTIME_CONNECTION",
            ConnectionStringForRole(container.GetConnectionString(), RuntimeRole, RuntimePassword));
        using var epoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", runtimeEpoch);

        Assert.Equal(1, await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev"], CancellationToken.None));
        Assert.Equal(1, await DurablePostgreSqlLocalExample.RunAsync(["verify-local"], CancellationToken.None));

        await new PostgreSqlDurableRuntimeSchemaManager(administratorDataSource).ApplyAsync();
        var roleRecipe = await container.ExecAsync(
            [
                "env",
                "PGAPPNAME=durable-local-example-coverage",
                "psql",
                "-U", AdministratorUser,
                "-d", DatabaseName,
                "-v", $"migration_owner_role={MigrationOwnerRole}",
                "-v", $"dispatcher_role={DispatcherRole}",
                "-v", $"runtime_role={RuntimeRole}",
                "-v", $"retention_operator_role={RetentionOperatorRole}",
                "-f", RoleRecipeContainerPath,
            ]);
        Assert.True(
            roleRecipe.ExitCode == 0,
            $"Role recipe failed with exit {roleRecipe.ExitCode}. stdout: {roleRecipe.Stdout} stderr: {roleRecipe.Stderr}");

        Assert.Equal(0, await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev"], CancellationToken.None));
        Assert.Equal(1, await DurablePostgreSqlLocalExample.RunAsync(["schema-bootstrap-dev"], CancellationToken.None));
        using (var mismatchedEpoch = new EnvironmentVariableScope("APPSURFACE_DURABLE_RUNTIME_EPOCH", Guid.NewGuid().ToString("D")))
        {
            Assert.Equal(1, await DurablePostgreSqlLocalExample.RunAsync(["verify-local"], CancellationToken.None));
        }

        using (var incorrectRuntimeRole = new EnvironmentVariableScope(
                   "APPSURFACE_DURABLE_RUNTIME_CONNECTION",
                   ConnectionStringForRole(container.GetConnectionString(), MigrationOwnerRole, MigrationOwnerPassword)))
        {
            Assert.Equal(1, await DurablePostgreSqlLocalExample.RunAsync(["verify-local"], CancellationToken.None));
        }

        Assert.Equal(0, await DurablePostgreSqlLocalExample.RunAsync(["verify-local"], CancellationToken.None));
        await AssertProofStateAsync(administratorDataSource, Guid.Parse(runtimeEpoch));
        AssertWorkerSchemaGuardRejectsEveryChange();
    }

    [Fact]
    public void RuntimeHealthCheckpoint_rejects_missing_schema_or_epoch_authorization()
    {
        DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(CreateHealthSnapshot(schemaCompatible: true, epochCompatible: true));

        Assert.Throws<ArgumentNullException>(() => DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(null!));
        Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(CreateHealthSnapshot(schemaCompatible: false, epochCompatible: true)));
        Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureRuntimeHealthIsCompatible(CreateHealthSnapshot(schemaCompatible: true, epochCompatible: false)));
    }

    [Fact]
    public async Task WorkerSweep_reports_a_timeout_when_no_hosted_pass_completes()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await DurablePostgreSqlLocalExample.WaitForHostedWorkerSweepAsync(
                health: new StaticRuntimeHealth(CreateHealthSnapshot(schemaCompatible: true, epochCompatible: true)),
                baselineSuccessfulSweep: null,
                cancellationToken: CancellationToken.None,
                waitTimeout: TimeSpan.FromMilliseconds(1)));

        Assert.Contains("within 0.001 seconds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerCatalogFingerprint_detects_every_schema_definition_type_that_worker_startup_must_not_change()
    {
        await using var container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase(DatabaseName)
            .WithUsername(AdministratorUser)
            .WithPassword(AdministratorPassword)
            .Build();
        await container.StartAsync();

        await using var administratorDataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var schemaManager = new PostgreSqlDurableRuntimeSchemaManager(administratorDataSource);
        await schemaManager.ApplyAsync();
        var schemaStatus = await schemaManager.GetStatusAsync();

        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE TABLE appsurface_durable.fingerprint_constraint_test (value integer NOT NULL);
            """,
            """
            ALTER TABLE appsurface_durable.fingerprint_constraint_test
                ADD CONSTRAINT fingerprint_constraint_test_value_check CHECK (value >= 0);
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE TABLE appsurface_durable.fingerprint_trigger_test (value integer NOT NULL);
            CREATE FUNCTION appsurface_durable.fingerprint_trigger_function()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RETURN NEW;
            END;
            $function$;
            """,
            """
            CREATE TRIGGER fingerprint_trigger_test_before_insert
                BEFORE INSERT ON appsurface_durable.fingerprint_trigger_test
                FOR EACH ROW EXECUTE FUNCTION appsurface_durable.fingerprint_trigger_function();
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE TABLE appsurface_durable.fingerprint_policy_test (value integer NOT NULL);
            ALTER TABLE appsurface_durable.fingerprint_policy_test ENABLE ROW LEVEL SECURITY;
            """,
            """
            CREATE POLICY fingerprint_policy_test_select
                ON appsurface_durable.fingerprint_policy_test
                FOR SELECT
                USING (value >= 0);
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE FUNCTION appsurface_durable.fingerprint_function_test()
            RETURNS integer
            LANGUAGE sql
            AS 'SELECT 1';
            """,
            """
            CREATE OR REPLACE FUNCTION appsurface_durable.fingerprint_function_test()
            RETURNS integer
            LANGUAGE sql
            AS 'SELECT 2';
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE TABLE appsurface_durable.fingerprint_default_test (value integer DEFAULT 1);
            """,
            """
            ALTER TABLE appsurface_durable.fingerprint_default_test
                ALTER COLUMN value SET DEFAULT 2;
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE TABLE appsurface_durable.fingerprint_index_test (value integer NOT NULL);
            CREATE INDEX fingerprint_index_test_value ON appsurface_durable.fingerprint_index_test (value);
            """,
            """
            DROP INDEX appsurface_durable.fingerprint_index_test_value;
            CREATE INDEX fingerprint_index_test_value
                ON appsurface_durable.fingerprint_index_test (value)
                WHERE value >= 0;
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE VIEW appsurface_durable.fingerprint_view_test AS
                SELECT 1 AS value;
            """,
            """
            CREATE OR REPLACE VIEW appsurface_durable.fingerprint_view_test AS
                SELECT 2 AS value;
            """);
        await AssertCatalogMutationIsDetectedAsync(
            administratorDataSource,
            schemaStatus,
            """
            CREATE MATERIALIZED VIEW appsurface_durable.fingerprint_materialized_view_test AS
                SELECT 1 AS value
                WITH NO DATA;
            """,
            """
            DROP MATERIALIZED VIEW appsurface_durable.fingerprint_materialized_view_test;
            CREATE MATERIALIZED VIEW appsurface_durable.fingerprint_materialized_view_test AS
                SELECT 2 AS value
                WITH NO DATA;
            """);
    }

    private static async Task CreateTutorialRolesAsync(NpgsqlDataSource administratorDataSource)
    {
        await using var command = administratorDataSource.CreateCommand(
            $"""
            CREATE ROLE {MigrationOwnerRole} LOGIN PASSWORD '{MigrationOwnerPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {DispatcherRole} LOGIN PASSWORD '{DispatcherPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {RuntimeRole} LOGIN PASSWORD '{RuntimePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {RetentionOperatorRole} LOGIN PASSWORD '{RetentionOperatorPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCatalogMutationIsDetectedAsync(
        NpgsqlDataSource administratorDataSource,
        DurableRuntimeSchemaStatus schemaStatus,
        string setupSql,
        string mutationSql)
    {
        await ExecuteSqlAsync(administratorDataSource, setupSql);
        var catalogBefore = await DurablePostgreSqlLocalExample.ReadDurableCatalogFingerprintAsync(administratorDataSource, CancellationToken.None);
        await ExecuteSqlAsync(administratorDataSource, mutationSql);
        var catalogAfter = await DurablePostgreSqlLocalExample.ReadDurableCatalogFingerprintAsync(administratorDataSource, CancellationToken.None);

        Assert.NotEqual(catalogBefore, catalogAfter);
        Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureWorkerHostDidNotChangeSchema(schemaStatus, schemaStatus, catalogBefore, catalogAfter));
    }

    private static async Task ExecuteSqlAsync(NpgsqlDataSource administratorDataSource, string sql)
    {
        await using var command = administratorDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string ConnectionStringForRole(string administratorConnectionString, string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(administratorConnectionString)
        {
            Username = role,
            Password = password,
        };
        return builder.ConnectionString;
    }

    private static void AssertWorkerSchemaGuardRejectsEveryChange()
    {
        var storeId = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var baseline = new DurableRuntimeSchemaStatus(
            DurableRuntimeSchemaCompatibility.Compatible,
            storeId,
            runtimeEpoch,
            installedVersion: 8,
            requiredVersion: 8,
            minimumReaderVersion: 1,
            maximumReaderVersion: 8,
            minimumWriterVersion: 1,
            maximumWriterVersion: 8,
            appliedVersions: [1, 2, 3, 4, 5, 6, 7, 8],
            pendingVersions: [],
            problem: null);

        DurablePostgreSqlLocalExample.EnsureWorkerHostDidNotChangeSchema(baseline, baseline, "before", "before");

        foreach (var changed in new[]
                 {
                     CreateStatus(baseline, storeId: Guid.NewGuid()),
                     CreateStatus(baseline, activeRuntimeEpoch: Guid.NewGuid()),
                     CreateStatus(baseline, installedVersion: 5),
                     CreateStatus(baseline, requiredVersion: 5),
                     CreateStatus(baseline, appliedVersions: [1, 2, 3, 4, 5]),
                 })
        {
            Assert.Throws<InvalidOperationException>(() =>
                DurablePostgreSqlLocalExample.EnsureWorkerHostDidNotChangeSchema(baseline, changed, "before", "before"));
        }

        Assert.Throws<InvalidOperationException>(() =>
            DurablePostgreSqlLocalExample.EnsureWorkerHostDidNotChangeSchema(baseline, baseline, "before", "after"));
    }

    private static DurableRuntimeSchemaStatus CreateStatus(
        DurableRuntimeSchemaStatus baseline,
        Guid? storeId = null,
        Guid? activeRuntimeEpoch = null,
        int? installedVersion = null,
        int? requiredVersion = null,
        IReadOnlyList<int>? appliedVersions = null) =>
        new(
            DurableRuntimeSchemaCompatibility.Compatible,
            storeId ?? baseline.StoreId,
            activeRuntimeEpoch ?? baseline.ActiveRuntimeEpoch,
            installedVersion ?? baseline.InstalledVersion,
            requiredVersion ?? baseline.RequiredVersion,
            baseline.MinimumReaderVersion,
            baseline.MaximumReaderVersion,
            baseline.MinimumWriterVersion,
            baseline.MaximumWriterVersion,
            appliedVersions ?? baseline.AppliedVersions,
            baseline.PendingVersions,
            baseline.Problem);

    private static DurableRuntimeHealthSnapshot CreateHealthSnapshot(bool schemaCompatible, bool epochCompatible)
    {
        var runtimeEpoch = Guid.NewGuid();
        return new DurableRuntimeHealthSnapshot(
            DurableRuntimeHealthState.Healthy,
            problemCode: null,
            schemaCompatible: schemaCompatible,
            epochCompatible: epochCompatible,
            installedSchemaVersion: 6,
            requiredSchemaVersion: 6,
            configuredRuntimeEpoch: runtimeEpoch,
            activeRuntimeEpoch: runtimeEpoch,
            workerId: "durable-local-proof",
            workerInstanceId: null,
            hostedSurfaces: DurableRuntimeSurface.All,
            observedAtUtc: DateTimeOffset.UtcNow,
            startedAtUtc: null,
            lastHeartbeatAtUtc: null,
            lastSuccessfulSweepAtUtc: null,
            isDraining: false,
            isPassActive: false,
            dueDispatchCount: 0,
            oldestDueAtUtc: null,
            oldestDueAge: null);
    }

    private sealed class StaticRuntimeHealth(DurableRuntimeHealthSnapshot snapshot) : IDurableRuntimeHealth
    {
        public ValueTask<DurableRuntimeHealthSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);
    }

    private static async Task AssertProofStateAsync(NpgsqlDataSource administratorDataSource, Guid runtimeEpoch)
    {
        var status = await new PostgreSqlDurableRuntimeSchemaManager(administratorDataSource).GetStatusAsync();
        Assert.True(status.IsCompatible);
        Assert.Equal(runtimeEpoch, status.ActiveRuntimeEpoch);

        await using var command = administratorDataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM appsurface_durable.work) AS work_count,
                (SELECT count(*) FROM appsurface_durable.flow_instance) AS flow_count,
                (SELECT count(*) FROM appsurface_durable.schedule_definition) AS schedule_count,
                (SELECT count(*) FROM appsurface_durable.flow_trace_context) AS trace_context_count,
                (SELECT count(*) FROM appsurface_durable.runtime_heartbeat) AS heartbeat_count;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetInt64(0) > 0, "The local proof should persist its accepted Work.");
        Assert.True(reader.GetInt64(1) > 0, "The local proof should persist its Flow instance.");
        Assert.True(reader.GetInt64(2) > 0, "The local proof should persist its Schedule definition.");
        Assert.True(reader.GetInt64(3) > 0, "The local proof should persist W3C Flow trace context.");
        Assert.True(reader.GetInt64(4) > 0, "The hosted worker should persist a heartbeat after its bounded sweep.");
        Assert.False(await reader.ReadAsync());
    }
}
