using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.Tests;

public sealed class DurableContractTests
{
    [Fact]
    public void Identifiers_preserve_ordinal_values_and_generate_nonempty_ids()
    {
        var scope = new DurableScopeId("Tenant-A");

        Assert.Equal("Tenant-A", scope.Value);
        Assert.False(string.IsNullOrWhiteSpace(DurableWorkId.New().Value));
        Assert.False(string.IsNullOrWhiteSpace(DurableCommandId.New().Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void Identifiers_reject_missing_or_control_text(string? value)
    {
        Assert.Throws<ArgumentException>(() => new DurableScopeId(value!));
        Assert.Throws<ArgumentException>(() => new DurableWorkId(value!));
        Assert.Throws<ArgumentException>(() => new DurableCommandId(value!));
    }

    [Theory]
    [InlineData("child@example.com")]
    [InlineData("https://example.test/resource")]
    [InlineData("Bearer secret-token")]
    [InlineData("password=hunter2")]
    [InlineData("raw payload")]
    public void Identifiers_reject_nonopaque_or_sensitive_shapes(string value)
    {
        Assert.Throws<ArgumentException>(() => new DurableScopeId(value));
        Assert.Throws<ArgumentException>(() => new DurableCommandId(value));
    }

    [Fact]
    public void Identifiers_reject_oversized_values()
    {
        Assert.Throws<ArgumentException>(() => new DurableScopeId(new string('a', 201)));
    }

    [Fact]
    public void Encoded_payload_copies_content_and_hashes_exact_bytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var payload = new DurableEncodedPayload(
            "work.test",
            "v1",
            DurableDataClassification.Operational,
            bytes);

        bytes[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, payload.Content.ToArray());
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", payload.Sha256);
    }

    [Fact]
    public void Encoded_payload_content_cannot_mutate_the_authoritative_bytes()
    {
        var payload = new DurableEncodedPayload(
            "work.test",
            "v1",
            DurableDataClassification.Operational,
            new byte[] { 1, 2, 3 });

        var exposedCopy = payload.Content.ToArray();
        exposedCopy[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, payload.Content.ToArray());
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", payload.Sha256);
    }

    [Fact]
    public void Encoded_payload_rejects_invalid_classification_and_oversized_content()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableEncodedPayload(
            "work.test",
            "v1",
            (DurableDataClassification)999,
            ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new DurableEncodedPayload(
            "work.test",
            "v1",
            DurableDataClassification.Operational,
            new byte[DurableEncodedPayload.ProtocolMaximumBytes + 1]));
    }

    [Fact]
    public void Json_codec_requires_policy_and_exact_contract()
    {
        var codec = CreateCodec(value => value.SafeCode.StartsWith("safe-", StringComparison.Ordinal));

        var encoded = codec.Encode(new TestPayload("safe-one"));

        Assert.Equal(new TestPayload("safe-one"), codec.Decode(encoded));
        Assert.Throws<ArgumentException>(() => codec.Encode(new TestPayload("secret")));
        Assert.Throws<System.Text.Json.JsonException>(() => codec.Decode(
            new DurableEncodedPayload(
                "other",
                "v1",
                DurableDataClassification.Operational,
                encoded.Content)));

        Assert.Throws<System.Text.Json.JsonException>(() => codec.Decode(
            new DurableEncodedPayload(
                encoded.ContractName,
                encoded.ContractVersion,
                encoded.Classification,
                encoded.Content,
                "different-retention-policy")));
    }

    [Fact]
    public void Json_codec_untyped_boundary_rejects_wrong_type()
    {
        IDurablePayloadCodec codec = CreateCodec(_ => true);

        Assert.Throws<ArgumentException>(() => codec.EncodeObject("wrong"));
    }

    [Fact]
    public void Json_codec_rejects_configuration_outside_protocol_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemTextJsonDurablePayloadCodec<TestPayload>(
            "test",
            "v1",
            (DurableDataClassification)999,
            DurableContractJsonContext.Default.TestPayload,
            _ => true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemTextJsonDurablePayloadCodec<TestPayload>(
            "test",
            "v1",
            DurableDataClassification.Operational,
            DurableContractJsonContext.Default.TestPayload,
            _ => true,
            maximumBytes: 0));
    }

    [Fact]
    public void Codec_registry_is_idempotent_for_same_instance_and_rejects_collisions()
    {
        var registry = new DurablePayloadCodecRegistry();
        var codec = CreateCodec(_ => true);
        registry.Register(codec);
        registry.Register(codec);

        Assert.Same(codec, registry.GetRequired(typeof(TestPayload)));
        Assert.Same(codec, registry.GetRequired("test.payload", "v1"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(CreateCodec(_ => true)));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(string)));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("missing", "v1"));
    }

    [Fact]
    public void Codec_registry_keeps_historical_versions_and_requires_exact_selection_when_type_is_ambiguous()
    {
        var registry = new DurablePayloadCodecRegistry();
        var first = CreateCodec(_ => true);
        var second = new SystemTextJsonDurablePayloadCodec<TestPayload>(
            "test.payload",
            "v2",
            DurableDataClassification.Operational,
            DurableContractJsonContext.Default.TestPayload,
            _ => true);
        registry.Register(first);
        registry.Register(second);

        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(TestPayload)));
        Assert.Same(second, registry.GetRequired(typeof(TestPayload), "test.payload", "v2"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired(typeof(TestResult), "test.payload", "v2"));
    }

    [Fact]
    public void Work_request_snapshots_policy_and_normalizes_due_time()
    {
        var payload = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        var due = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.FromHours(-4));

        var request = new DurableWorkRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("command"),
            "retry-key",
            "test.work",
            "v1",
            payload,
            DurableProviderSafety.ProviderKeyed,
            dueAtUtc: due);

        Assert.Same(DurableWorkRetryPolicy.Default, request.RetryPolicy);
        Assert.Equal(TimeSpan.Zero, request.DueAtUtc!.Value.Offset);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, request.ProviderSafety);
    }

    [Fact]
    public void Work_request_rejects_default_ids_and_undefined_safety()
    {
        var payload = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        Assert.Throws<ArgumentException>(() => new DurableWorkRequest(
            default,
            new DurableCommandId("command"),
            "key",
            "work",
            "v1",
            payload,
            DurableProviderSafety.Idempotent));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("command"),
            "key",
            "work",
            "v1",
            payload,
            (DurableProviderSafety)999));
    }

    [Fact]
    public void Retry_policy_rejects_incoherent_windows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(maximumAttempts: 0));
        Assert.Throws<ArgumentException>(() => CreatePolicy(
            initialRetryDelay: TimeSpan.FromMinutes(2),
            maximumRetryDelay: TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => CreatePolicy(
            leaseDuration: TimeSpan.FromMinutes(1),
            renewalCadence: TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => CreatePolicy(
            leaseDuration: TimeSpan.FromMinutes(3),
            maximumLeaseLifetime: TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Acceptance_rejects_invalid_revision_and_normalizes_time()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkAcceptance(
            new DurableWorkId("work"),
            new DurableCommandId("command"),
            DurableWorkAcceptanceKind.Accepted,
            0,
            DateTimeOffset.Now));

        var accepted = new DurableWorkAcceptance(
            new DurableWorkId("work"),
            new DurableCommandId("command"),
            DurableWorkAcceptanceKind.Duplicate,
            1,
            new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.FromHours(-4)));
        Assert.Equal(TimeSpan.Zero, accepted.AcceptedAtUtc.Offset);
    }

    [Fact]
    public void Operation_result_exposes_exact_success_or_problem()
    {
        var success = DurableOperationResult<string>.Success("ok");
        var problem = CreateProblem();
        var failure = DurableOperationResult<string>.Failure(problem);

        Assert.True(success.IsSuccess);
        Assert.Equal("ok", success.Value);
        Assert.Null(success.Problem);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Same(problem, failure.Problem);
    }

    [Fact]
    public void Problem_requires_absolute_http_documentation()
    {
        Assert.Throws<ArgumentException>(() => new DurableProblem(
            "code",
            "problem",
            "cause",
            "fix",
            new Uri("relative", UriKind.Relative),
            "correlation"));
        Assert.Throws<ArgumentException>(() => new DurableProblem(
            "code",
            "problem",
            "cause",
            "fix",
            new Uri("file:///tmp/docs"),
            "correlation"));
    }

    [Fact]
    public void Pump_request_enforces_bounds_and_surface_selection()
    {
        var request = new DurableRuntimePumpRequest();
        Assert.Equal(32, request.MaximumItems);
        Assert.Equal(DurableRuntimeSurface.All, request.Surfaces);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(maximumItems: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(timeBudget: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(surfaces: DurableRuntimeSurface.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequest(surfaces: (DurableRuntimeSurface)8));
    }

    [Fact]
    public void Pump_result_rejects_negative_counts_and_normalizes_next_due_time()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpResult(
            -1, 0, 0, 0, 0, false, null, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpResult(
            0, 0, 0, 0, 0, false, null, TimeSpan.FromTicks(-1)));

        var result = new DurableRuntimePumpResult(
            1,
            1,
            1,
            0,
            0,
            true,
            new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.FromHours(-4)),
            TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, result.NextDueAtUtc!.Value.Offset);
    }

    [Fact]
    public void Runtime_health_snapshot_validates_identity_and_normalizes_times()
    {
        var epoch = Guid.NewGuid();
        var observed = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.FromHours(-4));
        var snapshot = new DurableRuntimeHealthSnapshot(
            DurableRuntimeHealthState.Healthy,
            problemCode: null,
            schemaCompatible: true,
            epochCompatible: true,
            installedSchemaVersion: 5,
            requiredSchemaVersion: 5,
            epoch,
            epoch,
            "worker-a",
            Guid.NewGuid(),
            DurableRuntimeSurface.All,
            observed,
            observed,
            observed,
            observed,
            isDraining: false,
            isPassActive: false,
            dueDispatchCount: 1,
            observed,
            TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, snapshot.ObservedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.OldestDueAtUtc!.Value.Offset);
        Assert.Throws<ArgumentException>(() => new DurableRuntimeHealthSnapshot(
            DurableRuntimeHealthState.Healthy,
            null,
            true,
            true,
            5,
            5,
            Guid.Empty,
            null,
            "worker-a",
            null,
            DurableRuntimeSurface.All,
            observed,
            null,
            null,
            null,
            false,
            false,
            0,
            null,
            null));
    }

    [Fact]
    public void Work_inventory_contracts_enforce_bounds_and_preserve_recovery_fields()
    {
        var scopeId = new DurableScopeId("scope-list");
        var request = new DurableWorkListRequest(
            scopeId,
            DurableWorkState.Suspended,
            requiresRecoveryReleaseOnly: true,
            pageSize: 25,
            continuationToken: "work-a");
        var item = new DurableWorkListItem(
            new DurableWorkId("work-b"),
            "activity-b",
            "test.work",
            "v1",
            DurableWorkState.Suspended,
            DurableProviderSafety.ReconcileBeforeRetry,
            attemptNumber: 2,
            revision: 7,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DurableProblemCodes.RecoveryEpochRequired,
            cancellationRequested: true,
            requiresRecoveryRelease: true);
        var result = new DurableWorkListResult([item], "work-b");

        Assert.Equal(25, request.PageSize);
        Assert.True(request.RequiresRecoveryReleaseOnly);
        Assert.True(result.Items[0].RequiresRecoveryRelease);
        Assert.True(result.Items[0].CancellationRequested);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkListRequest(scopeId, pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkListRequest(scopeId, pageSize: 501));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkListRequest(
            scopeId,
            (DurableWorkState)999));
        Assert.Throws<ArgumentNullException>(() => new DurableWorkListResult(null!, null));
    }

    [Fact]
    public async Task Typed_registration_invokes_executor_with_fencing_identity_and_encodes_result()
    {
        var workCodec = CreateCodec(_ => true);
        var resultCodec = CreateResultCodec();
        var registration = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        var executor = new CapturingExecutor();
        var services = new ServiceCollection().AddSingleton(executor).BuildServiceProvider();
        var claim = CreateClaim(workCodec);

        var encoded = await registration.InvokeAsync(services, claim);

        Assert.Equal(new TestResult("done"), resultCodec.Decode(encoded));
        Assert.NotNull(executor.Observed);
        Assert.Equal("activity", executor.Observed.ExecutionIdentity!.ActivityId);
        Assert.Equal("provider-activity", executor.Observed.ExecutionIdentity.ProviderKey);
        Assert.Equal(new TestPayload("safe"), executor.Observed.Payload);
    }

    [Fact]
    public async Task Typed_registration_rejects_wrong_contract_or_safety_before_executor()
    {
        var workCodec = CreateCodec(_ => true);
        var registration = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            CreateResultCodec());
        var services = new ServiceCollection().AddSingleton(new CapturingExecutor()).BuildServiceProvider();
        var wrongName = CreateClaim(workCodec, workName: "other");
        var wrongSafety = CreateClaim(workCodec, safety: DurableProviderSafety.Idempotent);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.InvokeAsync(services, wrongName));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registration.InvokeAsync(services, wrongSafety));
    }

    [Fact]
    public void Work_registry_rejects_duplicates_and_missing_contracts()
    {
        var workCodec = CreateCodec(_ => true);
        var first = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            CreateResultCodec());
        var registry = new DurableWorkRegistry([first]);

        Assert.Same(first, registry.GetRequired("test.work", "v1"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("missing", "v1"));
        Assert.Throws<InvalidOperationException>(() => new DurableWorkRegistry([first, first]));
    }

    [Fact]
    public void Service_registration_adds_codecs_registry_and_transient_executor()
    {
        var services = new ServiceCollection();
        services.AddDurableWork<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            CreateCodec(_ => true),
            CreateResultCodec());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DurableWorkRegistry>(provider.GetRequiredService<IDurableWorkRegistry>());
        Assert.IsType<DurablePayloadCodecRegistry>(provider.GetRequiredService<IDurablePayloadCodecRegistry>());
        Assert.NotSame(provider.GetRequiredService<CapturingExecutor>(), provider.GetRequiredService<CapturingExecutor>());
    }

    [Fact]
    public void Flow_service_registration_adds_definition_and_shared_registries()
    {
        var definition = FlowGraphBuilder<FlowTestContext>
            .Create("flow", "v1")
            .AddNode("done", new CompleteFlowNode())
            .StartAt("done")
            .Build();
        var services = new ServiceCollection();
        services.AddDurableFlow(definition, CreateFlowContextCodec(), "implementation-v1");
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "flow",
            provider.GetRequiredService<IDurableFlowRegistry>().GetRequired("flow", "v1").FlowId);
        Assert.NotNull(provider.GetRequiredService<IDurablePayloadCodecRegistry>());
    }

    [Fact]
    public void Claimed_work_rejects_default_ids_and_nonpositive_fences()
    {
        var codec = CreateCodec(_ => true);
        Assert.Throws<ArgumentException>(() => new DurableClaimedWork(
            default,
            new DurableWorkId("work"),
            "activity",
            "test.work",
            "v1",
            codec.Encode(new TestPayload("safe")),
            DurableProviderSafety.ProviderKeyed,
            1,
            1,
            1,
            "epoch",
            "provider"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableClaimedWork(
            new DurableScopeId("scope"),
            new DurableWorkId("work"),
            "activity",
            "test.work",
            "v1",
            codec.Encode(new TestPayload("safe")),
            DurableProviderSafety.ProviderKeyed,
            0,
            1,
            1,
            "epoch",
            "provider"));
    }

    [Fact]
    public async Task Reconcile_before_retry_requires_and_invokes_side_effect_free_reconciler()
    {
        var workCodec = CreateCodec(_ => true);
        var resultCodec = CreateResultCodec();
        Assert.Throws<ArgumentException>(() => new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ReconcileBeforeRetry,
            workCodec,
            resultCodec));
        var reconciler = new CapturingReconciler(DurableEffectReconciliation<TestResult>.Applied(new TestResult("observed")));
        using var services = new ServiceCollection().AddSingleton(reconciler).BuildServiceProvider();
        var registration = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ReconcileBeforeRetry,
            workCodec,
            resultCodec,
            provider => provider.GetRequiredService<CapturingReconciler>());
        var claim = CreateClaim(workCodec, safety: DurableProviderSafety.ReconcileBeforeRetry);

        var outcome = await registration.ReconcileAsync(services, claim);

        Assert.True(registration.CanReconcile);
        Assert.Equal(DurableEffectReconciliationKind.Applied, outcome.Kind);
        Assert.Equal(new TestResult("observed"), resultCodec.Decode(outcome.Result!));
        Assert.Equal("provider-activity", reconciler.Observed!.ExecutionIdentity!.ProviderKey);
    }

    [Fact]
    public async Task Registration_without_reconciler_fails_closed_and_encoded_outcome_validates_shape()
    {
        var workCodec = CreateCodec(_ => true);
        var registration = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            CreateResultCodec());
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.False(registration.CanReconcile);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await registration.ReconcileAsync(services, CreateClaim(workCodec)));
        Assert.Throws<ArgumentException>(() => new DurableEncodedEffectReconciliation(
            DurableEffectReconciliationKind.Applied,
            null));
        Assert.Throws<ArgumentException>(() => new DurableEncodedEffectReconciliation(
            DurableEffectReconciliationKind.Unknown,
            workCodec.Encode(new TestPayload("safe"))));
    }

    [Fact]
    public void Reconciler_service_extension_registers_required_types()
    {
        var services = new ServiceCollection();
        services.AddDurableWorkWithReconciler<TestPayload, TestResult, CapturingExecutor, DefaultReconciler>(
            "test.work",
            "v1",
            CreateCodec(_ => true),
            CreateResultCodec());
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IDurableWorkRegistry>().GetRequired("test.work", "v1");
        Assert.True(registration.CanReconcile);
        Assert.NotSame(provider.GetRequiredService<DefaultReconciler>(), provider.GetRequiredService<DefaultReconciler>());
    }

    [Fact]
    public void Flow_start_and_event_contracts_preserve_active_wait_semantics()
    {
        var context = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        var start = new DurableFlowStartRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("start-command"),
            "start-key",
            new DurableFlowInstanceId("flow-instance"),
            "approval",
            "v1",
            context);
        var @event = new DurableFlowEventRequest(
            start.ScopeId,
            new DurableCommandId("event-command"),
            new DurableFlowEventId("event-id"),
            start.InstanceId,
            "approved",
            expectedRevision: 2);

        Assert.Equal("approval", start.FlowId);
        Assert.Equal("approved", @event.EventName);
        Assert.Equal(2, @event.ExpectedRevision);
        Assert.Equal("flow-instance", start.InstanceId.Value);
        Assert.False(string.IsNullOrWhiteSpace(DurableFlowInstanceId.New().Value));
        Assert.False(string.IsNullOrWhiteSpace(DurableFlowEventId.New().Value));
    }

    [Fact]
    public void Flow_contracts_reject_uninitialized_ids_and_bad_revisions()
    {
        var context = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        Assert.Throws<ArgumentException>(() => new DurableFlowStartRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("command"),
            "key",
            default,
            "flow",
            "v1",
            context));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowEventRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("command"),
            new DurableFlowEventId("event"),
            new DurableFlowInstanceId("instance"),
            "ready",
            expectedRevision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowCancelRequest(
            new DurableScopeId("scope"),
            new DurableCommandId("command"),
            new DurableFlowInstanceId("instance"),
            "operator-1",
            "cancel.requested",
            0));
    }

    [Fact]
    public void Flow_command_result_validates_enums_and_revision()
    {
        var result = new DurableFlowCommandResult(
            new DurableFlowInstanceId("instance"),
            DurableFlowCommandOutcome.NotWaitingYet,
            DurableFlowState.Ready,
            3);
        Assert.Equal(DurableFlowCommandOutcome.NotWaitingYet, result.Outcome);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowCommandResult(
            new DurableFlowInstanceId("instance"),
            (DurableFlowCommandOutcome)999,
            DurableFlowState.Ready,
            3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowCommandResult(
            new DurableFlowInstanceId("instance"),
            DurableFlowCommandOutcome.Accepted,
            (DurableFlowState)999,
            3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowCommandResult(
            new DurableFlowInstanceId("instance"),
            DurableFlowCommandOutcome.Accepted,
            DurableFlowState.Ready,
            0));
    }

    [Fact]
    public async Task Durable_flow_registration_encodes_activity_and_resumes_same_node_with_typed_result()
    {
        var callsite = new FlowActivityCallsite<TestPayload, TestResult>("send", 1, 1);
        var definition = FlowGraphBuilder<FlowTestContext>
            .Create("activity-flow", "v1")
            .AddNode("run", new ActivityFlowNode(callsite))
            .StartAt("run")
            .Build();
        var workCodec = CreateCodec(_ => true);
        var resultCodec = CreateResultCodec();
        var contextCodec = CreateFlowContextCodec();
        var workRegistration = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work",
            "v1",
            DurableProviderSafety.ProviderKeyed,
            workCodec,
            resultCodec);
        var binding = new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            callsite,
            workRegistration,
            workCodec,
            resultCodec);
        var registration = new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            [binding]);
        var codecs = new DurablePayloadCodecRegistry([workCodec, resultCodec, contextCodec]);

        var first = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "run",
                contextCodec.Encode(new FlowTestContext(1))),
            codecs);
        var resumed = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput(
                "run",
                first.Context!,
                activityCallsiteId: "send",
                activityResult: resultCodec.Encode(new TestResult("done"))),
            codecs);

        Assert.Equal(FlowTransitionKind.Activity, first.Kind);
        Assert.Equal("test.work", first.Activity!.WorkName);
        Assert.Equal(new TestPayload("safe-1"), workCodec.Decode(first.Activity.Work));
        Assert.Equal(FlowTransitionKind.Complete, resumed.Kind);
        Assert.Equal(2, contextCodec.Decode(resumed.Context!).Step);
    }

    [Fact]
    public void Durable_flow_evaluation_input_rejects_ambiguous_or_partial_resume_shapes()
    {
        var context = CreateFlowContextCodec().Encode(new FlowTestContext(1));
        var eventPayload = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        Assert.Throws<ArgumentException>(() => new DurableFlowEvaluationInput(
            "node",
            context,
            "event",
            eventPayload,
            activityCallsiteId: "activity",
            activityResult: eventPayload));
        Assert.Throws<ArgumentException>(() => new DurableFlowEvaluationInput(
            "node",
            context,
            resumeEventPayload: eventPayload));
        Assert.Throws<ArgumentException>(() => new DurableFlowEvaluationInput(
            "node",
            context,
            activityCallsiteId: "activity"));
    }

    [Fact]
    public void Durable_flow_registry_rejects_duplicate_or_missing_versions()
    {
        var definition = FlowGraphBuilder<FlowTestContext>
            .Create("flow", "v1")
            .AddNode("done", new CompleteFlowNode())
            .StartAt("done")
            .Build();
        var registration = new DurableFlowRegistration<FlowTestContext>(
            definition,
            CreateFlowContextCodec(),
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>());
        var registry = new DurableFlowRegistry([registration]);
        Assert.Same(registration, registry.GetRequired("flow", "v1"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("flow", "v2"));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry([registration, registration]));
    }

    [Fact]
    public async Task Durable_flow_determinism_verifier_compares_canonical_double_evaluations()
    {
        var codec = CreateFlowContextCodec();
        var codecs = new DurablePayloadCodecRegistry([codec]);
        var deterministicDefinition = FlowGraphBuilder<FlowTestContext>
            .Create("deterministic", "v1")
            .AddNode("done", new CompleteFlowNode())
            .StartAt("done")
            .Build();
        var deterministic = new DurableFlowRegistration<FlowTestContext>(
            deterministicDefinition,
            codec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>());
        var input = new DurableFlowEvaluationInput("done", codec.Encode(new FlowTestContext(1)));

        var stable = await DurableFlowDeterminismVerifier.VerifyAndThrowAsync(deterministic, input, codecs);

        Assert.True(stable.IsDeterministic);
        Assert.Equal(stable.FirstEvaluationSha256, stable.SecondEvaluationSha256);

        var nondeterministicDefinition = FlowGraphBuilder<FlowTestContext>
            .Create("nondeterministic", "v1")
            .AddNode("done", new IncrementingFlowNode())
            .StartAt("done")
            .Build();
        var nondeterministic = new DurableFlowRegistration<FlowTestContext>(
            nondeterministicDefinition,
            codec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>());

        var unstable = await DurableFlowDeterminismVerifier.VerifyAsync(nondeterministic, input, codecs);

        Assert.False(unstable.IsDeterministic);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DurableFlowDeterminismVerifier.VerifyAndThrowAsync(nondeterministic, input, codecs));
    }

    [Fact]
    public async Task Durable_flow_registration_rejects_nonportable_ids_and_fault_codes()
    {
        var codec = CreateFlowContextCodec();
        var invalidFlowId = FlowGraphBuilder<FlowTestContext>
            .Create("flow with spaces", "v1")
            .AddNode("done", new CompleteFlowNode())
            .StartAt("done")
            .Build();
        var invalidNodeId = FlowGraphBuilder<FlowTestContext>
            .Create("flow", "v1")
            .AddNode("first step", new CompleteFlowNode())
            .StartAt("first step")
            .Build();

        Assert.Throws<ArgumentException>(() => new DurableFlowRegistration<FlowTestContext>(
            invalidFlowId,
            codec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>()));
        Assert.Throws<ArgumentException>(() => new DurableFlowRegistration<FlowTestContext>(
            invalidNodeId,
            codec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>()));

        var invalidFaultDefinition = FlowGraphBuilder<FlowTestContext>
            .Create("invalid-fault", "v1")
            .AddNode("fault", new InvalidFaultCodeFlowNode())
            .StartAt("fault")
            .Build();
        var invalidFault = new DurableFlowRegistration<FlowTestContext>(
            invalidFaultDefinition,
            codec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await invalidFault.EvaluateAsync(
            new DurableFlowEvaluationInput("fault", codec.Encode(new FlowTestContext(1))),
            new DurablePayloadCodecRegistry([codec])));
    }

    private static SystemTextJsonDurablePayloadCodec<TestPayload> CreateCodec(Func<TestPayload, bool> policy) =>
        new(
            "test.payload",
            "v1",
            DurableDataClassification.Operational,
            DurableContractJsonContext.Default.TestPayload,
            policy);

    private static SystemTextJsonDurablePayloadCodec<TestResult> CreateResultCodec() =>
        new(
            "test.result",
            "v1",
            DurableDataClassification.Operational,
            DurableContractJsonContext.Default.TestResult,
            _ => true);

    private static SystemTextJsonDurablePayloadCodec<FlowTestContext> CreateFlowContextCodec() =>
        new(
            "test.flow-context",
            "v1",
            DurableDataClassification.ApprovedApplication,
            DurableContractJsonContext.Default.FlowTestContext,
            state => state.Step is >= 0 and <= 100);

    private static DurableClaimedWork CreateClaim(
        IDurablePayloadCodec<TestPayload> codec,
        string workName = "test.work",
        DurableProviderSafety safety = DurableProviderSafety.ProviderKeyed) =>
        new(
            new DurableScopeId("scope"),
            new DurableWorkId("work"),
            "activity",
            workName,
            "v1",
            codec.Encode(new TestPayload("safe")),
            safety,
            2,
            3,
            4,
            "epoch",
            "provider-activity");

    private static DurableWorkRetryPolicy CreatePolicy(
        int maximumAttempts = 3,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? renewalCadence = null,
        TimeSpan? maximumLeaseLifetime = null) =>
        new(
            maximumAttempts,
            TimeSpan.FromHours(1),
            initialRetryDelay ?? TimeSpan.FromSeconds(1),
            maximumRetryDelay ?? TimeSpan.FromMinutes(1),
            leaseDuration ?? TimeSpan.FromMinutes(1),
            renewalCadence ?? TimeSpan.FromSeconds(10),
            maximumLeaseLifetime ?? TimeSpan.FromMinutes(2),
            "exponential-v1");

    private static DurableProblem CreateProblem() => new(
        "test.problem",
        "Test problem",
        "Test cause",
        "Apply the test fix",
        new Uri("https://appsurface.dev/docs/durable/test"),
        "correlation");
}

internal sealed record TestPayload(string SafeCode);

internal sealed record TestResult(string Code);

internal sealed record FlowTestContext(int Step);

internal sealed class ActivityFlowNode(FlowActivityCallsite<TestPayload, TestResult> callsite) : IFlowNode<FlowTestContext>
{
    public ValueTask<FlowNodeOutcome<FlowTestContext>> ExecuteAsync(
        FlowExecutionContext<FlowTestContext> context,
        CancellationToken cancellationToken = default)
    {
        if (callsite.TryGetResult(context.ActivityResult, out _))
        {
            return ValueTask.FromResult<FlowNodeOutcome<FlowTestContext>>(
                FlowNodeOutcome<FlowTestContext>.Complete(context.State with { Step = context.State.Step + 1 }));
        }

        return ValueTask.FromResult<FlowNodeOutcome<FlowTestContext>>(
            FlowNodeOutcome<FlowTestContext>.Activity(
                callsite,
                new TestPayload($"safe-{context.State.Step}"),
                context.State));
    }
}

internal sealed class CompleteFlowNode : IFlowNode<FlowTestContext>
{
    public ValueTask<FlowNodeOutcome<FlowTestContext>> ExecuteAsync(
        FlowExecutionContext<FlowTestContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<FlowTestContext>>(
            FlowNodeOutcome<FlowTestContext>.Complete(context.State));
}

internal sealed class IncrementingFlowNode : IFlowNode<FlowTestContext>
{
    private int _evaluationCount;

    public ValueTask<FlowNodeOutcome<FlowTestContext>> ExecuteAsync(
        FlowExecutionContext<FlowTestContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<FlowTestContext>>(
            FlowNodeOutcome<FlowTestContext>.Complete(
                context.State with { Step = Interlocked.Increment(ref _evaluationCount) }));
}

internal sealed class InvalidFaultCodeFlowNode : IFlowNode<FlowTestContext>
{
    public ValueTask<FlowNodeOutcome<FlowTestContext>> ExecuteAsync(
        FlowExecutionContext<FlowTestContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<FlowTestContext>>(
            FlowNodeOutcome<FlowTestContext>.Fault("approval failed", "The durable fault code is invalid."));
}

internal sealed class CapturingExecutor : IDurableWorkerExecutor<TestPayload, TestResult>
{
    internal DurableWorkerEnvelope<TestPayload>? Observed { get; private set; }

    public ValueTask<TestResult> ExecuteAsync(
        DurableWorkerEnvelope<TestPayload> work,
        CancellationToken cancellationToken = default)
    {
        Observed = work;
        return ValueTask.FromResult(new TestResult("done"));
    }
}

internal sealed class CapturingReconciler(DurableEffectReconciliation<TestResult> outcome) :
    IDurableEffectReconciler<TestPayload, TestResult>
{
    internal DurableWorkerEnvelope<TestPayload>? Observed { get; private set; }

    public ValueTask<DurableEffectReconciliation<TestResult>> ReconcileAsync(
        DurableWorkerEnvelope<TestPayload> work,
        CancellationToken cancellationToken = default)
    {
        Observed = work;
        return ValueTask.FromResult(outcome);
    }
}

internal sealed class DefaultReconciler : IDurableEffectReconciler<TestPayload, TestResult>
{
    public ValueTask<DurableEffectReconciliation<TestResult>> ReconcileAsync(
        DurableWorkerEnvelope<TestPayload> work,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DurableEffectReconciliation<TestResult>.Unknown());
}

[JsonSerializable(typeof(TestPayload))]
[JsonSerializable(typeof(TestResult))]
[JsonSerializable(typeof(FlowTestContext))]
internal sealed partial class DurableContractJsonContext : JsonSerializerContext;
