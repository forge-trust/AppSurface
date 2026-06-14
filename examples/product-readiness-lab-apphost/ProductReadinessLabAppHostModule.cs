using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ProductReadinessLabAppHost;

/// <summary>
/// Root module marker for the product-readiness lab AppHost.
/// </summary>
/// <remarks>
/// The AppHost keeps Aspire composition in <see cref="ProductReadinessComponents" /> and leaves this
/// module intentionally empty so package evaluators can see that the example does not require hidden
/// service registration. Add dependencies here only when AppSurface-owned AppHost services become part
/// of the public proof contract; keep Postgres, web, and verifier wiring in the AppHost builder.
/// </remarks>
public sealed class ProductReadinessLabAppHostModule : IAppSurfaceHostModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
