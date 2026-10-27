using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Explicit singleton registration for the optional Google Cloud Tasks transport.</summary>
/// <remarks>Register a single singleton token source before activation. See <see href="https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#registration"/>.</remarks>
public static class CloudTasksServiceCollectionExtensions
{
    /// <summary>Registers one reusable activation client and its container-owned SDK channel without I/O.</summary>
    /// <param name="services">Host services.</param>
    /// <param name="options">Explicit overall token-plus-submission timeout.</param>
    /// <returns>The supplied collection.</returns>
    /// <remarks>A missing source remains passive until explicit client resolution. Duplicate/non-singleton sources and repeated registration are rejected atomically. Externally supplied singleton instances stay application-owned.</remarks>
    public static IServiceCollection AddCloudTasksActivation(this IServiceCollection services, CloudTasksActivationClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (options is null) throw CloudTasksValidation.Required(nameof(options), "explicit timeout options");
        if (services.Any(static d => d.ServiceType == typeof(ICloudTasksActivationClient)) || !ValidSources(services, allowNone: true)) throw Conflict();
        var sourceDescriptor = services.SingleOrDefault(static descriptor => descriptor.ServiceType == typeof(ICloudTasksAccessTokenSource));
        services.AddSingleton(static _ => new CloudTasksSdkTransport());
        services.AddSingleton<ICloudTasksActivationClient>(provider =>
        {
            // Recheck the descriptor shape to catch unsupported additions after opt-in before any factory/I/O.
            if (sourceDescriptor is null || !ValidSources(services, allowNone: false)
                || !ReferenceEquals(sourceDescriptor, services.Single(static descriptor => descriptor.ServiceType == typeof(ICloudTasksAccessTokenSource)))) throw Conflict();
            var source = provider.GetRequiredService<ICloudTasksAccessTokenSource>();
            return new CloudTasksActivationClient(source, options, provider.GetRequiredService<CloudTasksSdkTransport>(), TimeProvider.System);
        });
        return services;
    }

    /// <summary>Explicitly opts into the fixed Cloud Run metadata OAuth source as a container-owned singleton.</summary>
    /// <param name="services">Host services.</param>
    /// <param name="options">Explicit per-metadata-request timeout.</param>
    /// <returns>The supplied collection.</returns>
    /// <remarks>Call once before activation registration, with no other token-source descriptor present. This call performs no HTTP or credential discovery.</remarks>
    public static IServiceCollection AddCloudRunMetadataAccessTokenSource(this IServiceCollection services, CloudRunMetadataAccessTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (options is null) throw CloudTasksValidation.Required(nameof(options), "explicit timeout options");
        if (services.Any(static d => d.ServiceType == typeof(ICloudTasksActivationClient) || d.ServiceType == typeof(ICloudTasksAccessTokenSource))) throw Conflict();
        services.AddSingleton<ICloudTasksAccessTokenSource>(_ => new CloudRunMetadataAccessTokenSource(options));
        return services;
    }

    private static bool ValidSources(IServiceCollection services, bool allowNone)
    {
        var descriptors = services.Where(static d => d.ServiceType == typeof(ICloudTasksAccessTokenSource)).ToArray();
        return descriptors.Length == 0 ? allowNone : descriptors.Length == 1 && !descriptors[0].IsKeyedService && descriptors[0].Lifetime == ServiceLifetime.Singleton;
    }

    private static InvalidOperationException Conflict() => new("ASCTV004: Register exactly one unkeyed singleton ICloudTasksAccessTokenSource before calling AddCloudTasksActivation once. See https://github.com/forge-trust/AppSurface/blob/main/Durable/cloudtasks-activation.md#asctv004.")
    {
        Data = { ["CloudTasksValidationCode"] = "ASCTV004", ["CloudTasksConstraint"] = "exactly one unkeyed singleton source before one activation registration" }
    };
}
