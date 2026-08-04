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

    /// <summary>
    /// Registers browser-local Light/Dark preference support for the configured Web theme pair.
    /// </summary>
    /// <param name="services">The service collection receiving the preference adapter.</param>
    /// <param name="configure">
    /// Optional configuration for the origin-scoped browser storage key. The key is not rendered into the bootstrap
    /// script, so changing it does not change the published CSP script hash.
    /// </param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The configured storage key is blank, unsafe for HTML data attributes, or longer than 64 characters.</exception>
    /// <exception cref="InvalidOperationException">
    /// Neutral theming has not been registered before this call. Register <c>AddAppSurfaceTheming</c> first so this
    /// adapter can reuse the host-selected pair.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This opt-in establishes the base Web integration and replaces its document provider with a System-first
    /// preference document for the same neutral theme pair. It never reads HTTP state, writes a cookie, changes a URL,
    /// adds a cache key, or alters the neutral theming package. Existing hosts that use only
    /// <see cref="AddAppSurfaceWebTheming(IServiceCollection)"/> retain their configured startup mode and emit no
    /// preference script.
    /// </para>
    /// <para>
    /// The browser may persist only explicit <c>light</c> or <c>dark</c> values in local storage. Missing, blocked,
    /// malformed, and explicit <c>system</c> values use the operating-system preference through the server-rendered
    /// System stylesheet. Calling this method again replaces the previous preference configuration; the latest valid
    /// storage key is the one rendered by the head TagHelper.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAppSurfaceWebThemePreferences(
        this IServiceCollection services,
        Action<AppSurfaceThemePreferenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ForgeTrust.AppSurface.Theming.IAppSurfaceThemeResolver)))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME002: AddAppSurfaceWebThemePreferences requires AddAppSurfaceTheming to be registered first.");
        }

        var options = new AppSurfaceThemePreferenceOptions();
        configure?.Invoke(options);
        var snapshot = options.Snapshot();

        services.AddAppSurfaceWebTheming();
        services.Replace(ServiceDescriptor.Singleton<IAppSurfaceThemeDocumentProvider, AppSurfaceThemePreferenceDocumentProvider>());
        services.Replace(ServiceDescriptor.Singleton(snapshot));
        services.Replace(
            ServiceDescriptor.Singleton(
                provider => new AppSurfaceThemePreferenceBootstrap(
                    provider.GetRequiredService<AppSurfaceThemePreferenceOptions>())));
        return services;
    }
}
