using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Registers the surface-neutral AppSurface product-intelligence composition boundary.
/// </summary>
/// <remarks>
/// The module registers a passive dispatcher and options type. It does not configure PostHog, OpenTelemetry, cookies,
/// JavaScript autocapture, persistence, retention, access control, dashboards, or session replay.
/// </remarks>
public sealed class AppSurfaceProductIntelligenceModule : IAppSurfaceModule
{
    /// <summary>
    /// Registers product-intelligence services for the current startup graph.
    /// </summary>
    /// <param name="context">Startup context for the current AppSurface composition pass.</param>
    /// <param name="services">Service collection that receives product-intelligence services.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddAppSurfaceProductIntelligence();
    }

    /// <summary>
    /// Registers dependent modules required by product intelligence.
    /// </summary>
    /// <param name="builder">The module dependency builder for the current startup graph.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
