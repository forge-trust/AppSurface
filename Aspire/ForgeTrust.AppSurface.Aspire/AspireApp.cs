using System.Reflection;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Entry point for running an Aspire application with no specific root module.
/// </summary>
public static class AspireApp
{
    /// <summary>
    /// Runs the Aspire application asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A task representing the run operation.</returns>
    public static Task RunAsync(string[] args)
    {
        var startupContext = new StartupContext(args, new NoHostModule())
        {
            OverrideEntryPointAssembly = Assembly.GetCallingAssembly()
        };

        return new AspireAppStartup<NoHostModule>().RunAsync(startupContext);
    }
}

/// <summary>
/// Entry point for running an Aspire application with a specific root module.
/// </summary>
/// <typeparam name="TModule">The type of the root module.</typeparam>
public static class AspireApp<TModule>
    where TModule : IAppSurfaceHostModule, new()
{
    /// <summary>
    /// Runs the Aspire application asynchronously with the specified root module.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A task representing the run operation.</returns>
    public static Task RunAsync(string[] args) =>
        new AspireAppStartup<TModule>()
            .RunAsync(args);
}
