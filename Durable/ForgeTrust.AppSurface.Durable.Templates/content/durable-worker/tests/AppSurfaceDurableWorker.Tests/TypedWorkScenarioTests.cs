using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;

namespace DurableWorkerTemplate.Tests;

public sealed partial class TypedWorkScenarioTests
{
    [Fact]
    public async Task Published_testing_contract_builds_typed_work_and_keeps_admission_authoritative()
    {
        var definition = CreateDefinition();
        var observation = DurableWorkDefinitionObservation.Capture(definition);
        var request = new DurableWorkRequestBuilder<ScenarioWork, ScenarioResult>(definition)
            .WithScope(new DurableScopeId("scenario-scope"))
            .WithCommand(new DurableCommandId("scenario-command"))
            .WithIdempotencyKey("scenario-key")
            .WithWork(new ScenarioWork("safe-value"))
            .Build();

        Assert.Equal("template.tests.scenario", observation.WorkName);
        Assert.Equal("v1", observation.WorkVersion);
        Assert.Equal(typeof(ScenarioWork), observation.WorkPayloadType);
        Assert.Equal(typeof(ScenarioResult), observation.ResultPayloadType);
        Assert.Equal(DurableProviderSafety.Idempotent, observation.ProviderSafety);
        Assert.Equal(new ScenarioWork("safe-value"), definition.WorkCodec.Decode(request.Payload));

        var health = new FakeDurableRuntimeHealth(
            new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.NotStarted).Build());
        var admission = new RecordingDurableRuntimePump();
        var pumpRequest = new DurableRuntimePumpRequestBuilder()
            .WithMaximumItems(1)
            .WithTimeBudget(TimeSpan.FromSeconds(2))
            .WithSurfaces(DurableRuntimeSurface.Work)
            .Build();
        var scenario = new DurableHostScenario(health, admission, pumpRequest);

        var assessment = await scenario.AssessHealthAsync();
        var attempt = await scenario.RunDirectPumpOnceAsync();

        Assert.False(assessment.Snapshot.IsReady);
        Assert.True(assessment.Snapshot.CanAttemptPump);
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, attempt.Kind);
        var call = Assert.Single(admission.History);
        Assert.Same(pumpRequest, call.Request);
        Assert.Equal(DurableRuntimeSurface.Work, call.Surfaces);
        Assert.Same(assessment, Assert.Single(scenario.PumpInvocations).Assessment);
    }

    [Fact]
    public async Task Published_testing_contract_preserves_a_real_admission_refusal()
    {
        var health = new FakeDurableRuntimeHealth(
            new DurableHealthSnapshotBuilder().ForState(DurableRuntimeHealthState.Healthy).Build());
        var admission = new RecordingDurableRuntimePump
        {
            Admission = static (_, _) => ValueTask.FromResult(
                new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, result: null, problemCode: null)),
        };
        var scenario = new DurableHostScenario(
            health,
            admission,
            new DurableRuntimePumpRequestBuilder().WithSurfaces(DurableRuntimeSurface.Work).Build());

        await scenario.AssessHealthAsync();
        var attempt = await scenario.RunDirectPumpOnceAsync();

        Assert.Equal(DurableRuntimePumpAttemptKind.Refused, attempt.Kind);
        Assert.Null(attempt.Result);
        Assert.Equal(DurableRuntimePumpCallKind.Admission, Assert.Single(admission.History).Kind);
    }

    private static DurableWorkDefinition<ScenarioWork, ScenarioResult> CreateDefinition() => DurableWork.Define(
        "template.tests.scenario",
        "v1",
        new SystemTextJsonDurablePayloadCodec<ScenarioWork>(
            "template.tests.scenario-input",
            "v1",
            DurableDataClassification.ApprovedApplication,
            ScenarioJsonContext.Default.ScenarioWork,
            static work => !string.IsNullOrWhiteSpace(work.Value) && work.Value.Length <= 80,
            maximumBytes: 512),
        new SystemTextJsonDurablePayloadCodec<ScenarioResult>(
            "template.tests.scenario-result",
            "v1",
            DurableDataClassification.ApprovedApplication,
            ScenarioJsonContext.Default.ScenarioResult,
            static result => !string.IsNullOrWhiteSpace(result.Value) && result.Value.Length <= 80,
            maximumBytes: 512),
        DurableProviderSafety.Idempotent,
        DurableWorkRetryPolicy.Default);

    private sealed record ScenarioWork(string Value);

    private sealed record ScenarioResult(string Value);

    [JsonSerializable(typeof(ScenarioWork))]
    [JsonSerializable(typeof(ScenarioResult))]
    private sealed partial class ScenarioJsonContext : JsonSerializerContext;
}
