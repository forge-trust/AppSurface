using ForgeTrust.AppSurface.Durable.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Continues PostgreSQL durable registration while keeping storage and continuous activation separate.</summary>
public sealed class AppSurfaceDurablePostgreSqlBuilder
{
    internal AppSurfaceDurablePostgreSqlBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Gets the application service collection under configuration.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Adds the single critical host adapter that continuously invokes the bounded runtime pump.</summary>
    /// <remarks>
    /// Use this only in a continuously live worker process. Query-only hosts, migration tools, tests, and
    /// scale-to-zero deployments keep storage registration passive and may invoke <see cref="IDurableRuntimePump"/>
    /// from their own activator instead.
    /// </remarks>
    public AppSurfaceDurablePostgreSqlBuilder AddWorkerHost()
    {
        Services.AddAppSurfaceDurableWorkerHost();
        return this;
    }
}
