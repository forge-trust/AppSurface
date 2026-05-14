namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Provides a static entry point for starting a web application with a custom startup class and root module.
/// </summary>
/// <typeparam name="TStartup">The type of the custom startup class, inheriting from <see cref="WebStartup{TModule}"/>.</typeparam>
/// <typeparam name="TModule">The type of the root web module.</typeparam>
public static class WebApp<TStartup, TModule>
    where TStartup : WebStartup<TModule>, new()
    where TModule : IAppSurfaceWebModule, new()
{
    /// <summary>
    /// Asynchronously runs the web application using the specified command-line arguments and optional option configuration.
    /// </summary>
    /// <param name="args">The command-line arguments provided at application startup.</param>
    /// <param name="configureOptions">An optional delegate to further customize <see cref="WebOptions"/> during startup.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation of running the web application.</returns>
    public static Task RunAsync(
        string[] args,
        Action<WebOptions>? configureOptions = null) =>
        new TStartup()
            .WithOptions(configureOptions)
            .RunAsync(args);
}

/// <summary>
/// Provides a simplified static entry point for starting a web application using a default startup configuration.
/// </summary>
/// <typeparam name="TModule">The type of the root web module.</typeparam>
public static class WebApp<TModule>
    where TModule : IAppSurfaceWebModule, new()
{
    /// <summary>
    /// Asynchronously runs the web application with a default startup using the specified command-line arguments and optional configuration.
    /// </summary>
    /// <param name="args">The command-line arguments provided at application startup.</param>
    /// <param name="configureOptions">An optional delegate to customize <see cref="WebOptions"/> during startup.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation of running the web application.</returns>
    public static Task RunAsync(
        string[] args,
        Action<WebOptions>? configureOptions = null) =>
        WebApp<GenericWebStartup<TModule>, TModule>.RunAsync(args, configureOptions);

    private class GenericWebStartup<TNew> : WebStartup<TNew>
        where TNew : IAppSurfaceWebModule, new()
    {
    }
}
