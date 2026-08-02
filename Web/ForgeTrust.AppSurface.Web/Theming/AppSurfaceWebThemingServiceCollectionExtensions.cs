using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Web;

/// <summary>Registers the opt-in Web rendering adapter for neutral AppSurface theming.</summary>
public static class AppSurfaceWebThemingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Web theme document provider used by the AppSurface theme TagHelpers.
    /// </summary>
    /// <param name="services">Service collection receiving the provider.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <remarks>
    /// Call <c>AddAppSurfaceTheming</c> from the neutral theming package first, then call this method to opt the Web
    /// host into the <see cref="TagHelpers.AppSurfaceThemeRootTagHelper"/> and
    /// <see cref="TagHelpers.AppSurfaceThemeHeadTagHelper"/> integration. This method intentionally does not create
    /// a default theme pair or silently enable theme rendering.
    /// </remarks>
    public static IServiceCollection AddAppSurfaceWebTheming(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAppSurfaceThemeDocumentProvider, AppSurfaceThemeDocumentProvider>();
        return services;
    }
}
