using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace ForgeTrust.AppSurface.Durable.Provider.Tests;

public sealed class ExternalActivationBurstTests
{
    [Fact]
    public async Task Finite_controlled_burst_reads_each_health_then_leaves_overlap_refusal_to_provider_admission()
    {
        const int burstSize = 12;
        var allHealthEntered = NewSignal();
        var releaseHealth = NewSignal();
        var healthCalls = 0;
        var health = new ExternalActivationHealth(async _ =>
        {
            if (Interlocked.Increment(ref healthCalls) == burstSize)
            {
                allHealthEntered.TrySetResult();
            }

            await releaseHealth.Task;
            return ExternalActivationTestSupport.Health();
        });

        var firstAdmissionEntered = NewSignal();
        var allAdmissionsEntered = NewSignal();
        var releaseFirstAdmission = NewSignal();
        var admissionCalls = 0;
        var activeProviderPasses = 0;
        var maximumProviderPasses = 0;
        var aggregate = ExternalActivationTestSupport.PumpResult();
        var admission = new ExternalActivationAdmission(async (_, _) =>
        {
            var ordinal = Interlocked.Increment(ref admissionCalls);
            if (ordinal == burstSize)
            {
                allAdmissionsEntered.TrySetResult();
            }

            if (ordinal == 1)
            {
                var active = Interlocked.Increment(ref activeProviderPasses);
                UpdateMaximum(ref maximumProviderPasses, active);
                firstAdmissionEntered.TrySetResult();
                await releaseFirstAdmission.Task;
                Interlocked.Decrement(ref activeProviderPasses);
                return new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Completed, aggregate, null);
            }

            return new DurableRuntimePumpAttempt(DurableRuntimePumpAttemptKind.Refused, null, null);
        });
        var service = new DurableExternalActivationService(
            health,
            admission,
            TimeProvider.System,
            new ExternalActivationLogger());

        var activations = Enumerable.Range(0, burstSize)
            .Select(_ => service.ActivateAsync(new DurableExternalActivationRequest(
                new DurableRuntimePumpRequest(8, TimeSpan.FromSeconds(2), DurableRuntimeSurface.Work),
                TimeSpan.FromSeconds(30))).AsTask())
            .ToArray();

        await allHealthEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(burstSize, health.CallCount);
        releaseHealth.TrySetResult();
        await firstAdmissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await allAdmissionsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(burstSize, admission.CallCount);
        releaseFirstAdmission.TrySetResult();
        var results = await Task.WhenAll(activations);

        Assert.Equal(1, results.Count(result => result.Kind == DurableExternalActivationOutcomeKind.Completed));
        Assert.Equal(burstSize - 1, results.Count(result => result.Kind == DurableExternalActivationOutcomeKind.Busy));
        Assert.All(results, result => Assert.Equal(DurableRuntimeHealthState.Healthy, result.ObservedHealthState));
        Assert.Equal(1, maximumProviderPasses);
        Assert.Equal(burstSize, health.CallCount);
        Assert.Equal(burstSize, admission.CallCount);
        Assert.Equal(0, results.Count(result =>
            result.Kind is DurableExternalActivationOutcomeKind.ActivationFailed
                or DurableExternalActivationOutcomeKind.PumpFailed));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (observed >= candidate)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, observed) != observed);
    }
}
