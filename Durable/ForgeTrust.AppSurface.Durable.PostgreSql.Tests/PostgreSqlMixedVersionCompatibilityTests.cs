using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

/// <summary>
/// Discovers the exact historical-package proof only when the release lane has explicitly supplied its artifacts.
/// </summary>
internal sealed class ExactPackageReleaseProofFactAttribute : FactAttribute
{
    internal const string EnvironmentVariableName = "APPSURFACE_REQUIRE_PREVIOUS_PACKAGE_RELEASE_PROOF";

    /// <summary>Initializes the conditional release-proof fact.</summary>
    public ExactPackageReleaseProofFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariableName),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip =
                "The exact immediately previous package proof runs only in the explicit PostgreSQL release-proof lane.";
        }
    }
}

public sealed class PostgreSqlMixedVersionCompatibilityTests
{
    private const string ExactOldPackageVersion = "0.2.0-preview.8";
    private const string ExactOldPackageSha256 = "62a48f6b7ec299ad608f3714a49a39f18c5cb1fe45293d53915e5549422d311e";
    private const string ExactOldPackageCommit = "b34970c87489a63b132531657c043e075b092e5e";
    private const string PinnedPreview8SourceRoleRecipeSha256 = "14ad5affdc9872977318f798a8027d4d8783bd37bb099b6c58e3da875b88b3ab";
    // This is the immutable schema-11 role recipe from origin/main at 7ae380845ede06029d388232f49470ae34bef6e1.
    private const string PinnedSchema11RoleRecipeSha256 = "fbcb1aaa17d39a22ad0b19260c3532017e9cb0f30491a8096f6e8107d9c60a24";
    private const string HarnessPathEnvironment = "APPSURFACE_DURABLE_V020_HARNESS_PATH";
    private const string PackagePathEnvironment = "APPSURFACE_DURABLE_V020_PACKAGE_PATH";
    private const string DatabaseName = "appsurface_durable";
    private const string MigrationUser = "appsurface";
    private const string MigrationOwnerRole = "durable_owner";
    private const string DispatcherRole = "durable_dispatcher";
    private const string RuntimeRole = "durable_runtime";
    private const string RetentionRole = "durable_retention";
    private const string Preview8ContainerRoleRecipePath = "/tmp/configure-postgresql-roles-preview8.sql";
    private const string Schema11ContainerRoleRecipePath = "/tmp/configure-postgresql-roles-schema11.sql";
    private const string CurrentContainerRoleRecipePath = "/tmp/configure-postgresql-roles-current.sql";

    [Fact]
    public async Task ConcurrentIdenticalFlowStart_HasOneAcceptedWinnerAndStableDuplicates()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        await manager.InitializeRuntimeEpochAsync(epoch, "tests", "concurrent-start");
        var status = await manager.GetStatusAsync();
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.concurrent.flow", "v1");
        var payloads = new DurablePayloadCodecRegistry([contextCodec]);
        var work = new DurableWorkRegistry([]);
        var flow = new CompatibilityFlowRegistration(contextCodec);
        var flows = new DurableFlowRegistry([flow], work, payloads);
        var client = new PostgreSqlDurableFlowClient(
            database.DataSource,
            flows,
            payloads,
            new PostgreSqlDurableWorkOptions(epoch, status.StoreId));
        var request = new DurableFlowStartRequest(
            new DurableScopeId("concurrent-start"),
            new DurableCommandId("same-command"),
            "same-key",
            new DurableFlowInstanceId("same-flow"),
            flow.FlowId,
            flow.FlowVersion,
            contextCodec.EncodeObject(new byte[] { 1 }));

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => client.StartAsync(request).AsTask()));

        Assert.Single(outcomes, result => result.Value!.Outcome == DurableFlowCommandOutcome.Accepted);
        Assert.Equal(7, outcomes.Count(result => result.Value!.Outcome == DurableFlowCommandOutcome.Duplicate));
    }

    [Fact]
    public async Task TwoV12HostsDrainAndRemainCompatibleForRollbackAfterVersion13()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var migrations = DurablePostgreSqlMigrationCatalog.Load();
        var current = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, migrations);
        await current.ApplyAsync();
        var runtimeEpoch = Guid.NewGuid();
        await current.InitializeRuntimeEpochAsync(runtimeEpoch, "mixed-version-two-host-proof", "schema12-activation");

        var previousPackageCatalog = migrations.Take(9).ToArray();
        var previousPackage = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, previousPackageCatalog);
        var oldStatus = await previousPackage.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.StoreTooNew, oldStatus.Compatibility);
        Assert.Equal(12, oldStatus.InstalledVersion);
        Assert.Equal(9, oldStatus.RequiredVersion);
        await Assert.ThrowsAsync<DurableRuntimeSchemaException>(async () => await previousPackage.ValidateAsync());

        await RunCurrentPackageWorkPassAsync(
            database.CreateDataSource(),
            database.CreateDataSource(),
            runtimeEpoch,
            oldStatus.StoreId,
            "compatibility-upgrade-host-a",
            "compatibility-schema12-host-a");

        const string futureSql = "SELECT 1;";
        var futureHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(futureSql)));
        var futureMigration = new DurablePostgreSqlMigration(13, "future_compatibility_probe", futureSql, futureHash);
        var futureCatalog = migrations.Append(futureMigration).ToArray();
        var future = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, futureCatalog);
        var applied = await future.ApplyAsync();
        Assert.Equal([13], applied.AppliedVersions);

        var hostA = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, migrations);
        var hostB = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource, migrations);
        var hostAStatus = await hostA.GetStatusAsync();
        var hostBStatus = await hostB.GetStatusAsync();

        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, hostAStatus.Compatibility);
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, hostBStatus.Compatibility);
        Assert.Equal(13, hostAStatus.InstalledVersion);
        Assert.Equal(13, hostBStatus.InstalledVersion);
        Assert.Equal(12, hostAStatus.RequiredVersion);
        Assert.Equal(12, hostBStatus.RequiredVersion);
        Assert.Equal(12, hostAStatus.MinimumReaderVersion);
        Assert.Equal(12, hostBStatus.MinimumReaderVersion);
        Assert.Equal(12, hostAStatus.MinimumWriterVersion);
        Assert.Equal(12, hostBStatus.MinimumWriterVersion);
        await hostA.ValidateAsync();
        await hostB.ValidateAsync();

        await RunCurrentPackageWorkPassAsync(
            database.CreateDataSource(),
            database.CreateDataSource(),
            runtimeEpoch,
            hostBStatus.StoreId,
            "compatibility-rollback-host-b",
            "compatibility-schema13-host-b");

        await using var history = database.DataSource.CreateCommand(
            "SELECT array_agg(version ORDER BY version) FROM appsurface_durable.schema_migration;");
        var versions = await history.ExecuteScalarAsync();
        Assert.Equal(Enumerable.Range(1, 13).ToArray(), Assert.IsType<int[]>(versions));
        await using var completedWork = database.DataSource.CreateCommand(
            "SELECT scope_id, state FROM appsurface_durable.work WHERE scope_id IN ('compatibility-schema12-host-a', 'compatibility-schema13-host-b') ORDER BY scope_id;");
        await using var completedReader = await completedWork.ExecuteReaderAsync();
        Assert.True(await completedReader.ReadAsync());
        Assert.Equal("compatibility-schema12-host-a", completedReader.GetString(0));
        Assert.Equal("succeeded", completedReader.GetString(1));
        Assert.True(await completedReader.ReadAsync());
        Assert.Equal("compatibility-schema13-host-b", completedReader.GetString(0));
        Assert.Equal("succeeded", completedReader.GetString(1));
        Assert.False(await completedReader.ReadAsync());
    }

    [ExactPackageReleaseProofFact]
    public async Task ExactPreviousPackage_OperatesOnSchema11ThenRefusesSchema12()
    {
        var harnessPath = RequireFile(HarnessPathEnvironment);
        var packagePath = RequireFile(PackagePathEnvironment);
        Assert.Equal(ExactOldPackageSha256, await ComputeSha256Async(packagePath));

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var currentRoleRecipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable/configure-postgresql-roles.sql");
        var migrationPassword = CreateEphemeralPassword();
        var dispatcherPassword = CreateEphemeralPassword();
        var runtimePassword = CreateEphemeralPassword();
        var retentionPassword = CreateEphemeralPassword();

        await using var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase(DatabaseName)
            .WithUsername(MigrationUser)
            .WithPassword(migrationPassword)
            .WithResourceMapping(ReadPinnedHistoricalRoleRecipe(repositoryRoot), Preview8ContainerRoleRecipePath)
            .WithResourceMapping(ReadPinnedSchema11RoleRecipe(repositoryRoot), Schema11ContainerRoleRecipePath)
            .WithResourceMapping(File.ReadAllBytes(currentRoleRecipePath), CurrentContainerRoleRecipePath)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();

        await using var migrationDataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var migrations = DurablePostgreSqlMigrationCatalog.Load();
        Assert.Equal(12, migrations.Count);

        var schema9Manager = new PostgreSqlDurableRuntimeSchemaManager(
            migrationDataSource,
            migrations.Take(9).ToArray());
        var schema9Apply = await schema9Manager.ApplyAsync();
        Assert.Equal(Enumerable.Range(1, 9).ToArray(), schema9Apply.AppliedVersions);

        var schema10Manager = new PostgreSqlDurableRuntimeSchemaManager(
            migrationDataSource,
            migrations.Take(10).ToArray());
        var schema10Apply = await schema10Manager.ApplyAsync();
        Assert.Equal([10], schema10Apply.AppliedVersions);

        var schema11Manager = new PostgreSqlDurableRuntimeSchemaManager(
            migrationDataSource,
            migrations.Take(11).ToArray());
        var schema11Apply = await schema11Manager.ApplyAsync();
        Assert.Equal([11], schema11Apply.AppliedVersions);
        var schema11Status = await schema11Manager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, schema11Status.Compatibility);
        Assert.Equal(11, schema11Status.InstalledVersion);
        Assert.Equal(11, schema11Status.RequiredVersion);
        await schema11Manager.ValidateAsync();

        await CreateRestrictedRolesAsync(
            migrationDataSource,
            dispatcherPassword,
            runtimePassword,
            retentionPassword);
        var schema11RecipeResult = await RunRoleRecipeAsync(container, Schema11ContainerRoleRecipePath);
        Assert.True(
            schema11RecipeResult.ExitCode == 0,
            $"The immutable schema-11 role recipe from 7ae380845ede06029d388232f49470ae34bef6e1 failed. stdout: {schema11RecipeResult.Stdout} stderr: {schema11RecipeResult.Stderr}");

        var runtimeEpoch = Guid.NewGuid();
        await schema11Manager.InitializeRuntimeEpochAsync(
            runtimeEpoch,
            "mixed-version-release-proof",
            "schema11-activation");

        var dispatcherConnectionString = CreateRoleConnectionString(
            container.GetConnectionString(),
            DispatcherRole,
            dispatcherPassword,
            "appsurface-v020-dispatcher");
        var runtimeConnectionString = CreateRoleConnectionString(
            container.GetConnectionString(),
            RuntimeRole,
            runtimePassword,
            "appsurface-v020-runtime");
        await using var dispatcherDataSource = NpgsqlDataSource.Create(dispatcherConnectionString);
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);

        var schema11Checkpoints = await Task.WhenAll(
            RunOldPackageHarnessAsync(
                harnessPath,
                packagePath,
                dispatcherConnectionString,
                runtimeConnectionString,
                runtimeEpoch,
                schema11Status.StoreId,
                "host-a"),
            RunOldPackageHarnessAsync(
                harnessPath,
                packagePath,
                dispatcherConnectionString,
                runtimeConnectionString,
                runtimeEpoch,
                schema11Status.StoreId,
                "host-b"));
        Assert.Equal(2, schema11Checkpoints.Length);
        var schema11HostIds = schema11Checkpoints
            .Select(checkpoint => checkpoint.GetProperty("HostId").GetString()
                ?? throw new InvalidDataException("A preview.8 host checkpoint has no HostId."))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            schema11HostIds.SequenceEqual(["host-a", "host-b"], StringComparer.Ordinal),
            $"Expected schema-11 hosts host-a and host-b; observed {JsonSerializer.Serialize(schema11HostIds)}.");
        foreach (var schema11Checkpoint in schema11Checkpoints)
        {
            Assert.Equal("previous-package-schema11-drained", schema11Checkpoint.GetProperty("Phase").GetString());
            Assert.True(schema11Checkpoint.GetProperty("DrainedBeforeSchemaUpgrade").GetBoolean());
            Assert.Equal(ExactOldPackageVersion, schema11Checkpoint.GetProperty("PackageVersion").GetString());
            Assert.Equal(ExactOldPackageSha256, schema11Checkpoint.GetProperty("PackageSha256").GetString());
            Assert.Equal(ExactOldPackageCommit, schema11Checkpoint.GetProperty("RepositoryCommit").GetString());
            var oldProviderInformationalVersion = schema11Checkpoint.GetProperty("ProviderInformationalVersion").GetString();
            Assert.NotNull(oldProviderInformationalVersion);
            Assert.StartsWith(ExactOldPackageVersion, oldProviderInformationalVersion, StringComparison.Ordinal);
            Assert.True(schema11Checkpoint.GetProperty("StartupValidated").GetBoolean());
            Assert.True(schema11Checkpoint.GetProperty("LegacyHealthObserved").GetBoolean());
            Assert.Equal("NotStarted", schema11Checkpoint.GetProperty("InitialHealthState").GetString());
            Assert.Equal("Healthy", schema11Checkpoint.GetProperty("HealthyAfterWork").GetString());
            Assert.True(schema11Checkpoint.GetProperty("HeartbeatMaintained").GetBoolean());
            var oldSchema = schema11Checkpoint.GetProperty("Schema");
            Assert.Equal(11, oldSchema.GetProperty("InstalledVersion").GetInt32());
            Assert.Equal(9, oldSchema.GetProperty("RequiredVersion").GetInt32());
            Assert.Equal("Compatible", oldSchema.GetProperty("Compatibility").GetString());
            var oldWork = schema11Checkpoint.GetProperty("Work");
            Assert.Equal("Succeeded", oldWork.GetProperty("State").GetString());
            Assert.Equal(1, oldWork.GetProperty("InvocationCount").GetInt32());
            Assert.Equal(1, oldWork.GetProperty("Processed").GetInt32());
            Assert.Equal(0, oldWork.GetProperty("Failed").GetInt32());
        }

        var currentManager = new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource, migrations);
        var schema12Apply = await currentManager.ApplyAsync();
        Assert.Equal([12], schema12Apply.AppliedVersions);
        var schema12Status = await currentManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, schema12Status.Compatibility);
        Assert.Equal(12, schema12Status.InstalledVersion);
        Assert.Equal(12, schema12Status.RequiredVersion);
        Assert.Equal(12, schema12Status.MinimumReaderVersion);
        Assert.Equal(12, schema12Status.MinimumWriterVersion);

        var currentRecipeResult = await RunRoleRecipeAsync(container, CurrentContainerRoleRecipePath);
        Assert.True(
            currentRecipeResult.ExitCode == 0,
            $"The current role recipe failed after schema 12. stdout: {currentRecipeResult.Stdout} stderr: {currentRecipeResult.Stderr}");

        var currentRuntimeManager = new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource, migrations);
        var currentRuntimeStatus = await currentRuntimeManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, currentRuntimeStatus.Compatibility);
        Assert.Equal(12, currentRuntimeStatus.InstalledVersion);
        await currentRuntimeManager.ValidateAsync();

        await RunCurrentPackageWorkPassAsync(
            dispatcherDataSource,
            runtimeDataSource,
            runtimeEpoch,
            currentRuntimeStatus.StoreId,
            "compatibility-current-schema12-upgrade-host",
            "current-schema12-release-proof");

        await using var beforeRefusal = migrationDataSource.CreateCommand(
            """
            SELECT (SELECT count(*) FROM appsurface_durable.work),
                   (SELECT count(*) FROM appsurface_durable.runtime_heartbeat);
            """);
        await using var beforeReader = await beforeRefusal.ExecuteReaderAsync();
        Assert.True(await beforeReader.ReadAsync());
        var workCountBeforeRefusal = beforeReader.GetInt64(0);
        var heartbeatCountBeforeRefusal = beforeReader.GetInt64(1);
        await beforeReader.DisposeAsync();

        var refusal = await RunOldPackageHarnessProcessAsync(
            harnessPath,
            packagePath,
            dispatcherConnectionString,
            runtimeConnectionString,
            runtimeEpoch,
            schema12Status.StoreId,
            "refusal");
        Assert.NotEqual(0, refusal.ExitCode);
        Assert.Contains("StoreTooNew", refusal.StandardError, StringComparison.Ordinal);
        Assert.Contains("9/12", refusal.StandardError, StringComparison.Ordinal);

        await using var afterRefusal = migrationDataSource.CreateCommand(
            """
            SELECT (SELECT count(*) FROM appsurface_durable.work),
                   (SELECT count(*) FROM appsurface_durable.runtime_heartbeat),
                   (SELECT count(*) FROM appsurface_durable.work WHERE scope_id LIKE 'v020-preview8-release-proof-%');
            """);
        await using var afterReader = await afterRefusal.ExecuteReaderAsync();
        Assert.True(await afterReader.ReadAsync());
        Assert.Equal(workCountBeforeRefusal, afterReader.GetInt64(0));
        Assert.Equal(heartbeatCountBeforeRefusal, afterReader.GetInt64(1));
        Assert.Equal(2, afterReader.GetInt64(2));
        Assert.False(await afterReader.ReadAsync());

        const string futureSql = "SELECT 1;";
        var futureHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(futureSql)));
        var futureMigration = new DurablePostgreSqlMigration(13, "future_compatibility_probe", futureSql, futureHash);
        var futureManager = new PostgreSqlDurableRuntimeSchemaManager(
            migrationDataSource,
            migrations.Append(futureMigration).ToArray());
        var futureApply = await futureManager.ApplyAsync();
        Assert.Equal([13], futureApply.AppliedVersions);

        var rollbackRuntimeStatus = await currentRuntimeManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, rollbackRuntimeStatus.Compatibility);
        Assert.Equal(13, rollbackRuntimeStatus.InstalledVersion);
        Assert.Equal(12, rollbackRuntimeStatus.RequiredVersion);
        Assert.Equal(12, rollbackRuntimeStatus.MinimumReaderVersion);
        Assert.Equal(12, rollbackRuntimeStatus.MinimumWriterVersion);
        await RunCurrentPackageWorkPassAsync(
            dispatcherDataSource,
            runtimeDataSource,
            runtimeEpoch,
            rollbackRuntimeStatus.StoreId,
            "compatibility-current-schema13-rollback-host-a",
            "current-schema13-rollback-proof-host-a");
        await RunCurrentPackageWorkPassAsync(
            dispatcherDataSource,
            runtimeDataSource,
            runtimeEpoch,
            rollbackRuntimeStatus.StoreId,
            "compatibility-current-schema13-rollback-host-b",
            "current-schema13-rollback-proof-host-b");

        await using var verify = migrationDataSource.CreateCommand(
            """
            SELECT array_agg(version ORDER BY version),
                   (SELECT state FROM appsurface_durable.work WHERE scope_id = 'current-schema12-release-proof'),
                   (SELECT state FROM appsurface_durable.work WHERE scope_id = 'v020-preview8-release-proof-host-a'),
                   (SELECT state FROM appsurface_durable.work WHERE scope_id = 'v020-preview8-release-proof-host-b'),
                   (SELECT state FROM appsurface_durable.work WHERE scope_id = 'current-schema13-rollback-proof-host-a'),
                   (SELECT state FROM appsurface_durable.work WHERE scope_id = 'current-schema13-rollback-proof-host-b')
            FROM appsurface_durable.schema_migration;
            """);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Enumerable.Range(1, 13).ToArray(), reader.GetFieldValue<int[]>(0));
        Assert.Equal("succeeded", reader.GetString(1));
        Assert.Equal("succeeded", reader.GetString(2));
        Assert.Equal("succeeded", reader.GetString(3));
        Assert.Equal("succeeded", reader.GetString(4));
        Assert.Equal("succeeded", reader.GetString(5));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task RunCurrentPackageWorkPassAsync(
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        Guid runtimeEpoch,
        Guid storeId,
        string workerId,
        string scopeId)
    {
        var registration = new CurrentCompatibilityWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            dispatcherDataSource,
            runtimeDataSource,
            new PostgreSqlDurableWorkOptions(runtimeEpoch, storeId),
            new PostgreSqlDurableScheduleOptions(RuntimeRole),
            options =>
            {
                options.WorkerId = workerId;
                options.HostedSurfaces = DurableRuntimeSurface.Work;
                options.SendWakeNotifications = false;
            });

        await using var provider = services.BuildServiceProvider();
        var schema = provider.GetRequiredService<IDurableRuntimeSchemaManager>();
        var schemaStatus = await schema.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, schemaStatus.Compatibility);
        Assert.Equal(12, schemaStatus.RequiredVersion);
        await schema.ValidateAsync();

        var scope = new DurableScopeId(scopeId);
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId($"{scopeId}-command"),
            $"{scopeId}-idempotency",
            registration.WorkName,
            registration.WorkVersion,
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes(scopeId)),
            registration.ProviderSafety));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(
                maximumItems: 1,
                timeBudget: TimeSpan.FromSeconds(5),
                surfaces: DurableRuntimeSurface.Work));
        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, registration.InvocationCount);
        var terminal = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.Equal(DurableWorkState.Succeeded, terminal.Value!.State);
        var health = provider.GetRequiredService<IDurableRuntimeHealth>();
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await health.GetAsync()).State);
        await provider.GetRequiredService<IDurableRuntimeDrainControl>().BeginDrainAsync();
        Assert.Equal(DurableRuntimeHealthState.Draining, (await health.GetAsync()).State);
    }

    private static async Task<JsonElement> RunOldPackageHarnessAsync(
        string harnessPath,
        string packagePath,
        string dispatcherConnectionString,
        string runtimeConnectionString,
        Guid runtimeEpoch,
        Guid storeId,
        string hostSuffix)
    {
        var result = await RunOldPackageHarnessProcessAsync(
            harnessPath,
            packagePath,
            dispatcherConnectionString,
            runtimeConnectionString,
            runtimeEpoch,
            storeId,
            hostSuffix);
        Assert.True(
            result.ExitCode == 0,
            $"The exact {ExactOldPackageVersion} harness exited {result.ExitCode}. stderr: {result.StandardError}");
        var checkpoints = result.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(checkpoints);
        using var document = JsonDocument.Parse(checkpoints[0]);
        return document.RootElement.Clone();
    }

    private static async Task<OldPackageHarnessProcessResult> RunOldPackageHarnessProcessAsync(
        string harnessPath,
        string packagePath,
        string dispatcherConnectionString,
        string runtimeConnectionString,
        Guid runtimeEpoch,
        Guid storeId,
        string hostSuffix)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(harnessPath);
        startInfo.ArgumentList.Add(runtimeEpoch.ToString("D"));
        startInfo.ArgumentList.Add(storeId.ToString("D"));
        startInfo.ArgumentList.Add(RuntimeRole);
        startInfo.ArgumentList.Add(hostSuffix);
        startInfo.Environment["APPSURFACE_POSTGRES_DISPATCHER_CONNECTION"] = dispatcherConnectionString;
        startInfo.Environment["APPSURFACE_POSTGRES_RUNTIME_CONNECTION"] = runtimeConnectionString;
        startInfo.Environment[PackagePathEnvironment] = packagePath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The exact {ExactOldPackageVersion} harness could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new OldPackageHarnessProcessResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static byte[] ReadPinnedHistoricalRoleRecipe(string repositoryRoot)
    {
        // preview.8 did not package this SQL; this checked-in copy is the exact role recipe from its pinned source commit.
        var path = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable/compatibility/V2WorkHarness/preview8-configure-postgresql-roles.sql");
        var recipe = File.ReadAllBytes(path);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(recipe));
        if (!StringComparer.Ordinal.Equals(actualHash, PinnedPreview8SourceRoleRecipeSha256))
        {
            throw new InvalidDataException(
                $"The preview.8 role-recipe source does not match pinned commit {ExactOldPackageCommit}; " +
                $"expected SHA-256 {PinnedPreview8SourceRoleRecipeSha256}, observed {actualHash}.");
        }

        return recipe;
    }

    private static byte[] ReadPinnedSchema11RoleRecipe(string repositoryRoot)
    {
        // Migration 11 adds runtime_heartbeat_migration_owner; the preview.8 schema-9 recipe predates that policy.
        // Keep the schema-11 fixture tied to the canonical role recipe from the release commit that introduced schema 11.
        var path = TestPathUtils.PathUnder(
            repositoryRoot,
            "Durable/compatibility/V2WorkHarness/schema11-configure-postgresql-roles.sql");
        var recipe = File.ReadAllBytes(path);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(recipe));
        if (!StringComparer.Ordinal.Equals(actualHash, PinnedSchema11RoleRecipeSha256))
        {
            throw new InvalidDataException(
                $"The schema-11 role recipe does not match origin/main commit 7ae380845ede06029d388232f49470ae34bef6e1; " +
                $"expected SHA-256 {PinnedSchema11RoleRecipeSha256}, observed {actualHash}.");
        }

        return recipe;
    }

    private static async Task CreateRestrictedRolesAsync(
        NpgsqlDataSource dataSource,
        string dispatcherPassword,
        string runtimePassword,
        string retentionPassword)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            CREATE ROLE {MigrationOwnerRole} NOLOGIN NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE {DispatcherRole}
                LOGIN PASSWORD '{dispatcherPassword}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {RuntimeRole}
                LOGIN PASSWORD '{runtimePassword}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            CREATE ROLE {RetentionRole}
                LOGIN PASSWORD '{retentionPassword}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            """);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<DotNet.Testcontainers.Containers.ExecResult> RunRoleRecipeAsync(
        PostgreSqlContainer container,
        string recipePath,
        bool usesHistoricalRoleArguments = false)
    {
        var arguments = new List<string>
        {
            "psql",
            "-v", "ON_ERROR_STOP=1",
            "-U", MigrationUser,
            "-d", DatabaseName,
            "-v", $"migration_owner_role={MigrationOwnerRole}",
        };
        if (usesHistoricalRoleArguments)
        {
            arguments.AddRange(
            [
                "-v", $"dispatcher_role={DispatcherRole}",
                "-v", $"runtime_role={RuntimeRole}",
            ]);
        }
        else
        {
            arguments.AddRange(
            [
                "-v", $"role_pairs_json={{\"version\":1,\"pairs\":[{{\"dispatcher\":\"{DispatcherRole}\",\"runtime\":\"{RuntimeRole}\",\"dispatcher_profile\":\"full\"}}]}}",
            ]);
        }

        arguments.AddRange(
        [
            "-v", $"retention_operator_role={RetentionRole}",
            "-f", recipePath,
        ]);
        return container.ExecAsync(arguments);
    }

    private static string CreateRoleConnectionString(
        string migrationConnectionString,
        string username,
        string password,
        string applicationName) =>
        new NpgsqlConnectionStringBuilder(migrationConnectionString)
        {
            Username = username,
            Password = password,
            ApplicationName = applicationName,
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string CreateEphemeralPassword() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string RequireFile(string environmentName)
    {
        var path = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Strict mixed-version release proof requires an existing file in {environmentName}.");
        }

        return Path.GetFullPath(path);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed record OldPackageHarnessProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class CurrentCompatibilityWorkRegistration() : DurableWorkRegistration(
        "compatibility.current-schema12-work",
        "v1",
        DurableProviderSafety.Idempotent,
        new CurrentCompatibilityCodec("compatibility.current-schema12-work"),
        new CurrentCompatibilityCodec("compatibility.current-schema10-result"))
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            _ = WorkCodec.DecodeObject(work.Payload);
            return new CurrentCompatibilityPreparedWork(
                ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("schema10-result")),
                () => Interlocked.Increment(ref _invocationCount));
        }

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent compatibility Work does not reconcile.");
    }

    private sealed class CurrentCompatibilityPreparedWork(
        DurableEncodedPayload result,
        Action onInvoke) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onInvoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CurrentCompatibilityCodec(string contractName) : IDurablePayloadCodec
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion => "v1";

        public DurableDataClassification Classification => DurableDataClassification.Operational;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload EncodeObject(object value) => new(
            ContractName,
            ContractVersion,
            Classification,
            Assert.IsType<byte[]>(value),
            RetentionPolicyId);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            Assert.Equal(ContractName, payload.ContractName);
            Assert.Equal(ContractVersion, payload.ContractVersion);
            Assert.Equal(Classification, payload.Classification);
            Assert.Equal(RetentionPolicyId, payload.RetentionPolicyId);
            return payload.Content.ToArray();
        }
    }

    private sealed class CompatibilityFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.compat-flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-compat-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint => new('d', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The rolling compatibility test exercises persistence concurrency only.");
    }
}
