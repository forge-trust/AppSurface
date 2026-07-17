using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web;

internal static class PwaEndpointMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions ScriptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Map(IEndpointRouteBuilder endpoints, PwaOptions options, bool isDevelopment)
    {
        if (!options.HasAnySurfaceEnabled)
        {
            return;
        }

        if (options.Enabled)
        {
            endpoints.MapMethods(
                options.ManifestPath,
                [HttpMethods.Get, HttpMethods.Head],
                httpContext => WriteJsonAsync(httpContext, BuildManifest(httpContext, options), "application/manifest+json"));
        }

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

        if (options.IsWorkerEnabled)
        {
            endpoints.MapMethods(
                options.Worker.ServiceWorkerPath,
                [HttpMethods.Get, HttpMethods.Head],
                httpContext => WriteServiceWorkerAsync(httpContext, options));
        }

        if (options.Push.Enabled)
        {
            endpoints.MapMethods(
                options.Worker.RegistrationHelperPath,
                [HttpMethods.Get, HttpMethods.Head],
                WriteRegistrationHelperAsync);
        }

        if (options.Badging.Enabled)
        {
            endpoints.MapMethods(
                options.Badging.HelperPath,
                [HttpMethods.Get, HttpMethods.Head],
                WriteBadgingHelperAsync);
        }
    }

    internal static PwaManifestDocument BuildManifest(HttpContext httpContext, PwaOptions options)
    {
        var pathBase = httpContext.Request.PathBase;
        return new PwaManifestDocument(
            options.Name,
            options.ShortName,
            PwaPathBase.Add(pathBase, options.StartUrl),
            PwaPathBase.Add(pathBase, options.Scope),
            PwaOptionsValidator.FormatDisplayMode(options.Display),
            options.ThemeColor,
            options.BackgroundColor,
            options.Icons
                .Select(icon => new PwaManifestIcon(PwaPathBase.Add(pathBase, icon.Source), icon.Sizes, icon.Type, icon.Purpose))
                .ToArray());
    }

    internal static PwaDiagnosticsDocument BuildDiagnostics(HttpContext httpContext, PwaOptions options)
    {
        var request = httpContext.Request;
        var workerEnabled = options.IsWorkerEnabled;
        var workerPath = workerEnabled ? PwaPathBase.Add(request.PathBase, options.Worker.ServiceWorkerPath) : null;
        var diagnostics = PwaOptionsValidator.Validate(options).ToList();
        var isSecureContext = IsSecureInstallContext(request);
        if (!isSecureContext && (options.Enabled || options.IsWorkerEnabled))
        {
            diagnostics.Add(
                new PwaDiagnostic(
                    "ASPWA018",
                    PwaDiagnosticSeverity.Warning,
                    "This request is not HTTPS, localhost, 127.0.0.1, or ::1. Browsers normally require a secure context for service-worker registration and PWA installation."));
        }
        if (!isSecureContext && options.Badging.Enabled && !options.Enabled && !options.IsWorkerEnabled)
        {
            diagnostics.Add(
                new PwaDiagnostic(
                    "ASPWA026",
                    PwaDiagnosticSeverity.Warning,
                    "Badging is configured on a request that is not HTTPS, localhost, 127.0.0.1, or ::1. Browser support and badge visibility were not evaluated."));
        }

        return new PwaDiagnosticsDocument(
            Enabled: options.Enabled,
            ManifestPath: PwaPathBase.Add(request.PathBase, options.ManifestPath),
            OfflineEnabled: options.Offline.Enabled,
            ConfiguredServiceWorkerPath: options.Offline.Enabled || !workerEnabled
                ? PwaPathBase.Add(request.PathBase, options.Worker.ServiceWorkerPath)
                : null,
            ServiceWorkerPath: options.Offline.Enabled ? workerPath : null,
            OfflineFallbackPath: options.Offline.Enabled
                ? PwaPathBase.Add(request.PathBase, options.Offline.OfflineFallbackPath)
                : null,
            WorkerEnabled: workerEnabled,
            WorkerPath: workerPath,
            PushEnabled: options.Push.Enabled,
            WorkerScope: PwaPathBase.Add(request.PathBase, options.Scope),
            RegistrationHelperPath: options.Push.Enabled
                ? PwaPathBase.Add(request.PathBase, options.Worker.RegistrationHelperPath)
                : null,
            BadgingEnabled: options.Badging.Enabled,
            BadgingHelperPath: options.Badging.Enabled
                ? PwaPathBase.Add(request.PathBase, options.Badging.HelperPath)
                : null,
            Diagnostics: diagnostics
                .Select(diagnostic => new PwaDiagnosticDocument(diagnostic.Code, diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Message))
                .ToArray());
    }

    /// <summary>
    /// Composes the exact generated worker from a JSON configuration and capability-specific embedded sources.
    /// </summary>
    /// <param name="httpContext">The request providing the effective path base.</param>
    /// <param name="options">The validated PWA options.</param>
    /// <returns>The complete classic service-worker source.</returns>
    internal static string BuildServiceWorkerScript(HttpContext httpContext, PwaOptions options)
    {
        var pathBase = httpContext.Request.PathBase;
        var cachePrefix = $"appsurface-pwa-{BuildCacheScopeKey(pathBase, options)}-";
        var configuration = new PwaWorkerScriptConfiguration(
            options.Offline.Enabled,
            cachePrefix,
            cachePrefix + "v1",
            ["appsurface-pwa-v1"],
            options.Offline.Enabled
                ? options.Offline.StaticAssetPaths
                    .Append(options.Offline.OfflineFallbackPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => PwaPathBase.Add(pathBase, path))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [],
            options.Offline.Enabled ? PwaPathBase.Add(pathBase, options.Offline.OfflineFallbackPath) : null,
            pathBase.HasValue ? pathBase.Value! : "/",
            PwaPathBase.Add(pathBase, options.Scope),
            options.Push.Enabled && options.Push.HandlerScriptPath is not null
                ? PwaPathBase.Add(pathBase, options.Push.HandlerScriptPath)
                : null);

        var builder = new StringBuilder();
        builder.Append("const APPSURFACE_PWA_CONFIG = ");
        builder.Append(JsonSerializer.Serialize(configuration, ScriptJsonOptions));
        builder.AppendLine(";");
        builder.AppendLine(PwaScriptAssets.WorkerShared);
        if (options.Badging.Enabled)
        {
            builder.AppendLine(PwaScriptAssets.WorkerBadging);
        }

        if (options.Offline.Enabled)
        {
            builder.AppendLine(PwaScriptAssets.WorkerOffline);
        }

        if (options.Push.Enabled)
        {
            if (options.Push.HandlerScriptPath is null)
            {
                builder.AppendLine(PwaScriptAssets.WorkerPush);
            }
            else
            {
                builder.AppendLine(PwaScriptAssets.WorkerCustomHandler);
            }
        }

        return builder.ToString();
    }

    private static bool ShouldMapDiagnostics(PwaOptions options, bool isDevelopment) =>
        options.DiagnosticsExposure == PwaDiagnosticEndpointExposure.Always
        || (options.DiagnosticsExposure == PwaDiagnosticEndpointExposure.DevelopmentOnly && isDevelopment);

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
        var statusPath = PwaPathBase.Add(httpContext.Request.PathBase, $"{options.DiagnosticsPath.TrimEnd('/')}/status.json");
        var encodedName = HtmlEncoder.Default.Encode(options.Enabled ? options.Name : "Install metadata disabled");
        var encodedManifest = HtmlEncoder.Default.Encode(diagnostics.ManifestPath);
        var encodedStatusPath = HtmlEncoder.Default.Encode(statusPath);
        var fileVersionProvider = httpContext.RequestServices.GetService<IFileVersionProvider>();
        var encodedHeadSnippet = HtmlEncoder.Default.Encode(
            PwaHeadMetadataBuilder.Build(httpContext.Request.PathBase, options, fileVersionProvider));
        var items = diagnostics.Diagnostics.Count == 0
            ? "<li><strong>ASPWA000</strong> info: PWA configuration is valid for AppSurface startup.</li>"
            : string.Concat(
                diagnostics.Diagnostics.Select(
                    diagnostic =>
                        $"<li><strong>{HtmlEncoder.Default.Encode(diagnostic.Code)}</strong> {HtmlEncoder.Default.Encode(diagnostic.Severity)}: {HtmlEncoder.Default.Encode(diagnostic.Message)}</li>"));

        var manifestLine = options.Enabled
            ? $"<p>Manifest: <a href=\"{encodedManifest}\">{encodedManifest}</a></p>"
            : "<p>Manifest: disabled</p>";
        var registrationGuidance = options.Push.Enabled
            ? "<p>Push registration is explicit: <code>await window.AppSurface.Pwa.register();</code></p><p>Registration does not request permission, create a subscription, or prove delivery.</p>"
            : options.Offline.Enabled
                ? "<p>The registration helper is not emitted for this offline-only worker.</p>"
                : "<p>No worker capability or registration helper is active.</p>";
        var badgingGuidance = options.Badging.Enabled
            ? "<p>App badging: enabled. The helper reports accepted or unsupported outcomes and sanitized ASPWAJS041/ASPWAJS042 request failures; installation, permission, operating-system settings, visibility, and displayed value were not evaluated.</p>"
            : "<p>App badging: disabled.</p>";
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
                {{manifestLine}}
                <p>Status JSON: <a href="{{encodedStatusPath}}">{{encodedStatusPath}}</a></p>
                <p>Worker: {{(diagnostics.WorkerEnabled ? "enabled" : "disabled")}}</p>
                <p>Offline strategy: {{(diagnostics.OfflineEnabled ? "enabled" : "disabled")}}</p>
                <p>Push handlers: {{(diagnostics.PushEnabled ? "enabled" : "disabled")}}</p>
                <p>Badging helper: {{(diagnostics.BadgingEnabled ? "enabled" : "disabled")}}</p>
                <h2>Head metadata</h2>
                <p>Copy these tags into custom layouts that cannot use &lt;appsurface:pwa-head /&gt;.</p>
                <pre><code>{{encodedHeadSnippet}}</code></pre>
                {{registrationGuidance}}
                {{badgingGuidance}}
                <ul>{{items}}</ul>
              </main>
            </body>
            </html>
            """,
            httpContext.RequestAborted);
    }

    private static async Task WriteServiceWorkerAsync(HttpContext httpContext, PwaOptions options)
    {
        httpContext.Response.ContentType = "text/javascript; charset=utf-8";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        httpContext.Response.Headers["Service-Worker-Allowed"] = PwaPathBase.Add(httpContext.Request.PathBase, options.Scope);
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        await httpContext.Response.WriteAsync(BuildServiceWorkerScript(httpContext, options), httpContext.RequestAborted);
    }

    private static async Task WriteRegistrationHelperAsync(HttpContext httpContext)
    {
        await WriteVersionedScriptAsync(
            httpContext,
            PwaScriptAssets.RegistrationHelper,
            PwaScriptAssets.RegistrationHelperVersion);
    }

    private static async Task WriteBadgingHelperAsync(HttpContext httpContext)
    {
        await WriteVersionedScriptAsync(
            httpContext,
            PwaScriptAssets.BadgingHelper,
            PwaScriptAssets.BadgingHelperVersion);
    }

    private static async Task WriteVersionedScriptAsync(HttpContext httpContext, string script, string currentVersion)
    {
        httpContext.Response.ContentType = "text/javascript; charset=utf-8";
        var requestedVersions = httpContext.Request.Query["v"];
        httpContext.Response.Headers.CacheControl = requestedVersions.Count == 1
            && string.Equals(requestedVersions[0], currentVersion, StringComparison.Ordinal)
                ? "public, max-age=31536000, immutable"
                : "no-cache";
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        await httpContext.Response.WriteAsync(script, httpContext.RequestAborted);
    }

    private static string BuildCacheScopeKey(PathString pathBase, PwaOptions options)
    {
        var serviceWorkerPath = PwaPathBase.Add(pathBase, options.Worker.ServiceWorkerPath);
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

/// <summary>Represents privacy-safe, server-known PWA posture for diagnostics and CLI compatibility.</summary>
/// <param name="Enabled">Whether install metadata is enabled.</param>
/// <param name="ManifestPath">The configured manifest path after PathBase application.</param>
/// <param name="OfflineEnabled">Whether the offline capability is enabled.</param>
/// <param name="ConfiguredServiceWorkerPath">
/// The legacy configured worker path. It remains populated for offline mode and when both worker capabilities are
/// disabled so older CLI versions can prove that the configured endpoint is absent; it is omitted for push-only mode.
/// </param>
/// <param name="ServiceWorkerPath">The legacy active offline worker path, populated only when offline support is enabled.</param>
/// <param name="OfflineFallbackPath">The active offline fallback path.</param>
/// <param name="WorkerEnabled">Whether any worker capability is enabled.</param>
/// <param name="WorkerPath">The active shared worker path.</param>
/// <param name="PushEnabled">Whether push handlers are enabled.</param>
/// <param name="WorkerScope">The effective worker scope.</param>
/// <param name="RegistrationHelperPath">The active registration-helper path.</param>
/// <param name="BadgingEnabled">Whether the application-icon badging helper is enabled.</param>
/// <param name="BadgingHelperPath">The active PathBase-adjusted badging-helper path, or an explicit null when disabled.</param>
/// <param name="Diagnostics">Stable startup diagnostics.</param>
internal sealed record PwaDiagnosticsDocument(
    bool Enabled,
    string ManifestPath,
    bool OfflineEnabled,
    string? ConfiguredServiceWorkerPath,
    string? ServiceWorkerPath,
    string? OfflineFallbackPath,
    bool WorkerEnabled,
    string? WorkerPath,
    bool PushEnabled,
    string WorkerScope,
    string? RegistrationHelperPath,
    bool BadgingEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BadgingHelperPath,
    IReadOnlyList<PwaDiagnosticDocument> Diagnostics);

internal sealed record PwaDiagnosticDocument(string Code, string Severity, string Message);

/// <summary>Defines the JSON-safe configuration consumed by embedded worker fragments.</summary>
/// <param name="OfflineEnabled">Whether offline behavior is included.</param>
/// <param name="CachePrefix">The worker-path-specific owned cache prefix.</param>
/// <param name="CacheName">The active offline cache name.</param>
/// <param name="LegacyCacheNames">Legacy AppSurface cache names eligible for retirement.</param>
/// <param name="StaticAssets">PathBase-adjusted assets to precache.</param>
/// <param name="OfflineFallback">The PathBase-adjusted fallback path.</param>
/// <param name="PathBase">The effective application path base.</param>
/// <param name="Scope">The effective worker scope.</param>
/// <param name="HandlerScriptPath">The PathBase-adjusted custom push-handler path.</param>
internal sealed record PwaWorkerScriptConfiguration(
    bool OfflineEnabled,
    string CachePrefix,
    string CacheName,
    IReadOnlyList<string> LegacyCacheNames,
    IReadOnlyList<string> StaticAssets,
    string? OfflineFallback,
    string PathBase,
    string Scope,
    string? HandlerScriptPath);
