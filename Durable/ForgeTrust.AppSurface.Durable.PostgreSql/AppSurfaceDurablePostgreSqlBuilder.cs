using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Continues PostgreSQL durable registration while keeping storage and background execution separate.
/// </summary>
public sealed class AppSurfaceDurablePostgreSqlBuilder
{
    internal AppSurfaceDurablePostgreSqlBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Gets the application service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Explicitly adds the critical hosted loop that continuously calls the same bounded runtime pump exposed for
    /// external activation and tests.
    /// </summary>
    /// <remarks>
    /// Storage registration is passive by default. Add hosted execution only to a continuously live worker host. A
    /// scale-to-zero deployment must arrange an external activator that calls <see cref="IDurableRuntimePump"/> instead.
    /// </remarks>
    /// <returns>This builder.</returns>
    public AppSurfaceDurablePostgreSqlBuilder AddWorkerHost()
    {
        Services.AddAppSurfaceDurableWorkerHost();
        return this;
    }
}
