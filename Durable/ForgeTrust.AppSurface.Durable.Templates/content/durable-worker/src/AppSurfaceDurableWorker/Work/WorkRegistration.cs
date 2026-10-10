using ForgeTrust.AppSurface.Durable;
using Microsoft.Extensions.DependencyInjection;

namespace AppSurfaceDurableWorker.Work;

/// <summary>Registers the single sample Work contract and executor with an application service collection.</summary>
public static class WorkRegistration
{
    /// <summary>Adds the sample definition and its deterministic executor.</summary>
    /// <param name="services">Application service collection to configure.</param>
    /// <returns>The same service collection for further application composition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddSampleWork(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDurableWork(SampleWorkDefinition.Definition.ExecutedBy<SampleWorkExecutor>());
        return services;
    }
}
