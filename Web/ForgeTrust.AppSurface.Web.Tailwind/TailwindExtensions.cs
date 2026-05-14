using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Web.Tailwind;

/// <summary>
/// Provides extension methods for registering Tailwind CSS services.
/// </summary>
public static class TailwindExtensions
{
    /// <summary>
    /// Adds Tailwind CSS services to the service collection.
    /// </summary>
    /// <remarks>
    /// Use this overload when the default <see cref="TailwindOptions"/> are sufficient. It delegates to
    /// <see cref="AddTailwind(IServiceCollection, Action{TailwindOptions})"/> and registers both
    /// <see cref="TailwindCliManager"/> and the hosted <see cref="TailwindWatchService"/>. In tests or non-hosted
    /// scenarios, remember that the hosted watch service can start background file/process work.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTailwind(this IServiceCollection services)
    {
        return services.AddTailwind(_ => { });
    }

    /// <summary>
    /// Adds Tailwind CSS services with custom configuration to the service collection.
    /// </summary>
    /// <remarks>
    /// Use this overload to customize <see cref="TailwindOptions"/> before AppSurface registers
    /// <see cref="TailwindCliManager"/> and <see cref="TailwindWatchService"/>. The watch service is registered as an
    /// <see cref="IHostedService"/>, so hosts that should not run Tailwind background work should avoid this extension
    /// or replace the hosted service registration intentionally.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">An action to configure the <see cref="TailwindOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTailwind(this IServiceCollection services, Action<TailwindOptions> configureOptions)
    {
        services.Configure(configureOptions);
        services.TryAddSingleton<TailwindCliManager>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TailwindWatchService>());

        return services;
    }
}
