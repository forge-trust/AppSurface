using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Registers the ASP.NET Core AppSurface auth adapter.
/// </summary>
public static class AppSurfaceAspNetCoreAuthServiceCollectionExtensions
{
    /// <summary>
    /// Adds AppSurface auth mapping services for an ASP.NET Core host.
    /// </summary>
    /// <param name="services">Service collection that receives adapter registrations.</param>
    /// <param name="configure">Optional adapter options callback.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// The adapter registers <see cref="IHttpContextAccessor"/>, AppSurface auth options, adapter options, and scoped
    /// adapter services. It intentionally does not call <c>AddAuthentication</c>, register authentication schemes, call
    /// <c>AddAuthorization</c>, create policies, add middleware, challenge, forbid, redirect, or mutate cookies. Host
    /// applications must keep those choices in their ASP.NET Core security setup.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceAspNetCoreAuth(
        this IServiceCollection services,
        Action<AppSurfaceAspNetCoreAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddOptions<AppSurfaceAuthOptions>();

        var optionsBuilder = services.AddOptions<AppSurfaceAspNetCoreAuthOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddScoped<AppSurfaceAspNetCoreAuthContextMapper>();
        services.TryAddScoped<IAppSurfaceAspNetCoreAuthContextAccessor, AppSurfaceAspNetCoreAuthContextAccessor>();
        services.TryAddScoped<IAppSurfaceAspNetCorePolicyEvaluator, AppSurfaceAspNetCorePolicyEvaluator>();

        return services;
    }
}
