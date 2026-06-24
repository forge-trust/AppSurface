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

internal sealed class WebPwaInstallModule : IAppSurfaceWebModule
{
    public bool IncludeAsApplicationPart => true;

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
    }

    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
    {
    }

    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
    {
    }
}
