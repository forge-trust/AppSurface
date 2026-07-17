using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Registers the optional AppSurface Web Push safe rail.</summary>
public static class AppSurfaceWebPushServiceCollectionExtensions
{
    /// <summary>Registers Web Push with host-owned VAPID keys and exact push-service origins.</summary>
    /// <remarks>
    /// This method maps no route and does not enable the shared PWA worker. The host must separately enable
    /// <c>WebOptions.Pwa.Push.Enabled</c>, register <see cref="IAppSurfaceWebPushSubscriptionCustody"/>, and call one
    /// explicit protected mapping method.
    /// Options are validated during host startup; incomplete keys, non-canonical origins, and mismatched key pairs fail startup.
    /// </remarks>
    /// <param name="services">The host service collection.</param>
    /// <param name="configure">The host-owned key ring and exact-origin configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddAppSurfaceWebPush(
        this IServiceCollection services,
        Action<AppSurfaceWebPushOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAntiforgery();
        services.AddOptions<AppSurfaceWebPushOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AppSurfaceWebPushOptions>, AppSurfaceWebPushOptionsValidator>());
        services.TryAddSingleton<GuardedWebPushTransport>();
        services.TryAddSingleton(provider =>
            new GuardedWebPushAdapter(provider.GetRequiredService<GuardedWebPushTransport>().Handler));
        services.TryAddSingleton<AppSurfaceWebPushRouteRegistry>();
        services.TryAddScoped<IAppSurfaceWebPushSender, AppSurfaceWebPushSender>();
        return services;
    }

    /// <summary>Replaces network transport with a deterministic HTTP 201 proof transport in Development only.</summary>
    /// <param name="services">The host service collection after <see cref="AddAppSurfaceWebPush(IServiceCollection, Action{AppSurfaceWebPushOptions})"/>.</param>
    /// <param name="environment">The current host environment, which must be Development.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// This seam exists only for canonical examples and local integration proofs. It still runs the package sender,
    /// encryption, validation, and response classifier, but it performs no network request and proves no delivery.
    /// Never register it in staging or production.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The environment is not Development.</exception>
    public static IServiceCollection AddAppSurfaceWebPushDevelopmentProofTransport(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("The AppSurface Web Push proof transport is available only in Development.");
        }

        services.Replace(ServiceDescriptor.Singleton(
            new GuardedWebPushAdapter(new DevelopmentProofHandler())));
        return services;
    }

    private sealed class DevelopmentProofHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                RequestMessage = request,
            });
        }
    }
}
