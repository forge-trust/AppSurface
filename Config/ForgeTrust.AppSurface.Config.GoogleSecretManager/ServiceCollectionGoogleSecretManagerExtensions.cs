using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>
/// Provides service collection helpers for AppSurface Google Secret Manager.
/// </summary>
public static class ServiceCollectionGoogleSecretManagerExtensions
{
    /// <summary>
    /// Configures Google Secret Manager options.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">The options callback.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection ConfigureAppSurfaceGoogleSecretManager(
        this IServiceCollection services,
        Action<AppSurfaceGoogleSecretManagerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services;
    }

    /// <summary>
    /// Replaces the Google Secret Manager client seam.
    /// </summary>
    /// <typeparam name="TClient">The client implementation type.</typeparam>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection UseAppSurfaceGoogleSecretManagerClient<TClient>(this IServiceCollection services)
        where TClient : class, IAppSurfaceGoogleSecretManagerClient
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IAppSurfaceGoogleSecretManagerClient>();
        services.AddSingleton<IAppSurfaceGoogleSecretManagerClient, TClient>();
        return services;
    }

    /// <summary>
    /// Replaces the Google Secret Manager client seam with a specific instance.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="client">The client instance.</param>
    /// <returns>The original service collection.</returns>
    public static IServiceCollection UseAppSurfaceGoogleSecretManagerClient(
        this IServiceCollection services,
        IAppSurfaceGoogleSecretManagerClient client)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(client);

        services.RemoveAll<IAppSurfaceGoogleSecretManagerClient>();
        services.AddSingleton(client);
        return services;
    }
}
