using System.Text;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Flow;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableRuntimePumpTests
{
    [Fact]
    public async Task RunOnceAsync_ProcessesRegisteredWorkThroughTheProviderExecutionBoundary()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "initial");
        var workOptions = new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId);
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            workOptions,
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableWorkClient>();
        var accepted = await client.EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-scope"),
            new DurableCommandId("runtime-pump-command"),
            "runtime-pump-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(new DurableScopeId("runtime-pump-scope"), accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.Value!.State);
        Assert.Equal("runtime-pump-key", snapshot.Value.ProviderKey);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
    }

    [Fact]
    public async Task RunOnceAsync_ProcessesRegisteredFlowThroughTheProviderProcessor()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "flow");
        var contextCodec = new PostgreSqlOpaqueTestCodec("tests.runtime-pump.flow", "v1");
        var flow = new CompletingFlowRegistration(contextCodec);
        var services = new ServiceCollection();
        services.AddSingleton<IDurablePayloadCodec>(contextCodec);
        services.AddSingleton<DurableFlowRegistration>(flow);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-flow-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-flow-scope");
        var instance = new DurableFlowInstanceId("runtime-pump-flow-instance");
        var accepted = await provider.GetRequiredService<IDurableFlowClient>().StartAsync(new DurableFlowStartRequest(
            scope,
            new DurableCommandId("runtime-pump-flow-command"),
            "runtime-pump-flow-key",
            instance,
            flow.FlowId,
            flow.FlowVersion,
            contextCodec.EncodeObject(Encoding.UTF8.GetBytes("context"))));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Flow));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableFlowClient>().GetAsync(
            new DurableFlowGetRequest(scope, instance));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableFlowState.Completed, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_ProcessesDueScheduleThroughTheWorkFirstProcessor()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "schedule");
        var registration = new SuccessfulWorkRegistration();
        var scheduleCodec = new RuntimeSchedulePayloadCodec(
            registration.InputCodec.ContractName,
            registration.InputCodec.ContractVersion,
            registration.InputCodec.Classification);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddSingleton<IDurablePayloadCodec>(scheduleCodec);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-schedule-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-schedule-scope");
        var created = await provider.GetRequiredService<IDurableScheduleClient>().CreateAsync(new DurableScheduleCreateRequest(
            scope,
            new DurableCommandId("runtime-pump-schedule-command"),
            "runtime-pump-schedule-key",
            new DurableScheduleId("runtime-pump-schedule"),
            DurableSchedule.At(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)),
            DurableScheduleTarget.Work(
                SuccessfulWorkRegistration.Name,
                "v1",
                Encoding.UTF8.GetBytes("input"),
                scheduleCodec)));
        Assert.True(created.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Schedule));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Failed);
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM appsurface_durable.work WHERE scope_id = @scope_id;");
        count.Parameters.AddWithValue("scope_id", scope.Value);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RunOnceAsync_ReturnsAnEmptyHealthyPassWhenEverySelectedSurfaceIsQuiescent()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "empty");
        var services = new ServiceCollection();
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-empty-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.All));

        Assert.Equal(0, result.Discovered);
        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
    }

    [Fact]
    public async Task RunOnceAsync_PreservesAmbiguityWhenAPermittedProviderFails()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "failure");
        var registration = new FailingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-failure-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-failure-scope"),
            new DurableCommandId("runtime-pump-failure-command"),
            "runtime-pump-failure-key",
            FailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Claimed);
        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(new DurableScopeId("runtime-pump-failure-scope"), accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.AmbiguousExternalOutcome, snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_RejectsAnOverlappingPassUntilTheActiveProviderCallCompletes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "overlap");
        var registration = new BlockingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-overlap-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            new DurableScopeId("runtime-pump-overlap-scope"),
            new DurableCommandId("runtime-pump-overlap-command"),
            "runtime-pump-overlap-key",
            BlockingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        var request = new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work);
        var activePass = pump.RunOnceAsync(request).AsTask();
        await registration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pump.RunOnceAsync(request));
        Assert.StartsWith(DurableProblemCodes.WorkerIdentityConflict, overlap.Message, StringComparison.Ordinal);

        registration.Complete.TrySetResult(registration.ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
        Assert.Equal(1, (await activePass).Processed);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenPersistedProviderSafetyNoLongerMatchesTheRegistration()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "contract-mismatch");
        var registration = new SuccessfulWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-contract-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-contract-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-contract-command"),
            "runtime-pump-contract-key",
            SuccessfulWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);
        await using (var corrupt = database.DataSource.CreateCommand(
            "UPDATE appsurface_durable.work SET provider_safety = 'provider_keyed' WHERE scope_id = @scope_id AND work_id = @work_id;"))
        {
            corrupt.Parameters.AddWithValue("scope_id", scope.Value);
            corrupt.Parameters.AddWithValue("work_id", accepted.Value!.WorkId.Value);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenProviderPreparationRejectsTheClaim()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "prepare-failure");
        var registration = new PreparationFailingWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-preparation-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-preparation-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-preparation-command"),
            "runtime-pump-preparation-key",
            PreparationFailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
        Assert.Equal(DurableProblemCodes.WorkContractUnavailable, snapshot.Value.TerminalCode);
    }

    [Fact]
    public async Task RunOnceAsync_RenewsLeasesAndHeartbeatsWhileAProviderInvocationIsStillRunning()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "lease-renewal");
        var registration = new SlowWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-lease-worker";
                options.SendWakeNotifications = false;
                options.IdlePollingInterval = TimeSpan.FromMilliseconds(20);
                options.HeartbeatStaleAfter = TimeSpan.FromSeconds(1);
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-lease-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-lease-command"),
            "runtime-pump-lease-key",
            SlowWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent,
            new DurableWorkRetryPolicy(
                maximumAttempts: 2,
                maximumElapsedTime: TimeSpan.FromMinutes(1),
                initialRetryDelay: TimeSpan.FromMilliseconds(10),
                maximumRetryDelay: TimeSpan.FromMilliseconds(10),
                leaseDuration: TimeSpan.FromSeconds(2),
                renewalCadence: TimeSpan.FromMilliseconds(200),
                maximumLeaseLifetime: TimeSpan.FromMinutes(1),
                backoffAlgorithm: "exponential-v1")));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Processed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Succeeded, snapshot.Value!.State);
        Assert.Equal(DurableRuntimeHealthState.Healthy, (await provider.GetRequiredService<IDurableRuntimeHealth>().GetAsync()).State);
    }

    [Fact]
    public async Task RunOnceAsync_ObservesCancellationThatCommitsAfterClaimBeforeAnEffectPermit()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "cancel-before-permit");
        var registration = new PreparationGatedWorkRegistration();
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-cancel-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-cancel-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-cancel-command"),
            "runtime-pump-cancel-key",
            PreparationGatedWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.Idempotent));
        Assert.True(accepted.IsSuccess);

        var pump = provider.GetRequiredService<IDurableRuntimePump>();
        var running = pump.RunOnceAsync(new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work)).AsTask();
        await registration.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var control = provider.GetRequiredService<IDurableWorkControlClient>();
        var claimed = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(claimed.IsSuccess);
        Assert.Equal(DurableWorkState.Claimed, claimed.Value!.State);
        var canceled = await control.CancelAsync(new DurableWorkCancelRequest(
            scope,
            accepted.Value.WorkId,
            "runtime-pump-cancel-operator",
            "requested",
            claimed.Value.Revision));
        Assert.True(canceled.IsSuccess);

        registration.AllowPreparation.Set();
        var result = await running;

        Assert.Equal(1, result.Deferred);
        var snapshot = await control.GetAsync(new DurableWorkGetRequest(scope, accepted.Value.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.CanceledBeforeEffect, snapshot.Value!.State);
    }

    [Fact]
    public async Task RunOnceAsync_FailsClosedWhenAProviderKeyedEffectCannotReportTerminalTruth()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await schema.ApplyAsync();
        var epoch = Guid.NewGuid();
        await schema.InitializeRuntimeEpochAsync(epoch, "runtime-pump-tests", "retryable-failure");
        var registration = new FailingWorkRegistration(DurableProviderSafety.ProviderKeyed);
        var services = new ServiceCollection();
        services.AddSingleton<DurableWorkRegistration>(registration);
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.DataSource,
            new PostgreSqlDurableWorkOptions(epoch, (await schema.GetStatusAsync()).StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "runtime-pump-retryable-worker";
                options.SendWakeNotifications = false;
            });
        await using var provider = services.BuildServiceProvider();
        var scope = new DurableScopeId("runtime-pump-retryable-scope");
        var accepted = await provider.GetRequiredService<IDurableWorkClient>().EnqueueAsync(new DurableWorkRequest(
            scope,
            new DurableCommandId("runtime-pump-retryable-command"),
            "runtime-pump-retryable-key",
            FailingWorkRegistration.Name,
            "v1",
            registration.InputCodec.EncodeObject(Encoding.UTF8.GetBytes("input")),
            DurableProviderSafety.ProviderKeyed));
        Assert.True(accepted.IsSuccess);

        var result = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        Assert.Equal(1, result.Failed);
        var snapshot = await provider.GetRequiredService<IDurableWorkControlClient>().GetAsync(
            new DurableWorkGetRequest(scope, accepted.Value!.WorkId));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableWorkState.Suspended, snapshot.Value!.State);
    }

    private sealed class SuccessfulWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump";

        internal SuccessfulWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            _ = WorkCodec.DecodeObject(work.Payload);
            return new SuccessfulPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
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
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class FailingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.failure";

        internal FailingWorkRegistration(DurableProviderSafety providerSafety = DurableProviderSafety.Idempotent)
            : base(
                Name,
                "v1",
                providerSafety,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.failure.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.failure.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new FailingPreparedWork();

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class BlockingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.blocking";

        internal BlockingWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.blocking.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.blocking.result", "v1"))
        {
        }

        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<DurableEncodedPayload> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new BlockingPreparedWork(Started, Complete);

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class PreparationFailingWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.prepare-failure";

        internal PreparationFailingWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-failure.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-failure.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            throw new InvalidOperationException("Simulated provider preparation failure.");

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DurableEncodedPayload>(new InvalidOperationException("Preparation must run first."));

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class SlowWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.slow";

        internal SlowWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.slow.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.slow.result", "v1"))
        {
        }

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work) =>
            new SlowPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));

        public override ValueTask<DurableEncodedPayload> InvokeAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            Prepare(services, work).InvokeAsync(cancellationToken);

        public override ValueTask<DurableEncodedEffectReconciliation> ReconcileAsync(
            IServiceProvider services,
            DurableWorkExecutionContext work,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class PreparationGatedWorkRegistration : DurableWorkRegistration
    {
        internal const string Name = "tests.runtime-pump.prepare-gated";

        internal PreparationGatedWorkRegistration()
            : base(
                Name,
                "v1",
                DurableProviderSafety.Idempotent,
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-gated.input", "v1"),
                new PostgreSqlOpaqueTestCodec("tests.runtime-pump.prepare-gated.result", "v1"))
        {
        }

        internal TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim AllowPreparation { get; } = new(false);

        internal IDurablePayloadCodec InputCodec => WorkCodec;

        public override bool CanReconcile => false;

        public override DurablePreparedWork Prepare(IServiceProvider services, DurableWorkExecutionContext work)
        {
            PreparationStarted.TrySetResult();
            AllowPreparation.Wait();
            return new SuccessfulPreparedWork(ResultCodec.EncodeObject(Encoding.UTF8.GetBytes("result")));
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
            throw new InvalidOperationException("Idempotent test Work does not reconcile.");
    }

    private sealed class SuccessfulPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class FailingPreparedWork : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<DurableEncodedPayload>(new InvalidOperationException("Simulated provider failure."));
    }

    private sealed class BlockingPreparedWork(
        TaskCompletionSource started,
        TaskCompletionSource<DurableEncodedPayload> complete) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            return new ValueTask<DurableEncodedPayload>(complete.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class SlowPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override async ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(850), cancellationToken);
            return result;
        }
    }

    private sealed class CompletingFlowRegistration(IDurablePayloadCodec contextCodec) : DurableFlowRegistration
    {
        public override string FlowId => "tests.runtime-pump.flow";

        public override string FlowVersion => "v1";

        public override string ImplementationVersion => "tests-runtime-pump-v1";

        public override string StartNodeId => "start";

        public override string DefinitionFingerprint => new('f', 64);

        public override IDurablePayloadCodec ContextCodec { get; } = contextCodec;

        public override IReadOnlyList<DurableFlowEventBinding> EventBindings => [];

        public override IReadOnlyList<DurableWorkRegistration> ActivityWorkRegistrations => [];

        public override ValueTask<DurableFlowEvaluationResult> EvaluateAsync(
            DurableFlowEvaluationInput input,
            IDurablePayloadCodecRegistry payloadCodecs,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DurableFlowEvaluationResult(
                FlowTransitionKind.Complete,
                input.NodeId,
                input.Context,
                nextNodeId: null,
                eventName: null,
                timeout: null,
                fault: null,
                activity: null));
    }

    private sealed class RuntimeSchedulePayloadCodec(
        string contractName,
        string contractVersion,
        DurableDataClassification classification) : IDurablePayloadCodec<byte[]>
    {
        public Type PayloadType => typeof(byte[]);

        public string ContractName { get; } = contractName;

        public string ContractVersion { get; } = contractVersion;

        public DurableDataClassification Classification { get; } = classification;

        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(byte[] value) =>
            new(ContractName, ContractVersion, Classification, value, RetentionPolicyId);

        public DurableEncodedPayload EncodeObject(object value) => Encode(Assert.IsType<byte[]>(value));

        public byte[] Decode(DurableEncodedPayload payload) => (byte[])DecodeObject(payload);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.ContractName != ContractName
                || payload.ContractVersion != ContractVersion
                || payload.Classification != Classification
                || payload.RetentionPolicyId != RetentionPolicyId)
            {
                throw new InvalidOperationException("The runtime Schedule test payload does not match its registered contract.");
            }

            return payload.Content.ToArray();
        }
    }
}
