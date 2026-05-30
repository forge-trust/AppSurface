using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// AppSurface Console root module for the release cockpit.
/// </summary>
internal sealed class ReleaseCliModule : IAppSurfaceHostModule
{
    /// <inheritdoc />
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton(new ReleaseExecutionContext(Directory.GetCurrentDirectory()));
        services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        services.AddSingleton<IReleaseClock, SystemReleaseClock>();
    }

    /// <inheritdoc />
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <inheritdoc />
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}

/// <summary>
/// Per-invocation execution context supplied by the CLI entry point.
/// </summary>
/// <param name="CurrentDirectory">Directory used to resolve default repository-relative paths.</param>
internal sealed record ReleaseExecutionContext(string CurrentDirectory);
