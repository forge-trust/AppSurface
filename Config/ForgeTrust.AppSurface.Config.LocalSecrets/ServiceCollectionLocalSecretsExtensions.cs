using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Provides service collection helpers for AppSurface LocalSecrets.
/// </summary>
public static class ServiceCollectionLocalSecretsExtensions
{
    /// <summary>
    /// Configures LocalSecrets options.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">The options callback.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection ConfigureAppSurfaceLocalSecrets(
        this IServiceCollection services,
        Action<AppSurfaceLocalSecretsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services;
    }

    /// <summary>
    /// Replaces the local secret store implementation.
    /// </summary>
    /// <typeparam name="TStore">The store implementation type.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection UseAppSurfaceLocalSecretStore<TStore>(this IServiceCollection services)
        where TStore : class, IAppSurfaceLocalSecretStore
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IAppSurfaceLocalSecretStore>();
        services.AddSingleton<IAppSurfaceLocalSecretStore, TStore>();
        return services;
    }

    /// <summary>
    /// Replaces the local secret store with a specific instance.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="store">The store instance.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection UseAppSurfaceLocalSecretStore(
        this IServiceCollection services,
        IAppSurfaceLocalSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(store);

        services.RemoveAll<IAppSurfaceLocalSecretStore>();
        services.AddSingleton(store);
        return services;
    }
}
