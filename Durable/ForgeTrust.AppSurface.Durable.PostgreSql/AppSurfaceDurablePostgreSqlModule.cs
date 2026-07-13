using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Durable.PostgreSql;

/// <summary>
/// Declares the host-neutral durable module dependency for applications that configure PostgreSQL storage explicitly.
/// </summary>
/// <remarks>
/// The module does not guess a connection string, create an <c>NpgsqlDataSource</c>, apply schema changes, or start a
/// worker. Call <see cref="AppSurfaceDurablePostgreSqlServiceCollectionExtensions.AddAppSurfaceDurablePostgreSql"/> in
/// application service configuration and opt into hosted execution only where continuous liveness is guaranteed.
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
