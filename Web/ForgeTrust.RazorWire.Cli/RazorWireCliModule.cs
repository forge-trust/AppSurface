using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// A terminal/CLI module for RazorWire providing static site export capabilities.
/// </summary>
public class RazorWireCliModule : IAppSurfaceHostModule
{
    /// <summary>
    /// Configures services needed for the CLI, including the export pipeline and command-owned logging defaults.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection to populate.</param>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.AddSingleton<ExportEngine>();
        services.AddSingleton<ExportSourceRequestFactory>();
        services.AddSingleton<ExportSourceResolver>();
        services.AddSingleton<ITargetAppProcessFactory, TargetAppProcessFactory>();
        services.AddHttpClient("ExportEngine", client => { client.Timeout = TimeSpan.FromSeconds(60); });

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
    }

    /// <summary>
    /// Executes pre-service host configuration; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Executes post-service host configuration; currently no implementation is required.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="builder">The host builder.</param>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Registers dependencies for this module; currently no implementation is required.
    /// </summary>
    /// <param name="builder">The module dependency builder.</param>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }
}
