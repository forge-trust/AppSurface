using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Standalone;

/// <summary>
/// Creates and runs the standalone AppSurface Docs host used by the executable wrapper and integration tests.
/// </summary>
/// <remarks>
/// Use this type when code needs the same host shape as <c>Program.cs</c> without shelling out to
/// <c>dotnet run</c>. The builder keeps the AppSurface Docs root module, routes, static web assets, and
/// configuration binding on the normal AppSurface Web startup path.
/// <c>CreateBuilder</c> is a low-level host-builder seam; callers that start the host themselves should
/// pass an explicit endpoint or configure the web host before building.
/// </remarks>
public static class AppSurfaceDocsStandaloneHost
{
    /// <summary>
    /// Runs the standalone AppSurface Docs web application until the host shuts down.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the AppSurface Web startup pipeline.</param>
    /// <returns>A task that completes when the host exits.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Process lifetime wrapper delegates to the covered host-builder seam and runs until host shutdown.")]
    public static Task RunAsync(string[] args)
    {
        return RunAsync(args, configureOptions: null);
    }

    /// <summary>
    /// Runs the standalone AppSurface Docs web application with optional host web-option customization.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the AppSurface Web startup pipeline.</param>
    /// <param name="configureOptions">
    /// Optional web-options callback applied after module defaults and standalone browser-status-page defaults, before
    /// the host is built. Package-hosted command-line tools use this seam to disable static-web-asset manifest loading
    /// when their required assets are embedded in assemblies instead.
    /// </param>
    /// <returns>A task that completes when the host exits.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Process lifetime wrapper delegates to the covered host-builder seam and runs until host shutdown.")]
    public static Task RunAsync(string[] args, Action<WebOptions>? configureOptions)
    {
        return new AppSurfaceDocsStandaloneStartup()
            .WithOptions(CreateStandaloneWebOptionsCallback(configureOptions))
            .RunAsync(args);
    }

    /// <summary>
    /// Creates a configured host builder for the standalone AppSurface Docs application without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the Generic Host and AppSurface Docs configuration binder.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by AppSurface startup decisions before the Generic Host has been built.
    /// Leave unset for normal executable startup; tests can pass a fixed provider to avoid process-wide environment
    /// variable mutation.
    /// </param>
    /// <remarks>
    /// The builder pins the standalone assembly as the entry point identity so in-process test hosts still resolve the
    /// same static web asset manifest that the executable resolves. Without that override, test runners would use their
    /// own process entry assembly and miss AppSurface Docs assets. This overload is kept for source and binary compatibility;
    /// use the three-parameter overload when a packaged tool needs to customize AppSurface Web options.
    /// </remarks>
    /// <returns>An <see cref="IHostBuilder"/> for the standalone AppSurface Docs application.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider = null)
    {
        return CreateBuilder(args, environmentProvider, configureOptions: null);
    }

    /// <summary>
    /// Creates a configured host builder for the standalone AppSurface Docs application without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the Generic Host and AppSurface Docs configuration binder.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by AppSurface startup decisions before the Generic Host has been built.
    /// Leave unset for normal executable startup; tests can pass a fixed provider to avoid process-wide environment
    /// variable mutation.
    /// </param>
    /// <param name="configureOptions">
    /// Optional web-options callback applied after module defaults and standalone browser-status-page defaults, before
    /// the host is built.
    /// </param>
    /// <remarks>
    /// This overload preserves the original two-parameter <c>CreateBuilder</c> method for already-compiled callers while
    /// giving packaged tools a host-shape seam for static-web-asset and startup-watchdog options.
    /// </remarks>
    /// <returns>An <see cref="IHostBuilder"/> for the standalone AppSurface Docs application.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider,
        Action<WebOptions>? configureOptions = null)
    {
        var context = new StartupContext(
            args,
            new AppSurfaceDocsStandaloneWebModule(),
            EnvironmentProvider: environmentProvider)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceDocsStandaloneHost).Assembly
        };

        return ((IAppSurfaceStartup)new AppSurfaceDocsStandaloneStartup().WithOptions(
                CreateStandaloneWebOptionsCallback(configureOptions)))
            .CreateHostBuilder(context);
    }

    private static Action<WebOptions> CreateStandaloneWebOptionsCallback(Action<WebOptions>? configureOptions)
    {
        return options =>
        {
            options.Errors.UseConventionalBrowserStatusPages();
            configureOptions?.Invoke(options);
        };
    }

    private sealed class AppSurfaceDocsStandaloneStartup : WebStartup<AppSurfaceDocsStandaloneWebModule>
    {
    }

    private sealed class AppSurfaceDocsStandaloneWebModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => true;

        public void ConfigureWebOptions(StartupContext context, WebOptions options)
        {
        }

        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
            builder.AddModule<AppSurfaceDocsWebModule>();
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }
}
