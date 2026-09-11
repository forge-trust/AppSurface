using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class PostgreSqlDurableFlowDefinitionCodecTests
{
    [Fact]
    public async Task Definition_owned_context_view_is_accepted_by_the_postgresql_client_and_completes()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database, "definition-flow");
        var definition = CreateWorkDefinition();
        var flowDefinition = CreateCompletingFlowDefinition();
        var services = new ServiceCollection();

        services.AddDurableWork(definition.ExecutedBy<NoOpExecutor>());
        services.AddDurableFlow(
            flowDefinition,
            definition.WorkCodec,
            "definition-flow-implementation");
        services.AddAppSurfaceDurablePostgreSql(
            database.DataSource,
            database.CreateDataSource(),
            new PostgreSqlDurableWorkOptions(schema.RuntimeEpoch, schema.StoreId),
            new PostgreSqlDurableScheduleOptions("appsurface"),
            options =>
            {
                options.WorkerId = "definition-flow-worker";
                options.SendWakeNotifications = false;
            });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IDurableFlowClient>();
        var selectedCodec = provider.GetRequiredService<IDurablePayloadCodecRegistry>().GetRequired(
            typeof(FlowContext),
            definition.WorkCodec.ContractName,
            definition.WorkCodec.ContractVersion);
        Assert.NotSame(definition.WorkCodec, selectedCodec);
        Assert.True(DurablePayloadCodecSnapshot.AreCompatible(definition.WorkCodec, selectedCodec));
        var instance = new DurableFlowInstanceId("definition-flow-instance");
        var request = new DurableFlowStartRequest(
            new DurableScopeId("definition-flow-scope"),
            new DurableCommandId("definition-flow-command"),
            "definition-flow-key",
            instance,
            flowDefinition.FlowId,
            flowDefinition.Version,
            definition.WorkCodec.Encode(new FlowContext("start")));

        var accepted = await client.StartAsync(request);
        var duplicate = await client.StartAsync(request);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Duplicate, duplicate.Value!.Outcome);
        Assert.Equal(1, await CountFlowInstancesAsync(database, request.ScopeId));

        var pump = await provider.GetRequiredService<IDurableRuntimePump>().RunOnceAsync(
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Flow));
        Assert.Equal(1, pump.Discovered);
        Assert.Equal(1, pump.Claimed);
        Assert.Equal(1, pump.Processed);
        Assert.Equal(0, pump.Failed);

        var snapshot = await client.GetAsync(new DurableFlowGetRequest(request.ScopeId, instance));
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(DurableFlowState.Completed, snapshot.Value!.State);
    }

    [Fact]
    public async Task Equal_looking_unrelated_context_codec_is_rejected_before_persistence()
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database, "unrelated-flow");
        var expected = new FlowCodec("tests.definition.flow.context", "v1");
        var unrelated = new FlowCodec(expected.ContractName, expected.ContractVersion);
        var registration = CreateRegistration(expected);
        var client = CreateClient(
            database,
            schema,
            registration,
            new FixedPayloadCodecRegistry(unrelated));
        var request = CreateRequest(registration, expected, "unrelated-flow");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.StartAsync(request));

        Assert.Contains("exact allowlisted context codec", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountFlowInstancesAsync(database, request.ScopeId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatible_selected_context_codec_rejects_null_or_wrong_decode_before_persistence(bool returnWrongType)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database, returnWrongType ? "wrong-flow" : "null-flow");
        var codec = new FlowCodec("tests.definition.flow.context", "v1")
        {
            DecodeObjectOverride = returnWrongType
                ? _ => "wrong-type"
                : _ => null,
        };
        var registration = CreateRegistration(codec);
        var client = CreateClient(database, schema, registration, new FixedPayloadCodecRegistry(codec));
        var request = CreateRequest(registration, codec, returnWrongType ? "wrong-flow" : "null-flow");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.StartAsync(request));

        Assert.Contains("context codec returned an incompatible payload type", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountFlowInstancesAsync(database, request.ScopeId));
    }

    [Theory]
    [InlineData(DurableDataClassification.ApprovedApplication, "different-retention")]
    [InlineData(DurableDataClassification.Operational, DurableEncodedPayload.DefaultRetentionPolicyId)]
    public async Task Definition_context_view_rejects_selected_raw_codec_metadata_before_persistence(
        DurableDataClassification classification, string retentionPolicyId)
    {
        await using var database = await PostgreSqlIntegrationTestDatabase.TryCreateAsync();
        var schema = await InitializeAsync(database, "metadata-flow");
        var source = new FlowCodec("tests.definition.flow.context", "v1");
        var contextView = (IDurablePayloadCodec<FlowContext>)DurablePayloadCodecSnapshot.Capture(source).CreateView();
        var registration = CreateRegistration(contextView);
        var client = CreateClient(database, schema, registration, new FixedPayloadCodecRegistry(source));
        var invalidRequest = new DurableFlowStartRequest(
            new DurableScopeId("metadata-flow-scope"),
            new DurableCommandId("metadata-flow-invalid-command"),
            "metadata-flow-invalid-key",
            new DurableFlowInstanceId("metadata-flow-invalid-instance"),
            registration.FlowId,
            registration.FlowVersion,
            new DurableEncodedPayload(
                source.ContractName,
                source.ContractVersion,
                classification,
                new byte[] { 1 },
                retentionPolicyId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.StartAsync(invalidRequest));

        Assert.Contains("does not match the captured codec contract", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.DecodeObjectCalls);
        Assert.Equal(0, await CountFlowInstancesAsync(database, invalidRequest.ScopeId));

        var validRequest = CreateRequest(
            registration,
            contextView,
            "metadata-flow-valid");
        var accepted = await client.StartAsync(validRequest);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(DurableFlowCommandOutcome.Accepted, accepted.Value!.Outcome);
        Assert.Equal(1, await CountFlowInstancesAsync(database, validRequest.ScopeId));
    }

    private static DurableWorkDefinition<FlowContext, FlowResult> CreateWorkDefinition() =>
        DurableWork.Define(
            "tests.definition.flow.work",
            "v1",
            new FlowCodec("tests.definition.flow.context", "v1"),
            new ResultCodec("tests.definition.flow.result", "v1"),
            DurableProviderSafety.Idempotent,
            DurableWorkRetryPolicy.Default);

    private static FlowDefinition<FlowContext> CreateCompletingFlowDefinition() =>
        FlowGraphBuilder<FlowContext>
            .Create("tests.definition.flow", "v1")
            .AddNode("complete", new CompleteNode())
            .StartAt("complete")
            .Build();

    private static DurableFlowRegistration<FlowContext> CreateRegistration(IDurablePayloadCodec<FlowContext> codec) =>
        new(
            CreateCompletingFlowDefinition(),
            codec,
            "definition-flow-implementation",
            new FlowTransitionEvaluator<FlowContext>());

    private static DurableFlowStartRequest CreateRequest(
        DurableFlowRegistration<FlowContext> registration,
        IDurablePayloadCodec<FlowContext> codec,
        string suffix) =>
        new(
            new DurableScopeId($"{suffix}-scope"),
            new DurableCommandId($"{suffix}-command"),
            $"{suffix}-key",
            new DurableFlowInstanceId($"{suffix}-instance"),
            registration.FlowId,
            registration.FlowVersion,
            codec.Encode(new FlowContext("start")));

    private static PostgreSqlDurableFlowClient CreateClient(
        PostgreSqlIntegrationTestDatabase database,
        PostgreSqlDurableRuntimeSchemaStatus schema,
        DurableFlowRegistration<FlowContext> registration,
        IDurablePayloadCodecRegistry payloadCodecs) =>
        new(
            database.DataSource,
            new SingleFlowRegistry(registration),
            payloadCodecs,
            new PostgreSqlDurableWorkOptions(schema.RuntimeEpoch, schema.StoreId));

    private static async ValueTask<PostgreSqlDurableRuntimeSchemaStatus> InitializeAsync(
        PostgreSqlIntegrationTestDatabase database,
        string storeId)
    {
        var manager = new PostgreSqlDurableRuntimeSchemaManager(database.DataSource);
        await manager.ApplyAsync();
        var epoch = Guid.NewGuid();
        await manager.InitializeRuntimeEpochAsync(epoch, "definition-flow-tests", storeId);
        var status = await manager.GetStatusAsync();
        return new PostgreSqlDurableRuntimeSchemaStatus(epoch, status.StoreId);
    }

    private static async ValueTask<long> CountFlowInstancesAsync(
        PostgreSqlIntegrationTestDatabase database,
        DurableScopeId scope)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setScope = connection.CreateCommand())
        {
            setScope.Transaction = transaction;
            setScope.CommandText = "SELECT set_config('appsurface_durable.scope_id', @scope_id, true);";
            setScope.Parameters.AddWithValue("scope_id", scope.Value);
            await setScope.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM appsurface_durable.flow_instance WHERE scope_id = @scope_id;";
        command.Parameters.AddWithValue("scope_id", scope.Value);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private sealed record PostgreSqlDurableRuntimeSchemaStatus(Guid RuntimeEpoch, Guid StoreId);

    private sealed record FlowContext(string Value);

    private sealed record FlowResult(string Value);

    private sealed class CompleteNode : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                FlowNodeOutcome<FlowContext>.Complete(context.State));
    }

    private sealed class NoOpExecutor : IDurableWorkerExecutor<FlowContext, FlowResult>
    {
        public ValueTask<FlowResult> ExecuteAsync(
            DurableWorkerEnvelope<FlowContext> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FlowResult(work.Payload!.Value));
    }

    private sealed class FlowCodec : IDurablePayloadCodec<FlowContext>
    {
        public FlowCodec(string contractName, string contractVersion)
        {
            ContractName = contractName;
            ContractVersion = contractVersion;
        }

        public Type PayloadType => typeof(FlowContext);
        public string ContractName { get; }
        public string ContractVersion { get; }
        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;
        public Func<DurableEncodedPayload, object?>? DecodeObjectOverride { get; init; }
        public int DecodeObjectCalls { get; private set; }

        public DurableEncodedPayload Encode(FlowContext value) => new(
            ContractName,
            ContractVersion,
            Classification,
            System.Text.Encoding.UTF8.GetBytes(value.Value),
            RetentionPolicyId);

        public FlowContext Decode(DurableEncodedPayload payload) =>
            new(System.Text.Encoding.UTF8.GetString(payload.Content.Span));

        public DurableEncodedPayload EncodeObject(object value) => Encode((FlowContext)value);

        public object DecodeObject(DurableEncodedPayload payload)
        {
            DecodeObjectCalls++;
            return DecodeObjectOverride is { } decode ? decode(payload)! : Decode(payload);
        }
    }

    private sealed class ResultCodec(string contractName, string contractVersion) : IDurablePayloadCodec<FlowResult>
    {
        public Type PayloadType => typeof(FlowResult);
        public string ContractName { get; } = contractName;
        public string ContractVersion { get; } = contractVersion;
        public DurableDataClassification Classification => DurableDataClassification.ApprovedApplication;
        public string RetentionPolicyId => DurableEncodedPayload.DefaultRetentionPolicyId;

        public DurableEncodedPayload Encode(FlowResult value) => new(
            ContractName,
            ContractVersion,
            Classification,
            System.Text.Encoding.UTF8.GetBytes(value.Value),
            RetentionPolicyId);

        public FlowResult Decode(DurableEncodedPayload payload) =>
            new(System.Text.Encoding.UTF8.GetString(payload.Content.Span));

        public DurableEncodedPayload EncodeObject(object value) => Encode((FlowResult)value);

        public object DecodeObject(DurableEncodedPayload payload) => Decode(payload);
    }

    private sealed class SingleFlowRegistry(DurableFlowRegistration<FlowContext> registration) : IDurableFlowRegistry
    {
        public DurableFlowRegistration GetRequired(string flowId, string flowVersion) => registration;
    }

    private sealed class FixedPayloadCodecRegistry(IDurablePayloadCodec codec) : IDurablePayloadCodecRegistry
    {
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();

        public IDurablePayloadCodec GetRequired(Type payloadType) => codec;

        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) => codec;

        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) => codec;
    }
}
