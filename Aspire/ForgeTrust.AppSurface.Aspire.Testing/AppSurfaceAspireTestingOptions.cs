using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Supplies synchronous, per-call customization before typed profile activation and graph composition.
/// </summary>
/// <remarks>
/// All delegates default to null and are snapshotted when CreateConfiguredAsync is called. Each runs once when its
/// phase is reached; later delegates do not run after failure or cancellation. Capture immutable scenario values.
/// Mutable captures, thread safety and objects created inside a callback remain the caller's responsibility. Do not
/// use async-void delegates, build a separate service provider, run the AppHost entry point or start resources here.
/// These hooks do not replace the module's parameterless constructor or the profile's command-line binding.
/// </remarks>
public sealed class AppSurfaceAspireTestingOptions
{
    /// <summary>
    /// Gets the application-configuration callback applied after module callbacks and before activation services.
    /// </summary>
    /// <remarks>
    /// Defaults to null. Add a per-call overlay here when profile or component constructors consume IConfiguration.
    /// The callback receives the activation configuration builder, not Aspire's ConfigurationManager. Later sources
    /// have normal configuration precedence. Configuration built explicitly by the callback is caller-owned.
    /// </remarks>
    public Action<IConfigurationBuilder>? ConfigureActivationConfiguration { get; init; }

    /// <summary>
    /// Gets the service-collection callback applied after module and framework registrations and before provider creation.
    /// </summary>
    /// <remarks>
    /// Defaults to null. Use this hook to inject immutable scenario services or replace constructor dependencies.
    /// The selected service-provider factory is retained. Later ConfigureContainer registrations, including Autofac
    /// modules, still follow that container's precedence; this hook is not a universal final override. Singleton
    /// instances supplied by the caller retain normal container ownership policy. Aspire builder services belong to
    /// a different provider and cannot affect profile activation.
    /// </remarks>
    public Action<IServiceCollection>? ConfigureActivationServices { get; init; }

    /// <summary>
    /// Gets the Aspire-builder callback applied after profile activation and before composition on the same guarded builder.
    /// </summary>
    /// <remarks>
    /// Defaults to null. Configuration, services, resources and resource-builder creation are available during this
    /// callback and composition. Build, BuildAsync, Dispose and DisposeAsync throw InvalidOperationException while
    /// factory preparation is in progress. Catching that rejection permits preparation to continue. An escaping
    /// exception aborts the factory and invalidates captured builder references. Use the returned builder for its
    /// single explicit build after CreateConfiguredAsync completes; no resource is started by this factory.
    /// </remarks>
    public Action<IDistributedApplicationBuilder>? ConfigureAspireBuilder { get; init; }
}
