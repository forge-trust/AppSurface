using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableFlowCodecCompatibilityTests
{
    [Fact]
    public void Activity_binding_accepts_raw_or_equivalent_views_but_executes_registration_views()
    {
        var workSource = new CountingCodec<FlowWork>("flow.work", "v1");
        var resultSource = new CountingCodec<FlowResult>("flow.result", "v1");
        var workView = (IDurablePayloadCodec<FlowWork>)DurablePayloadCodecSnapshot.Capture(workSource).CreateView();
        var resultView = (IDurablePayloadCodec<FlowResult>)DurablePayloadCodecSnapshot.Capture(resultSource).CreateView();
        var registration = new DurableWorkRegistration<FlowWork, FlowResult, FlowExecutor>(
            "work", "v1", DurableProviderSafety.ProviderKeyed, workView, resultView);
        var callsite = new FlowActivityCallsite<FlowWork, FlowResult>("activity", 1, 1);

        var binding = new DurableFlowActivityBinding<FlowContext, FlowWork, FlowResult>(
            callsite, registration, workSource, resultSource);
        var encodedWork = binding.EncodeWork(new ActivityRequest(new FlowWork("input")));
        var result = binding.DecodeResult(resultSource.Encode(new FlowResult("done")));

        Assert.Equal(new FlowWork("input"), workSource.Decode(encodedWork));
        Assert.Equal("activity", result.CallsiteId);
        Assert.Equal(1, workSource.EncodeCalls);
        Assert.Equal(1, resultSource.DecodeCalls);

        var unrelated = new CountingCodec<FlowWork>("flow.work", "v1");
        Assert.Throws<ArgumentException>(() => new DurableFlowActivityBinding<FlowContext, FlowWork, FlowResult>(
            callsite, registration, unrelated, resultSource));

        var differentSnapshotSource = new CountingCodec<FlowWork>("flow.work", "v1")
        {
            RetentionPolicyId = "different-retention",
        };
        Assert.Throws<ArgumentException>(() => new DurableFlowActivityBinding<FlowContext, FlowWork, FlowResult>(
            callsite, registration, differentSnapshotSource, resultSource));
    }

    [Fact]
    public void Activity_binding_keeps_result_expectation_from_the_registration_owned_view()
    {
        var workSource = new CountingCodec<FlowWork>("flow.work", "v1");
        var resultSource = new CountingCodec<FlowResult>("flow.result", "v1");
        var registration = new DurableWorkRegistration<FlowWork, FlowResult, FlowExecutor>(
            "work", "v1", DurableProviderSafety.ProviderKeyed,
            (IDurablePayloadCodec<FlowWork>)DurablePayloadCodecSnapshot.Capture(workSource).CreateView(),
            (IDurablePayloadCodec<FlowResult>)DurablePayloadCodecSnapshot.Capture(resultSource).CreateView());
        var binding = new DurableFlowActivityBinding<FlowContext, FlowWork, FlowResult>(
            new FlowActivityCallsite<FlowWork, FlowResult>("activity", 1, 4),
            registration,
            workSource,
            resultSource);

        resultSource.ContractName = "mutated.result";

        Assert.Equal("flow.result", binding.ResultExpectation.ContractId);
        Assert.Equal("v1", binding.ResultExpectation.SchemaVersion);
        Assert.Equal("flow.result@v1", binding.ResultExpectation.CodecId);
    }

    [Fact]
    public void Flow_registry_accepts_equivalent_context_and_event_views_but_rejects_unrelated_sources()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        var eventSource = new CountingCodec<FlowEvent>("flow.event", "v1");
        var callsite = new FlowEventCallsite<FlowEvent>("approved", "flow.event", "v1");
        var registration = CreateRegistration(
            contextSource,
            eventBindings: [new DurableFlowEventBinding<FlowEvent>(callsite, eventSource)]);
        var workRegistry = new DurableWorkRegistry([]);
        var payloadRegistry = new DurablePayloadCodecRegistry([
            DurablePayloadCodecSnapshot.Capture(contextSource).CreateView(),
            DurablePayloadCodecSnapshot.Capture(eventSource).CreateView(),
        ]);

        var registry = new DurableFlowRegistry([registration], workRegistry, payloadRegistry);

        Assert.Same(registration, registry.GetRequired("flow", "v1"));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry(
            [registration],
            workRegistry,
            new DurablePayloadCodecRegistry([
                new CountingCodec<FlowContext>("flow.context", "v1"),
                DurablePayloadCodecSnapshot.Capture(eventSource).CreateView(),
            ])));
    }

    [Fact]
    public void Flow_registry_keeps_exact_global_work_registration_identity_requirement()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        var workSource = new CountingCodec<FlowWork>("flow.work", "v1");
        var resultSource = new CountingCodec<FlowResult>("flow.result", "v1");
        var activity = new DurableFlowActivityBinding<FlowContext, FlowWork, FlowResult>(
            new FlowActivityCallsite<FlowWork, FlowResult>("activity", 1, 1),
            CreateWorkRegistration(workSource, resultSource),
            workSource,
            resultSource);
        var registration = CreateRegistration(contextSource, activityBindings: [activity]);
        var equivalentLookingRegistration = CreateWorkRegistration(workSource, resultSource);

        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry(
            [registration],
            new DurableWorkRegistry([equivalentLookingRegistration]),
            new DurablePayloadCodecRegistry([
                DurablePayloadCodecSnapshot.Capture(contextSource).CreateView(),
                DurablePayloadCodecSnapshot.Capture(workSource).CreateView(),
                DurablePayloadCodecSnapshot.Capture(resultSource).CreateView(),
            ])));
    }

    [Fact]
    public async Task Raw_only_context_and_event_references_keep_existing_flow_behavior()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        var eventSource = new CountingCodec<FlowEvent>("flow.event", "v1");
        var callsite = new FlowEventCallsite<FlowEvent>("approved", "flow.event", "v1");
        var definition = FlowGraphBuilder<FlowContext>
            .Create("raw-flow", "v1")
            .AddNode("wait", new EventNode<FlowEvent>(callsite))
            .AddNode("done", new ContextCompleteNode())
            .StartAt("wait")
            .Build();
        var registration = new DurableFlowRegistration<FlowContext>(
            definition,
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            eventBindings: [new DurableFlowEventBinding<FlowEvent>(callsite, eventSource)]);
        var codecs = new DurablePayloadCodecRegistry([contextSource, eventSource]);
        _ = new DurableFlowRegistry([registration], new DurableWorkRegistry([]), codecs);

        var waiting = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", contextSource.Encode(new FlowContext(1))), codecs);
        var resumed = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "wait",
                contextSource.Encode(new FlowContext(1)),
                resumeEventName: "approved",
                resumeEventPayload: eventSource.Encode(new FlowEvent("yes"))),
            codecs);

        Assert.Equal(FlowTransitionKind.Wait, waiting.Kind);
        Assert.Equal(FlowTransitionKind.Complete, resumed.Kind);
        Assert.Same(contextSource, registration.ContextCodec);
    }

    [Fact]
    public async Task Context_evaluation_uses_the_selected_compatible_codec_for_decode_and_encode()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        IDurablePayloadCodec contextView = DurablePayloadCodecSnapshot.Capture(contextSource).CreateView();
        var definition = FlowGraphBuilder<FlowContext>
            .Create("context-flow", "v1")
            .AddNode("start", new ContextNextNode(), "done")
            .AddNode("done", new ContextCompleteNode())
            .StartAt("start")
            .Build();
        var registration = new DurableFlowRegistration<FlowContext>(
            definition,
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>());
        var registry = new SingleCodecRegistry(contextView);

        var result = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("start", contextSource.Encode(new FlowContext(1))),
            registry);

        Assert.Equal(FlowTransitionKind.Next, result.Kind);
        Assert.Equal(1, contextSource.DecodeCalls);
        Assert.Equal(2, contextSource.EncodeCalls);
        Assert.Same(contextSource, registration.ContextCodec);
    }

    [Fact]
    public async Task Snapshot_context_guard_rejects_selected_raw_codec_payload_metadata_before_decode()
    {
        var source = new CountingCodec<FlowContext>("snapshot.context", "v1");
        var contextView = (IDurablePayloadCodec<FlowContext>)DurablePayloadCodecSnapshot.Capture(source).CreateView();
        var registration = CreateRegistration(contextView);
        var malformed = new DurableEncodedPayload(
            "snapshot.context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            new byte[] { 1 },
            "different-retention");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("start", malformed), new FixedRegistry(source)));

        Assert.Contains("does not match the captured codec contract", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, source.DecodeObjectCalls);
    }

    [Fact]
    public async Task Snapshot_context_guard_rejects_selected_raw_codec_output_metadata_after_encode()
    {
        var source = new CountingCodec<FlowContext>("snapshot.encode.context", "v1")
        {
            EncodeObjectOverride = _ => new DurableEncodedPayload(
                "snapshot.encode.context",
                "v1",
                DurableDataClassification.ApprovedApplication,
                new byte[] { 1 },
                "different-retention"),
        };
        var contextView = (IDurablePayloadCodec<FlowContext>)DurablePayloadCodecSnapshot.Capture(source).CreateView();
        var registration = CreateRegistration(contextView);
        var input = new DurableFlowEvaluationInput(
            "start",
            contextView.Encode(new FlowContext(1)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            input, new FixedRegistry(source)));

        Assert.Contains("does not match the captured codec contract", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, source.EncodeObjectCalls);
    }

    [Theory]
    [InlineData(DurableDataClassification.ApprovedApplication, "different-retention")]
    public async Task Snapshot_event_guard_rejects_selected_raw_codec_payload_classification_or_retention(
        DurableDataClassification classification, string retentionPolicyId)
    {
        var contextSource = new CountingCodec<FlowContext>("snapshot.event.context", "v1");
        var eventSource = new CountingCodec<FlowEvent>("snapshot.event", "v1");
        var callsite = new FlowEventCallsite<FlowEvent>("approved", "snapshot.event", "v1");
        var contextView = (IDurablePayloadCodec<FlowContext>)DurablePayloadCodecSnapshot.Capture(contextSource).CreateView();
        var eventView = (IDurablePayloadCodec<FlowEvent>)DurablePayloadCodecSnapshot.Capture(eventSource).CreateView();
        var registration = new DurableFlowRegistration<FlowContext>(
            FlowGraphBuilder<FlowContext>
                .Create("snapshot-event-flow", "v1")
                .AddNode("wait", new EventNode<FlowEvent>(callsite))
                .AddNode("done", new ContextCompleteNode())
                .StartAt("wait")
                .Build(),
            contextView,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            eventBindings: [new DurableFlowEventBinding<FlowEvent>(callsite, eventView)]);
        var malformed = new DurableEncodedPayload(
            "snapshot.event",
            "v1",
            classification,
            new byte[] { 1 },
            retentionPolicyId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "wait",
                contextView.Encode(new FlowContext(1)),
                resumeEventName: "approved",
                resumeEventPayload: malformed),
            new MultiCodecRegistry(contextSource, eventSource)));

        Assert.Contains("does not match the captured codec contract", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, eventSource.DecodeObjectCalls);
    }

    [Fact]
    public async Task Context_evaluation_fails_closed_for_missing_wrong_type_null_and_unrelated_codecs()
    {
        var source = new CountingCodec<FlowContext>("flow.context", "v1");
        var registration = CreateRegistration(source);
        var input = new DurableFlowEvaluationInput("start", source.Encode(new FlowContext(1)));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(input, new ThrowingRegistry()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(input, new NullRegistry()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(input,
            new FixedRegistry(new CountingCodec<FlowResult>("flow.context", "v1"))));

        source.ReturnNull = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(input,
            new FixedRegistry(DurablePayloadCodecSnapshot.Capture(source).CreateView())));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(input,
            new FixedRegistry(new CountingCodec<FlowContext>("flow.context", "v1")
            {
                RetentionPolicyId = "unrelated",
            })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_compatible_context_codec_rejects_wrong_or_null_untyped_decode(bool returnNull)
    {
        var source = new CountingCodec<FlowContext>("raw.guard.context", "v1")
        {
            DecodeObjectOverride = returnNull ? _ => null : _ => "wrong-context",
        };
        var registration = CreateRegistration(source);
        var input = new DurableFlowEvaluationInput("start", source.Encode(new FlowContext(1)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            input, new FixedRegistry(source)));

        Assert.Contains("context codec returned an incompatible payload type", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_compatible_event_codec_rejects_wrong_or_null_untyped_decode(bool returnNull)
    {
        var contextSource = new CountingCodec<FlowContext>("raw.guard.event.context", "v1");
        var eventSource = new CountingCodec<FlowEvent>("raw.guard.event", "v1")
        {
            DecodeObjectOverride = returnNull ? _ => null : _ => new FlowContext(2),
        };
        var callsite = new FlowEventCallsite<FlowEvent>("approved", "raw.guard.event", "v1");
        var registration = CreateStringWaitRegistration(
            contextSource,
            [new DurableFlowEventBinding<FlowEvent>(callsite, eventSource)]);
        var context = contextSource.Encode(new FlowContext(1));
        var eventPayload = eventSource.Encode(new FlowEvent("payload"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", context, "approved", eventPayload),
            new MultiCodecRegistry(contextSource, eventSource)));

        Assert.Contains("event 'approved' codec returned an incompatible payload type", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_compatible_context_codec_rejects_null_untyped_encode()
    {
        var source = new CountingCodec<FlowContext>("raw.guard.encode", "v1");
        var registration = CreateRegistration(source);
        var input = new DurableFlowEvaluationInput("start", source.Encode(new FlowContext(1)));
        source.EncodeObjectOverride = _ => null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            input, new FixedRegistry(source)));

        Assert.Contains("context codec returned no encoded payload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activity_result_wait_resume_uses_selected_context_and_event_codecs_and_rejects_wrong_sources()
    {
        var contextSource = new CountingCodec<FlowContext>("composed.context", "v1");
        var workSource = new CountingCodec<FlowWork>("composed.work", "v1");
        var resultSource = new CountingCodec<FlowResult>("composed.result", "v1");
        var eventSource = new CountingCodec<FlowEvent>("composed.event", "v1");
        var workRegistration = CreateWorkRegistration(workSource, resultSource);
        var activityCallsite = new FlowActivityCallsite<FlowWork, FlowResult>("send", 1, 1);
        var eventCallsite = new FlowEventCallsite<FlowEvent>("approved", "composed.event", "v1");
        var activity = new DurableFlowActivityBinding<FlowContext, FlowWork, FlowResult>(
            activityCallsite, workRegistration, workSource, resultSource);
        var definition = FlowGraphBuilder<FlowContext>
            .Create("composed-flow", "v1")
            .AddNode("activity", new ActivityThenWaitNode(activityCallsite, eventCallsite))
            .StartAt("activity")
            .Build();
        var registration = new DurableFlowRegistration<FlowContext>(
            definition,
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            activityBindings: [activity],
            eventBindings: [new DurableFlowEventBinding<FlowEvent>(eventCallsite, eventSource)]);
        var contextView = DurablePayloadCodecSnapshot.Capture(contextSource).CreateView();
        var eventView = DurablePayloadCodecSnapshot.Capture(eventSource).CreateView();
        var codecs = new MultiCodecRegistry(contextView, eventView);
        var initialContext = contextSource.Encode(new FlowContext(1));

        var scheduled = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("activity", initialContext), codecs);

        Assert.Equal(FlowTransitionKind.Activity, scheduled.Kind);
        Assert.NotNull(scheduled.Activity);
        Assert.Equal(1, contextSource.DecodeCalls);
        Assert.Equal(2, contextSource.EncodeCalls);

        var waiting = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "activity",
                scheduled.Context!,
                activityCallsiteId: "send",
                activityResult: resultSource.Encode(new FlowResult("sent"))),
            codecs);

        Assert.Equal(FlowTransitionKind.Wait, waiting.Kind);
        Assert.NotNull(waiting.Context);
        Assert.Equal("composed.event", waiting.EventContract!.ContractName);
        Assert.Equal(1, resultSource.DecodeCalls);
        Assert.Equal(2, contextSource.DecodeCalls);
        Assert.Equal(3, contextSource.EncodeCalls);

        var completed = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "activity",
                waiting.Context!,
                resumeEventName: "approved",
                resumeEventPayload: eventSource.Encode(new FlowEvent("confirmed"))),
            codecs);

        Assert.Equal(FlowTransitionKind.Complete, completed.Kind);
        Assert.Equal(new FlowEvent("confirmed"), eventSource.LastDecoded);
        Assert.Equal(1, eventSource.DecodeCalls);
        Assert.Equal(3, contextSource.DecodeCalls);
        Assert.Equal(4, contextSource.EncodeCalls);

        var wrongContextSource = new CountingCodec<FlowContext>("composed.context", "v1");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("activity", initialContext),
            new MultiCodecRegistry(
                DurablePayloadCodecSnapshot.Capture(wrongContextSource).CreateView(), eventView)));

        var wrongEventSource = new CountingCodec<FlowEvent>("composed.event", "v1");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "activity",
                waiting.Context!,
                resumeEventName: "approved",
                resumeEventPayload: eventSource.Encode(new FlowEvent("wrong-source"))),
            new MultiCodecRegistry(
                contextView, DurablePayloadCodecSnapshot.Capture(wrongEventSource).CreateView())));
    }

    [Fact]
    public async Task Concurrent_evaluations_using_distinct_registries_do_not_replace_registration_codec()
    {
        var source = new CountingCodec<FlowContext>("flow.context", "v1");
        var registration = CreateRegistration(source);
        var inputOne = new DurableFlowEvaluationInput("start", source.Encode(new FlowContext(1)));
        var inputTwo = new DurableFlowEvaluationInput("start", source.Encode(new FlowContext(2)));
        var registryOne = new FixedRegistry(DurablePayloadCodecSnapshot.Capture(source).CreateView());
        var registryTwo = new FixedRegistry(DurablePayloadCodecSnapshot.Capture(source).CreateView());

        var results = await Task.WhenAll(
            registration.EvaluateAsync(inputOne, registryOne).AsTask(),
            registration.EvaluateAsync(inputTwo, registryTwo).AsTask());

        Assert.All(results, result => Assert.Equal(FlowTransitionKind.Next, result.Kind));
        Assert.Same(source, registration.ContextCodec);
    }

    [Fact]
    public async Task Event_wait_and_resume_use_the_selected_compatible_codec_without_fallback()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        var eventSource = new CountingCodec<FlowEvent>("flow.event", "v1");
        IDurablePayloadCodec contextView = DurablePayloadCodecSnapshot.Capture(contextSource).CreateView();
        var eventView = DurablePayloadCodecSnapshot.Capture(eventSource).CreateView();
        var callsite = new FlowEventCallsite<FlowEvent>("approved", "flow.event", "v1");
        var definition = FlowGraphBuilder<FlowContext>
            .Create("event-flow", "v1")
            .AddNode("wait", new EventNode<FlowEvent>(callsite))
            .AddNode("done", new ContextCompleteNode())
            .StartAt("wait")
            .Build();
        var registration = new DurableFlowRegistration<FlowContext>(
            definition,
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            eventBindings: [new DurableFlowEventBinding<FlowEvent>(callsite, eventSource)]);
        var registry = new MultiCodecRegistry(contextView, eventView);

        var waiting = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", contextSource.Encode(new FlowContext(1))),
            registry);
        var resumed = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "wait",
                contextSource.Encode(new FlowContext(1)),
                resumeEventName: "approved",
                resumeEventPayload: eventSource.Encode(new FlowEvent("yes"))),
            registry);

        Assert.Equal(FlowTransitionKind.Wait, waiting.Kind);
        Assert.Equal(FlowTransitionKind.Complete, resumed.Kind);
        Assert.Equal(new FlowEvent("yes"), eventSource.LastDecoded);
        Assert.Equal(2, contextSource.DecodeCalls);
        Assert.Equal(1, eventSource.DecodeCalls);
        Assert.Same(contextSource, registration.ContextCodec);
    }

    [Fact]
    public async Task Event_resume_rejects_unknown_contracts_malformed_payloads_and_multiple_versions()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        var eventV1 = new CountingCodec<FlowEvent>("flow.event", "v1");
        var eventV2 = new CountingCodec<FlowEvent>("flow.event", "v2");
        var callsiteV1 = new FlowEventCallsite<FlowEvent>("approved", "flow.event", "v1");
        var callsiteV2 = new FlowEventCallsite<FlowEvent>("approved", "flow.event", "v2");
        var definition = FlowGraphBuilder<FlowContext>
            .Create("event-flow", "v1")
            .AddNode("wait", new EventNode<FlowEvent>(callsiteV1))
            .AddNode("done", new ContextCompleteNode())
            .StartAt("wait")
            .Build();
        var registration = new DurableFlowRegistration<FlowContext>(
            definition,
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            eventBindings: [
                new DurableFlowEventBinding<FlowEvent>(callsiteV1, eventV1),
                new DurableFlowEventBinding<FlowEvent>(callsiteV2, eventV2),
            ]);
        var context = contextSource.Encode(new FlowContext(1));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", context, "unknown", eventV1.Encode(new FlowEvent("x"))),
            new MultiCodecRegistry(
                DurablePayloadCodecSnapshot.Capture(contextSource).CreateView(),
                DurablePayloadCodecSnapshot.Capture(eventV1).CreateView())));

        var malformed = new DurableEncodedPayload(
            "flow.event",
            "v1",
            DurableDataClassification.ApprovedApplication,
            new byte[] { 9 });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", context, "approved", malformed),
            new MultiCodecRegistry(
                DurablePayloadCodecSnapshot.Capture(contextSource).CreateView(),
                DurablePayloadCodecSnapshot.Capture(eventV1).CreateView())));

        var resumedV2 = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", context, "approved", eventV2.Encode(new FlowEvent("v2"))),
            new MultiCodecRegistry(
                DurablePayloadCodecSnapshot.Capture(contextSource).CreateView(),
                DurablePayloadCodecSnapshot.Capture(eventV1).CreateView(),
                DurablePayloadCodecSnapshot.Capture(eventV2).CreateView()));
        Assert.Equal(FlowTransitionKind.Complete, resumedV2.Kind);
    }

    [Fact]
    public async Task Payloadless_event_and_timeout_resume_without_payload_decoding()
    {
        var contextSource = new CountingCodec<FlowContext>("flow.context", "v1");
        var registration = CreateStringWaitRegistration(contextSource);
        var codecs = new FixedRegistry(DurablePayloadCodecSnapshot.Capture(contextSource).CreateView());
        var context = contextSource.Encode(new FlowContext(1));

        var payloadless = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", context, "approved"), codecs);
        var timeout = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", context, "approved", isTimeout: true), codecs);

        Assert.Equal(FlowTransitionKind.Complete, payloadless.Kind);
        Assert.Equal(FlowTransitionKind.Complete, timeout.Kind);
        Assert.All(codecs.Requests, request => Assert.Equal(typeof(FlowContext), request.PayloadType));
    }

    [Fact]
    public async Task Shared_work_context_and_event_codec_views_survive_both_registration_orders_and_two_providers()
    {
        foreach (var workFirst in new[] { true, false })
        {
            var shared = new CountingCodec<FlowContext>("shared", "v1");
            var result = new CountingCodec<FlowResult>("result", "v1");
            var workRegistration = new DurableWorkRegistration<FlowContext, FlowResult, SharedFlowExecutor>(
                "work", "v1", DurableProviderSafety.ProviderKeyed,
                (IDurablePayloadCodec<FlowContext>)DurablePayloadCodecSnapshot.Capture(shared).CreateView(),
                (IDurablePayloadCodec<FlowResult>)DurablePayloadCodecSnapshot.Capture(result).CreateView());
            var eventCallsite = new FlowEventCallsite<FlowContext>("approved", "shared", "v1");
            var activity = new DurableFlowActivityBinding<FlowContext, FlowContext, FlowResult>(
                new FlowActivityCallsite<FlowContext, FlowResult>("activity", 1, 1),
                workRegistration,
                shared,
                result);
            var flow = CreateRegistration(
                shared,
                activityBindings: [activity],
                eventBindings: [new DurableFlowEventBinding<FlowContext>(eventCallsite, shared)]);
            var workRegistry = new DurableWorkRegistry([workRegistration]);
            var providerOneCodecs = new DurablePayloadCodecRegistry(workFirst
                ? [DurablePayloadCodecSnapshot.Capture(shared).CreateView(), DurablePayloadCodecSnapshot.Capture(result).CreateView()]
                : [DurablePayloadCodecSnapshot.Capture(result).CreateView(), DurablePayloadCodecSnapshot.Capture(shared).CreateView()]);
            var providerTwoCodecs = new DurablePayloadCodecRegistry([
                DurablePayloadCodecSnapshot.Capture(shared).CreateView(),
                DurablePayloadCodecSnapshot.Capture(result).CreateView(),
            ]);

            Assert.Same(flow, new DurableFlowRegistry([flow], workRegistry, providerOneCodecs).GetRequired("flow", "v1"));
            Assert.Same(flow, new DurableFlowRegistry([flow], workRegistry, providerTwoCodecs).GetRequired("flow", "v1"));
        }
    }

    private static DurableWorkRegistration<FlowWork, FlowResult, FlowExecutor> CreateWorkRegistration(
        CountingCodec<FlowWork> workSource,
        CountingCodec<FlowResult> resultSource) =>
        new(
            "work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            (IDurablePayloadCodec<FlowWork>)DurablePayloadCodecSnapshot.Capture(workSource).CreateView(),
            (IDurablePayloadCodec<FlowResult>)DurablePayloadCodecSnapshot.Capture(resultSource).CreateView());

    private static DurableFlowRegistration<FlowContext> CreateRegistration(
        IDurablePayloadCodec<FlowContext> contextSource,
        IEnumerable<DurableFlowActivityBinding<FlowContext>>? activityBindings = null,
        IEnumerable<DurableFlowEventBinding>? eventBindings = null)
    {
        var events = eventBindings?.ToArray() ?? [];
        return new(
            FlowGraphBuilder<FlowContext>
                .Create("flow", "v1")
                .AddNode("start", new ContextNextNode(), "done")
                .AddNode("done", new ContextCompleteNode())
                .StartAt("start")
                .Build(),
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            activityBindings,
            events);
    }

    private static DurableFlowRegistration<FlowContext> CreateStringWaitRegistration(
        CountingCodec<FlowContext> contextSource,
        IEnumerable<DurableFlowEventBinding>? eventBindings = null) =>
        new(
            FlowGraphBuilder<FlowContext>
                .Create("string-flow", "v1")
                .AddNode("wait", new StringWaitNode())
                .StartAt("wait")
                .Build(),
            contextSource,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowContext>(),
            eventBindings: eventBindings);

    private sealed record FlowContext(int Value);
    private sealed record FlowWork(string Value);
    private sealed record FlowResult(string Value);
    private sealed record FlowEvent(string Value);

    private sealed class FlowExecutor : IDurableWorkerExecutor<FlowWork, FlowResult>
    {
        public ValueTask<FlowResult> ExecuteAsync(
            DurableWorkerEnvelope<FlowWork> work,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SharedFlowExecutor : IDurableWorkerExecutor<FlowContext, FlowResult>
    {
        public ValueTask<FlowResult> ExecuteAsync(
            DurableWorkerEnvelope<FlowContext> work,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ActivityRequest(FlowWork work) : IFlowActivityRequest<FlowContext>
    {
        public string CallsiteId => "activity";
        public Type WorkType => typeof(FlowWork);
        public int WorkContractVersion => 1;
        public Type ResultType => typeof(FlowResult);
        public int ResultContractVersion => 1;
        public object Work => work;
        public FlowContext Context => new(1);
        public FlowActivityWorkResult CreateResult(object result) =>
            new FlowActivityCallsite<FlowWork, FlowResult>(CallsiteId, WorkContractVersion, ResultContractVersion)
                .CreateResult((FlowResult)result);
    }

    private sealed class ContextNextNode : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                FlowNodeOutcome<FlowContext>.Next("done", context.State with { Value = context.State.Value + 1 }));
    }

    private sealed class ContextCompleteNode : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                FlowNodeOutcome<FlowContext>.Complete(context.State));
    }

    private sealed class EventNode<TPayload>(FlowEventCallsite<TPayload> callsite) : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            context.ResumeEvent is null
                ? ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(FlowNodeOutcome<FlowContext>.Wait(callsite, context.State))
                : ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(FlowNodeOutcome<FlowContext>.Complete(context.State));
    }

    private sealed class ActivityThenWaitNode(
        FlowActivityCallsite<FlowWork, FlowResult> activityCallsite,
        FlowEventCallsite<FlowEvent> eventCallsite) : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default)
        {
            if (context.ResumeEvent is not null)
            {
                return ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                    FlowNodeOutcome<FlowContext>.Complete(context.State with { Value = context.State.Value + 1 }));
            }

            if (context.ActivityResult is null)
            {
                return ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                    FlowNodeOutcome<FlowContext>.Activity(activityCallsite, new FlowWork("send"), context.State));
            }

            var result = activityCallsite.GetResult(context.ActivityResult);
            return ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                FlowNodeOutcome<FlowContext>.Wait(eventCallsite, context.State with { Value = result.Value.Length }));
        }
    }

    private sealed class StringWaitNode : IFlowNode<FlowContext>
    {
        public ValueTask<FlowNodeOutcome<FlowContext>> ExecuteAsync(
            FlowExecutionContext<FlowContext> context,
            CancellationToken cancellationToken = default) =>
            context.ResumeEvent is null
                ? ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                    FlowNodeOutcome<FlowContext>.Wait("approved", context.State))
                : ValueTask.FromResult<FlowNodeOutcome<FlowContext>>(
                    FlowNodeOutcome<FlowContext>.Complete(context.State));
    }

    private sealed class CountingCodec<T> : IDurablePayloadCodec<T>
    {
        private T? _lastValue;

        public CountingCodec(string contractName, string contractVersion)
        {
            ContractName = contractName;
            ContractVersion = contractVersion;
        }

        public int EncodeCalls { get; private set; }
        public int EncodeObjectCalls { get; private set; }
        public int DecodeCalls { get; private set; }
        public int DecodeObjectCalls { get; private set; }
        public T? LastDecoded => _lastValue;
        public Type PayloadType => typeof(T);
        public string ContractName { get; set; }
        public string ContractVersion { get; set; }
        public DurableDataClassification Classification => DurableDataClassification.Operational;
        public string RetentionPolicyId { get; set; } = DurableEncodedPayload.DefaultRetentionPolicyId;
        public bool ReturnNull { get; set; }
        public Func<DurableEncodedPayload, object?>? DecodeObjectOverride { get; set; }
        public Func<object, DurableEncodedPayload?>? EncodeObjectOverride { get; set; }

        public DurableEncodedPayload Encode(T value)
        {
            EncodeCalls++;
            _lastValue = value;
            return new(ContractName, ContractVersion, Classification, new byte[] { 1 });
        }

        public T Decode(DurableEncodedPayload payload)
        {
            DecodeCalls++;
            if (ReturnNull)
            {
                return default!;
            }

            var value = _lastValue;
            return value is null ? throw new InvalidOperationException("No test value was queued.") : value;
        }

        public DurableEncodedPayload EncodeObject(object value)
        {
            EncodeObjectCalls++;
            return EncodeObjectOverride is { } encode ? encode(value)! : Encode((T)value);
        }

        public object DecodeObject(DurableEncodedPayload payload)
        {
            DecodeObjectCalls++;
            return DecodeObjectOverride is { } decode ? decode(payload)! : Decode(payload)!;
        }

    }

    private sealed class SingleCodecRegistry(IDurablePayloadCodec codec) : IDurablePayloadCodecRegistry
    {
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();
        public IDurablePayloadCodec GetRequired(Type payloadType) => codec;
        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) => codec;
        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) => codec;
    }

    private sealed class FixedRegistry(IDurablePayloadCodec codec) : IDurablePayloadCodecRegistry
    {
        public List<(Type PayloadType, string ContractName, string ContractVersion)> Requests { get; } = [];
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();
        public IDurablePayloadCodec GetRequired(Type payloadType) => codec;
        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion)
        {
            Requests.Add((payloadType, contractName, contractVersion));
            return codec;
        }

        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) => codec;
    }

    private sealed class ThrowingRegistry : IDurablePayloadCodecRegistry
    {
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();
        public IDurablePayloadCodec GetRequired(Type payloadType) => throw new InvalidOperationException("missing codec");
        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) =>
            throw new InvalidOperationException("missing codec");
        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) =>
            throw new InvalidOperationException("missing codec");
    }

    private sealed class NullRegistry : IDurablePayloadCodecRegistry
    {
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();
        public IDurablePayloadCodec GetRequired(Type payloadType) => null!;
        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) => null!;
        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) => null!;
    }

    private sealed class MultiCodecRegistry(params IDurablePayloadCodec[] codecs) : IDurablePayloadCodecRegistry
    {
        public void Register(IDurablePayloadCodec codec) => throw new NotSupportedException();
        public IDurablePayloadCodec GetRequired(Type payloadType) => codecs.Single(codec => codec.PayloadType == payloadType);
        public IDurablePayloadCodec GetRequired(Type payloadType, string contractName, string contractVersion) =>
            codecs.Single(codec => codec.PayloadType == payloadType && codec.ContractName == contractName && codec.ContractVersion == contractVersion);
        public IDurablePayloadCodec GetRequired(string contractName, string contractVersion) =>
            codecs.Single(codec => codec.ContractName == contractName && codec.ContractVersion == contractVersion);
    }
}
