using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;
using ForgeTrust.AppSurface.Workers;

namespace ForgeTrust.AppSurface.Durable.Testing.Tests;

public sealed class BuilderObservationTests
{
    [Theory]
    [InlineData(DurableRuntimeHealthState.Healthy, true, true, true, true)]
    [InlineData(DurableRuntimeHealthState.NotStarted, true, true, true, false)]
    [InlineData(DurableRuntimeHealthState.Stale, true, true, true, false)]
    [InlineData(DurableRuntimeHealthState.Draining, true, true, false, false)]
    [InlineData(DurableRuntimeHealthState.Incompatible, true, false, false, false)]
    [InlineData(DurableRuntimeHealthState.Unavailable, false, false, false, false)]
    public void NamedHealthStatesHaveExpectedProductionPredicates(DurableRuntimeHealthState state,
        bool observed, bool activation, bool pump, bool ready)
    {
        var snapshot = new DurableHealthSnapshotBuilder().ForState(state).Build();
        Assert.Equal(observed, snapshot.WasStoreObserved);
        Assert.Equal(activation, snapshot.CanEnableActivation);
        Assert.Equal(pump, snapshot.CanAttemptPump);
        Assert.Equal(ready, snapshot.IsReady);
    }

    [Fact]
    public void HealthBuilderDelegatesInvalidBoundariesAndAllowsContradictionsExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableHealthSnapshotBuilder().WithDueDispatchCount(-1).Build());
        Assert.Throws<ArgumentException>(() => new DurableHealthSnapshotBuilder().WithWorkerId(" ").Build());
        Assert.Throws<ArgumentException>(() => new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Healthy)
            .WithSchemaCompatible(false).Build());
        var contradictory = new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Unavailable)
            .WithSchemaCompatible(true).BuildContradictoryForTest();
        Assert.True(contradictory.SchemaCompatible);
        Assert.False(contradictory.WasStoreObserved);
    }

    [Fact]
    public void HealthBuilderPreservesExplicitNullOverrides()
    {
        Assert.Null(new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Stale)
            .WithProblemCode(null).Build().ProblemCode);
        var snapshot = new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Incompatible)
            .WithActiveRuntimeEpoch(null)
            .WithStartedAtUtc(null)
            .WithLastSuccessfulSweepAtUtc(null)
            .Build();
        Assert.Null(snapshot.ActiveRuntimeEpoch);
        Assert.Null(snapshot.StartedAtUtc);
        Assert.Null(snapshot.LastSuccessfulSweepAtUtc);
        var heartbeatConflict = Assert.Throws<ArgumentException>(() => new DurableHealthSnapshotBuilder()
            .WithLastHeartbeatAtUtc(null).Build());
        Assert.Contains("LastHeartbeatAtUtc", heartbeatConflict.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new DurableHealthSnapshotBuilder()
            .WithActiveRuntimeEpoch(null).Build());
        Assert.Throws<ArgumentException>(() => new DurableHealthSnapshotBuilder()
            .WithStartedAtUtc(null).Build());
        Assert.Throws<ArgumentException>(() => new DurableHealthSnapshotBuilder()
            .ForState(DurableRuntimeHealthState.Draining).WithStartedAtUtc(null).Build());
        var epoch = Guid.NewGuid();
        Assert.Equal(epoch, new DurableHealthSnapshotBuilder().WithConfiguredRuntimeEpoch(epoch).Build().ActiveRuntimeEpoch);
    }

    [Fact]
    public void PumpBuildersUseProductionValidationAndSupportAllOutcomeKinds()
    {
        var result = new DurableRuntimePumpResultBuilder().WithCounts(2, 2, 1, 0, 1).WithElapsed(TimeSpan.FromMilliseconds(5)).Build();
        Assert.Equal(2, result.Discovered);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpRequestBuilder().WithMaximumItems(0).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurableRuntimePumpResultBuilder().WithCounts(-1, 0, 0, 0, 0).Build());
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, new DurableRuntimePumpAttemptBuilder().Build().Kind);
        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, new DurableRuntimePumpAttemptBuilder()
            .WithKind(DurableRuntimePumpAttemptKind.Refused).Build().Kind);
        Assert.Equal(DurableRuntimePumpAttemptKind.Unavailable, new DurableRuntimePumpAttemptBuilder()
            .WithKind(DurableRuntimePumpAttemptKind.Unavailable).WithProblemCode(DurableProblemCodes.StoreUnavailable).Build().Kind);
        Assert.Equal(DurableRuntimePumpAttemptKind.Incompatible, new DurableRuntimePumpAttemptBuilder()
            .WithKind(DurableRuntimePumpAttemptKind.Incompatible).WithProblemCode(DurableProblemCodes.SchemaMissing).Build().Kind);
        Assert.Throws<ArgumentException>(() => new DurableRuntimePumpAttemptBuilder()
            .WithKind(DurableRuntimePumpAttemptKind.Refused).WithResult(result).Build());
    }

    [Fact]
    public void TypedRequestMatchesDefinitionAndBindingObservationsKeepExactIdentity()
    {
        var codec = new SystemTextJsonDurablePayloadCodec<string>("test.input", "v1", DurableDataClassification.ApprovedApplication,
            (JsonTypeInfo<string>)new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(string)), static _ => true);
        var definition = DurableWork.Define<string, string>("test.work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);
        var request = new DurableWorkRequestBuilder<string, string>(definition)
            .WithScope(new DurableScopeId("scope")).WithCommand(new DurableCommandId("command"))
            .WithIdempotencyKey("key").WithWork("payload").Build();
        var direct = definition.CreateRequest(new DurableScopeId("scope"), new DurableCommandId("command"), "key", "payload");
        Assert.Equal(direct.Fingerprint, request.Fingerprint);
        Assert.Equal(direct.Payload, request.Payload);
        var facts = DurableWorkDefinitionObservation.Capture(definition);
        Assert.Equal("test.work", facts.WorkName);
        Assert.Equal(typeof(string), facts.WorkPayloadType);

        var binding = definition.ExecutedBy<TestExecutor>();
        var observed = DurableWorkBindingObservation.Capture(binding);
        Assert.Same(definition, observed.Definition);

        var defaults = new DurableWorkRequestBuilder<string, string>(definition).WithWork("payload").Build();
        Assert.Equal("test-scope", defaults.ScopeId.Value);
        Assert.Equal("test-command", defaults.CommandId.Value);
    }

    [Fact]
    public void RegistryObservationReturnsExactRegistrationAndPreservesMissingFailure()
    {
        var codec = new SystemTextJsonDurablePayloadCodec<string>("registry.input", "v1", DurableDataClassification.ApprovedApplication,
            (JsonTypeInfo<string>)new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(string)), static _ => true);
        var registered = new DurableWorkRegistration<string, string, TestExecutor>("registered", "v1",
            DurableProviderSafety.Idempotent, codec, codec);
        var registry = new DurableWorkRegistry([registered]);
        var registration = DurableWorkRegistryObservation.Capture(registry, "registered", "v1");
        Assert.Equal("registered", registration.WorkName);
        Assert.Same(registered, registration.Registration);
        Assert.Throws<InvalidOperationException>(() => { _ = DurableWorkRegistryObservation.Capture(registry, "missing", "v1"); });
        Assert.Throws<InvalidOperationException>(() => new DurableWorkRegistry([registered, registered]));
    }

    [Fact]
    public void TypedRequestBuilderRequiresWorkAndPreservesCodecRejection()
    {
        var codec = new SystemTextJsonDurablePayloadCodec<string>("rejected.input", "v1",
            DurableDataClassification.ApprovedApplication,
            (JsonTypeInfo<string>)new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            }.GetTypeInfo(typeof(string)), static _ => false);
        var definition = DurableWork.Define<string, string>("rejected.work", "v1", codec, codec,
            DurableProviderSafety.Idempotent, DurableWorkRetryPolicy.Default);

        Assert.Throws<ArgumentNullException>(() => new DurableWorkRequestBuilder<string, string>(null!));
        Assert.Throws<InvalidOperationException>(() => new DurableWorkRequestBuilder<string, string>(definition).Build());
        Assert.Throws<ArgumentException>(() => new DurableWorkRequestBuilder<string, string>(definition)
            .WithWork("rejected").Build());
    }

    [Fact]
    public void NativeEnvelopeRetainsFenceIdentity()
    {
        var identity = DurableWorkerExecutionIdentity.CreateInitial("activity-1", 3, 2, "epoch-a");
        var envelope = new DurableWorkerEnvelopeBuilder<string>().WithOutcome(DurableWorkerProjectionOutcome.Completed)
            .WithReasonCode("ok").WithRetryability(DurableWorkerRetryability.Terminal)
            .WithCorrelation(new DurableWorkerCorrelation("worker", "command", "scope", "attempt-1"))
            .WithExecutionIdentity(identity).WithPayload("done").Build();
        Assert.Same(identity, envelope.ExecutionIdentity);
        Assert.Equal(3, envelope.ExecutionIdentity!.LeaseGeneration);
        Assert.Equal("done", envelope.Payload);
    }

    private sealed class TestExecutor : IDurableWorkerExecutor<string, string>
    {
        public ValueTask<string> ExecuteAsync(DurableWorkerEnvelope<string> work, CancellationToken cancellationToken = default) => ValueTask.FromResult(work.Payload ?? string.Empty);
    }
}
