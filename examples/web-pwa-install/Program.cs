using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web;

await WebApp<WebPwaInstallModule>.RunAsync(
    args,
    options =>
    {
        // docs:snippet web-pwa-options:start
        options.StartupTimeout = TimeSpan.FromSeconds(60);
        options.Mvc = options.Mvc with { MvcSupportLevel = MvcSupport.ControllersWithViews };
        options.Pwa.Enabled = true;
        options.Pwa.Name = "AppSurface PWA Field Notes";
        options.Pwa.ShortName = "Field Notes";
        options.Pwa.ThemeColor = "#2563eb";
        options.Pwa.BackgroundColor = "#ffffff";
        options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-192.svg", Sizes = "192x192", Type = "image/svg+xml" });
        options.Pwa.Icons.Add(new PwaIcon { Source = "/icons/app-512.svg", Sizes = "512x512", Type = "image/svg+xml" });

        options.Pwa.Offline.Enabled = true;
        options.Pwa.Offline.OfflineFallbackPath = "/offline.html";
        options.Pwa.Offline.StaticAssetPaths = ["/icons/app-192.svg", "/icons/app-512.svg", "/offline.html"];
        options.Pwa.Push.Enabled = true;
        // docs:snippet web-pwa-options:end

        options.MapEndpoints = endpoints =>
        {
            endpoints.MapGet(
                "/offline.html",
                () => Results.Content(
                    "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Offline</title></head><body><main><h1>Offline</h1><p>The AppSurface PWA proof is installed, but the live app is unavailable.</p></main></body></html>",
                    "text/html"));
        };
    });

/// <summary>
/// Sample module that exposes the MVC assets required by the PWA install example.
/// </summary>
/// <remarks>
/// Keep <see cref="IncludeAsApplicationPart"/> enabled so the sample controller and views are discoverable
/// without additional application-part wiring in the host.
/// </remarks>
internal sealed class WebPwaInstallModule : IAppSurfaceWebModule
{
    /// <summary>
    /// Gets a value indicating whether AppSurface should register this sample assembly as an MVC application part.
    /// </summary>
    public bool IncludeAsApplicationPart => true;

    /// <summary>
    /// Registers services for the sample module.
    /// </summary>
    /// <remarks>
    /// The example does not need module-owned services; PWA options and offline fallback endpoints are configured
    /// from the host delegate above.
    /// </remarks>
    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Registers AppSurface module dependencies required by this sample.
    /// </summary>
    /// <remarks>
    /// No additional dependencies are required because the sample starts from <see cref="ForgeTrust.AppSurface.Web.WebApp{TModule}"/>.
    /// </remarks>
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host before AppSurface builds the service collection.
    /// </summary>
    /// <remarks>
    /// This hook is intentionally empty; host-level PWA settings are configured in the startup options delegate.
    /// </remarks>
    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    /// <summary>
    /// Configures the host after AppSurface builds the service collection.
    /// </summary>
    /// <remarks>
    /// This hook is intentionally empty because the sample does not need post-service host customization.
    /// </remarks>
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
