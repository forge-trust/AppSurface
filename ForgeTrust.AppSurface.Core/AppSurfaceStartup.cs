using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Provides a base implementation for starting and running applications with module support.
/// </summary>
public abstract class AppSurfaceStartup
{
    /// <summary>
    /// A lazy-initialized logger factory used during the early stages of application startup.
    /// </summary>
    protected static readonly Lazy<ILoggerFactory> StartupLoggerFactory =
        new(() => LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug)));

    /// <summary>
    /// Provides an <see cref="ILogger"/> named after the concrete startup type.
    /// </summary>
    /// <returns>An <see cref="ILogger"/> whose category name is the concrete startup type's name.</returns>
    protected ILogger GetStartupLogger()
    {
        return StartupLoggerFactory.Value.CreateLogger(GetType().Name);
    }
}

/// <summary>
/// An abstract base class for initializing a AppSurface application with a specific root module.
/// </summary>
/// <typeparam name="TRootModule">The type of the root module for the application.</typeparam>
public abstract class AppSurfaceStartup<TRootModule> : AppSurfaceStartup, IAppSurfaceStartup
    where TRootModule : IAppSurfaceHostModule, new()
{
    /// <summary>
    /// Runs the startup sequence using the provided startup context.
    /// </summary>
    /// <param name="context">Startup configuration including application arguments, root module, and dependency/module registrations.</param>
    /// <returns>A task that completes when the startup sequence finishes.</returns>
    async Task IAppSurfaceStartup.RunAsync(StartupContext context)
    {
        await RunAsync(context);
    }

    /// <summary>
    /// Creates an IHostBuilder configured for the provided startup context.
    /// </summary>
    /// <param name="context">The startup context whose application name, modules, and registrations drive host configuration.</param>
    /// <returns>An <see cref="IHostBuilder"/> configured according to the given <paramref name="context"/>.</returns>
    IHostBuilder IAppSurfaceStartup.CreateHostBuilder(StartupContext context) => CreateHostBuilderCore(context);

    /// <summary>
    /// Starts the application's run sequence using the specified command-line arguments.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the application.</param>
    /// <returns>A task that completes when the host run finishes.</returns>
    public Task RunAsync(string[] args) => RunAsync(new StartupContext(args, CreateRootModule()));

    /// <summary>
    /// Runs the host configured by the provided startup context and logs lifecycle events according to the configured console output mode.
    /// </summary>
    /// <param name="context">Context containing application name, root module, dependencies, and any custom registrations used to build and configure the host.</param>
    /// <returns>A task that completes when the host run has finished and shutdown processing is complete.</returns>
    /// <remarks>
    /// By default this method emits an informational shutdown log via <c>logger.LogInformation("Run Exited - Shutting down")</c> after <c>host.RunAsync()</c> completes.
    /// When <see cref="StartupContext.ConsoleOutputMode"/> is <see cref="ConsoleOutputMode.CommandFirst"/>, that shutdown log is suppressed so command-first console flows can keep help and validation output free of lifecycle noise.
    /// Logs a warning if shutdown is cancelled or does not complete in time. On an unhandled exception logs a critical error and sets <c>Environment.ExitCode</c> to <c>-100</c>.
    /// </remarks>
    public async Task RunAsync(StartupContext context)
    {
        try
        {
            var host = CreateHost(context);
            var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(GetType().Name);

            await host.RunAsync();

            if (context.ConsoleOutputMode != ConsoleOutputMode.CommandFirst)
            {
                logger.LogInformation("Run Exited - Shutting down");
            }
        }
        catch (OperationCanceledException ex)
        {
            GetStartupLogger().LogWarning(ex, "Service(s) did not exit in a timely fashion.");
        }
        catch (Exception e)
        {
            GetStartupLogger().LogCritical(e, "Fatal Processing Error");
            Environment.ExitCode = -100;
        }
    }

    /// <summary>
    /// Builds an <see cref="IHost"/> from the host builder configured for the provided startup context.
    /// </summary>
    /// <param name="context">The startup context that provides application name, modules, and configuration used to construct the host.</param>
    /// <returns>The constructed <see cref="IHost"/>.</returns>
    private IHost CreateHost(StartupContext context) => ((IAppSurfaceStartup)this).CreateHostBuilder(context).Build();

    /// <summary>
    /// Create and configure a host builder using values from the provided startup context.
    /// </summary>
    /// <param name="context">Startup context containing the application label, host application identity, root module, and dependencies used to configure the host builder.</param>
    /// <returns>A host builder configured with the context's host application identity, registered modules, and service registrations.</returns>
    private IHostBuilder CreateHostBuilderCore(StartupContext context)
    {
        var builder = Host.CreateDefaultBuilder(context.Args);

        // Support --port flag as a shortcut for --urls (e.g. --port 5001).
        // We parse once here and reuse in both Host and App configuration stages.
        var argConfig = new ConfigurationBuilder().AddCommandLine(context.Args).Build();
        var portOverlay = !string.IsNullOrEmpty(argConfig["port"])
            ? new Dictionary<string, string?>
            {
                ["urls"] = $"http://localhost:{argConfig["port"]};http://*:{argConfig["port"]}"
            }
            : null;

        // Keep Generic Host identity assembly-backed so ASP.NET static web assets can resolve runtime manifests.
        // The context's ApplicationName remains available as a caller-controlled display label.
        builder.ConfigureAppConfiguration((hostingContext, config) =>
        {
            hostingContext.HostingEnvironment.ApplicationName = context.HostApplicationName;

            if (portOverlay != null)
            {
                config.AddInMemoryCollection(portOverlay);
            }
        });

        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(
                new Dictionary<string, string?> { [HostDefaults.ApplicationKey] = context.HostApplicationName });

            if (portOverlay != null)
            {
                config.AddInMemoryCollection(portOverlay);
            }
        });

        // Ensure internal services (like Default IEnvironmentProvider) are included first so external modules can override them.
        context.Dependencies.AddModule<Defaults.InternalServicesModule>();
        context.RootModule.RegisterDependentModules(context.Dependencies);

        ConfigureHostBeforeServicesCore(context, builder);

        builder.ConfigureServices(services =>
            ConfigureServicesInternal(context, services));

        ConfigureHostAfterServicesCore(context, builder);

        ConfigureBuilderForAppType(context, builder);

        return builder;
    }

    /// <summary>
    /// Creates a new instance of the root module.
    /// </summary>
    /// <returns>A new <typeparamref name="TRootModule"/> instance.</returns>
    protected virtual TRootModule CreateRootModule() => new();

    private void ConfigureHostBeforeServicesCore(
        StartupContext context,
        IHostBuilder builder)
    {
        foreach (var dep in context.Dependencies.Modules)
        {
            if (dep is IAppSurfaceHostModule host)
            {
                host.ConfigureHostBeforeServices(context, builder);
            }
        }

        context.RootModule.ConfigureHostBeforeServices(context, builder);
    }

    private void ConfigureHostAfterServicesCore(
        StartupContext context,
        IHostBuilder builder)
    {
        foreach (var dep in context.Dependencies.Modules)
        {
            if (dep is IAppSurfaceHostModule host)
            {
                host.ConfigureHostAfterServices(context, builder);
            }
        }

        context.RootModule.ConfigureHostAfterServices(context, builder);
    }

    private void ConfigureServicesInternal(
        StartupContext context,
        IServiceCollection services)
    {
        // 1) Now register services for the specific app type (e.g., Web) which may use context.IsDevelopment.
        ConfigureServicesForAppType(context, services);

        // 2) Let modules register their services next so they can override defaults.
        ConfigureServicesFromModule(context, services);

        // 3) Finally, allow custom registrations from startup to override anything else if needed.
        context.CustomRegistrations.ForEach(cr => cr(services));
    }

    private void ConfigureServicesFromModule(
        StartupContext context,
        IServiceCollection services)
    {
        foreach (var dep in context.Dependencies.Modules)
        {
            dep.ConfigureServices(context, services);
        }

        context.RootModule.ConfigureServices(context, services);
    }

    /// <summary>
    /// Registers services required for the specific application type (for example, web or worker) into the provided service collection.
    /// </summary>
    /// <param name="context">Startup context containing application metadata, dependencies, and configuration used during registration.</param>
    /// <param name="services">The service collection to which app-type-specific services should be added or overridden.</param>
    protected abstract void ConfigureServicesForAppType(StartupContext context, IServiceCollection services);

    /// <summary>
    /// Allows the startup to customize the host builder for the specific application type.
    /// </summary>
    /// <param name="context">The startup context containing application metadata and dependency modules.</param>
    /// <param name="builder">The host builder to customize.</param>
    /// <returns>The configured <see cref="IHostBuilder"/> (by default returns the provided builder unchanged).</returns>
    protected virtual IHostBuilder ConfigureBuilderForAppType(StartupContext context, IHostBuilder builder) => builder;
}
