using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.AppSurface.Web;

internal static class PwaEndpointMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static void Map(IEndpointRouteBuilder endpoints, PwaOptions options, bool isDevelopment)
    {
        if (!options.Enabled)
        {
            return;
        }

        endpoints.MapMethods(
            options.ManifestPath,
            [HttpMethods.Get, HttpMethods.Head],
            httpContext => WriteJsonAsync(httpContext, BuildManifest(httpContext, options), "application/manifest+json"));

        if (ShouldMapDiagnostics(options, isDevelopment))
        {
            endpoints.MapMethods(
                options.DiagnosticsPath,
                [HttpMethods.Get, HttpMethods.Head],
                httpContext => WriteDiagnosticsHtmlAsync(httpContext, options));
            endpoints.MapMethods(
                $"{options.DiagnosticsPath.TrimEnd('/')}/status.json",
                [HttpMethods.Get, HttpMethods.Head],
                httpContext => WriteJsonAsync(httpContext, BuildDiagnostics(httpContext, options), "application/json"));
        }

        if (options.Offline.Enabled)
        {
            endpoints.MapMethods(
                options.Offline.ServiceWorkerPath,
                [HttpMethods.Get, HttpMethods.Head],
                httpContext => WriteServiceWorkerAsync(httpContext, options));
        }
    }

    internal static PwaManifestDocument BuildManifest(HttpContext httpContext, PwaOptions options)
    {
        var pathBase = httpContext.Request.PathBase;
        return new PwaManifestDocument(
            options.Name,
            options.ShortName,
            AddPathBase(pathBase, options.StartUrl),
            AddPathBase(pathBase, options.Scope),
            PwaOptionsValidator.FormatDisplayMode(options.Display),
            options.ThemeColor,
            options.BackgroundColor,
            options.Icons
                .Select(icon => new PwaManifestIcon(AddPathBase(pathBase, icon.Source), icon.Sizes, icon.Type, icon.Purpose))
                .ToArray());
    }

    internal static PwaDiagnosticsDocument BuildDiagnostics(HttpContext httpContext, PwaOptions options)
    {
        var request = httpContext.Request;
        var manifestUrl = request.PathBase.Add(new PathString(options.ManifestPath)).Value ?? options.ManifestPath;
        var diagnostics = PwaOptionsValidator.Validate(options).ToList();
        if (!IsSecureInstallContext(request))
        {
            diagnostics.Add(
                new PwaDiagnostic(
                    "ASPWA018",
                    PwaDiagnosticSeverity.Warning,
                    "This request is not HTTPS, localhost, 127.0.0.1, or ::1. Browsers normally require a secure context for PWA installation."));
        }

        return new PwaDiagnosticsDocument(
            options.Enabled,
            manifestUrl,
            options.Offline.Enabled,
            AddPathBase(request.PathBase, options.Offline.ServiceWorkerPath),
            options.Offline.Enabled ? AddPathBase(request.PathBase, options.Offline.ServiceWorkerPath) : null,
            options.Offline.Enabled ? AddPathBase(request.PathBase, options.Offline.OfflineFallbackPath) : null,
            diagnostics.Select(diagnostic => new PwaDiagnosticDocument(diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message)).ToArray());
    }

    private static bool ShouldMapDiagnostics(PwaOptions options, bool isDevelopment)
    {
        return options.DiagnosticsExposure == PwaDiagnosticEndpointExposure.Always
            || (options.DiagnosticsExposure == PwaDiagnosticEndpointExposure.DevelopmentOnly && isDevelopment);
    }

    private static bool IsSecureInstallContext(HttpRequest request)
    {
        if (request.IsHttps)
        {
            return true;
        }

        var host = request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal);
    }

    private static async Task WriteJsonAsync(HttpContext httpContext, object payload, string contentType)
    {
        httpContext.Response.ContentType = $"{contentType}; charset=utf-8";
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        await JsonSerializer.SerializeAsync(httpContext.Response.Body, payload, JsonOptions, httpContext.RequestAborted);
    }

    private static async Task WriteDiagnosticsHtmlAsync(HttpContext httpContext, PwaOptions options)
    {
        httpContext.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        var diagnostics = BuildDiagnostics(httpContext, options);
        var statusPath = AddPathBase(httpContext.Request.PathBase, $"{options.DiagnosticsPath.TrimEnd('/')}/status.json");
        var encodedName = HtmlEncoder.Default.Encode(options.Name);
        var encodedManifest = HtmlEncoder.Default.Encode(diagnostics.ManifestPath);
        var encodedStatusPath = HtmlEncoder.Default.Encode(statusPath);
        var encodedHeadSnippet = HtmlEncoder.Default.Encode(BuildHeadMetadataSnippet(httpContext.Request.PathBase, options));
        var items = diagnostics.Diagnostics.Count == 0
            ? "<li><strong>ASPWA000</strong> info: PWA configuration is valid for AppSurface startup.</li>"
            : string.Concat(
                diagnostics.Diagnostics.Select(
                    diagnostic =>
                        $"<li><strong>{HtmlEncoder.Default.Encode(diagnostic.Code)}</strong> {HtmlEncoder.Default.Encode(diagnostic.Severity)}: {HtmlEncoder.Default.Encode(diagnostic.Message)}</li>"));

        await httpContext.Response.WriteAsync(
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <title>AppSurface PWA diagnostics</title>
              <meta name="viewport" content="width=device-width, initial-scale=1" />
            </head>
            <body>
              <main>
                <h1>AppSurface PWA diagnostics</h1>
                <p>App: {{encodedName}}</p>
                <p>Manifest: <a href="{{encodedManifest}}">{{encodedManifest}}</a></p>
                <p>Status JSON: <a href="{{encodedStatusPath}}">{{encodedStatusPath}}</a></p>
                <p>Offline strategy: {{(diagnostics.OfflineEnabled ? "enabled" : "disabled")}}</p>
                <h2>Head metadata</h2>
                <p>Copy these tags into custom layouts that cannot use &lt;appsurface:pwa-head /&gt;.</p>
                <pre><code>{{encodedHeadSnippet}}</code></pre>
                <ul>{{items}}</ul>
              </main>
            </body>
            </html>
            """,
            httpContext.RequestAborted);
    }

    private static string BuildHeadMetadataSnippet(PathString pathBase, PwaOptions options)
    {
        var manifestPath = EscapeAttribute(AddPathBase(pathBase, options.ManifestPath));
        var builder = new StringBuilder();
        builder.AppendLine($"""<link rel="manifest" href="{manifestPath}" />""");
        builder.AppendLine($"""<meta name="theme-color" content="{EscapeAttribute(options.ThemeColor)}" />""");
        builder.AppendLine($"""<meta name="application-name" content="{EscapeAttribute(options.Name)}" />""");
        builder.AppendLine("""<meta name="apple-mobile-web-app-capable" content="yes" />""");
        builder.AppendLine($"""<meta name="apple-mobile-web-app-title" content="{EscapeAttribute(options.ShortName)}" />""");

        foreach (var icon in options.Icons)
        {
            var iconPath = EscapeAttribute(AddPathBase(pathBase, icon.Source));
            builder.AppendLine(
                $"""<link rel="icon" href="{iconPath}" sizes="{EscapeAttribute(icon.Sizes)}" type="{EscapeAttribute(icon.Type)}" />""");
        }

        if (options.Offline.Enabled)
        {
            var serviceWorkerPath = EscapeAttribute(AddPathBase(pathBase, options.Offline.ServiceWorkerPath));
            builder.AppendLine($"""<meta name="appsurface:pwa-service-worker" content="{serviceWorkerPath}" />""");
        }

        return builder.ToString().TrimEnd();
    }

    private static string EscapeAttribute(string value)
    {
        return HtmlEncoder.Default.Encode(value);
    }

    private static async Task WriteServiceWorkerAsync(HttpContext httpContext, PwaOptions options)
    {
        httpContext.Response.ContentType = "text/javascript; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-cache";
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        var paths = options.Offline.StaticAssetPaths
            .Append(options.Offline.OfflineFallbackPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => AddPathBase(httpContext.Request.PathBase, path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var offlineFallbackPath = AddPathBase(httpContext.Request.PathBase, options.Offline.OfflineFallbackPath);
        var assetsJson = JsonSerializer.Serialize(paths);
        var fallbackJson = JsonSerializer.Serialize(offlineFallbackPath);
        var cachePrefix = $"appsurface-pwa-{BuildCacheScopeKey(httpContext.Request.PathBase, options)}-";
        var cachePrefixJson = JsonSerializer.Serialize(cachePrefix);
        var cacheNameJson = JsonSerializer.Serialize(cachePrefix + "v1");

        await httpContext.Response.WriteAsync(
            $$"""
            const CACHE_PREFIX = {{cachePrefixJson}};
            const CACHE_NAME = {{cacheNameJson}};
            const LEGACY_CACHE_NAMES = ["appsurface-pwa-v1"];
            const STATIC_ASSETS = {{assetsJson}};
            const OFFLINE_FALLBACK = {{fallbackJson}};

            self.addEventListener("install", event => {
              event.waitUntil((async () => {
                const cache = await caches.open(CACHE_NAME);
                await cache.addAll(STATIC_ASSETS);
                await self.skipWaiting();
              })());
            });

            self.addEventListener("activate", event => {
              event.waitUntil((async () => {
                const keys = await caches.keys();
                await Promise.all(keys.filter(shouldDeleteCache).map(key => caches.delete(key)));
                await self.clients.claim();
              })());
            });

            function shouldDeleteCache(key) {
              return (key.startsWith(CACHE_PREFIX) && key !== CACHE_NAME) || LEGACY_CACHE_NAMES.includes(key);
            }

            self.addEventListener("fetch", event => {
              const request = event.request;
              if (request.method !== "GET") return;
              const url = new URL(request.url);
              if (url.origin !== self.location.origin) return;

              if (STATIC_ASSETS.includes(url.pathname)) {
                event.respondWith(caches.match(request).then(cached => cached || fetch(request)));
                return;
              }

              if (request.mode === "navigate") {
                event.respondWith(fetch(request).catch(() => caches.match(OFFLINE_FALLBACK)));
              }
            });
            """,
            httpContext.RequestAborted);
    }

    private static string AddPathBase(PathString pathBase, string path)
    {
        return pathBase.Add(new PathString(path)).Value ?? path;
    }

    private static string BuildCacheScopeKey(PathString pathBase, PwaOptions options)
    {
        var serviceWorkerPath = AddPathBase(pathBase, options.Offline.ServiceWorkerPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceWorkerPath));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

internal sealed record PwaManifestDocument(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("short_name")] string ShortName,
    [property: JsonPropertyName("start_url")] string StartUrl,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("display")] string Display,
    [property: JsonPropertyName("theme_color")] string ThemeColor,
    [property: JsonPropertyName("background_color")] string BackgroundColor,
    [property: JsonPropertyName("icons")] IReadOnlyList<PwaManifestIcon> Icons);

internal sealed record PwaManifestIcon(
    [property: JsonPropertyName("src")] string Source,
    [property: JsonPropertyName("sizes")] string Sizes,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("purpose")] string? Purpose);

internal sealed record PwaDiagnosticsDocument(
    bool Enabled,
    string ManifestPath,
    bool OfflineEnabled,
    string ConfiguredServiceWorkerPath,
    string? ServiceWorkerPath,
    string? OfflineFallbackPath,
    IReadOnlyList<PwaDiagnosticDocument> Diagnostics);

internal sealed record PwaDiagnosticDocument(string Code, string Severity, string Message);
