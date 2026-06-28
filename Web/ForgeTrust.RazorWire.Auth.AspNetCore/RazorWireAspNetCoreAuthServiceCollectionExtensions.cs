using ForgeTrust.RazorWire.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.RazorWire.Auth.AspNetCore;

/// <summary>
/// Registers RazorWire auth projection integration for ASP.NET Core hosts.
/// </summary>
public static class RazorWireAspNetCoreAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RazorWire auth result provider backed by AppSurface ASP.NET Core policy evaluation.
    /// </summary>
    /// <param name="services">Service collection that receives the provider registration.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// Call this after registering `ForgeTrust.AppSurface.Auth.AspNetCore` with
    /// <c>AddAppSurfaceAspNetCoreAuth(...)</c>. This method does not call <c>AddAuthentication</c>,
    /// <c>AddAuthorization</c>, <c>AddRazorWire</c>, or any challenge/forbid APIs; those remain host-owned.
    /// The default provider is added with <c>TryAddScoped</c>, so an existing
    /// <see cref="IRazorWireAuthResultProvider"/> registration is preserved. Keep a custom provider when a host
    /// needs non-policy projection, custom tenant/resource resolution, or a test-specific auth result source instead
    /// of the ASP.NET Core policy adapter.
    /// </remarks>
    public static IServiceCollection AddRazorWireAspNetCoreAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IRazorWireAuthResultProvider, RazorWireAspNetCoreAuthResultProvider>();
        return services;
    }
}
