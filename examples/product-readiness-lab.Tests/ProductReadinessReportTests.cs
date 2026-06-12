using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Flow.DurableTask;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
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
        await using var provider = ProductReadinessTestServices.BuildProvider(
            new InMemoryProductStateStore(),
            includeReportService: true);
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
        await using var provider = ProductReadinessTestServices.BuildProvider(
            new ProbeProductStateStore(succeeded: true, isPostgresBacked: true),
            includeReportService: true);
        var report = await provider.GetRequiredService<ProductReadinessReportService>().BuildAsync();

        var postgres = Assert.Single(report.Rows, row => row.Area == "postgres-product-state");
        var durableTask = Assert.Single(report.Rows, row => row.Area == "durabletask-backend-boundary");

        Assert.Equal(ReadinessStatus.ProvenLocally, postgres.Status);
        Assert.Equal(ReadinessStatus.HostOwned, durableTask.Status);
    }

    [Fact]
    public async Task ReadinessResponse_UsesStableStatusWireNames()
    {
        await using var provider = ProductReadinessTestServices.BuildProvider(
            new InMemoryProductStateStore(),
            includeReportService: true);
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
    public void WorkflowMutationEndpoints_RequireOperatorPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        builder.Services.AddProductReadinessLab("Development", isDevelopment: true);
        using var app = builder.Build();

        ProductReadinessEndpoints.Map(app);

        AssertEndpointRequiresOperatorPolicy(app, "/workflow/start");
        AssertEndpointRequiresOperatorPolicy(app, "/workflow/{instanceId}/resume");
    }

    [Fact]
    public async Task AuthPolicies_RequireExpectedProofClaims()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddProductReadinessLab("Development", isDevelopment: true);
        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var operatorResult = await authorization.AuthorizeAsync(
            ProductReadinessProofUsers.CreatePrincipal("operator")!,
            ProductReadinessPolicies.OperatorsOnly);
        var viewerResult = await authorization.AuthorizeAsync(
            ProductReadinessProofUsers.CreatePrincipal("viewer")!,
            ProductReadinessPolicies.OperatorsOnly);
        var noSubjectResult = await authorization.AuthorizeAsync(
            ProductReadinessProofUsers.CreatePrincipal("nosub")!,
            ProductReadinessPolicies.OperatorsOnly);
        var unavailableEntitlementResult = await authorization.AuthorizeAsync(
            ProductReadinessProofUsers.CreatePrincipal("operator")!,
            ProductReadinessPolicies.UnavailableEntitlement);

        Assert.True(operatorResult.Succeeded);
        Assert.False(viewerResult.Succeeded);
        Assert.False(noSubjectResult.Succeeded);
        Assert.False(unavailableEntitlementResult.Succeeded);
    }

    [Fact]
    public async Task InProcessHost_ProvesWaitResumeTimeoutFaultAndLateEvent()
    {
        await using var provider = ProductReadinessTestServices.BuildProvider(
            new InMemoryProductStateStore(),
            includeReportService: true);
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();

        var started = await host.StartAsync("Acme", "Team");
        var completed = await host.ResumeAsync(started.InstanceId, "approved", "operator-1");
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
    public async Task InProcessHost_UsesAuthenticatedCallerForResumeAuthorization()
    {
        var authorizer = new CapturingResumeAuthorizer();
        await using var provider = ProductReadinessTestServices.BuildProvider(
            new InMemoryProductStateStore(),
            authorizer,
            includeReportService: true);
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();

        var started = await host.StartAsync("Caller Co", "Team");
        var completed = await host.ResumeAsync(started.InstanceId, "approved", "operator-42");

        var request = Assert.Single(authorizer.Requests);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("operator-42", request.Caller);
        Assert.Equal(ProductReadinessFlowDefinition.Version, request.Version);
        Assert.Equal(ProductReadinessFlowDefinition.ReviewNodeId, request.NodeId);
    }

    [Fact]
    public async Task InProcessHost_DeniesResumeFromUnexpectedCaller()
    {
        await using var provider = ProductReadinessTestServices.BuildProvider(
            new InMemoryProductStateStore(),
            includeReportService: true);
        var host = provider.GetRequiredService<ProductApprovalInProcessHost>();

        var started = await host.StartAsync("Viewer Co", "Team");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.ResumeAsync(started.InstanceId, "approved", "viewer-1"));

        Assert.Contains("Resume denied", exception.Message, StringComparison.Ordinal);
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

    private static void AssertEndpointRequiresOperatorPolicy(WebApplication app, string routePattern)
    {
        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(dataSource => dataSource.Endpoints),
            candidate => string.Equals(
                (candidate as RouteEndpoint)?.RoutePattern.RawText,
                routePattern,
                StringComparison.Ordinal));
        var authorizeData = Assert.Single(endpoint.Metadata.OfType<IAuthorizeData>());

        Assert.Equal(ProductReadinessPolicies.OperatorsOnly, authorizeData.Policy);
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

    private sealed class CapturingResumeAuthorizer : IFlowResumeAuthorizer
    {
        public List<FlowResumeAuthorizationRequest> Requests { get; } = [];

        public ValueTask<FlowResumeAuthorizationResult> AuthorizeAsync(
            FlowResumeAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(FlowResumeAuthorizationResult.Allow());
        }
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
