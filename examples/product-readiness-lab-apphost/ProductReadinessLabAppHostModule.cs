using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ProductReadinessLabAppHost;

/// <summary>
/// Root module for the product-readiness lab AppHost.
/// </summary>
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
