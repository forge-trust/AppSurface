using System.Diagnostics.CodeAnalysis;
using ForgeTrust.Runnable.Core;
using ForgeTrust.Runnable.Web;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.Standalone;

/// <summary>
/// Creates and runs the standalone RazorDocs host used by the executable wrapper and integration tests.
/// </summary>
/// <remarks>
/// Use this type when code needs the same host shape as <c>Program.cs</c> without shelling out to
/// <c>dotnet run</c>. The builder keeps the RazorDocs root module, routes, static web assets, and
/// configuration binding on the normal Runnable Web startup path.
/// <c>CreateBuilder</c> is a low-level host-builder seam; callers that start the host themselves should
/// pass an explicit endpoint or configure the web host before building.
/// </remarks>
public static class RazorDocsStandaloneHost
{
    /// <summary>
    /// Runs the standalone RazorDocs web application until the host shuts down.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the Runnable Web startup pipeline.</param>
    /// <returns>A task that completes when the host exits.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Process lifetime wrapper delegates to the covered host-builder seam and runs until host shutdown.")]
    public static Task RunAsync(string[] args)
    {
        return new RazorDocsStandaloneStartup()
            .RunAsync(args);
    }

    /// <summary>
    /// Creates a configured host builder for the standalone RazorDocs application without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the Generic Host and RazorDocs configuration binder.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by Runnable startup decisions before the Generic Host has been built.
    /// Leave unset for normal executable startup; tests can pass a fixed provider to avoid process-wide environment
    /// variable mutation.
    /// </param>
    /// <remarks>
    /// The builder pins the standalone assembly as the entry point identity so in-process test hosts still resolve the
    /// same static web asset manifest that the executable resolves. Without that override, test runners would use their
    /// own process entry assembly and miss RazorDocs assets.
    /// </remarks>
    /// <returns>An <see cref="IHostBuilder"/> for the standalone RazorDocs application.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider = null)
    {
        var context = new StartupContext(
            args,
            new RazorDocsWebModule(),
            EnvironmentProvider: environmentProvider)
        {
            OverrideEntryPointAssembly = typeof(RazorDocsStandaloneHost).Assembly
        };

        return ((IRunnableStartup)new RazorDocsStandaloneStartup())
            .CreateHostBuilder(context);
    }

    private sealed class RazorDocsStandaloneStartup : WebStartup<RazorDocsWebModule>
    {
    }
}
