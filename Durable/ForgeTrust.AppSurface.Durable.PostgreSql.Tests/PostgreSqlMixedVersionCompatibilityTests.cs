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
    internal const string EnvironmentVariableName = "APPSURFACE_REQUIRE_V020_RELEASE_PROOF";

    /// <summary>Initializes the conditional release-proof fact.</summary>
    public ExactPackageReleaseProofFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariableName),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip =
                "The exact v0.2.0-preview.8 package proof runs only in the explicit PostgreSQL release-proof lane.";
        }
    }
}

public sealed class PostgreSqlMixedVersionCompatibilityTests
{
    private const string ExactOldPackageVersion = "0.2.0-preview.8";
    private const string ExactOldPackageSha256 = "62a48f6b7ec299ad608f3714a49a39f18c5cb1fe45293d53915e5549422d311e";
    private const string ExactOldPackageCommit = "b34970c87489a63b132531657c043e075b092e5e";
    private const string HarnessPathEnvironment = "APPSURFACE_DURABLE_V020_HARNESS_PATH";
    private const string PackagePathEnvironment = "APPSURFACE_DURABLE_V020_PACKAGE_PATH";
    private const string DatabaseName = "appsurface_durable";
    private const string MigrationUser = "appsurface";
    private const string MigrationOwnerRole = "durable_owner";
    private const string DispatcherRole = "durable_dispatcher";
    private const string RuntimeRole = "durable_runtime";
    private const string RetentionRole = "durable_retention";
    private const string ContainerRoleRecipePath = "/tmp/configure-postgresql-roles.sql";

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

    [ExactPackageReleaseProofFact]
    public async Task ExactV020Preview8Package_OperatesAfterSchema10AndSupportsBinaryRollback()
    {
        var harnessPath = RequireFile(HarnessPathEnvironment);
        var packagePath = RequireFile(PackagePathEnvironment);
        Assert.Equal(ExactOldPackageSha256, await ComputeSha256Async(packagePath));

        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var roleRecipePath = TestPathUtils.PathUnder(repositoryRoot, "Durable/configure-postgresql-roles.sql");
        var migrationPassword = CreateEphemeralPassword();
        var dispatcherPassword = CreateEphemeralPassword();
        var runtimePassword = CreateEphemeralPassword();
        var retentionPassword = CreateEphemeralPassword();

        await using var container = new PostgreSqlBuilder(PostgreSqlTestContainerImage.Reference)
            .WithDatabase(DatabaseName)
            .WithUsername(MigrationUser)
            .WithPassword(migrationPassword)
            .WithResourceMapping(File.ReadAllBytes(roleRecipePath), ContainerRoleRecipePath)
            .WithCleanUp(true)
            .Build();
        await container.StartAsync();

        await using var migrationDataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var migrations = DurablePostgreSqlMigrationCatalog.Load();
        Assert.Equal(10, migrations.Count);

        var schema9Manager = new PostgreSqlDurableRuntimeSchemaManager(
            migrationDataSource,
            migrations.Take(9).ToArray());
        var schema9Apply = await schema9Manager.ApplyAsync();
        Assert.Equal(Enumerable.Range(1, 9).ToArray(), schema9Apply.AppliedVersions);

        var currentManager = new PostgreSqlDurableRuntimeSchemaManager(migrationDataSource);
        var currentAtSchema9 = await currentManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, currentAtSchema9.Compatibility);
        Assert.Equal(9, currentAtSchema9.InstalledVersion);
        Assert.Equal(10, currentAtSchema9.RequiredVersion);
        var startupFailure = await Assert.ThrowsAsync<DurableRuntimeSchemaException>(
            async () => await currentManager.ValidateAsync());
        Assert.Equal(DurableRuntimeSchemaCompatibility.UpgradeRequired, startupFailure.Status.Compatibility);

        var schema10Apply = await currentManager.ApplyAsync();
        Assert.Equal([10], schema10Apply.AppliedVersions);
        var currentAtSchema10 = await currentManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, currentAtSchema10.Compatibility);
        Assert.Equal(10, currentAtSchema10.InstalledVersion);
        Assert.Equal(10, currentAtSchema10.RequiredVersion);
        await currentManager.ValidateAsync();

        var runtimeEpoch = Guid.NewGuid();
        await currentManager.InitializeRuntimeEpochAsync(
            runtimeEpoch,
            "mixed-version-release-proof",
            "schema10-activation");
        await CreateRestrictedRolesAsync(
            migrationDataSource,
            dispatcherPassword,
            runtimePassword,
            retentionPassword);
        var recipeResult = await RunRoleRecipeAsync(container);
        Assert.True(
            recipeResult.ExitCode == 0,
            $"The canonical role recipe failed. stdout: {recipeResult.Stdout} stderr: {recipeResult.Stderr}");

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

        var restrictedCurrentManager = new PostgreSqlDurableRuntimeSchemaManager(runtimeDataSource);
        var restrictedCurrentStatus = await restrictedCurrentManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, restrictedCurrentStatus.Compatibility);
        Assert.Equal(10, restrictedCurrentStatus.InstalledVersion);
        await restrictedCurrentManager.ValidateAsync();

        await RunCurrentPackageWorkPassAsync(
            dispatcherDataSource,
            runtimeDataSource,
            runtimeEpoch,
            restrictedCurrentStatus.StoreId);

        var checkpoint = await RunOldPackageHarnessAsync(
            harnessPath,
            packagePath,
            dispatcherConnectionString,
            runtimeConnectionString,
            runtimeEpoch,
            restrictedCurrentStatus.StoreId);
        Assert.Equal("v0.2.0-preview.8-operational", checkpoint.GetProperty("Phase").GetString());
        Assert.Equal(ExactOldPackageVersion, checkpoint.GetProperty("PackageVersion").GetString());
        Assert.Equal(ExactOldPackageSha256, checkpoint.GetProperty("PackageSha256").GetString());
        Assert.Equal(ExactOldPackageCommit, checkpoint.GetProperty("RepositoryCommit").GetString());
        var oldProviderInformationalVersion = checkpoint.GetProperty("ProviderInformationalVersion").GetString();
        Assert.NotNull(oldProviderInformationalVersion);
        Assert.StartsWith(
            ExactOldPackageVersion,
            oldProviderInformationalVersion,
            StringComparison.Ordinal);
        Assert.True(checkpoint.GetProperty("StartupValidated").GetBoolean());
        Assert.True(checkpoint.GetProperty("LegacyHealthObserved").GetBoolean());
        Assert.Equal("NotStarted", checkpoint.GetProperty("InitialHealthState").GetString());
        Assert.Equal("Healthy", checkpoint.GetProperty("HealthyAfterWork").GetString());
        Assert.True(checkpoint.GetProperty("HeartbeatMaintained").GetBoolean());
        var oldSchema = checkpoint.GetProperty("Schema");
        Assert.Equal(10, oldSchema.GetProperty("InstalledVersion").GetInt32());
        Assert.Equal(9, oldSchema.GetProperty("RequiredVersion").GetInt32());
        Assert.Equal("Compatible", oldSchema.GetProperty("Compatibility").GetString());
        var oldWork = checkpoint.GetProperty("Work");
        Assert.Equal("Succeeded", oldWork.GetProperty("State").GetString());
        Assert.Equal(1, oldWork.GetProperty("InvocationCount").GetInt32());
        Assert.Equal(1, oldWork.GetProperty("Processed").GetInt32());
        Assert.Equal(0, oldWork.GetProperty("Failed").GetInt32());

        var currentAfterRollback = await restrictedCurrentManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, currentAfterRollback.Compatibility);
        await restrictedCurrentManager.ValidateAsync();

        await using var verify = migrationDataSource.CreateCommand(
            """
            SELECT
                (SELECT array_agg(version ORDER BY version) FROM appsurface_durable.schema_migration),
                (SELECT state FROM appsurface_durable.work WHERE scope_id = 'current-schema10-release-proof'),
                (SELECT state FROM appsurface_durable.work WHERE scope_id = 'v020-preview8-release-proof');
            """);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Enumerable.Range(1, 10).ToArray(), reader.GetFieldValue<int[]>(0));
        Assert.Equal("succeeded", reader.GetString(1));
        Assert.Equal("succeeded", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task RunCurrentPackageWorkPassAsync(
        NpgsqlDataSource dispatcherDataSource,
        NpgsqlDataSource runtimeDataSource,
        Guid runtimeEpoch,
        Guid storeId)
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
                options.WorkerId = "compatibility-current-schema10-worker";
                options.HostedSurfaces = DurableRuntimeSurface.Work;
                options.SendWakeNotifications = false;
            });

        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("current-schema10-release-proof");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("current-schema10-command"),
            "current-schema10-idempotency",
            registration.WorkName,
            registration.WorkVersion,
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("schema10")),
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
    }

    private static async Task<JsonElement> RunOldPackageHarnessAsync(
        string harnessPath,
        string packagePath,
        string dispatcherConnectionString,
        string runtimeConnectionString,
        Guid runtimeEpoch,
        Guid storeId)
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
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(
                process.ExitCode == 0,
                $"The exact {ExactOldPackageVersion} harness exited {process.ExitCode}. stderr: {stderr}");
            var checkpoints = stdout
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Single(checkpoints);
            using var document = JsonDocument.Parse(checkpoints[0]);
            return document.RootElement.Clone();
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
        PostgreSqlContainer container) =>
        container.ExecAsync(
            [
                "psql",
                "-v", "ON_ERROR_STOP=1",
                "-U", MigrationUser,
                "-d", DatabaseName,
                "-v", $"migration_owner_role={MigrationOwnerRole}",
                "-v", $"dispatcher_role={DispatcherRole}",
                "-v", $"runtime_role={RuntimeRole}",
                "-v", $"retention_operator_role={RetentionRole}",
                "-f", ContainerRoleRecipePath,
            ]);

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

    private sealed class CurrentCompatibilityWorkRegistration() : DurableWorkRegistration(
        "compatibility.current-schema10-work",
        "v1",
        DurableProviderSafety.Idempotent,
        new CurrentCompatibilityCodec("compatibility.current-schema10-work"),
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
