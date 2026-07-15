using System.Diagnostics.CodeAnalysis;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Web;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.ConsumerFixture;

/// <summary>
/// Creates the real ASP.NET Core consumer host used to verify AppSurface Docs Razor Class Library precedence.
/// </summary>
/// <remarks>
/// The fixture deliberately owns conventional <c>Views/_ViewStart.cshtml</c> and
/// <c>Views/Shared/_Layout.cshtml</c> files. AppSurface Docs views must retain their package shell when this host is
/// the application, while hosts remain free to deliberately override the package-specific layout path.
/// </remarks>
public static class AppSurfaceDocsConsumerFixtureHost
{
    /// <summary>
    /// Runs the consumer fixture until the host shuts down.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to AppSurface Web startup.</param>
    /// <returns>A task that completes when the host exits.</returns>
    [ExcludeFromCodeCoverage(
        Justification = "Process lifetime wrapper delegates to the covered host-builder seam and runs until shutdown.")]
    public static Task RunAsync(string[] args) => new ConsumerFixtureStartup().RunAsync(args);

    /// <summary>
    /// Creates a consumer host builder without starting it.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to AppSurface Web startup.</param>
    /// <param name="environmentProvider">
    /// Optional environment provider used by AppSurface startup decisions before the generic host is built.
    /// </param>
    /// <returns>A configured host builder whose application identity is the consumer fixture assembly.</returns>
    public static IHostBuilder CreateBuilder(
        string[] args,
        IEnvironmentProvider? environmentProvider = null)
    {
        var context = new StartupContext(
            args,
            new ConsumerFixtureModule(),
            EnvironmentProvider: environmentProvider)
        {
            OverrideEntryPointAssembly = typeof(AppSurfaceDocsConsumerFixtureHost).Assembly
        };

        return ((IAppSurfaceStartup)new ConsumerFixtureStartup()).CreateHostBuilder(context);
    }

    private sealed class ConsumerFixtureStartup : WebStartup<ConsumerFixtureModule>
    {
    }

    private sealed class ConsumerFixtureModule : IAppSurfaceWebModule
    {
        public bool IncludeAsApplicationPart => true;

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
