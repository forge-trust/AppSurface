using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Verifies that a running web app exposes AppSurface-compatible PWA install metadata.
/// </summary>
[Command("pwa verify", Description = "Verify PWA install metadata for a running AppSurface Web app.")]
internal sealed partial class PwaVerifyCommand : ICommand
{
    private readonly PwaVerifier _verifier;

    public PwaVerifyCommand(PwaVerifier verifier)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    /// <summary>
    /// Gets the app origin or URL to verify.
    /// </summary>
    [CommandOption("url", Description = "App origin or URL to verify, for example https://app.example.com.")]
    public string? Url { get; set; }

    /// <summary>
    /// Gets a value indicating whether machine-readable JSON should be written.
    /// </summary>
    [CommandOption("json", Description = "Write machine-readable verification JSON.")]
    public bool Json { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CommandException("--url must be an absolute http or https URL.");
        }

        var report = await _verifier.VerifyAsync(uri, console.RegisterCancellationHandler());
        if (Json)
        {
            await console.Output.WriteLineAsync(JsonSerializer.Serialize(report, PwaVerifier.JsonOptions));
        }
        else
        {
            await WriteTextReportAsync(console, report);
        }

        if (!report.Passed)
        {
            throw new CommandException("PWA verification failed.");
        }
    }

    private static async Task WriteTextReportAsync(IConsole console, PwaVerificationReport report)
    {
        await console.Output.WriteLineAsync(report.Passed
            ? "PWA verification passed."
            : "PWA verification failed.");
        foreach (var diagnostic in report.Diagnostics)
        {
            await console.Output.WriteLineAsync(
                $"{diagnostic.Severity.ToUpperInvariant()} {diagnostic.Code}: {diagnostic.Message}");
        }
    }
}

internal sealed partial class PwaVerifier
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly IPwaVerificationHttpClient _httpClient;

    public PwaVerifier(IPwaVerificationHttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<PwaVerificationReport> VerifyAsync(Uri url, CancellationToken cancellationToken)
    {
        var target = PwaVerificationTarget.Create(url);
        var diagnostics = new List<PwaVerificationDiagnostic>();
        if (!IsSecureInstallContext(target.Origin))
        {
            diagnostics.Add(Error("ASPWA200", "The URL must use HTTPS, localhost, 127.0.0.1, or ::1 for browser PWA installation."));
        }

        var root = await _httpClient.GetAsync(target.BaseUri, cancellationToken);
        var manifestUri = new Uri(target.BaseUri, "manifest.webmanifest");
        if (root.IsSuccess && root.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var extractedManifestPath = ExtractManifestPath(root.Body);
            if (string.IsNullOrWhiteSpace(extractedManifestPath))
            {
                diagnostics.Add(Error("ASPWA224", "The app root HTML must include a manifest link in the document head."));
            }
            else if (!ResolvesToOrigin(target, target.BaseUri, extractedManifestPath, out var linkedManifestUri))
            {
                diagnostics.Add(Error("ASPWA225", "The app root manifest link must resolve to the app origin."));
            }
            else if (!IsUnderBasePath(target, linkedManifestUri.AbsolutePath))
            {
                diagnostics.Add(Error("ASPWA227", "The app root manifest link must stay under the verified base path."));
            }
            else
            {
                manifestUri = linkedManifestUri;
            }
        }
        else
        {
            diagnostics.Add(Warning("ASPWA201", "The app root did not return HTML, so the verifier used manifest.webmanifest under the verified base path."));
        }

        var manifest = await _httpClient.GetAsync(manifestUri, cancellationToken);
        if (!manifest.IsSuccess)
        {
            diagnostics.Add(Error("ASPWA202", $"Manifest request failed with HTTP {(int)manifest.StatusCode}."));
            return BuildReport(target, manifestUri, diagnostics, diagnostics.All(d => d.Severity != "error"));
        }

        if (!manifest.ContentType.StartsWith("application/manifest+json", StringComparison.OrdinalIgnoreCase)
            && !manifest.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("ASPWA203", $"Manifest content type should be application/manifest+json. Actual: {manifest.ContentType}."));
        }

        PwaManifestProbe? manifestDocument = null;
        try
        {
            manifestDocument = JsonSerializer.Deserialize<PwaManifestProbe>(manifest.Body, JsonOptions);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error("ASPWA204", $"Manifest JSON could not be parsed: {ex.Message}"));
        }

        if (manifestDocument is not null)
        {
            ValidateManifest(target, manifestUri, manifestDocument, diagnostics);
            foreach (var icon in manifestDocument.Icons ?? [])
            {
                if (string.IsNullOrWhiteSpace(icon.Source))
                {
                    continue;
                }

                if (!ResolvesToOrigin(target, manifestUri, icon.Source, out var iconUri))
                {
                    diagnostics.Add(Error("ASPWA214", $"Icon {icon.Source} must resolve to the app origin."));
                    continue;
                }

                if (!IsUnderBasePath(target, iconUri.AbsolutePath))
                {
                    diagnostics.Add(Error("ASPWA228", $"Icon {icon.Source} must stay under the verified base path."));
                    continue;
                }

                var iconResponse = await _httpClient.GetAsync(iconUri, cancellationToken);
                if (!iconResponse.IsSuccess)
                {
                    diagnostics.Add(Error("ASPWA212", $"Icon {icon.Source} returned HTTP {(int)iconResponse.StatusCode}."));
                }
                else if (!iconResponse.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error("ASPWA213", $"Icon {icon.Source} returned non-image content type {iconResponse.ContentType}."));
                }
            }
        }

        var diagnosticsResponse = await _httpClient.GetAsync(new Uri(target.BaseUri, "_appsurface/pwa/status.json"), cancellationToken);
        if (diagnosticsResponse.StatusCode == HttpStatusCode.NotFound)
        {
            diagnostics.Add(Info("ASPWA220", "AppSurface PWA diagnostics are not exposed at /_appsurface/pwa/status.json. This is expected for production defaults."));
        }
        else if (!diagnosticsResponse.IsSuccess)
        {
            diagnostics.Add(Warning("ASPWA221", $"AppSurface PWA diagnostics returned HTTP {(int)diagnosticsResponse.StatusCode}."));
        }
        else
        {
            try
            {
                var status = JsonSerializer.Deserialize<PwaStatusProbe>(diagnosticsResponse.Body, JsonOptions);
                if (status?.OfflineEnabled == true && string.IsNullOrWhiteSpace(status.ServiceWorkerPath))
                {
                    diagnostics.Add(Error("ASPWA222", "Diagnostics report offline enabled without a service worker path."));
                }
                else if (status?.OfflineEnabled == true)
                {
                    var serviceWorkerPath = status.ServiceWorkerPath!;
                    if (!ResolvesToOrigin(target, target.BaseUri, serviceWorkerPath, out var serviceWorkerUri))
                    {
                        diagnostics.Add(Error("ASPWA229", "Diagnostics service worker path must resolve to the app origin."));
                        return BuildReport(target, manifestUri, diagnostics, diagnostics.All(d => d.Severity != "error"));
                    }

                    if (!IsUnderBasePath(target, serviceWorkerUri.AbsolutePath))
                    {
                        diagnostics.Add(Error("ASPWA230", "Diagnostics service worker path must stay under the verified base path."));
                        return BuildReport(target, manifestUri, diagnostics, diagnostics.All(d => d.Severity != "error"));
                    }

                    var serviceWorker = await _httpClient.GetAsync(serviceWorkerUri, cancellationToken);
                    if (!serviceWorker.IsSuccess)
                    {
                        diagnostics.Add(Error("ASPWA226", $"Service worker {serviceWorkerPath} returned HTTP {(int)serviceWorker.StatusCode}."));
                    }
                }
            }
            catch (JsonException ex)
            {
                diagnostics.Add(Warning("ASPWA223", $"AppSurface PWA diagnostics JSON could not be parsed: {ex.Message}"));
            }
        }

        return BuildReport(target, manifestUri, diagnostics, diagnostics.All(d => d.Severity != "error"));
    }

    private static void ValidateManifest(
        PwaVerificationTarget target,
        Uri manifestUri,
        PwaManifestProbe manifest,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        RequireText(manifest.Name, "ASPWA205", "Manifest name is required.", diagnostics);
        RequireText(manifest.ShortName, "ASPWA206", "Manifest short_name is required.", diagnostics);
        RequireText(manifest.Display, "ASPWA207", "Manifest display is required.", diagnostics);
        RequireSameOriginPath(target, manifestUri, manifest.StartUrl, "ASPWA208", "Manifest start_url must resolve to the app origin.", diagnostics);
        RequireSameOriginPath(target, manifestUri, manifest.Scope, "ASPWA209", "Manifest scope must resolve to the app origin.", diagnostics);

        var icons = manifest.Icons ?? [];
        if (!icons.Any(icon => string.Equals(icon.Sizes, "192x192", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Error("ASPWA210", "Manifest must declare a 192x192 icon."));
        }

        if (!icons.Any(icon => string.Equals(icon.Sizes, "512x512", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Error("ASPWA211", "Manifest must declare a 512x512 icon."));
        }
    }

    private static void RequireText(string? value, string code, string message, List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(Error(code, message));
        }
    }

    private static void RequireSameOriginPath(
        PwaVerificationTarget target,
        Uri baseUri,
        string? value,
        string code,
        string message,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !ResolvesToOrigin(target, baseUri, value, out var uri))
        {
            diagnostics.Add(Error(code, message));
            return;
        }

        if (!IsUnderBasePath(target, uri.AbsolutePath))
        {
            diagnostics.Add(Error("ASPWA231", $"{message} It must also stay under the verified base path."));
        }
    }

    private static bool ResolvesToOrigin(PwaVerificationTarget target, Uri baseUri, string value, out Uri uri)
    {
        return Uri.TryCreate(baseUri, value, out uri!)
            && uri.Scheme == target.Origin.Scheme
            && uri.Host == target.Origin.Host
            && uri.Port == target.Origin.Port;
    }

    private static bool IsSecureInstallContext(Uri origin)
    {
        return origin.Scheme == Uri.UriSchemeHttps
            || string.Equals(origin.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(origin.Host, "::1", StringComparison.Ordinal);
    }

    private static string? ExtractManifestPath(string html)
    {
        foreach (Match link in LinkTagRegex().Matches(html))
        {
            var tag = link.Value;
            var rel = RelAttributeRegex().Match(tag);
            if (!rel.Success
                || !rel.Groups["value"].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("manifest", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = HrefAttributeRegex().Match(tag);
            if (href.Success)
            {
                return WebUtility.HtmlDecode(href.Groups["value"].Value);
            }
        }

        return null;
    }

    private static PwaVerificationReport BuildReport(
        PwaVerificationTarget target,
        Uri manifestUri,
        IReadOnlyList<PwaVerificationDiagnostic> diagnostics,
        bool passed)
    {
        return new PwaVerificationReport(
            passed,
            target.BaseUri.ToString().TrimEnd('/'),
            manifestUri.PathAndQuery,
            diagnostics);
    }

    private static bool IsUnderBasePath(PwaVerificationTarget target, string path)
    {
        if (target.BasePath == "/")
        {
            return true;
        }

        return string.Equals(path, target.BasePath.TrimEnd('/'), StringComparison.Ordinal)
            || path.StartsWith(target.BasePath, StringComparison.Ordinal);
    }

    private static PwaVerificationDiagnostic Error(string code, string message) => new(code, "error", message);

    private static PwaVerificationDiagnostic Warning(string code, string message) => new(code, "warning", message);

    private static PwaVerificationDiagnostic Info(string code, string message) => new(code, "info", message);

    [GeneratedRegex("""<link\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex("""\brel\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttributeRegex();

    [GeneratedRegex("""\bhref\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttributeRegex();
}

internal interface IPwaVerificationHttpClient
{
    Task<PwaHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken);
}

internal sealed class PwaVerificationHttpClient(HttpClient httpClient) : IPwaVerificationHttpClient
{
    public async Task<PwaHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PwaHttpResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            body);
    }
}

internal sealed record PwaHttpResponse(HttpStatusCode StatusCode, string ContentType, string Body)
{
    public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;
}

internal sealed record PwaVerificationReport(
    bool Passed,
    string Origin,
    string ManifestPath,
    IReadOnlyList<PwaVerificationDiagnostic> Diagnostics);

internal sealed record PwaVerificationDiagnostic(string Code, string Severity, string Message);

internal sealed record PwaVerificationTarget(Uri Origin, Uri BaseUri, string BasePath)
{
    public static PwaVerificationTarget Create(Uri url)
    {
        var origin = new UriBuilder(url.Scheme, url.Host, url.Port).Uri;
        var basePath = string.IsNullOrWhiteSpace(url.AbsolutePath) || url.AbsolutePath == "/"
            ? "/"
            : url.AbsolutePath.TrimEnd('/') + "/";
        return new PwaVerificationTarget(origin, new Uri(origin, basePath), basePath);
    }
}

internal sealed record PwaManifestProbe(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("short_name")] string? ShortName,
    [property: JsonPropertyName("start_url")] string? StartUrl,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("display")] string? Display,
    [property: JsonPropertyName("icons")] IReadOnlyList<PwaIconProbe>? Icons);

internal sealed record PwaIconProbe(
    [property: JsonPropertyName("src")] string? Source,
    [property: JsonPropertyName("sizes")] string? Sizes,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record PwaStatusProbe(
    bool Enabled,
    bool OfflineEnabled,
    string? ServiceWorkerPath);
