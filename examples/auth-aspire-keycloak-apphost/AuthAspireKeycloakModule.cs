using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthAspireKeycloakAppHost;

/// <summary>
/// AppSurface module for the focused Auth Aspire Keycloak proof AppHost.
/// </summary>
public sealed class AuthAspireKeycloakModule : IAppSurfaceHostModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
