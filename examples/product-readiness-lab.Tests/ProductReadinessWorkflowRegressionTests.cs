using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace ProductReadinessLab.Tests;

public sealed class ProductReadinessWorkflowRegressionTests
{
    [Fact]
    public async Task ResumeWorkflowAsync_DuplicateResume_ReturnsLateEventConflict()
    {
        // Regression: ISSUE-001 - duplicate workflow resume returned HTTP 500.
        // Found by /qa on 2026-06-10.
        // Report: .gstack/qa-reports/qa-report-product-readiness-lab-2026-06-10.md
        await using var provider = BuildProvider(new InMemoryProductStateStore());
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();
        var started = await host.StartAsync("QA Co", "Team");
        var request = new ResumeWorkflowRequest("approved");

        var first = await ProductReadinessEndpoints.ResumeWorkflowAsync(
            host,
            ProductReadinessProofUsers.CreatePrincipal("operator")!,
            started.InstanceId,
            request,
            CancellationToken.None);
        var second = await ProductReadinessEndpoints.ResumeWorkflowAsync(
            host,
            ProductReadinessProofUsers.CreatePrincipal("operator")!,
            started.InstanceId,
            request,
            CancellationToken.None);

        Assert.IsType<Ok<WorkflowProbe>>(first.Result);
        var conflict = Assert.IsType<Conflict<WorkflowResumeRejected>>(second.Result);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(started.InstanceId, conflict.Value?.InstanceId);
        Assert.Equal("IgnoredLateEvent", conflict.Value?.Status);
    }

    [Fact]
    public async Task ResumeWorkflowAsync_CanceledResume_CanBeRetried()
    {
        // Regression: canceled resume consumed the waiting run before processing completed.
        // Found by /review on 2026-06-10.
        await using var provider = BuildProvider(new InMemoryProductStateStore());
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();
        var started = await host.StartAsync("Retry Co", "Team");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.ResumeAsync(started.InstanceId, "approved", "operator-1", canceled.Token));

        var retry = await ProductReadinessEndpoints.ResumeWorkflowAsync(
            host,
            ProductReadinessProofUsers.CreatePrincipal("operator")!,
            started.InstanceId,
            new ResumeWorkflowRequest("approved"),
            CancellationToken.None);

        var ok = Assert.IsType<Ok<WorkflowProbe>>(retry.Result);
        Assert.Equal(started.InstanceId, ok.Value?.InstanceId);
        Assert.Equal("Completed", ok.Value?.Status);
    }

    [Fact]
    public async Task ResumeWorkflowAsync_InvalidRequest_ReturnsBadRequest()
    {
        await using var provider = BuildProvider(new InMemoryProductStateStore());
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();
        var started = await host.StartAsync("Validation Co", "Team");

        var response = await ProductReadinessEndpoints.ResumeWorkflowAsync(
            host,
            ProductReadinessProofUsers.CreatePrincipal("operator")!,
            started.InstanceId,
            new ResumeWorkflowRequest(string.Empty),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequest<WorkflowRequestRejected>>(response.Result);
        Assert.NotNull(badRequest.Value);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal("InvalidRequest", badRequest.Value.Status);
        Assert.Contains(nameof(ResumeWorkflowRequest.Decision), badRequest.Value.Errors.Keys);
    }

    private static ServiceProvider BuildProvider(IProductStateStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton(store);
        services.AddSingleton(ProductReadinessFlowDefinition.Build());
        services.AddSingleton<IFlowDefinitionRegistry>(sp =>
        {
            var registry = new FlowDefinitionRegistry();
            registry.Register(sp.GetRequiredService<FlowDefinition<ProductApprovalState>>());
            return registry;
        });
        services.AddSingleton<IFlowResumeAuthorizer, ProductReadinessResumeAuthorizer>();
        services.AddSingleton<FlowContextSerializationValidator>();
        services.AddSingleton<IFlowContextSerializer, SystemTextJsonFlowContextSerializer>();
        services.AddOptions<AppSurfaceFlowDurableTaskOptions>();
        services.AddSingleton(typeof(IDurableTaskFlowRunner<>), typeof(DurableTaskFlowRunner<>));
        services.AddSingleton(typeof(IDurableTaskFlowClient<>), typeof(DurableTaskFlowClient<>));
        services.AddSingleton<ProductApprovalInProcessHost>();

        return services.BuildServiceProvider();
    }
}
