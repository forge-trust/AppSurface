using ForgeTrust.AppSurface.Flow;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableReplayFlowCoverageTests
{
    private static readonly DurableScopeId Scope = new("scope");
    private static readonly DurableFlowInstanceId Instance = new("instance");
    private static readonly DateTimeOffset LocalTime = new(2026, 7, 15, 1, 2, 3, TimeSpan.FromHours(-4));

    [Fact]
    public void Flow_query_contracts_preserve_values_and_copy_results()
    {
        var get = new DurableFlowGetRequest(Scope, Instance);
        var list = new DurableFlowListRequest(Scope, DurableFlowState.Suspended, true, 25, "next.page");
        var snapshot = CreateSnapshot();
        var source = new List<DurableFlowSnapshot> { snapshot };
        var result = new DurableFlowListResult(source, "next.page");
        source.Clear();

        Assert.Equal(Scope, get.ScopeId);
        Assert.Equal(Instance, get.InstanceId);
        Assert.Equal(Scope, list.ScopeId);
        Assert.Equal(DurableFlowState.Suspended, list.State);
        Assert.True(list.RequiresRecoveryRelease);
        Assert.Equal(25, list.PageSize);
        Assert.Equal("next.page", list.ContinuationToken);
        Assert.Same(snapshot, Assert.Single(result.Flows));
        Assert.Equal("next.page", result.ContinuationToken);
        Assert.Equal(Instance, snapshot.InstanceId);
        Assert.Equal("approval", snapshot.FlowId);
        Assert.Equal("v1", snapshot.FlowVersion);
        Assert.Equal(DurableFlowState.Suspended, snapshot.State);
        Assert.Equal("waiting", snapshot.CurrentNodeId);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.UpdatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.CancellationRequestedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.TerminalAtUtc!.Value.Offset);
        Assert.Equal("operator.hold", snapshot.TerminalCode);
        Assert.True(snapshot.RequiresRecoveryRelease);
    }

    [Fact]
    public void Flow_query_contracts_cover_defaults_and_validation()
    {
        var list = new DurableFlowListRequest(Scope);
        var snapshot = new DurableFlowSnapshot(
            Instance,
            "approval",
            "v1",
            DurableFlowState.Ready,
            "start",
            1,
            LocalTime,
            LocalTime,
            null,
            null,
            null);
        var result = new DurableFlowListResult([], null);

        Assert.Null(list.State);
        Assert.Null(list.RequiresRecoveryRelease);
        Assert.Equal(100, list.PageSize);
        Assert.Null(list.ContinuationToken);
        Assert.Null(snapshot.CancellationRequestedAtUtc);
        Assert.Null(snapshot.TerminalAtUtc);
        Assert.Null(snapshot.TerminalCode);
        Assert.False(snapshot.RequiresRecoveryRelease);
        Assert.Empty(result.Flows);
        Assert.Null(result.ContinuationToken);

        Assert.Throws<ArgumentException>(() => new DurableFlowGetRequest(default, Instance));
        Assert.Throws<ArgumentException>(() => new DurableFlowGetRequest(Scope, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(Scope, (DurableFlowState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(Scope, pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(Scope, pageSize: 1_001));
        Assert.Throws<ArgumentException>(() => new DurableFlowListRequest(Scope, continuationToken: "bad value"));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowListResult(null!, null));
        Assert.Throws<ArgumentException>(() => new DurableFlowSnapshot(
            default,
            "approval",
            "v1",
            DurableFlowState.Ready,
            "start",
            1,
            LocalTime,
            LocalTime,
            null,
            null,
            null));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(state: (DurableFlowState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateSnapshot(revision: 0));
        Assert.Throws<ArgumentException>(() => CreateSnapshot(terminalCode: "bad value"));
    }

    [Fact]
    public void Event_contracts_and_bindings_cover_payload_and_no_payload_shapes()
    {
        var codec = CreatePayloadCodec();
        var callsite = new FlowEventCallsite<TestPayload>("approved", "test.payload", "v1");
        var binding = new DurableFlowEventBinding<TestPayload>(callsite, codec);
        var required = new DurableFlowEventContract(
            true,
            codec.ContractName,
            codec.ContractVersion,
            codec.Classification,
            codec.RetentionPolicyId);
        var empty = new DurableFlowEventContract(false);

        Assert.Same(callsite, binding.Callsite);
        Assert.Same(codec, binding.PayloadCodec);
        Assert.True(required.PayloadRequired);
        Assert.Equal("test.payload", required.ContractName);
        Assert.Equal("v1", required.ContractVersion);
        Assert.Equal(DurableDataClassification.Operational, required.Classification);
        Assert.Equal(DurableEncodedPayload.DefaultRetentionPolicyId, required.RetentionPolicyId);
        Assert.False(empty.PayloadRequired);
        Assert.Null(empty.ContractName);
        Assert.Null(empty.ContractVersion);
        Assert.Null(empty.Classification);
        Assert.Null(empty.RetentionPolicyId);

        Assert.Throws<ArgumentException>(() => new DurableFlowEventContract(true));
        Assert.Throws<ArgumentException>(() => new DurableFlowEventContract(false, "test.payload", "v1", DurableDataClassification.Operational, "retention"));
        Assert.Throws<ArgumentException>(() => new DurableFlowEventContract(true, "bad value", "v1", DurableDataClassification.Operational, "retention"));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowEventBinding<TestPayload>(null!, codec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowEventBinding<TestPayload>(callsite, null!));
        Assert.Throws<ArgumentException>(() => new DurableFlowEventBinding<TestPayload>(
            callsite,
            CreatePayloadCodec("other.payload")));
    }

    [Fact]
    public void Activity_command_and_binding_preserve_contracts_and_validate_inputs()
    {
        var workCodec = CreatePayloadCodec();
        var resultCodec = CreateResultCodec();
        var registration = CreateWorkRegistration(workCodec, resultCodec);
        var callsite = new FlowActivityCallsite<TestPayload, TestResult>("send", 2, 3);
        var binding = new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            callsite,
            registration,
            workCodec,
            resultCodec);
        var request = new TestActivityRequest("send", 2, 3, new TestPayload("safe"));
        var encoded = binding.EncodeWork(request);
        var decodedResult = binding.DecodeResult(resultCodec.Encode(new TestResult("done")));
        var command = new DurableFlowActivityCommand(
            binding.CallsiteId,
            binding.ResultContractVersion,
            registration.WorkName,
            registration.WorkVersion,
            registration.ProviderSafety,
            encoded);

        Assert.Equal("send", binding.CallsiteId);
        Assert.Same(registration, binding.WorkRegistration);
        Assert.Equal(2, binding.WorkContractVersion);
        Assert.Equal(3, binding.ResultContractVersion);
        Assert.Equal(new TestPayload("safe"), workCodec.Decode(encoded));
        Assert.Equal("send", decodedResult.CallsiteId);
        Assert.Equal(3, decodedResult.ResultContractVersion);
        Assert.Equal("send", command.CallsiteId);
        Assert.Equal(3, command.ResultContractVersion);
        Assert.Equal("test.work", command.WorkName);
        Assert.Equal("v1", command.WorkVersion);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, command.ProviderSafety);
        Assert.Same(encoded, command.Work);

        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowActivityCommand(
            "send", 0, "test.work", "v1", DurableProviderSafety.ProviderKeyed, encoded));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowActivityCommand(
            "send", 1, "test.work", "v1", (DurableProviderSafety)999, encoded));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowActivityCommand(
            "send", 1, "test.work", "v1", DurableProviderSafety.ProviderKeyed, null!));
        Assert.Throws<ArgumentNullException>(() => binding.EncodeWork(null!));
        Assert.Throws<InvalidOperationException>(() => binding.EncodeWork(
            new TestActivityRequest("other", 2, 3, new TestPayload("safe"))));
        Assert.Throws<InvalidOperationException>(() => binding.EncodeWork(
            new TestActivityRequest("send", 1, 3, new TestPayload("safe"))));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            null!, registration, workCodec, resultCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            callsite, registration, null!, resultCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            callsite, registration, workCodec, null!));
        Assert.Throws<ArgumentException>(() => new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            callsite,
            CreateWorkRegistration(CreatePayloadCodec(), resultCodec),
            workCodec,
            resultCodec));
    }

    [Fact]
    public void Evaluation_input_and_result_cover_optional_shapes_and_validation()
    {
        var context = CreateContextCodec().Encode(new FlowTestContext(1));
        var payload = CreatePayloadCodec().Encode(new TestPayload("safe"));
        var eventInput = new DurableFlowEvaluationInput("wait", context, "approved", payload, true);
        var activityInput = new DurableFlowEvaluationInput("run", context, activityCallsiteId: "send", activityResult: payload);
        var result = new DurableFlowEvaluationResult(
            FlowTransitionKind.Wait,
            "wait",
            context,
            null,
            "approved",
            null,
            null,
            null,
            new DurableFlowEventContract(false));

        Assert.Equal("wait", eventInput.NodeId);
        Assert.Same(context, eventInput.Context);
        Assert.Equal("approved", eventInput.ResumeEventName);
        Assert.Same(payload, eventInput.ResumeEventPayload);
        Assert.True(eventInput.IsTimeout);
        Assert.Null(eventInput.ActivityCallsiteId);
        Assert.Null(eventInput.ActivityResult);
        Assert.Equal("send", activityInput.ActivityCallsiteId);
        Assert.Same(payload, activityInput.ActivityResult);
        Assert.Equal(FlowTransitionKind.Wait, result.Kind);
        Assert.Equal("wait", result.NodeId);
        Assert.Same(context, result.Context);
        Assert.Null(result.NextNodeId);
        Assert.Equal("approved", result.EventName);
        Assert.Null(result.Timeout);
        Assert.Null(result.Fault);
        Assert.Null(result.Activity);
        Assert.NotNull(result.EventContract);

        Assert.Throws<ArgumentNullException>(() => new DurableFlowEvaluationInput("node", null!));
        Assert.Throws<ArgumentException>(() => new DurableFlowEvaluationInput("bad value", context));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowEvaluationResult(
            (FlowTransitionKind)999, "node", context, null, null, null, null, null));
    }

    [Fact]
    public void Flow_registration_and_registries_cover_manifest_and_global_identity_rules()
    {
        var contextCodec = CreateContextCodec();
        var workCodec = CreatePayloadCodec();
        var resultCodec = CreateResultCodec();
        var workRegistration = CreateWorkRegistration(workCodec, resultCodec);
        var activityCallsite = new FlowActivityCallsite<TestPayload, TestResult>("send", 1, 1);
        var activityBinding = new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            activityCallsite,
            workRegistration,
            workCodec,
            resultCodec);
        var eventCallsite = new FlowEventCallsite<TestPayload>("approved", "test.payload", "v1");
        var eventBinding = new DurableFlowEventBinding<TestPayload>(eventCallsite, workCodec);
        var definition = FlowGraphBuilder<FlowTestContext>
            .Create("approval", "v1")
            .AddNode("run", new CompleteFlowNode())
            .StartAt("run")
            .Build();
        var registration = new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            [activityBinding],
            [eventBinding]);
        var workRegistry = new DurableWorkRegistry([workRegistration]);
        var payloadRegistry = new DurablePayloadCodecRegistry([contextCodec, workCodec, resultCodec]);
        var registry = new DurableFlowRegistry([registration], workRegistry, payloadRegistry);

        Assert.Equal("approval", registration.FlowId);
        Assert.Equal("v1", registration.FlowVersion);
        Assert.Equal("implementation-v1", registration.ImplementationVersion);
        Assert.Equal("run", registration.StartNodeId);
        Assert.Equal(64, registration.DefinitionFingerprint.Length);
        Assert.Equal(DurableFlowRegistration.CurrentAuthoringModel, registration.AuthoringModel);
        Assert.Same(contextCodec, registration.ContextCodec);
        Assert.Same(eventBinding, Assert.Single(registration.EventBindings));
        Assert.Same(workRegistration, Assert.Single(registration.ActivityWorkRegistrations));
        Assert.Same(registration, registry.GetRequired("approval", "v1"));

        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistration<FlowTestContext>(
            null!, contextCodec, "implementation-v1", new FlowTransitionEvaluator<FlowTestContext>()));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistration<FlowTestContext>(
            definition, null!, "implementation-v1", new FlowTransitionEvaluator<FlowTestContext>()));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistration<FlowTestContext>(
            definition, contextCodec, "implementation-v1", null!));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            [null!]));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            [activityBinding, activityBinding]));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            eventBindings: [eventBinding, eventBinding]));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistry(null!));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistry([null!]));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry([registration, registration]));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistry([registration], null!));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowRegistry([registration], workRegistry, null!));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry(
            [registration],
            new DurableWorkRegistry([CreateWorkRegistration(workCodec, resultCodec)])));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry(
            [registration],
            workRegistry,
            new DurablePayloadCodecRegistry([CreateContextCodec(), workCodec, resultCodec])));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("approval", "v2"));
    }

    private static DurableFlowSnapshot CreateSnapshot(
        DurableFlowInstanceId? instanceId = null,
        DurableFlowState state = DurableFlowState.Suspended,
        long revision = 7,
        string? terminalCode = "operator.hold") =>
        new(
            instanceId ?? Instance,
            "approval",
            "v1",
            state,
            "waiting",
            revision,
            LocalTime,
            LocalTime,
            LocalTime,
            LocalTime,
            terminalCode,
            true);

    private static SystemTextJsonDurablePayloadCodec<TestPayload> CreatePayloadCodec(
        string contractName = "test.payload") =>
        new(
            contractName,
            "v1",
            DurableDataClassification.Operational,
            DurableContractJsonContext.Default.TestPayload,
            _ => true);

    private static SystemTextJsonDurablePayloadCodec<TestResult> CreateResultCodec() =>
        new(
            "test.result",
            "v1",
            DurableDataClassification.Operational,
            DurableContractJsonContext.Default.TestResult,
            _ => true);

    private static SystemTextJsonDurablePayloadCodec<FlowTestContext> CreateContextCodec() =>
        new(
            "test.flow-context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            DurableContractJsonContext.Default.FlowTestContext,
            _ => true);

    private static DurableWorkRegistration CreateWorkRegistration(
        IDurablePayloadCodec<TestPayload> workCodec,
        IDurablePayloadCodec<TestResult> resultCodec) =>
        new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);

    private sealed class TestActivityRequest(
        string callsiteId,
        int workContractVersion,
        int resultContractVersion,
        TestPayload work) : IFlowActivityRequest<FlowTestContext>
    {
        public string CallsiteId => callsiteId;

        public Type WorkType => typeof(TestPayload);

        public int WorkContractVersion => workContractVersion;

        public Type ResultType => typeof(TestResult);

        public int ResultContractVersion => resultContractVersion;

        public object Work => work;

        public FlowTestContext Context => new(1);

        public FlowActivityWorkResult CreateResult(object result) =>
            new FlowActivityCallsite<TestPayload, TestResult>(CallsiteId, WorkContractVersion, ResultContractVersion)
                .CreateResult((TestResult)result);
    }
}
