using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Standalone;

/// <summary>
/// Creates and runs the standalone RazorDocs host used by the executable wrapper and integration tests.
/// </summary>
/// <remarks>
/// Use this type when code needs the same host shape as <c>Program.cs</c> without shelling out to
/// <c>dotnet run</c>. The builder keeps the RazorDocs root module, routes, static web assets, and
/// configuration binding on the normal AppSurface Web startup path.
/// <c>CreateBuilder</c> is a low-level host-builder seam; callers that start the host themselves should
/// pass an explicit endpoint or configure the web host before building.
/// </remarks>
public static class RazorDocsStandaloneHost
{
    /// <summary>
    /// Runs the standalone RazorDocs web application until the host shuts down.
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
    /// Runs the standalone RazorDocs web application with optional host web-option customization.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the AppSurface Web startup pipeline.</param>
    /// <param name="configureOptions">
    /// Optional web-options callback applied after module defaults, before the host is built. Package-hosted command-line
    /// tools use this seam to disable static-web-asset manifest loading when their required assets are embedded in
    /// assemblies instead.
    /// </param>
    /// <returns>A task that completes when the host exits.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Process lifetime wrapper delegates to the covered host-builder seam and runs until host shutdown.")]
    public static Task RunAsync(string[] args, Action<WebOptions>? configureOptions)
    {
        return new RazorDocsStandaloneStartup()
            .WithOptions(configureOptions)
            .RunAsync(args);
    }

    /// <summary>
    /// Creates a configured host builder for the standalone RazorDocs application without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the Generic Host and RazorDocs configuration binder.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by AppSurface startup decisions before the Generic Host has been built.
    /// Leave unset for normal executable startup; tests can pass a fixed provider to avoid process-wide environment
    /// variable mutation.
    /// </param>
    /// <remarks>
    /// The builder pins the standalone assembly as the entry point identity so in-process test hosts still resolve the
    /// same static web asset manifest that the executable resolves. Without that override, test runners would use their
    /// own process entry assembly and miss RazorDocs assets. This overload is kept for source and binary compatibility;
    /// use the three-parameter overload when a packaged tool needs to customize AppSurface Web options.
    /// </remarks>
    /// <returns>An <see cref="IHostBuilder"/> for the standalone RazorDocs application.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider = null)
    {
        return CreateBuilder(args, environmentProvider, configureOptions: null);
    }

    /// <summary>
    /// Creates a configured host builder for the standalone RazorDocs application without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the Generic Host and RazorDocs configuration binder.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by AppSurface startup decisions before the Generic Host has been built.
    /// Leave unset for normal executable startup; tests can pass a fixed provider to avoid process-wide environment
    /// variable mutation.
    /// </param>
    /// <param name="configureOptions">
    /// Optional web-options callback applied after module defaults, before the host is built.
    /// </param>
    /// <remarks>
    /// This overload preserves the original two-parameter <c>CreateBuilder</c> method for already-compiled callers while
    /// giving packaged tools a host-shape seam for static-web-asset and startup-watchdog options.
    /// </remarks>
    /// <returns>An <see cref="IHostBuilder"/> for the standalone RazorDocs application.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider,
        Action<WebOptions>? configureOptions = null)
    {
        var context = new StartupContext(
            args,
            new RazorDocsWebModule(),
            EnvironmentProvider: environmentProvider)
        {
            OverrideEntryPointAssembly = typeof(RazorDocsStandaloneHost).Assembly
        };

        return ((IAppSurfaceStartup)new RazorDocsStandaloneStartup().WithOptions(configureOptions))
            .CreateHostBuilder(context);
    }

    private sealed class RazorDocsStandaloneStartup : WebStartup<RazorDocsWebModule>
    {
    }
}
