using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>Declares the host-neutral durable module dependency for applications configuring PostgreSQL explicitly.</summary>
/// <remarks>
/// This module does not create data sources, choose database credentials, apply migrations, or start a worker. Call
/// <see cref="AppSurfaceDurablePostgreSqlServiceCollectionExtensions.AddAppSurfaceDurablePostgreSql"/> with the
/// application's reviewed dispatcher and runtime data sources, then opt into <c>AddWorkerHost</c> only where
/// continuous activation is intended.
/// </remarks>
public sealed class AppSurfaceDurablePostgreSqlModule : IAppSurfaceModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddModule<AppSurfaceDurableModule>();
    }
}
