using System.Text;
using ForgeTrust.AppSurface.Flow;
using Npgsql;

namespace ForgeTrust.AppSurface.Durable.PostgreSql.Tests;

public sealed class DurableFlowRequestFingerprintTests
{
    [Fact]
    public void Compute_StartExcludesTransportLookupKeysButIncludesSemanticTargetAndContext()
    {
        var original = CreateStart("command-a", "key-a", "instance-a", "flow-a", "v1", "context-a");
        var exactRetry = CreateStart("command-a", "key-a", "instance-a", "flow-a", "v1", "context-a");

        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(exactRetry));
        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateStart("command-b", "key-a", "instance-a", "flow-a", "v1", "context-a")));
        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateStart("command-a", "key-b", "instance-a", "flow-a", "v1", "context-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateStart("command-a", "key-a", "instance-b", "flow-a", "v1", "context-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateStart("command-a", "key-a", "instance-a", "flow-b", "v1", "context-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateStart("command-a", "key-a", "instance-a", "flow-a", "v2", "context-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateStart("command-a", "key-a", "instance-a", "flow-a", "v1", "context-b")));
    }

    [Fact]
    public void Compute_EventExcludesTransportLookupKeysButIncludesWaitRevisionAndPayload()
    {
        var original = CreateEvent("command-a", "event-a", "ready", 3, "payload-a");

        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-a", "event-a", "ready", 3, "payload-a")));
        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-b", "event-a", "ready", 3, "payload-a")));
        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-a", "event-b", "ready", 3, "payload-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-a", "event-a", "other", 3, "payload-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-a", "event-a", "ready", 4, "payload-a")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-a", "event-a", "ready", 3, "payload-b")));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(CreateEvent("command-a", "event-a", "ready", 3, payload: null)));
    }

    [Fact]
    public void Compute_CancellationExcludesCommandLookupKeyButIncludesActorReasonAndRevision()
    {
        var original = CreateCancellation("command-a", "operator-a", "consumer_requested", 3);

        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateCancellation("command-a", "operator-a", "consumer_requested", 3)));
        Assert.Equal(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateCancellation("command-b", "operator-a", "consumer_requested", 3)));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateCancellation("command-a", "operator-b", "consumer_requested", 3)));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateCancellation("command-a", "operator-a", "account_closed", 3)));
        Assert.NotEqual(
            DurableFlowRequestFingerprint.Compute(original),
            DurableFlowRequestFingerprint.Compute(
                CreateCancellation("command-a", "operator-a", "consumer_requested", 4)));
    }

    [Fact]
    public void CreateActivityIdentity_IsStableAndRevisionScoped()
    {
        var scope = new DurableScopeId("scope-a");
        var instance = new DurableFlowInstanceId("instance-a");

        var first = DurableFlowRequestFingerprint.CreateActivityIdentity(scope, instance, 4, "send");
        var retry = DurableFlowRequestFingerprint.CreateActivityIdentity(scope, instance, 4, "send");
        var nextRevision = DurableFlowRequestFingerprint.CreateActivityIdentity(scope, instance, 5, "send");

        Assert.Equal(first, retry);
        Assert.NotEqual(first, nextRevision);
        Assert.StartsWith("flow-activity-", first, StringComparison.Ordinal);
        Assert.Equal(46, first.Length);
    }

    [Fact]
    public async Task StoreAndClient_RejectMissingDependenciesAndEmptyEpoch()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var registry = new DurableFlowRegistry([]);
        var codecs = new DurablePayloadCodecRegistry();

        Assert.Throws<ArgumentNullException>(() => new PostgreSqlDurableFlowStore(null!, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new PostgreSqlDurableFlowStore(dataSource, Guid.Empty));
        Assert.Throws<ArgumentNullException>(
            () => new PostgreSqlDurableFlowClient(null!, registry, codecs, Guid.NewGuid()));
        Assert.Throws<ArgumentNullException>(
            () => new PostgreSqlDurableFlowClient(dataSource, null!, codecs, Guid.NewGuid()));
        Assert.Throws<ArgumentNullException>(
            () => new PostgreSqlDurableFlowClient(dataSource, registry, null!, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(
            () => new PostgreSqlDurableFlowClient(dataSource, registry, codecs, Guid.Empty));
    }

    [Fact]
    public async Task Client_RequiresExactContextCodecAndAppliesItsDecodePolicyBeforeDatabaseAccess()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=not-opened;Username=not-opened;Password=not-opened");
        var definition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.client-validation", "v1")
            .AddNode("done", new CountingCompleteFlowNode(new FlowNodeTracker()))
            .StartAt("done")
            .Build();
        var registrationCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowContext>(
            "tests.client-context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowContext,
            context => context.Step is >= 0 and < 10);
        var shadowCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowContext>(
            "tests.client-context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowContext,
            _ => true);
        var registration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            definition,
            registrationCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var registry = new DurableFlowRegistry([registration]);
        var request = new DurableFlowStartRequest(
            new DurableScopeId("scope-validation"),
            new DurableCommandId("command-validation"),
            "idempotency-validation",
            new DurableFlowInstanceId("instance-validation"),
            registration.FlowId,
            registration.FlowVersion,
            registrationCodec.Encode(new PostgreSqlFlowContext(1)));
        var shadowClient = new PostgreSqlDurableFlowClient(
            dataSource,
            registry,
            new DurablePayloadCodecRegistry([shadowCodec]),
            Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await shadowClient.StartAsync(request));

        var policyClient = new PostgreSqlDurableFlowClient(
            dataSource,
            registry,
            new DurablePayloadCodecRegistry([registrationCodec]),
            Guid.NewGuid());
        var rejectedContext = new DurableEncodedPayload(
            registrationCodec.ContractName,
            registrationCodec.ContractVersion,
            DurableDataClassification.ApprovedApplication,
            "{\"Step\":99}"u8.ToArray());
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await policyClient.StartAsync(
            new DurableFlowStartRequest(
                request.ScopeId,
                request.CommandId,
                request.IdempotencyKey,
                request.InstanceId,
                request.FlowId,
                request.FlowVersion,
                rejectedContext)));
    }

    [Fact]
    public void DefinitionFingerprint_IsDeterministicAndActivityBindingsRequireExactGlobalRegistrations()
    {
        var contextCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowContext>(
            "tests.manifest-context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowContext,
            _ => true);
        var firstDefinition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.manifest", "v1")
            .AddNode("first", new CountingNextFlowNode(new FlowNodeTracker()), "done")
            .AddNode("done", new CountingCompleteFlowNode(new FlowNodeTracker()))
            .StartAt("first")
            .Build();
        var equivalentDefinition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.manifest", "v1")
            .AddNode("done", new CountingCompleteFlowNode(new FlowNodeTracker()))
            .AddNode("first", new CountingNextFlowNode(new FlowNodeTracker()), "done")
            .StartAt("first")
            .Build();
        var changedDefinition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.manifest", "v1")
            .AddNode("done", new CountingCompleteFlowNode(new FlowNodeTracker()))
            .StartAt("done")
            .Build();
        var first = new DurableFlowRegistration<PostgreSqlFlowContext>(
            firstDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var equivalent = new DurableFlowRegistration<PostgreSqlFlowContext>(
            equivalentDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var changed = new DurableFlowRegistration<PostgreSqlFlowContext>(
            changedDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());
        var changedImplementation = new DurableFlowRegistration<PostgreSqlFlowContext>(
            equivalentDefinition,
            contextCodec,
            "implementation-v2",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>());

        Assert.Equal(first.DefinitionFingerprint, equivalent.DefinitionFingerprint);
        Assert.NotEqual(first.DefinitionFingerprint, changed.DefinitionFingerprint);
        Assert.NotEqual(first.DefinitionFingerprint, changedImplementation.DefinitionFingerprint);
        Assert.Equal(64, first.DefinitionFingerprint.Length);

        var callsite = new FlowActivityCallsite<PostgreSqlFlowWork, PostgreSqlFlowResult>("manifest-work", 1, 1);
        var workCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowWork>(
            "tests.manifest-work",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowWork,
            _ => true);
        var shadowWorkCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowWork>(
            "tests.manifest-work",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowWork,
            _ => true);
        var resultCodec = new SystemTextJsonDurablePayloadCodec<PostgreSqlFlowResult>(
            "tests.manifest-result",
            "v1",
            DurableDataClassification.Operational,
            PostgreSqlFlowJsonContext.Default.PostgreSqlFlowResult,
            _ => true);
        var workRegistration = new DurableWorkRegistration<
            PostgreSqlFlowWork,
            PostgreSqlFlowResult,
            PostgreSqlFlowExecutor>(
                "tests.manifest-work",
                "v1",
                DurableProviderSafety.Idempotent,
                workCodec,
                resultCodec);
        Assert.Throws<ArgumentException>(() => new DurableFlowActivityBinding<
            PostgreSqlFlowContext,
            PostgreSqlFlowWork,
            PostgreSqlFlowResult>(callsite, workRegistration, shadowWorkCodec, resultCodec));
        var binding = new DurableFlowActivityBinding<
            PostgreSqlFlowContext,
            PostgreSqlFlowWork,
            PostgreSqlFlowResult>(callsite, workRegistration, workCodec, resultCodec);
        var activityDefinition = FlowGraphBuilder<PostgreSqlFlowContext>
            .Create("tests.manifest-activity", "v1")
            .AddNode("activity", new PostgreSqlActivityFlowNode(callsite))
            .StartAt("activity")
            .Build();
        var activityRegistration = new DurableFlowRegistration<PostgreSqlFlowContext>(
            activityDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<PostgreSqlFlowContext>(),
            [binding]);

        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry(
            [activityRegistration],
            new DurableWorkRegistry([])));
        var validatedRegistry = new DurableFlowRegistry(
            [activityRegistration],
            new DurableWorkRegistry([workRegistration]));
        Assert.Same(activityRegistration, validatedRegistry.GetRequired("tests.manifest-activity", "v1"));
    }

    private static DurableFlowStartRequest CreateStart(
        string command,
        string idempotencyKey,
        string instance,
        string flowId,
        string flowVersion,
        string context) =>
        new(
            new DurableScopeId("scope-a"),
            new DurableCommandId(command),
            idempotencyKey,
            new DurableFlowInstanceId(instance),
            flowId,
            flowVersion,
            Payload("tests.context", context));

    private static DurableFlowEventRequest CreateEvent(
        string command,
        string eventId,
        string eventName,
        long? revision,
        string? payload) =>
        new(
            new DurableScopeId("scope-a"),
            new DurableCommandId(command),
            new DurableFlowEventId(eventId),
            new DurableFlowInstanceId("instance-a"),
            eventName,
            payload is null ? null : Payload("tests.event", payload),
            revision);

    private static DurableFlowCancelRequest CreateCancellation(
        string command,
        string actor,
        string reason,
        long revision) =>
        new(
            new DurableScopeId("scope-a"),
            new DurableCommandId(command),
            new DurableFlowInstanceId("instance-a"),
            actor,
            reason,
            revision);

    private static DurableEncodedPayload Payload(string contract, string content) =>
        new(
            contract,
            "v1",
            DurableDataClassification.Operational,
            Encoding.UTF8.GetBytes(content));
}
