using ForgeTrust.AppSurface.Web.Theming;
using Microsoft.AspNetCore.Hosting;
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
    /// Registers browser-local System/Light/Dark preference support for the configured Web theme pair.
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
    /// An <see cref="ForgeTrust.AppSurface.Theming.IAppSurfaceThemeResolver"/> has not been registered before this
    /// call. <c>AddAppSurfaceTheming</c> is one supported registration path; hosts may instead register a compatible
    /// custom resolver for the selected pair.
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
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AppSurfaceThemeSelectionRegistrationMarker)))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME005: AddAppSurfaceWebThemePreferences cannot be combined with AddAppSurfaceWebThemeSelection. Choose one document-provider adapter.");
        }

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ForgeTrust.AppSurface.Theming.IAppSurfaceThemeResolver)))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME002: AddAppSurfaceWebThemePreferences requires an IAppSurfaceThemeResolver to be registered first. AddAppSurfaceTheming is one supported registration path.");
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
        services.TryAddSingleton<AppSurfaceThemePreferenceRegistrationMarker>();
        return services;
    }

    /// <summary>
    /// Registers a scoped host-policy adapter that selects one registered theme pair before Web rendering.
    /// </summary>
    /// <param name="services">The service collection receiving the selection adapter.</param>
    /// <returns>The original <paramref name="services"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Neutral theme services, a scoped <see cref="IAppSurfaceWebThemeSelectionPolicy"/>, or the built-in ordinary
    /// Web document provider are missing; a conflicting adapter is registered; or selection is registered twice.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Register <c>AddAppSurfaceTheming</c>, a scoped host implementation of
    /// <see cref="IAppSurfaceWebThemeSelectionPolicy"/>, then this opt-in. The policy consumes only already-authorized
    /// application context and returns either one registered <c>AppSurfaceThemeId</c> or <see langword="false"/> for
    /// the configured default. The adapter validates selected ids against the sealed registry and never serializes a
    /// caller-supplied theme pair.
    /// </para>
    /// <para>
    /// This adapter intentionally cannot compose with browser-local preferences or a consumer-owned
    /// <see cref="IAppSurfaceThemeDocumentProvider"/>. It caches package-owned documents by immutable pair id, but
    /// it never configures HTTP caching, response variation, tenant resolution, authorization, or invalidation. A pair
    /// id is not a safe host response-cache key; the host must partition or disable tenant-sensitive response caches.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAppSurfaceWebThemeSelection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ForgeTrust.AppSurface.Theming.IAppSurfaceThemeResolver))
            || !services.Any(descriptor => descriptor.ServiceType == typeof(ForgeTrust.AppSurface.Theming.IAppSurfaceThemeRegistry)))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME003: AddAppSurfaceWebThemeSelection requires IAppSurfaceThemeResolver and IAppSurfaceThemeRegistry to be registered first. AddAppSurfaceTheming is the supported registration path.");
        }

        var policyDescriptor = services.LastOrDefault(
            descriptor => descriptor.ServiceType == typeof(IAppSurfaceWebThemeSelectionPolicy));
        if (policyDescriptor is null || policyDescriptor.Lifetime != ServiceLifetime.Scoped)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME004: AddAppSurfaceWebThemeSelection requires a scoped IAppSurfaceWebThemeSelectionPolicy to be registered first.");
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(AppSurfaceThemePreferenceRegistrationMarker)))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME005: AddAppSurfaceWebThemeSelection cannot be combined with AddAppSurfaceWebThemePreferences. Choose one document-provider adapter.");
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(AppSurfaceThemeSelectionRegistrationMarker)))
        {
            throw new InvalidOperationException(
                "ASWEBTHEME007: AddAppSurfaceWebThemeSelection can be registered only once.");
        }

        EnsureOrdinaryDocumentProvider(services);
        services.Replace(ServiceDescriptor.Scoped<IAppSurfaceThemeDocumentProvider, AppSurfaceThemeSelectionDocumentProvider>());
        services.AddSingleton<AppSurfaceThemeSelectionDocumentCache>();
        services.AddSingleton(new AppSurfaceThemeSelectionRegistrationState(services));
        services.AddSingleton<AppSurfaceThemeSelectionRegistrationMarker>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, AppSurfaceThemeSelectionStartupValidator>());
        return services;
    }

    private static void EnsureOrdinaryDocumentProvider(IServiceCollection services)
    {
        var descriptors = services
            .Where(descriptor => descriptor.ServiceType == typeof(IAppSurfaceThemeDocumentProvider))
            .ToArray();
        if (descriptors.Length == 0)
        {
            services.AddAppSurfaceWebTheming();
            descriptors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IAppSurfaceThemeDocumentProvider))
                .ToArray();
        }

        if (descriptors.Length == 1
            && descriptors[0].Lifetime == ServiceLifetime.Singleton
            && descriptors[0].ImplementationType == typeof(AppSurfaceThemeDocumentProvider))
        {
            return;
        }

        throw new InvalidOperationException(
            "ASWEBTHEME006: AddAppSurfaceWebThemeSelection can replace only the built-in AppSurfaceThemeDocumentProvider. Remove the consumer-owned IAppSurfaceThemeDocumentProvider replacement or do not opt into selection.");
    }
}
