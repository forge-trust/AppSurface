using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Intelligence;

/// <summary>
/// Registers AppSurface product-intelligence services.
/// </summary>
public static class AppSurfaceProductIntelligenceServiceCollectionExtensions
{
    /// <summary>
    /// Adds the AppSurface product-intelligence dispatcher and options.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional options callback. Call <see cref="AppSurfaceProductIntelligenceOptions.EnableExperimentalEvents"/> to emit dogfood events.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection AddAppSurfaceProductIntelligence(
        this IServiceCollection services,
        Action<AppSurfaceProductIntelligenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<AppSurfaceProductIntelligenceOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddScoped<IAppSurfaceProductIntelligence, AppSurfaceProductIntelligenceDispatcher>();
        return services;
    }
}
