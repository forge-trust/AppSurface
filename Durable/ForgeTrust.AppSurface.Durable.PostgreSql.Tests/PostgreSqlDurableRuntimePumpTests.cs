using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimePumpTests
{
    [Fact]
    public async Task WorkPump_ExecutesRegisteredWorkAndPersistsProviderIdentityAndResult()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var capture = new PumpExecutorCapture();
        var workCodec = CreateWorkCodec("tests.pump-work");
        var resultCodec = CreateResultCodec("tests.pump-result");
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddDurableWork<PumpWork, PumpResult, SuccessfulPumpExecutor>(
            "tests.pump",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options =>
            {
                options.WorkerId = "pump-test-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableWorkClient>();
        var accepted = await client.EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("scope-pump-success"),
            new DurableCommandId("command-pump-success"),
            "idempotency-pump-success",
            "tests.pump",
            "v1",
            workCodec.Encode(new PumpWork("safe-input")),
            DurableProviderSafety.ProviderKeyed));

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));

        Assert.True(accepted.IsSuccess);
        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.NotNull(capture.Envelope);
        Assert.Equal("safe-input", capture.Envelope!.Payload!.SafeCode);
        Assert.Equal(accepted.Value!.WorkId.Value, capture.Envelope.Correlation.WorkId);
        Assert.NotNull(capture.Envelope.ExecutionIdentity);
        Assert.Equal(accepted.Value.WorkId.Value, capture.Envelope.ExecutionIdentity!.ActivityId);
        Assert.StartsWith("asdur-v1-", capture.Envelope.ExecutionIdentity.ProviderKey, StringComparison.Ordinal);
        Assert.Equal(
            ("succeeded", "completed", "tests.pump-result"),
            await ReadWorkTerminalAsync(database.DataSource, new DurableScopeId("scope-pump-success"), accepted.Value.WorkId));

        var emptyPass = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        Assert.Equal(0, emptyPass.Discovered);
    }

    [Fact]
    public async Task WorkPump_PostPermitCancellationStopsExecutorAndRecordsAmbiguousOutcome()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var capture = new CancellationAwarePumpExecutorCapture();
        var workCodec = CreateWorkCodec("tests.cancel-aware-work");
        var resultCodec = CreateResultCodec("tests.cancel-aware-result");
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddDurableWork<PumpWork, PumpResult, CancellationAwarePumpExecutor>(
            "tests.cancel-aware",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options =>
            {
                options.WorkerId = "pump-cancel-test-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableWorkClient>();
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var scopeId = new DurableScopeId("scope-pump-post-permit-cancel");
        var accepted = await client.EnqueueAsync(new DurableWorkRequest(
            scopeId,
            new DurableCommandId("command-pump-post-permit-cancel"),
            "idempotency-pump-post-permit-cancel",
            "tests.cancel-aware",
            "v1",
            workCodec.Encode(new PumpWork("safe-input")),
            DurableProviderSafety.ProviderKeyed,
            new DurableWorkRetryPolicy(
                maximumAttempts: 3,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(5),
                leaseDuration: TimeSpan.FromSeconds(3),
                renewalCadence: TimeSpan.FromSeconds(1),
                maximumLeaseLifetime: TimeSpan.FromMinutes(1),
                backoffAlgorithm: "exponential-v1")));

        var pumpTask = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(20), DurableRuntimeSurface.Work)).AsTask();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = await control.GetAsync(new DurableWorkGetRequest(scopeId, accepted.Value!.WorkId));
        var cancellation = await control.CancelAsync(new DurableWorkCancelRequest(
            scopeId,
            accepted.Value.WorkId,
            "operator-test",
            "consumer_requested",
            snapshot.Value!.Revision));

        try
        {
            await capture.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            capture.Release.TrySetResult();
            await pumpTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var result = await pumpTask;
        Assert.True(cancellation.IsSuccess);
        Assert.Equal(DurableWorkState.CancelPending, cancellation.Value!.State);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(
            ("suspended_ambiguous_external_outcome", DurableProblemCodes.AmbiguousExternalOutcome, (string?)null),
            await ReadWorkTerminalAsync(database.DataSource, scopeId, accepted.Value.WorkId));
    }

    [Fact]
    public async Task WorkPump_RenewsAtTheSnapshottedWorkCadence()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var capture = new CancellationAwarePumpExecutorCapture();
        var workCodec = CreateWorkCodec("tests.cadence-work");
        var resultCodec = CreateResultCodec("tests.cadence-result");
        var services = new ServiceCollection();
        services.AddSingleton(capture);
        services.AddDurableWork<PumpWork, PumpResult, CancellationAwarePumpExecutor>(
            "tests.cadence",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options =>
            {
                options.WorkerId = "pump-cadence-test-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scopeId = new DurableScopeId("scope-pump-custom-renewal-cadence");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scopeId,
            new DurableCommandId("command-pump-custom-renewal-cadence"),
            "idempotency-pump-custom-renewal-cadence",
            "tests.cadence",
            "v1",
            workCodec.Encode(new PumpWork("safe-cadence")),
            DurableProviderSafety.ProviderKeyed,
            new DurableWorkRetryPolicy(
                maximumAttempts: 3,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromSeconds(1),
                maximumRetryDelay: TimeSpan.FromSeconds(5),
                leaseDuration: TimeSpan.FromSeconds(3),
                renewalCadence: TimeSpan.FromMilliseconds(150),
                maximumLeaseLifetime: TimeSpan.FromSeconds(5),
                backoffAlgorithm: "exponential-v1")));

        var pumpTask = provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work)).AsTask();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await WaitForScopedCountAsync(
                database.DataSource,
                scopeId,
                "SELECT count(*) FROM appsurface_durable.work_history WHERE event_type = 'lease_renewed';",
                minimumCount: 3,
                TimeSpan.FromMilliseconds(1_500));
        }
        finally
        {
            capture.Release.TrySetResult();
        }

        var result = await pumpTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(accepted.IsSuccess);
        Assert.Equal(1, result.Processed);
        Assert.True(await CountScopedAsync(
            database.DataSource,
            scopeId,
            "SELECT count(*) FROM appsurface_durable.work_history WHERE event_type = 'lease_renewed';") >= 3);
    }

    [Fact]
    public async Task Health_ReportsLagSweepStalenessAndGracefulDrain()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var epoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec("tests.health-work");
        var resultCodec = CreateResultCodec("tests.health-result");
        var services = new ServiceCollection();
        services.AddSingleton(new PumpExecutorCapture());
        services.AddDurableWork<PumpWork, PumpResult, SuccessfulPumpExecutor>(
            "tests.health",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options =>
            {
                options.WorkerId = "pump-health-worker";
                options.SendWakeNotifications = false;
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(10);
            });
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IDurableRuntimeHealth>();
        var drain = provider.GetRequiredService<IDurableRuntimeDrainControl>();
        var beforeStart = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.NotStarted, beforeStart.State);
        Assert.Equal(DurableProblemCodes.ActivatorStale, beforeStart.ProblemCode);

        var scopeId = new DurableScopeId("scope-pump-health");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scopeId,
            new DurableCommandId("command-pump-health"),
            "idempotency-pump-health",
            "tests.health",
            "v1",
            workCodec.Encode(new PumpWork("safe-health")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);
        var awaitingActivator = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.NotStarted, awaitingActivator.State);
        Assert.True(awaitingActivator.EpochCompatible);
        Assert.Equal(1, awaitingActivator.DueDispatchCount);
        Assert.NotNull(awaitingActivator.OldestDueAge);

        await drain.BeginDrainAsync();
        var refused = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        Assert.Equal(0, refused.Discovered);
        var draining = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Draining, draining.State);
        Assert.True(draining.IsDraining);
        Assert.Null(draining.LastSuccessfulSweepAtUtc);

        await drain.ResumeAsync();
        var completed = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));
        Assert.Equal(1, completed.Processed);
        var healthy = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Healthy, healthy.State);
        Assert.Null(healthy.ProblemCode);
        Assert.NotNull(healthy.LastSuccessfulSweepAtUtc);
        Assert.Equal(0, healthy.DueDispatchCount);

        await using (var orphan = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET pass_active = true,
                pass_started_at = clock_timestamp()
            WHERE worker_id = 'pump-health-worker';
            """))
        {
            Assert.Equal(1, await orphan.ExecuteNonQueryAsync());
        }

        var recoveredPass = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work));
        Assert.Equal(0, recoveredPass.Discovered);
        Assert.False((await health.GetAsync()).IsPassActive);

        var duplicateServices = new ServiceCollection();
        duplicateServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options =>
            {
                options.WorkerId = "pump-health-worker";
                options.SendWakeNotifications = false;
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(10);
            });
        await using (var duplicateProvider = duplicateServices.BuildServiceProvider())
        {
            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await duplicateProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
                    new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work)));
            Assert.Contains(DurableProblemCodes.WorkerIdentityConflict, conflict.Message, StringComparison.Ordinal);
        }

        await using (var command = database.DataSource.CreateCommand(
            """
            UPDATE appsurface_durable.runtime_heartbeat
            SET last_heartbeat_at = clock_timestamp() - interval '1 hour'
            WHERE worker_id = 'pump-health-worker';
            """))
        {
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var stale = await health.GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Stale, stale.State);
        Assert.Equal(DurableProblemCodes.ActivatorStale, stale.ProblemCode);
    }

    [Fact]
    public async Task Drain_BlocksWorkerIdentityHandoffUntilActiveProviderPassCompletes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var epoch = Guid.NewGuid();
        var capture = new CancellationAwarePumpExecutorCapture();
        var workCodec = CreateWorkCodec("tests.drain-work");
        var resultCodec = CreateResultCodec("tests.drain-result");
        var firstServices = new ServiceCollection();
        firstServices.AddSingleton(capture);
        firstServices.AddDurableWork<PumpWork, PumpResult, CancellationAwarePumpExecutor>(
            "tests.drain",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        firstServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options =>
            {
                options.WorkerId = "pump-drain-worker";
                options.SendWakeNotifications = false;
                options.IdlePollingInterval = TimeSpan.FromMilliseconds(100);
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(30);
            });
        await using var firstProvider = firstServices.BuildServiceProvider();
        var accepted = await firstProvider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("scope-pump-drain"),
            new DurableCommandId("command-pump-drain"),
            "idempotency-pump-drain",
            "tests.drain",
            "v1",
            workCodec.Encode(new PumpWork("safe-drain")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var firstPass = firstProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work)).AsTask();
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var active = await firstProvider.GetRequiredService<IDurableRuntimeHealth>().GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Healthy, active.State);
        Assert.True(active.IsPassActive);
        var localOverlap = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await firstProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
                new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work)));
        Assert.Contains(DurableProblemCodes.WorkerIdentityConflict, localOverlap.Message, StringComparison.Ordinal);

        var replacementServices = new ServiceCollection();
        replacementServices.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options =>
            {
                options.WorkerId = "pump-drain-worker";
                options.SendWakeNotifications = false;
                options.IdlePollingInterval = TimeSpan.FromMilliseconds(100);
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(30);
            });
        await using var replacementProvider = replacementServices.BuildServiceProvider();
        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await replacementProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
                new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work)));
        Assert.Contains(DurableProblemCodes.WorkerIdentityConflict, overlap.Message, StringComparison.Ordinal);
        await firstProvider.GetRequiredService<IDurableRuntimeDrainControl>().BeginDrainAsync();
        var drainingOverlap = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await replacementProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
                new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work)));
        Assert.Contains(DurableProblemCodes.WorkerIdentityConflict, drainingOverlap.Message, StringComparison.Ordinal);

        capture.Release.TrySetResult();
        var completed = await firstPass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, completed.Processed);
        var drained = await firstProvider.GetRequiredService<IDurableRuntimeHealth>().GetAsync();
        Assert.Equal(DurableRuntimeHealthState.Draining, drained.State);
        Assert.False(drained.IsPassActive);

        var replacementPass = await replacementProvider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work));
        Assert.Equal(0, replacementPass.Discovered);
        Assert.Equal(DurableRuntimeHealthState.Healthy,
            (await replacementProvider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
    }

    [Fact]
    public async Task HealthAndDrain_RunWithExactNonOwnerRuntimeRole()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var role = $"durable_health_{Guid.NewGuid():N}";
        const string password = "appsurface-health-role-password";
        await using (var grant = database.DataSource.CreateCommand($$"""
            CREATE ROLE "{{role}}" LOGIN PASSWORD '{{password}}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
            GRANT USAGE ON SCHEMA appsurface_durable TO "{{role}}";
            GRANT EXECUTE ON FUNCTION appsurface_durable.initialize_runtime_epoch(uuid) TO "{{role}}";
            GRANT SELECT ON TABLE
                appsurface_durable.schema_migration,
                appsurface_durable.store_metadata,
                appsurface_durable.dispatch
            TO "{{role}}";
            GRANT SELECT, INSERT, UPDATE ON TABLE appsurface_durable.runtime_heartbeat TO "{{role}}";
            """))
        {
            await grant.ExecuteNonQueryAsync();
        }

        var runtimeBuilder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
        };
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeBuilder.ConnectionString);
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            runtimeDataSource,
            Guid.NewGuid(),
            options =>
            {
                options.WorkerId = "exact-role-health-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var pass = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(1, TimeSpan.FromSeconds(1), DurableRuntimeSurface.Work));
        Assert.Equal(0, pass.Discovered);
        Assert.Equal(DurableRuntimeHealthState.Healthy,
            (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
        await provider.GetRequiredService<IDurableRuntimeDrainControl>().BeginDrainAsync();
        Assert.Equal(DurableRuntimeHealthState.Draining,
            (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);

        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = runtimeDataSource.CreateCommand(
                "UPDATE appsurface_durable.store_metadata SET schema_version = schema_version;");
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task WorkPump_UnknownProviderOutcomeSuspendsManualResolutionWork()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var workCodec = CreateWorkCodec("tests.ambiguous-work");
        var resultCodec = CreateResultCodec("tests.ambiguous-result");
        var services = new ServiceCollection();
        services.AddDurableWork<PumpWork, PumpResult, ThrowingPumpExecutor>(
            "tests.ambiguous",
            "v1",
            DurableProviderSafety.ManualResolution,
            workCodec,
            resultCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            Guid.NewGuid(),
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("scope-pump-ambiguous"),
            new DurableCommandId("command-pump-ambiguous"),
            "idempotency-pump-ambiguous",
            "tests.ambiguous",
            "v1",
            workCodec.Encode(new PumpWork("safe-input")),
            DurableProviderSafety.ManualResolution));

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(
            ("suspended_manual_resolution", DurableProblemCodes.AmbiguousExternalOutcome, (string?)null),
            await ReadWorkTerminalAsync(
                database.DataSource,
                new DurableScopeId("scope-pump-ambiguous"),
                accepted.Value!.WorkId));
    }

    [Fact]
    public async Task WorkPump_MissingRegistrationFailsBeforeEffectPermit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        await new PostgreSqlDurableRuntimeSchemaManager(database.DataSource).ApplyAsync();
        var epoch = Guid.NewGuid();
        var workCodec = CreateWorkCodec("tests.missing-work");
        var directClient = new PostgreSqlDurableWorkClient(
            database.DataSource,
            new PostgreSqlDurableWorkTransactionWriter(
                database.DataSource,
                epoch,
                PostgreSqlTestWorkContracts.CreateRegistry(new PostgreSqlTestWorkContract(
                    "tests.not-registered",
                    "v1",
                    DurableProviderSafety.ProviderKeyed,
                    "tests.missing-work",
                    "v1",
                    DurableDataClassification.Operational)),
                sendWakeNotification: false));
        var accepted = await directClient.EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("scope-pump-missing"),
            new DurableCommandId("command-pump-missing"),
            "idempotency-pump-missing",
            "tests.not-registered",
            "v1",
            workCodec.Encode(new PumpWork("safe-input")),
            DurableProviderSafety.ProviderKeyed));
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            epoch,
            options => options.SendWakeNotifications = false);
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(10, TimeSpan.FromSeconds(10), DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        Assert.Equal(
            ("suspended_contract_unavailable", DurableProblemCodes.WorkContractUnavailable, (string?)null),
            await ReadWorkTerminalAsync(
                database.DataSource,
                new DurableScopeId("scope-pump-missing"),
                accepted.Value!.WorkId));
        Assert.Equal(
            0,
            await CountScopedAsync(
                database.DataSource,
                new DurableScopeId("scope-pump-missing"),
                "SELECT count(*) FROM appsurface_durable.effect_permit;"));
    }

    private static SystemTextJsonDurablePayloadCodec<PumpWork> CreateWorkCodec(string contractName) =>
        new(
            contractName,
            "v1",
            DurableDataClassification.Operational,
            PumpJsonContext.Default.PumpWork,
            value => value.SafeCode.StartsWith("safe-", StringComparison.Ordinal));

    private static SystemTextJsonDurablePayloadCodec<PumpResult> CreateResultCodec(string contractName) =>
        new(
            contractName,
            "v1",
            DurableDataClassification.Operational,
            PumpJsonContext.Default.PumpResult,
            _ => true);

    private static async ValueTask<(string State, string Code, string? ResultContract)> ReadWorkTerminalAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        DurableWorkId workId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(
            "SELECT state, terminal_code, result_contract_id FROM appsurface_durable.work WHERE work_id = @work_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("work_id", workId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
        await reader.CloseAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async ValueTask<long> CountScopedAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scopeId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async ValueTask WaitForScopedCountAsync(
        NpgsqlDataSource dataSource,
        DurableScopeId scopeId,
        string sql,
        long minimumCount,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            if (await CountScopedAsync(dataSource, scopeId, sql) >= minimumCount)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"The scoped count did not reach {minimumCount} within {timeout}.");
    }

    private static async ValueTask SetScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableScopeId scopeId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteScalarAsync();
    }
}

internal sealed record PumpWork(string SafeCode);

internal sealed record PumpResult(string Code);

internal sealed class PumpExecutorCapture
{
    internal DurableWorkerEnvelope<PumpWork>? Envelope { get; set; }
}

internal sealed class CancellationAwarePumpExecutorCapture
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource CancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class SuccessfulPumpExecutor(PumpExecutorCapture capture) : IDurableWorkerExecutor<PumpWork, PumpResult>
{
    public ValueTask<PumpResult> ExecuteAsync(
        DurableWorkerEnvelope<PumpWork> work,
        CancellationToken cancellationToken = default)
    {
        capture.Envelope = work;
        return ValueTask.FromResult(new PumpResult("done"));
    }
}

internal sealed class ThrowingPumpExecutor : IDurableWorkerExecutor<PumpWork, PumpResult>
{
    public ValueTask<PumpResult> ExecuteAsync(
        DurableWorkerEnvelope<PumpWork> work,
        CancellationToken cancellationToken = default) =>
        throw new TimeoutException("Simulated provider response loss; this text must not be persisted.");
}

internal sealed class CancellationAwarePumpExecutor(CancellationAwarePumpExecutorCapture capture)
    : IDurableWorkerExecutor<PumpWork, PumpResult>
{
    public async ValueTask<PumpResult> ExecuteAsync(
        DurableWorkerEnvelope<PumpWork> work,
        CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(() => capture.CancellationObserved.TrySetResult());
        capture.Started.TrySetResult();
        await Task.WhenAny(capture.Release.Task, capture.CancellationObserved.Task).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new PumpResult("released");
    }
}

[JsonSerializable(typeof(PumpWork))]
[JsonSerializable(typeof(PumpResult))]
internal sealed partial class PumpJsonContext : JsonSerializerContext;
