// Design-only sketch for AppSurface issue #793.
// It intentionally references APIs proposed by the adoption rail and is not compiled.

using ForgeTrust.AppSurface.Durable;
using ForgeTrust.AppSurface.Durable.Provider;

namespace Skoolit.Web.Billing;

internal static class BillingLifecycleExecutableContractSketch
{
    // appsurface-adoption:registration:start
    internal static readonly DurableWorkDefinition<BillingLifecycleDurableWork, BillingLifecycleDurableResult> Definition =
        DurableWork.Define(
            BillingLifecycleDurableWorkContract.WorkName,
            BillingLifecycleDurableWorkContract.WorkVersion,
            BillingLifecycleDurableWorkContract.CreateWorkCodec(),
            BillingLifecycleDurableWorkContract.CreateResultCodec(),
            BillingLifecycleDurableWorkContract.ProviderSafety,
            BillingLifecycleDurableWorkContract.CreateRetryPolicy());
    internal static void Register(IServiceCollection services) =>
        services.AddDurableWork(Definition.ExecutedBy<BillingLifecycleDurableExecutor>());
    // appsurface-adoption:registration:end

    // Host-owned route, authorization, response, and deadline policy remains explicit.
    // Application reconciliation and activation-outbox dispatch remain outside this mapping.
    // appsurface-adoption:external-activation-mapping:start
    internal static void MapActivation(WebApplication app, int maximumItems)
    {
        app.MapGet("/ready", async (IDurableRuntimeHealth health, CancellationToken cancellationToken) =>
        {
            var snapshot = await health.GetAsync(cancellationToken);
            return snapshot.IsReady ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });
        app.MapPost("/_internal/durable/billing/pump", async (IDurableRuntimePumpAdmission admission, CancellationToken cancellationToken) =>
        {
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestBudget.CancelAfter(BillingLifecycleHostPolicy.RequestBudget);
            var request = new DurableRuntimePumpRequest(maximumItems, BillingLifecycleHostPolicy.PumpDiscoveryBudget, DurableRuntimeSurface.Work);
            var attempt = await admission.TryRunOnceAsync(request, requestBudget.Token);
            return attempt.Kind switch
            {
                DurableRuntimePumpAttemptKind.Completed => BillingLifecycleActivationResponses.Completed(attempt.Result!),
                DurableRuntimePumpAttemptKind.Refused => BillingLifecycleActivationResponses.Refused(),
                DurableRuntimePumpAttemptKind.Unavailable => BillingLifecycleActivationResponses.Unavailable(attempt.ProblemCode!),
                DurableRuntimePumpAttemptKind.Incompatible => BillingLifecycleActivationResponses.Incompatible(attempt.ProblemCode!),
                _ => throw new UnreachableException(),
            };
        })
        .RequireAuthorization(BillingLifecycleAuthorization.ActivationPolicy);
    }
    // appsurface-adoption:external-activation-mapping:end

    // The first-party scenario removes hand-built snapshots, pumps, and timeout polling.
    // PostgreSQL-sensitive claim, lease, fence, and effect tests remain provider tests.
    // appsurface-adoption:primary-lifecycle-test:start
    [Fact]
    internal static async Task ColdActivationCompletesPumpPassAndReachesReadiness(IServiceProvider services)
    {
        var scenario = DurableHostScenario
            .For(Definition)
            .Using(services);
        var before = await scenario.AssessHealthAsync();
        Assert.True(before.Snapshot.CanEnableActivation);
        Assert.True(before.Snapshot.CanAttemptPump);
        Assert.False(before.Snapshot.IsReady);
        var pump = await scenario.RunDirectPumpOnceAsync();
        Assert.Equal(DurableRuntimePumpAttemptKind.Completed, pump.Attempt.Kind);
        var after = await scenario.AssessHealthAsync();
        Assert.True(after.Snapshot.IsReady);
    }
    // appsurface-adoption:primary-lifecycle-test:end
}
