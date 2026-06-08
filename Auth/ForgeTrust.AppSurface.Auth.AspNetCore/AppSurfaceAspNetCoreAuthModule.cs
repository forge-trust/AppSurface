using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Registers the ASP.NET Core adapter for AppSurface auth contracts.
/// </summary>
/// <remarks>
/// This module composes the neutral <see cref="AppSurfaceAuthModule"/> and registers adapter services only. It does not
/// configure ASP.NET Core authentication schemes, authorization policies, middleware, challenges, forbids, redirects,
/// cookies, identity providers, endpoint filters, RazorWire UI, or Minimal API helpers.
/// </remarks>
public sealed class AppSurfaceAspNetCoreAuthModule : IAppSurfaceModule
{
    /// <summary>
    /// Registers ASP.NET Core AppSurface auth adapter services.
    /// </summary>
    /// <param name="context">Startup context for the current AppSurface composition pass.</param>
    /// <param name="services">Service collection that receives adapter registrations.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddAppSurfaceAspNetCoreAuth();
    }

    /// <summary>
    /// Registers the neutral AppSurface auth module dependency.
    /// </summary>
    /// <param name="builder">Module dependency builder for the current startup graph.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceAuthModule>();
    }
}
