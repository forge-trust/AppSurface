using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Tests.SharedErrorPagesFixture;

// This project is intentionally not a friend assembly of ForgeTrust.AppSurface.Web. Keeping the README samples here
// proves that the documented named-canary path compiles entirely through the package's public API.

// docs:snippet appsurface-canary-evaluator:start
using ForgeTrust.AppSurface.Web;

public sealed class ForwardingCanaryEvaluator : IAppSurfaceCanaryEvaluator
{
    public const string ProofKindDetailKey = "proof.kind";

    public ValueTask<AppSurfaceCanaryResult> EvaluateAsync(
        AppSurfaceCanaryEvaluationContext context,
        CancellationToken cancellationToken)
    {
        // Compile-only placeholder: query application-owned proof using context.Marker and context.FreshSince.
        return ValueTask.FromResult(
            new AppSurfaceCanaryResult(AppSurfaceCanaryStatus.Pending));
    }
}
// docs:snippet appsurface-canary-evaluator:end

public sealed class NamedCanaryPublicApiFixture : IAppSurfaceWebModule
{
    // docs:snippet appsurface-canary-registration:start
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddAuthorization(options =>
            options.AddPolicy("DeployOperators", policy => policy.RequireAuthenticatedUser()));

        services.AddAppSurfaceCanary<ForwardingCanaryEvaluator>(
            "forwarding.alpha-evidence",
            canary =>
            {
                canary.RequireMarker();
                canary.RequireFreshSince();
                canary.AllowedDetailKeys.Add(ForwardingCanaryEvaluator.ProofKindDetailKey);
            });
    }
    // docs:snippet appsurface-canary-registration:end

    // docs:snippet appsurface-canary-mapping:start
    public void ConfigureEndpointAwareMiddleware(StartupContext context, IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
    }

    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAppSurfaceCanaries("DeployOperators");
    }
    // docs:snippet appsurface-canary-mapping:end

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}

public sealed class NamedCanaryAlwaysOkPublicApiFixture : IAppSurfaceWebModule
{
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    // docs:snippet appsurface-canary-always-ok:start
    public void ConfigureEndpoints(StartupContext context, IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAppSurfaceCanaries(
            "DeployOperators",
            options => options.CompletedResponseMode = AppSurfaceCanaryCompletedResponseMode.AlwaysOk);
    }
    // docs:snippet appsurface-canary-always-ok:end
}
