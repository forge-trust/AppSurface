using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow;
using ForgeTrust.AppSurface.Flow.DurableTask;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProductReadinessLab.Tests;

public sealed class ProductReadinessReportTests
{
    [Fact]
    public void StatusWireNames_AreStable()
    {
        Assert.Equal("proven-locally", ReadinessReportMarkdownRenderer.ToWireName(ReadinessStatus.ProvenLocally));
        Assert.Equal("host-owned", ReadinessReportMarkdownRenderer.ToWireName(ReadinessStatus.HostOwned));
        Assert.Equal("deferred", ReadinessReportMarkdownRenderer.ToWireName(ReadinessStatus.Deferred));
        Assert.Equal("unsafe-to-copy", ReadinessReportMarkdownRenderer.ToWireName(ReadinessStatus.UnsafeToCopy));
        Assert.Equal("blocked", ReadinessReportMarkdownRenderer.ToWireName(ReadinessStatus.Blocked));
    }

    [Fact]
    public void ExitCodePolicy_FailsOnlyBlockedRows()
    {
        var nonBlocking = new ReadinessReport(
            DateTimeOffset.UnixEpoch,
            [
                Row(ReadinessStatus.ProvenLocally),
                Row(ReadinessStatus.HostOwned),
                Row(ReadinessStatus.Deferred),
                Row(ReadinessStatus.UnsafeToCopy),
            ]);
        var blocked = nonBlocking with { Rows = [.. nonBlocking.Rows, Row(ReadinessStatus.Blocked)] };

        Assert.Equal(0, ReadinessReportExitCodePolicy.GetExitCode(nonBlocking));
        Assert.Equal(1, ReadinessReportExitCodePolicy.GetExitCode(blocked));
    }

    [Fact]
    public async Task Report_WithInMemoryStore_MarksPostgresBlockedAndDurableTaskHostOwned()
    {
        await using var provider = BuildProvider(new InMemoryProductStateStore());
        var report = await provider.GetRequiredService<ProductReadinessReportService>().BuildAsync();

        var postgres = Assert.Single(report.Rows, row => row.Area == "postgres-product-state");
        var durableTask = Assert.Single(report.Rows, row => row.Area == "durabletask-backend-boundary");
        var markdown = ReadinessReportMarkdownRenderer.Render(report);

        Assert.Equal(ReadinessStatus.Blocked, postgres.Status);
        Assert.Equal(ReadinessStatus.HostOwned, durableTask.Status);
        Assert.DoesNotContain("DurableTask Postgres storage", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings__", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_WithPostgresBackedStore_MarksProductStateProvenAndDurableTaskHostOwned()
    {
        await using var provider = BuildProvider(new ProbeProductStateStore(succeeded: true, isPostgresBacked: true));
        var report = await provider.GetRequiredService<ProductReadinessReportService>().BuildAsync();

        var postgres = Assert.Single(report.Rows, row => row.Area == "postgres-product-state");
        var durableTask = Assert.Single(report.Rows, row => row.Area == "durabletask-backend-boundary");

        Assert.Equal(ReadinessStatus.ProvenLocally, postgres.Status);
        Assert.Equal(ReadinessStatus.HostOwned, durableTask.Status);
    }

    [Fact]
    public async Task ReadinessResponse_UsesStableStatusWireNames()
    {
        await using var provider = BuildProvider(new InMemoryProductStateStore());
        var report = await provider.GetRequiredService<ProductReadinessReportService>().BuildAsync();

        var response = ReadinessReportResponse.FromReport(report);

        Assert.Contains(response.Rows, row => row.Status == "proven-locally");
        Assert.Contains(response.Rows, row => row.Status == "host-owned");
        Assert.Contains(response.Rows, row => row.Status == "deferred");
        Assert.Contains(response.Rows, row => row.Status == "unsafe-to-copy");
        Assert.Contains(response.Rows, row => row.Status == "blocked");
    }

    [Fact]
    public void ProductReadinessModule_AddsAuthenticationAndAuthorizationMiddleware()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddProductReadinessLab("Development", isDevelopment: true);
        using var provider = services.BuildServiceProvider();
        var app = new RecordingApplicationBuilder(provider);

        new ProductReadinessModule().ConfigureEndpointAwareMiddleware(
            new StartupContext([], new ProductReadinessModule()),
            app);

        Assert.Equal(2, app.MiddlewareCount);
    }

    [Fact]
    public async Task InProcessHost_ProvesWaitResumeTimeoutFaultAndLateEvent()
    {
        await using var provider = BuildProvider(new InMemoryProductStateStore());
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();

        var started = await host.StartAsync("Acme", "Team");
        var completed = await host.ResumeAsync(started.InstanceId, "approved");
        var probe = await host.ProbeAsync();

        Assert.Equal("Waiting", started.Status);
        Assert.Equal(ProductReadinessFlowDefinition.ApprovalEventName, started.WaitingEventName);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("WaitForExternalEvent", probe.WaitingStatus);
        Assert.Equal("Complete", probe.CompletedStatus);
        Assert.Equal("TimedOut", probe.TimeoutStatus);
        Assert.Equal("IgnoreLateEvent", probe.LateEventStatus);
        Assert.Equal("approval.denied", probe.FaultCode);
    }

    [Fact]
    public void ProofAuthGuard_RejectsEnabledProofAuthOutsideDevelopment()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductReadinessProofAuthGuard.Validate("Production", isDevelopment: false, proofAuthEnabled: true));

        Assert.Contains("cannot run", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProofAuthDefaults_ToDevelopmentOnlyWhenUnset()
    {
        Assert.True(ProductReadinessServiceCollectionExtensions.ResolveProofAuthEnabled(isDevelopment: true, configuredValue: null));
        Assert.False(ProductReadinessServiceCollectionExtensions.ResolveProofAuthEnabled(isDevelopment: false, configuredValue: null));
    }

    [Fact]
    public void ProofAuthConfig_ExplicitValueOverridesDefault()
    {
        Assert.True(ProductReadinessServiceCollectionExtensions.ResolveProofAuthEnabled(isDevelopment: false, configuredValue: "true"));
        Assert.False(ProductReadinessServiceCollectionExtensions.ResolveProofAuthEnabled(isDevelopment: true, configuredValue: "false"));
        Assert.False(ProductReadinessServiceCollectionExtensions.ResolveProofAuthEnabled(isDevelopment: true, configuredValue: "not-bool"));
    }

    [Fact]
    public void ProofAuthGuard_AllowsDisabledProofAuthOutsideDevelopment()
    {
        ProductReadinessProofAuthGuard.Validate("Production", isDevelopment: false, proofAuthEnabled: false);
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
        services.AddSingleton<ProductReadinessReportService>();

        return services.BuildServiceProvider();
    }

    private static ReadinessRow Row(ReadinessStatus status) =>
        new("area", status, "evidence", "problem", "cause", "fix", "copy");

    private sealed class ProbeProductStateStore : IProductStateStore
    {
        private readonly bool _succeeded;

        public ProbeProductStateStore(bool succeeded, bool isPostgresBacked)
        {
            _succeeded = succeeded;
            IsPostgresBacked = isPostgresBacked;
        }

        public bool IsPostgresBacked { get; }

        public Task<ProductSubscription> SaveAsync(
            ProductSubscription subscription,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(subscription);

        public Task<ProductStateProbe> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProductStateProbe(_succeeded, IsPostgresBacked, "safe diagnostic"));
    }

    private sealed class RecordingApplicationBuilder : IApplicationBuilder
    {
        public RecordingApplicationBuilder(IServiceProvider applicationServices)
        {
            ApplicationServices = applicationServices;
        }

        public IServiceProvider ApplicationServices { get; set; }

        public IFeatureCollection ServerFeatures { get; } = new FeatureCollection();

        public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>();

        public int MiddlewareCount { get; private set; }

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);
            MiddlewareCount++;
            return this;
        }

        public IApplicationBuilder New() => new RecordingApplicationBuilder(ApplicationServices);

        public RequestDelegate Build() => _ => Task.CompletedTask;
    }
}
