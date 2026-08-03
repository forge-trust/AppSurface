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

    private sealed class SuccessfulPreparedWork(DurableEncodedPayload result) : DurablePreparedWork
    {
        public override ValueTask<DurableEncodedPayload> InvokeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
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
