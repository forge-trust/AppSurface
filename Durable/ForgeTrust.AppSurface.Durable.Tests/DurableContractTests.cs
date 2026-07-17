using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
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
        var work = new DurableWorkId("work-a");
        var command = new DurableCommandId("command-a");

        Assert.Equal("Tenant-A", scope.Value);
        Assert.Equal(scope.Value, scope.ToString());
        Assert.Equal(work.Value, work.ToString());
        Assert.Equal(command.Value, command.ToString());
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
    public void Encoded_payload_equality_uses_metadata_and_content_values()
    {
        var first = new DurableEncodedPayload(
            "work.test", "v1", DurableDataClassification.Operational, new byte[] { 1, 2, 3 });
        var equivalent = new DurableEncodedPayload(
            "work.test", "v1", DurableDataClassification.Operational, new byte[] { 1, 2, 3 });

        Assert.Equal(first, equivalent);
        Assert.Equal(first.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(first, new DurableEncodedPayload(
            "work.test", "v1", DurableDataClassification.Operational, new byte[] { 1, 2, 4 }));
        Assert.NotEqual(first, new DurableEncodedPayload(
            "other.test", "v1", DurableDataClassification.Operational, new byte[] { 1, 2, 3 }));
        Assert.NotEqual(first, new DurableEncodedPayload(
            "work.test", "v2", DurableDataClassification.Operational, new byte[] { 1, 2, 3 }));
        Assert.NotEqual(first, new DurableEncodedPayload(
            "work.test", "v1", DurableDataClassification.ApprovedApplication, new byte[] { 1, 2, 3 }));
        Assert.NotEqual(first, new DurableEncodedPayload(
            "work.test",
            "v1",
            DurableDataClassification.Operational,
            new byte[] { 1, 2, 3 },
            "other-retention"));

        var exposedCopy = equivalent.Content.ToArray();
        exposedCopy[0] = 9;
        Assert.Equal(first, equivalent);
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
        Assert.Equal(claim.ExecutionIdentity, executor.Observed.ExecutionIdentity);
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
    public void Execution_context_rejects_default_ids()
    {
        var codec = CreateCodec(_ => true);
        Assert.Throws<ArgumentException>(() => new DurableWorkExecutionContext(
            default,
            new DurableWorkId("work"),
            "test.work",
            "v1",
            codec.Encode(new TestPayload("safe")),
            DurableProviderSafety.ProviderKeyed,
            DurableWorkerExecutionIdentity.CreateInitial("activity", 1, 1, "epoch")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableWorkExecutionContext(
            new DurableScopeId("scope"),
            new DurableWorkId("work"),
            "test.work",
            "v1",
            codec.Encode(new TestPayload("safe")),
            (DurableProviderSafety)99,
            DurableWorkerExecutionIdentity.CreateInitial("activity", 1, 1, "epoch")));
        Assert.Throws<ArgumentNullException>(() => new DurableWorkExecutionContext(
            new DurableScopeId("scope"),
            new DurableWorkId("work"),
            "test.work",
            "v1",
            codec.Encode(new TestPayload("safe")),
            DurableProviderSafety.ProviderKeyed,
            null!));
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
        Assert.Equal("activity", reconciler.Observed!.ExecutionIdentity!.ProviderKey);
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
    public void Flow_requests_expose_command_and_audit_values()
    {
        var scope = new DurableScopeId("scope");
        var command = new DurableCommandId("command");
        var instance = new DurableFlowInstanceId("instance");
        var eventId = new DurableFlowEventId("event");
        var context = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        var start = new DurableFlowStartRequest(scope, command, "retry", instance, "flow", "v1", context);
        var flowEvent = new DurableFlowEventRequest(scope, command, eventId, instance, "approved", context, 3);
        var cancel = new DurableFlowCancelRequest(scope, command, instance, "actor", "cancel", 4);
        var release = new DurableFlowReleaseRequest(scope, command, instance, "operator", "repair", 5);

        Assert.Equal(scope, start.ScopeId);
        Assert.Equal(command, start.CommandId);
        Assert.Equal("retry", start.IdempotencyKey);
        Assert.Equal(instance, start.InstanceId);
        Assert.Equal("flow", start.FlowId);
        Assert.Equal("v1", start.FlowVersion);
        Assert.Same(context, start.Context);
        Assert.Equal(command, flowEvent.CommandId);
        Assert.Equal(eventId, flowEvent.EventId);
        Assert.Equal(instance, flowEvent.InstanceId);
        Assert.Same(context, flowEvent.Payload);
        Assert.Equal(scope, cancel.ScopeId);
        Assert.Equal(command, cancel.CommandId);
        Assert.Equal(instance, cancel.InstanceId);
        Assert.Equal("actor", cancel.ActorId);
        Assert.Equal("cancel", cancel.ReasonCode);
        Assert.Equal(4, cancel.ExpectedRevision);
        Assert.Equal(scope, release.ScopeId);
        Assert.Equal(command, release.CommandId);
        Assert.Equal(instance, release.InstanceId);
        Assert.Equal("operator", release.ActorId);
        Assert.Equal("repair", release.ReasonCode);
        Assert.Equal(5, release.ExpectedRevision);
        Assert.Equal("instance", instance.ToString());
        Assert.Equal("event", eventId.ToString());
    }

    [Fact]
    public void Flow_query_contracts_validate_copy_and_normalize()
    {
        var scope = new DurableScopeId("scope");
        var instance = new DurableFlowInstanceId("instance");
        var get = new DurableFlowGetRequest(scope, instance);
        var created = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.FromHours(-4));
        var snapshot = new DurableFlowSnapshot(
            instance,
            "flow",
            "v1",
            DurableFlowState.Suspended,
            "wait",
            7,
            created,
            created.AddMinutes(1),
            created.AddSeconds(10),
            created.AddMinutes(2),
            "repair.required",
            requiresRecoveryRelease: true);
        var request = new DurableFlowListRequest(
            scope,
            DurableFlowState.Suspended,
            requiresRecoveryRelease: true,
            pageSize: 25,
            continuationToken: "page=2&after=x");
        var source = new List<DurableFlowSnapshot> { snapshot };
        var result = new DurableFlowListResult(source, "page=2&after=x");
        source.Clear();

        Assert.Equal(scope, get.ScopeId);
        Assert.Equal(instance, get.InstanceId);
        Assert.Equal(scope, request.ScopeId);
        Assert.Equal(DurableFlowState.Suspended, request.State);
        Assert.True(request.RequiresRecoveryRelease);
        Assert.Equal(25, request.PageSize);
        Assert.Equal("page=2&after=x", request.ContinuationToken);
        Assert.Single(result.Flows);
        Assert.Equal("page=2&after=x", result.ContinuationToken);
        Assert.Equal(instance, snapshot.InstanceId);
        Assert.Equal("flow", snapshot.FlowId);
        Assert.Equal("v1", snapshot.FlowVersion);
        Assert.Equal(DurableFlowState.Suspended, snapshot.State);
        Assert.Equal("wait", snapshot.CurrentNodeId);
        Assert.Equal(7, snapshot.Revision);
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.UpdatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.CancellationRequestedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.TerminalAtUtc!.Value.Offset);
        Assert.Equal("repair.required", snapshot.TerminalCode);
        Assert.True(snapshot.RequiresRecoveryRelease);

        Assert.Throws<ArgumentException>(() => new DurableFlowGetRequest(default, instance));
        Assert.Throws<ArgumentException>(() => new DurableFlowGetRequest(scope, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(scope, (DurableFlowState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(scope, pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowListRequest(scope, pageSize: 1_001));
        Assert.Throws<ArgumentException>(() => new DurableFlowListRequest(scope, continuationToken: " "));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowListResult(null!, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowSnapshot(
            instance, "flow", "v1", (DurableFlowState)99, "wait", 1, created, created, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowSnapshot(
            instance, "flow", "v1", DurableFlowState.Ready, "wait", 0, created, created, null, null, null));
        Assert.Throws<ArgumentException>(() => new DurableFlowSnapshot(
            instance, "flow", "v1", DurableFlowState.Ready, "wait", 1, created, created, null, null, " "));
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
    public void Durable_flow_evaluation_contracts_expose_values_and_validate_shapes()
    {
        var context = CreateFlowContextCodec().Encode(new FlowTestContext(1));
        var payload = CreateCodec(_ => true).Encode(new TestPayload("safe"));
        var eventInput = new DurableFlowEvaluationInput("wait", context, "approved", payload, isTimeout: true);
        var activityInput = new DurableFlowEvaluationInput(
            "run", context, activityCallsiteId: "send", activityResult: payload);
        var activity = new DurableFlowActivityCommand(
            "send", 1, "work", "v1", DurableProviderSafety.ProviderKeyed, payload);
        var eventContract = new DurableFlowEventContract(
            true,
            payload.ContractName,
            payload.ContractVersion,
            payload.Classification,
            payload.RetentionPolicyId);
        var result = new DurableFlowEvaluationResult(
            FlowTransitionKind.Wait,
            "wait",
            context,
            null,
            "approved",
            new FlowTimeout(TimeSpan.FromMinutes(5)),
            null,
            activity,
            eventContract);

        Assert.Equal("wait", eventInput.NodeId);
        Assert.Same(context, eventInput.Context);
        Assert.Equal("approved", eventInput.ResumeEventName);
        Assert.Same(payload, eventInput.ResumeEventPayload);
        Assert.True(eventInput.IsTimeout);
        Assert.Null(eventInput.ActivityCallsiteId);
        Assert.Equal("send", activityInput.ActivityCallsiteId);
        Assert.Same(payload, activityInput.ActivityResult);
        Assert.Equal("send", activity.CallsiteId);
        Assert.Equal(1, activity.ResultContractVersion);
        Assert.Equal("work", activity.WorkName);
        Assert.Equal("v1", activity.WorkVersion);
        Assert.Equal(DurableProviderSafety.ProviderKeyed, activity.ProviderSafety);
        Assert.Same(payload, activity.Work);
        Assert.True(eventContract.PayloadRequired);
        Assert.Equal(payload.ContractName, eventContract.ContractName);
        Assert.Equal(payload.ContractVersion, eventContract.ContractVersion);
        Assert.Equal(payload.Classification, eventContract.Classification);
        Assert.Equal(payload.RetentionPolicyId, eventContract.RetentionPolicyId);
        Assert.Equal(FlowTransitionKind.Wait, result.Kind);
        Assert.Equal("wait", result.NodeId);
        Assert.Same(context, result.Context);
        Assert.Null(result.NextNodeId);
        Assert.Equal("approved", result.EventName);
        Assert.NotNull(result.Timeout);
        Assert.Null(result.Fault);
        Assert.Same(activity, result.Activity);
        Assert.Same(eventContract, result.EventContract);

        var noPayload = new DurableFlowEventContract(false);
        Assert.False(noPayload.PayloadRequired);
        Assert.Null(noPayload.ContractName);
        Assert.Null(noPayload.ContractVersion);
        Assert.Null(noPayload.Classification);
        Assert.Null(noPayload.RetentionPolicyId);
        Assert.Throws<ArgumentException>(() => new DurableFlowEventContract(true));
        Assert.Throws<ArgumentException>(() => new DurableFlowEventContract(
            false,
            "event",
            "v1",
            DurableDataClassification.Operational,
            DurableEncodedPayload.DefaultRetentionPolicyId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowActivityCommand(
            "send", 0, "work", "v1", DurableProviderSafety.Idempotent, payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowActivityCommand(
            "send", 1, "work", "v1", (DurableProviderSafety)99, payload));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowActivityCommand(
            "send", 1, "work", "v1", DurableProviderSafety.Idempotent, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableFlowEvaluationResult(
            (FlowTransitionKind)99, "wait", null, null, null, null, null, null));
    }

    [Fact]
    public async Task Durable_flow_typed_event_binding_is_manifested_and_evaluated()
    {
        var contextCodec = CreateFlowContextCodec();
        var eventCodec = CreateCodec(_ => true);
        var callsite = new FlowEventCallsite<TestPayload>("approved", eventCodec.ContractName, eventCodec.ContractVersion);
        var binding = new DurableFlowEventBinding<TestPayload>(callsite, eventCodec);
        var definition = FlowGraphBuilder<FlowTestContext>
            .Create("event-flow", "v1")
            .AddNode("wait", new WaitingFlowNode(callsite))
            .StartAt("wait")
            .Build();
        var registration = new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            eventBindings: [binding]);
        var codecs = new DurablePayloadCodecRegistry([contextCodec, eventCodec]);

        Assert.Same(callsite, binding.Callsite);
        Assert.Same(eventCodec, binding.PayloadCodec);
        Assert.Equal("event-flow", registration.FlowId);
        Assert.Equal("v1", registration.FlowVersion);
        Assert.Equal("implementation-v1", registration.ImplementationVersion);
        Assert.Equal("wait", registration.StartNodeId);
        Assert.Equal(DurableFlowRegistration.CurrentAuthoringModel, registration.AuthoringModel);
        Assert.Equal(64, registration.DefinitionFingerprint.Length);
        Assert.Same(contextCodec, registration.ContextCodec);
        Assert.Same(binding, Assert.Single(registration.EventBindings));
        Assert.Empty(registration.ActivityWorkRegistrations);
        var result = await registration.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", contextCodec.Encode(new FlowTestContext(1))),
            codecs);
        Assert.Equal(FlowTransitionKind.Wait, result.Kind);
        Assert.Equal("approved", result.EventName);
        Assert.True(result.EventContract!.PayloadRequired);

        var workRegistry = new DurableWorkRegistry([]);
        Assert.Same(
            registration,
            new DurableFlowRegistry([registration], workRegistry, codecs).GetRequired("event-flow", "v1"));
        Assert.Throws<ArgumentException>(() => new DurableFlowEventBinding<TestPayload>(
            new FlowEventCallsite<TestPayload>("approved", "other", "v1"),
            eventCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowEventBinding<TestPayload>(null!, eventCodec));
        Assert.Throws<ArgumentNullException>(() => new DurableFlowEventBinding<TestPayload>(callsite, null!));

        var duplicate = new DurableFlowEventBinding<TestPayload>(
            new FlowEventCallsite<TestPayload>("approved", eventCodec.ContractName, eventCodec.ContractVersion),
            eventCodec);
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistration<FlowTestContext>(
            definition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            eventBindings: [binding, duplicate]));

        var undeclaredCallsite = new FlowEventCallsite<TestPayload>(
            "rejected", eventCodec.ContractName, eventCodec.ContractVersion);
        var undeclaredDefinition = FlowGraphBuilder<FlowTestContext>
            .Create("undeclared-event-flow", "v1")
            .AddNode("wait", new WaitingFlowNode(undeclaredCallsite))
            .StartAt("wait")
            .Build();
        var undeclared = new DurableFlowRegistration<FlowTestContext>(
            undeclaredDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            eventBindings: [binding]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await undeclared.EvaluateAsync(
            new DurableFlowEvaluationInput("wait", contextCodec.Encode(new FlowTestContext(1))),
            codecs));
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
        var registry = new DurableFlowRegistry(
            [registration],
            new DurableWorkRegistry([]),
            new DurablePayloadCodecRegistry([registration.ContextCodec]));
        Assert.Same(registration, registry.GetRequired("flow", "v1"));
        Assert.Throws<InvalidOperationException>(() => registry.GetRequired("flow", "v2"));
        Assert.Throws<InvalidOperationException>(() => new DurableFlowRegistry(
            [registration, registration],
            new DurableWorkRegistry([]),
            new DurablePayloadCodecRegistry([registration.ContextCodec])));
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
    public async Task Durable_flow_determinism_verifier_covers_activity_and_event_contract_decisions()
    {
        var workCodec = CreateCodec(_ => true);
        var resultCodec = CreateResultCodec();
        var contextCodec = CreateFlowContextCodec();
        var activityCallsite = new FlowActivityCallsite<TestPayload, TestResult>("send", 1, 1);
        var workRegistration = new DurableWorkRegistration<TestPayload, TestResult, CapturingExecutor>(
            "test.work", "v1", DurableProviderSafety.ProviderKeyed, workCodec, resultCodec);
        var activityBinding = new DurableFlowActivityBinding<FlowTestContext, TestPayload, TestResult>(
            activityCallsite, workRegistration, workCodec, resultCodec);
        var activityDefinition = FlowGraphBuilder<FlowTestContext>
            .Create("activity-determinism", "v1")
            .AddNode("run", new ActivityFlowNode(activityCallsite))
            .StartAt("run")
            .Build();
        var activityRegistration = new DurableFlowRegistration<FlowTestContext>(
            activityDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            [activityBinding]);
        var eventCallsite = new FlowEventCallsite<TestPayload>("approved", workCodec.ContractName, workCodec.ContractVersion);
        var eventBinding = new DurableFlowEventBinding<TestPayload>(eventCallsite, workCodec);
        var waitDefinition = FlowGraphBuilder<FlowTestContext>
            .Create("wait-determinism", "v1")
            .AddNode("wait", new WaitingFlowNode(eventCallsite))
            .StartAt("wait")
            .Build();
        var waitRegistration = new DurableFlowRegistration<FlowTestContext>(
            waitDefinition,
            contextCodec,
            "implementation-v1",
            new FlowTransitionEvaluator<FlowTestContext>(),
            eventBindings: [eventBinding]);
        var codecs = new DurablePayloadCodecRegistry([workCodec, resultCodec, contextCodec]);

        var activity = await DurableFlowDeterminismVerifier.VerifyAndThrowAsync(
            activityRegistration,
            new DurableFlowEvaluationInput("run", contextCodec.Encode(new FlowTestContext(1))),
            codecs);
        var waiting = await DurableFlowDeterminismVerifier.VerifyAndThrowAsync(
            waitRegistration,
            new DurableFlowEvaluationInput("wait", contextCodec.Encode(new FlowTestContext(1))),
            codecs);

        Assert.True(activity.IsDeterministic);
        Assert.True(waiting.IsDeterministic);
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

    private static DurableWorkExecutionContext CreateClaim(
        IDurablePayloadCodec<TestPayload> codec,
        string workName = "test.work",
        DurableProviderSafety safety = DurableProviderSafety.ProviderKeyed) =>
        new(
            new DurableScopeId("scope"),
            new DurableWorkId("work"),
            workName,
            "v1",
            codec.Encode(new TestPayload("safe")),
            safety,
            DurableWorkerExecutionIdentity.CreateInitial("activity", 3, 4, "epoch").Advance(2, 3, 4, "epoch-next"));

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

internal sealed class WaitingFlowNode(FlowEventCallsite<TestPayload> callsite) : IFlowNode<FlowTestContext>
{
    public ValueTask<FlowNodeOutcome<FlowTestContext>> ExecuteAsync(
        FlowExecutionContext<FlowTestContext> context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<FlowNodeOutcome<FlowTestContext>>(
            FlowNodeOutcome<FlowTestContext>.Wait(callsite, context.State, new FlowTimeout(TimeSpan.FromMinutes(5))));
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
