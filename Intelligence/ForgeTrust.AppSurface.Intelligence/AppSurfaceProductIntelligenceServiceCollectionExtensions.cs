using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Registers AppSurface product-intelligence services.
/// </summary>
public static class AppSurfaceProductIntelligenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds the AppSurface product-intelligence dispatcher and options.
    /// </summary>
    /// <remarks>
    /// Hosts own <see cref="IAppSurfaceProductIntelligenceSink" /> registration, transport, retention, and vendor setup.
    /// This extension registers an immutable composed <see cref="IAppSurfaceProductEventRegistry" /> and the default dispatcher with <c>TryAddScoped</c>,
    /// so repeated calls do not replace an existing <see cref="IAppSurfaceProductIntelligence" /> registration. Repeated
    /// calls can still accumulate options configuration delegates, but identical contract registrations are idempotent.
    /// Call <see cref="AppSurfaceProductIntelligenceOptions.EnableExperimentalEvents()" /> only when experimental dogfood
    /// events should be emitted to host-owned sinks.
    /// </remarks>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional options callback. Call <see cref="AppSurfaceProductIntelligenceOptions.EnableExperimentalEvents()"/> to emit dogfood events.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddAppSurfaceProductIntelligence(
        this IServiceCollection services,
        Action<AppSurfaceProductIntelligenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AppSurfaceProductIntelligenceOptions>()
            .ValidateOnStart();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AppSurfaceProductIntelligenceOptions>,
            AppSurfaceProductIntelligenceOptionsValidator>());
        services.TryAddSingleton<IAppSurfaceProductEventRegistry>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AppSurfaceProductIntelligenceOptions>>();
            return new DefaultAppSurfaceProductEventRegistry(options.Value.RegisteredEventContracts);
        });
        services.TryAddScoped<IAppSurfaceProductIntelligence, AppSurfaceProductIntelligenceDispatcher>();
        return services;
    }
}
