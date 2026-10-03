using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Durable.Provider;

/// <summary>Provides explicit, passive external-activation composition for runtime hosts.</summary>
public static class DurableExternalActivationServiceCollectionExtensions
{
    /// <summary>Repeat-safely supplies a singleton activation service, clock, and standard logging services.</summary>
    /// <param name="services">The host-owned service collection.</param>
    /// <returns>The same collection for chaining.</returns>
    /// <remarks>
    /// Existing activation and clock registrations win; health/admission may be registered in either order.
    /// This adds no worker, route, connection, DDL, timer, listener, or exporter. Enable ValidateOnBuild and
    /// ValidateScopes and resolve the activation service before listening; build validation does not execute factory
    /// bodies. The default service requires singleton-compatible health and admission dependencies.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddDurableExternalActivation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDurableExternalActivationService, DurableExternalActivationService>();
        return services;
    }
}
