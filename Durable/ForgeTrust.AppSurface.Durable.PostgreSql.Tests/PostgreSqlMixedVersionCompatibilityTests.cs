using System.Diagnostics;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlMixedVersionCompatibilityTests
{
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
    public async Task V2CatalogAndConcurrentWorkRemainCompatibleAfterV6TraceContextUpgrade()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var currentManager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await currentManager.ApplyAsync();
        var oldCatalogManager = new PostgreSqlDurableRuntimeSchemaManager(
            database.DataSource,
            DurablePostgreSqlMigrationCatalog.Load().Take(2).ToArray());
        var oldStatus = await oldCatalogManager.GetStatusAsync();
        Assert.Equal(DurableRuntimeSchemaCompatibility.Compatible, oldStatus.Compatibility);
        Assert.Equal(2, oldStatus.RequiredVersion);
        Assert.Equal(6, oldStatus.InstalledVersion);

        var epoch = Guid.NewGuid();
        await currentManager.InitializeRuntimeEpochAsync(epoch, "tests", "mixed-v2-v3");
        var currentStatus = await currentManager.GetStatusAsync();
        var workCodec = new PostgreSqlOpaqueTestCodec("tests.compat.work", "v1");
        var resultCodec = new PostgreSqlOpaqueTestCodec("tests.compat.result", "v1");
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.compat.flow", "v1");
        var workRegistration = new PostgreSqlOpaqueTestWorkRegistration(
            "tests.compat.work",
            "v1",
            DurableProviderSafety.Idempotent,
            workCodec,
            resultCodec);
        var workRegistry = new DurableWorkRegistry([workRegistration]);
        var payloads = new DurablePayloadCodecRegistry([workCodec, resultCodec, contextCodec]);
        var flowRegistration = new CompatibilityFlowRegistration(contextCodec);
        var flowRegistry = new DurableFlowRegistry([flowRegistration], workRegistry, payloads);
        var options = new PostgreSqlDurableWorkOptions(epoch, currentStatus.StoreId);
        var workClient = new PostgreSqlDurableWorkClient(database.DataSource, workRegistry, options);
        var flowClient = new PostgreSqlDurableFlowClient(database.DataSource, flowRegistry, payloads, options);
        var operations = Enumerable.Range(0, 16).SelectMany(index => new Task[]
        {
            workClient.EnqueueAsync(new DurableWorkRequest(
                new DurableScopeId("mixed-scope"),
                new DurableCommandId($"v2-work-command-{index}"),
                $"v2-work-key-{index}",
                workRegistration.WorkName,
                workRegistration.WorkVersion,
                workCodec.EncodeObject(new byte[] { checked((byte)index) }),
                workRegistration.ProviderSafety)).AsTask(),
            flowClient.StartAsync(new DurableFlowStartRequest(
                new DurableScopeId("mixed-scope"),
                new DurableCommandId($"v3-flow-command-{index}"),
                $"v3-flow-key-{index}",
                new DurableFlowInstanceId($"v3-flow-{index}"),
                flowRegistration.FlowId,
                flowRegistration.FlowVersion,
                contextCodec.EncodeObject(new byte[] { checked((byte)index) }))).AsTask(),
        });

        var oldBinaryPath = Environment.GetEnvironmentVariable("APPSURFACE_DURABLE_V2_TESTHOST_PATH");
        var requireOldBinary = string.Equals(
            Environment.GetEnvironmentVariable("APPSURFACE_REQUIRE_V2_BINARY"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (requireOldBinary && (string.IsNullOrWhiteSpace(oldBinaryPath) || !File.Exists(oldBinaryPath)))
        {
            throw new InvalidOperationException(
                "Strict mixed-version verification requires APPSURFACE_DURABLE_V2_TESTHOST_PATH.");
        }

        var oldBinaryTask = string.IsNullOrWhiteSpace(oldBinaryPath)
            ? Task.CompletedTask
            : RunV2WorkBinaryAsync(
                oldBinaryPath,
                database.ConnectionString,
                epoch,
                currentStatus.StoreId);
        await Task.WhenAll(operations.Append(oldBinaryTask));

        await using var command = database.DataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM appsurface_durable.work WHERE scope_id = 'mixed-scope'),
                (SELECT count(*) FROM appsurface_durable.flow_instance WHERE scope_id = 'mixed-scope');
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(16, reader.GetInt64(0));
        Assert.Equal(16, reader.GetInt64(1));
        if (!string.IsNullOrWhiteSpace(oldBinaryPath))
        {
            await reader.DisposeAsync();
            await using var oldWork = database.DataSource.CreateCommand(
                "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = 'v2-binary-compatibility';");
            Assert.Equal(1, (long)(await oldWork.ExecuteScalarAsync())!);
        }
    }

    private static async Task RunV2WorkBinaryAsync(
        string assemblyPath,
        string connectionString,
        Guid runtimeEpoch,
        Guid storeId)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(runtimeEpoch.ToString("D"));
        startInfo.ArgumentList.Add(storeId.ToString("D"));
        startInfo.Environment["APPSURFACE_POSTGRES_REFERENCE_CONNECTION"] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The pinned v2 Work binary could not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                throw new InvalidOperationException($"The pinned v2 Work binary exited before its checkpoint: {error}");
            }

            using var checkpoint = JsonDocument.Parse(line);
            Assert.Equal("v2-terminal", checkpoint.RootElement.GetProperty("Phase").GetString());
            await process.WaitForExitAsync(timeout.Token);
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
