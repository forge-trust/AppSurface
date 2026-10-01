using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;
using ForgeTrust.AppSurface.Durable.Testing;
using Xunit;

namespace TestingConsumer;

public sealed class TestingPackageConsumerTests
{
    [Theory]
    [InlineData(DurableRuntimePumpAttemptKind.Completed)]
    [InlineData(DurableRuntimePumpAttemptKind.Refused)]
    [InlineData(DurableRuntimePumpAttemptKind.Unavailable)]
    [InlineData(DurableRuntimePumpAttemptKind.Incompatible)]
    public async Task Packed_testing_package_exposes_health_and_authoritative_attempt_observations(
        DurableRuntimePumpAttemptKind kind)
    {
        var snapshot = new DurableHealthSnapshotBuilder()
            .ForState(DurableRuntimeHealthState.Healthy)
            .Build();
        var health = new FakeDurableRuntimeHealth(snapshot);
        var expectedAttempt = new DurableRuntimePumpAttemptBuilder()
            .WithKind(kind)
            .WithProblemCode(kind switch
            {
                DurableRuntimePumpAttemptKind.Unavailable => DurableProblemCodes.StoreUnavailable,
                DurableRuntimePumpAttemptKind.Incompatible => DurableProblemCodes.SchemaMissing,
                _ => null,
            })
            .Build();
        var pump = new RecordingDurableRuntimePump
        {
            Admission = (_, _) => ValueTask.FromResult(expectedAttempt),
        };
        var scenario = new DurableHostScenario(
            health,
            pump,
            new DurableRuntimePumpRequest(maximumItems: 1, surfaces: DurableRuntimeSurface.Work));

        var assessment = await scenario.AssessHealthAsync();
        var attempt = await scenario.RunDirectPumpOnceAsync();

        Assert.True(assessment.Snapshot.IsReady);
        Assert.Equal(kind, attempt.Kind);
        Assert.Same(expectedAttempt, attempt);
        Assert.Single(pump.History);
        Assert.Same(assessment, scenario.PumpInvocations[0].Assessment);
    }
}
