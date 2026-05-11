using System.Reflection;
using System.Runtime.CompilerServices;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Core.Defaults;

namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Entry point for running an Aspire application with no specific root module.
/// </summary>
/// <remarks>
/// Use <see cref="AspireApp.RunAsync(string[])"/> when the AppHost should use the framework-default
/// <see cref="NoHostModule"/> and discover Aspire components from the calling assembly. If the caller has a concrete
/// root module type that should participate in compile-time dependency and configuration registration, prefer
/// <see cref="AspireApp{TModule}.RunAsync(string[])"/> instead.
/// </remarks>
public static class AspireApp
{
    /// <summary>
    /// Runs the Aspire application asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>A task representing the run operation.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
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
/// <remarks>
/// Use <see cref="AspireApp{TModule}.RunAsync(string[])"/> when the root module is known at compile time and should
/// participate in AppSurface dependency discovery, configuration registration, and host hooks. The
/// <c>new()</c> constraint means <typeparamref name="TModule"/> is activated by parameterless constructor rather than
/// dependency injection, so keep constructors cheap, deterministic, and free of external side effects. Use
/// <see cref="AspireApp.RunAsync(string[])"/> when no root module is needed or module selection is driven by
/// framework defaults. The call delegates to <see cref="AspireAppStartup{TModule}"/> for host activation.
/// </remarks>
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
