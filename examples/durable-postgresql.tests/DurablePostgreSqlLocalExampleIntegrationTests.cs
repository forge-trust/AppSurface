using ForgeTrust.AppSurface.Durable.PostgreSql;
using ForgeTrust.AppSurface.Durable.Provider;
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
    private const string MigrationOwnerPassword = "durable-owner-test-password";
    private const string DispatcherPassword = "durable-dispatcher-test-password";
    private const string RuntimePassword = "durable-runtime-test-password";
    private const string RoleRecipeContainerPath = "/tmp/configure-postgresql-roles.sql";
    private const string PostgreSqlImage =
        "postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302";

    [Fact]
    public async Task Commands_bootstrap_and_verify_the_restricted_local_postgresql_proof()
    {
        var roleRecipePath = Path.Combine(FindRepositoryRoot(), "Durable", "configure-postgresql-roles.sql");
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

    private static async Task CreateTutorialRolesAsync(NpgsqlDataSource administratorDataSource)
    {
        await using var command = administratorDataSource.CreateCommand(
            $"""
            CREATE ROLE {MigrationOwnerRole} LOGIN PASSWORD '{MigrationOwnerPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {DispatcherRole} LOGIN PASSWORD '{DispatcherPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {RuntimeRole} LOGIN PASSWORD '{RuntimePassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            """);
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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ForgeTrust.AppSurface.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root for the durable role recipe.");
    }

    private static void AssertWorkerSchemaGuardRejectsEveryChange()
    {
        var storeId = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var baseline = new DurableRuntimeSchemaStatus(
            DurableRuntimeSchemaCompatibility.Compatible,
            storeId,
            runtimeEpoch,
            installedVersion: 6,
            requiredVersion: 6,
            minimumReaderVersion: 1,
            maximumReaderVersion: 6,
            minimumWriterVersion: 1,
            maximumWriterVersion: 6,
            appliedVersions: [1, 2, 3, 4, 5, 6],
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
