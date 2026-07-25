using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Registers application-owned named canary evaluators and immutable metadata.
/// </summary>
public static class AppSurfaceCanaryServiceCollectionExtensions
{
    /// <summary>
    /// Registers one typed evaluator under an exact lowercase, dot-separated canary name.
    /// </summary>
    /// <typeparam name="TEvaluator">The concrete application-owned evaluator type.</typeparam>
    /// <param name="services">The service collection to update after validation succeeds.</param>
    /// <param name="name">The unique 1-128 character canary name.</param>
    /// <param name="configure">
    /// An optional callback that configures registration metadata, required inputs, and allowed result-detail keys.
    /// </param>
    /// <returns>The original service collection.</returns>
    /// <remarks>
    /// Registration does not expose an HTTP endpoint. Call
    /// <see cref="AppSurfaceCanaryEndpointRouteBuilderExtensions.MapAppSurfaceCanaries"/> explicitly with a host-owned
    /// authorization policy. The evaluator is added as transient only when the host has not already registered the
    /// concrete type, so singleton, scoped, and transient overrides are supported.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name, configured registration metadata, or allowed detail-key declaration is invalid.
    /// </exception>
    public static IServiceCollection AddAppSurfaceCanary<TEvaluator>(
        this IServiceCollection services,
        string name,
        Action<AppSurfaceCanaryRegistrationOptions>? configure = null)
        where TEvaluator : class, IAppSurfaceCanaryEvaluator
    {
        ArgumentNullException.ThrowIfNull(services);
        AppSurfaceCanaryValidation.ValidateName(name);

        var options = new AppSurfaceCanaryRegistrationOptions(name);
        configure?.Invoke(options);
        var descriptor = AppSurfaceCanaryValidation.CreateDescriptor(name, typeof(TEvaluator), options);

        services.AddSingleton(descriptor);
        services.TryAddTransient<TEvaluator>();
        services.TryAddSingleton(serviceProvider =>
            new AppSurfaceCanaryRegistry(serviceProvider.GetServices<AppSurfaceCanaryDescriptor>()));
        services.TryAddScoped(serviceProvider =>
            new AppSurfaceCanaryEvaluationRunner(
                serviceProvider.GetRequiredService<AppSurfaceCanaryRegistry>(),
                serviceProvider));
        services.TryAddSingleton(_ => new AppSurfaceCanaryMappingState());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, AppSurfaceCanaryStartupValidator>());

        return services;
    }
}
