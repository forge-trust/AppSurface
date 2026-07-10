using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Web;

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
    /// Gets the app base URL to verify.
    /// </summary>
    [CommandOption("base-url", Description = "App base URL to verify. Use this instead of --url when also passing --entry-path.")]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets the app-root-relative entry path whose HTML should expose the manifest link.
    /// </summary>
    [CommandOption("entry-path", Description = "App-root-relative page path to verify for manifest discovery, for example /account/resume.")]
    public string EntryPath { get; set; } = "/";

    /// <summary>
    /// Gets the expected manifest start_url value.
    /// </summary>
    [CommandOption("expect-start-url", Description = "Expected manifest start_url value.")]
    public string? ExpectedStartUrl { get; set; }

    /// <summary>
    /// Gets the expected manifest scope value.
    /// </summary>
    [CommandOption("expect-scope", Description = "Expected manifest scope value.")]
    public string? ExpectedScope { get; set; }

    /// <summary>
    /// Gets the expected manifest display mode.
    /// </summary>
    [CommandOption("expect-display", Description = "Expected manifest display mode, for example standalone.")]
    public string? ExpectedDisplay { get; set; }

    /// <summary>
    /// Gets the expected manifest theme_color value.
    /// </summary>
    [CommandOption("expect-theme-color", Description = "Expected manifest theme_color value.")]
    public string? ExpectedThemeColor { get; set; }

    /// <summary>
    /// Gets the expected manifest background_color value.
    /// </summary>
    [CommandOption("expect-background-color", Description = "Expected manifest background_color value.")]
    public string? ExpectedBackgroundColor { get; set; }

    /// <summary>
    /// Gets expected icon size tokens, optionally followed by a purpose after a colon.
    /// </summary>
    [CommandOption("expect-icon", Description = "Repeatable expected icon token such as 192x192 or 512x512:maskable.")]
    public string[] ExpectedIcons { get; set; } = [];

    /// <summary>
    /// Gets a value indicating whether machine-readable JSON should be written.
    /// </summary>
    [CommandOption("json", Description = "Write machine-readable verification JSON.")]
    public bool Json { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        var urlText = ResolveUrlText();
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CommandException("--url or --base-url must be an absolute http or https URL.");
        }

        PwaVerificationOptions options;
        try
        {
            options = PwaVerificationOptions.Create(
                uri,
                EntryPath,
                ExpectedStartUrl,
                ExpectedScope,
                ExpectedDisplay,
                ExpectedThemeColor,
                ExpectedBackgroundColor,
                ExpectedIcons);
        }
        catch (ArgumentException ex)
        {
            throw new CommandException(ex.Message);
        }

        var report = await _verifier.VerifyAsync(options, console.RegisterCancellationHandler());
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

    private string? ResolveUrlText()
    {
        if (!string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(BaseUrl)
            && !string.Equals(Url, BaseUrl, StringComparison.Ordinal))
        {
            throw new CommandException("Use either --url or --base-url, not both.");
        }

        return !string.IsNullOrWhiteSpace(BaseUrl) ? BaseUrl : Url;
    }

    private static async Task WriteTextReportAsync(IConsole console, PwaVerificationReport report)
    {
        await console.Output.WriteLineAsync(report.Passed
            ? "PWA verification passed."
            : "PWA verification failed.");
        await console.Output.WriteLineAsync($"Entry: {report.EntryUrl}");
        await console.Output.WriteLineAsync($"Manifest: {report.ManifestPath}");
        foreach (var diagnostic in report.Diagnostics)
        {
            var details = string.IsNullOrWhiteSpace(diagnostic.Subject)
                ? string.Empty
                : $" [{diagnostic.Subject}]";
            await console.Output.WriteLineAsync(
                $"{diagnostic.Severity.ToUpperInvariant()} {diagnostic.Code}{details}: {diagnostic.Message}");
        }
    }
}

internal sealed partial class PwaVerifier
{
    private const int MaxRedirects = 5;
    private const int MaxTextResponseBytes = 1024 * 1024;
    private const int MaxIconResponseBytes = 2 * 1024 * 1024;

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

    public Task<PwaVerificationReport> VerifyAsync(Uri url, CancellationToken cancellationToken)
    {
        return VerifyAsync(PwaVerificationOptions.Create(url), cancellationToken);
    }

    public async Task<PwaVerificationReport> VerifyAsync(
        PwaVerificationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var target = PwaVerificationTarget.Create(options.BaseUrl, options.EntryPath);
        var diagnostics = new List<PwaVerificationDiagnostic>();
        var iconEvidence = new List<PwaIconEvidence>();
        PwaManifestProbe? manifestDocument = null;
        if (!IsSecureInstallContext(target.Origin))
        {
            diagnostics.Add(Error(
                "ASPWA200",
                "The URL must use HTTPS, localhost, 127.0.0.1, or ::1 for browser PWA installation.",
                "url",
                "https-or-localhost",
                target.Origin.ToString().TrimEnd('/'),
                "Verify through the public HTTPS URL or a localhost development URL."));
        }

        var entry = await FetchAsync(target, target.EntryUri, "entry", MaxTextResponseBytes, diagnostics, cancellationToken);
        var manifestUri = new Uri(target.BaseUri, "manifest.webmanifest");
        if (entry.IsSuccess && entry.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var extractedManifestPath = ExtractManifestPath(entry.Body);
            if (string.IsNullOrWhiteSpace(extractedManifestPath))
            {
                diagnostics.Add(Error(
                    "ASPWA224",
                    "The entry HTML must include a manifest link in the document head.",
                    "entry.head",
                    """<link rel="manifest" href="...">""",
                    "missing",
                    "Add <appsurface:pwa-head /> to the layout used by the verified entry path."));
            }
            else if (!ResolvesToOrigin(target, target.BaseUri, extractedManifestPath, out var linkedManifestUri))
            {
                diagnostics.Add(Error(
                    "ASPWA225",
                    "The entry manifest link must resolve to the app origin.",
                    "entry.head.manifest",
                    target.Origin.ToString().TrimEnd('/'),
                    RedactUriValue(extractedManifestPath),
                    "Use an app-root-relative manifest href."));
            }
            else if (!IsUnderBasePath(target, linkedManifestUri.AbsolutePath))
            {
                diagnostics.Add(Error(
                    "ASPWA227",
                    "The entry manifest link must stay under the verified base path.",
                    "entry.head.manifest",
                    target.BasePath,
                    linkedManifestUri.AbsolutePath,
                    "Verify the externally visible base URL or keep the manifest under that path base."));
            }
            else
            {
                manifestUri = linkedManifestUri;
            }
        }
        else
        {
            diagnostics.Add(Error(
                "ASPWA201",
                "The entry path must return HTML so browsers can discover the manifest link in the document head.",
                "entry",
                "text/html",
                entry.IsSuccess ? entry.ContentType : $"HTTP {(int)entry.StatusCode}",
                "Pass --entry-path for a real app page that renders the PWA head metadata."));
        }

        var manifest = await FetchAsync(target, manifestUri, "manifest", MaxTextResponseBytes, diagnostics, cancellationToken);
        if (!manifest.IsSuccess)
        {
            diagnostics.Add(Error(
                "ASPWA202",
                $"Manifest request failed with HTTP {(int)manifest.StatusCode}.",
                "manifest",
                "2xx",
                $"HTTP {(int)manifest.StatusCode}",
                "Enable AppSurface PWA support and make sure the manifest endpoint is reachable."));
            return BuildReport(target, manifestUri, manifestDocument, iconEvidence, diagnostics);
        }

        if (!manifest.ContentType.StartsWith("application/manifest+json", StringComparison.OrdinalIgnoreCase)
            && !manifest.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "ASPWA203",
                $"Manifest content type should be application/manifest+json. Actual: {manifest.ContentType}.",
                "manifest.contentType",
                "application/manifest+json",
                manifest.ContentType,
                "Serve the generated manifest endpoint with the manifest JSON content type."));
        }

        try
        {
            manifestDocument = JsonSerializer.Deserialize<PwaManifestProbe>(manifest.Body, JsonOptions);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Error(
                "ASPWA204",
                $"Manifest JSON could not be parsed: {ex.Message}",
                "manifest.json",
                "valid-json",
                "invalid-json",
                "Inspect the generated manifest response for malformed JSON."));
        }

        if (manifestDocument is not null)
        {
            ValidateManifest(target, manifestUri, manifestDocument, options, diagnostics);
            foreach (var icon in manifestDocument.Icons ?? [])
            {
                if (string.IsNullOrWhiteSpace(icon.Source))
                {
                    iconEvidence.Add(new PwaIconEvidence(icon.Source, icon.Sizes, icon.Type, icon.Purpose, null, null, null, null, false));
                    continue;
                }

                if (!ResolvesToOrigin(target, manifestUri, icon.Source, out var iconUri))
                {
                    diagnostics.Add(Error(
                        "ASPWA214",
                        $"Icon {icon.Source} must resolve to the app origin.",
                        "manifest.icons[].src",
                        target.Origin.ToString().TrimEnd('/'),
                        RedactUriValue(icon.Source),
                        "Serve manifest icons from same-origin app-root-relative URLs."));
                    continue;
                }

                if (!IsUnderBasePath(target, iconUri.AbsolutePath))
                {
                    diagnostics.Add(Error(
                        "ASPWA228",
                        $"Icon {icon.Source} must stay under the verified base path.",
                        "manifest.icons[].src",
                        target.BasePath,
                        iconUri.AbsolutePath,
                        "Keep manifest icons under the verified path base."));
                    continue;
                }

                var iconResponse = await FetchAsync(target, iconUri, $"icon:{icon.Source}", MaxIconResponseBytes, diagnostics, cancellationToken);
                PwaImageDimensions? dimensions = null;
                if (!iconResponse.IsSuccess)
                {
                    diagnostics.Add(Error(
                        "ASPWA212",
                        $"Icon {icon.Source} returned HTTP {(int)iconResponse.StatusCode}.",
                        "manifest.icons[].src",
                        "2xx",
                        $"HTTP {(int)iconResponse.StatusCode}",
                        "Publish the icon file at the declared manifest path."));
                }
                else if (!iconResponse.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error(
                        "ASPWA213",
                        $"Icon {icon.Source} returned non-image content type {iconResponse.ContentType}.",
                        "manifest.icons[].type",
                        "image/*",
                        iconResponse.ContentType,
                        "Serve the icon with an image content type."));
                }
                else
                {
                    dimensions = TryDecodePngDimensions(iconResponse.BodyBytes);
                    if (dimensions is not null)
                    {
                        ValidateDecodedIconDimensions(icon, dimensions, options.ExpectedIcons, diagnostics);
                    }
                }

                iconEvidence.Add(
                    new PwaIconEvidence(
                        icon.Source,
                        icon.Sizes,
                        icon.Type,
                        icon.Purpose,
                        EvidencePath(iconUri),
                        iconResponse.ContentType,
                        dimensions?.Width,
                        dimensions?.Height,
                        iconResponse.IsSuccess));
            }

            ValidateExpectedIcons(manifestDocument, iconEvidence, options.ExpectedIcons, diagnostics);
        }

        await ValidateDiagnosticsAsync(target, diagnostics, cancellationToken);
        return BuildReport(target, manifestUri, manifestDocument, iconEvidence, diagnostics);
    }

    private async Task ValidateDiagnosticsAsync(
        PwaVerificationTarget target,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var diagnosticsResponse = await FetchAsync(
            target,
            new Uri(target.BaseUri, "_appsurface/pwa/status.json"),
            "diagnostics",
            MaxTextResponseBytes,
            diagnostics,
            cancellationToken);
        if (diagnosticsResponse.StatusCode == HttpStatusCode.NotFound)
        {
            diagnostics.Add(Info("ASPWA220", "AppSurface PWA diagnostics are not exposed at /_appsurface/pwa/status.json. This is expected for production defaults."));
            return;
        }

        if (!diagnosticsResponse.IsSuccess)
        {
            diagnostics.Add(Warning("ASPWA221", $"AppSurface PWA diagnostics returned HTTP {(int)diagnosticsResponse.StatusCode}."));
            return;
        }

        try
        {
            var status = JsonSerializer.Deserialize<PwaStatusProbe>(diagnosticsResponse.Body, JsonOptions);
            if (status?.OfflineEnabled == true)
            {
                await ValidateEnabledOfflineDiagnosticsAsync(target, status, diagnostics, cancellationToken);
            }
            else if (status is not null && !string.IsNullOrWhiteSpace(status.ConfiguredServiceWorkerPath))
            {
                await ProveServiceWorkerAbsentAsync(target, status.ConfiguredServiceWorkerPath!, diagnostics, cancellationToken);
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Warning("ASPWA223", $"AppSurface PWA diagnostics JSON could not be parsed: {ex.Message}"));
        }
    }

    private async Task ValidateEnabledOfflineDiagnosticsAsync(
        PwaVerificationTarget target,
        PwaStatusProbe status,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(status.ServiceWorkerPath))
        {
            diagnostics.Add(Error(
                "ASPWA222",
                "Diagnostics report offline enabled without a service worker path.",
                "diagnostics.serviceWorkerPath",
                "configured-path",
                "missing",
                "Configure PwaOptions.Offline.ServiceWorkerPath or disable offline support."));
        }
        else
        {
            var serviceWorkerPath = status.ServiceWorkerPath!;
            if (!ResolvesToOrigin(target, target.BaseUri, serviceWorkerPath, out var serviceWorkerUri))
            {
                diagnostics.Add(Error("ASPWA229", "Diagnostics service worker path must resolve to the app origin."));
            }
            else if (!IsUnderBasePath(target, serviceWorkerUri.AbsolutePath))
            {
                diagnostics.Add(Error("ASPWA230", "Diagnostics service worker path must stay under the verified base path."));
            }
            else
            {
                var serviceWorker = await FetchAsync(target, serviceWorkerUri, "service-worker", MaxTextResponseBytes, diagnostics, cancellationToken);
                if (!serviceWorker.IsSuccess)
                {
                    diagnostics.Add(Error("ASPWA226", $"Service worker {serviceWorkerPath} returned HTTP {(int)serviceWorker.StatusCode}."));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(status.OfflineFallbackPath))
        {
            diagnostics.Add(Error("ASPWA235", "Diagnostics report offline enabled without an offline fallback path."));
            return;
        }

        var offlineFallbackPath = status.OfflineFallbackPath!;
        if (!ResolvesToOrigin(target, target.BaseUri, offlineFallbackPath, out var offlineFallbackUri))
        {
            diagnostics.Add(Error("ASPWA236", "Diagnostics offline fallback path must resolve to the app origin."));
        }
        else if (!IsUnderBasePath(target, offlineFallbackUri.AbsolutePath))
        {
            diagnostics.Add(Error("ASPWA237", "Diagnostics offline fallback path must stay under the verified base path."));
        }
        else
        {
            var offlineFallback = await FetchAsync(target, offlineFallbackUri, "offline-fallback", MaxTextResponseBytes, diagnostics, cancellationToken);
            if (!offlineFallback.IsSuccess)
            {
                diagnostics.Add(Error("ASPWA238", $"Offline fallback {offlineFallbackPath} returned HTTP {(int)offlineFallback.StatusCode}."));
            }
        }
    }

    private static void ValidateManifest(
        PwaVerificationTarget target,
        Uri manifestUri,
        PwaManifestProbe manifest,
        PwaVerificationOptions options,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        RequireText(manifest.Name, "ASPWA205", "Manifest name is required.", diagnostics);
        RequireText(manifest.ShortName, "ASPWA206", "Manifest short_name is required.", diagnostics);
        RequireDisplayMode(manifest.Display, diagnostics);
        RequireSameOriginPath(target, manifestUri, manifest.StartUrl, "ASPWA208", "Manifest start_url must resolve to the app origin.", diagnostics);
        RequireSameOriginPath(target, manifestUri, manifest.Scope, "ASPWA209", "Manifest scope must resolve to the app origin.", diagnostics);
        RequireStartUrlWithinScope(target, manifestUri, manifest.StartUrl, manifest.Scope, diagnostics);
        RequireHexColor(manifest.ThemeColor, "ASPWA232", "Manifest theme_color must be a CSS hex color such as #2563eb.", diagnostics);
        RequireHexColor(manifest.BackgroundColor, "ASPWA233", "Manifest background_color must be a CSS hex color such as #ffffff.", diagnostics);
        RequireExpectedValue(options.ExpectedStartUrl, manifest.StartUrl, "ASPWA244", "Manifest start_url did not match the expected value.", "manifest.start_url", diagnostics);
        RequireExpectedValue(options.ExpectedScope, manifest.Scope, "ASPWA245", "Manifest scope did not match the expected value.", "manifest.scope", diagnostics);
        RequireExpectedValue(options.ExpectedDisplay, manifest.Display, "ASPWA246", "Manifest display did not match the expected value.", "manifest.display", diagnostics);
        RequireExpectedValue(options.ExpectedThemeColor, manifest.ThemeColor, "ASPWA247", "Manifest theme_color did not match the expected value.", "manifest.theme_color", diagnostics);
        RequireExpectedValue(options.ExpectedBackgroundColor, manifest.BackgroundColor, "ASPWA248", "Manifest background_color did not match the expected value.", "manifest.background_color", diagnostics);

        var icons = manifest.Icons ?? [];
        if (!icons.Any(icon => HasIconSizeToken(icon.Sizes, "192x192")))
        {
            diagnostics.Add(Error("ASPWA210", "Manifest must declare a 192x192 icon."));
        }

        if (!icons.Any(icon => HasIconSizeToken(icon.Sizes, "512x512")))
        {
            diagnostics.Add(Error("ASPWA211", "Manifest must declare a 512x512 icon."));
        }
    }

    private static void RequireDisplayMode(string? value, List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(Error("ASPWA207", "Manifest display is required."));
            return;
        }

        if (!IsSupportedDisplayMode(value))
        {
            diagnostics.Add(Error("ASPWA234", "Manifest display must be browser, minimal-ui, standalone, or fullscreen."));
        }
    }

    private static bool IsSupportedDisplayMode(string value)
    {
        return value is "browser" or "minimal-ui" or "standalone" or "fullscreen";
    }

    private static void RequireText(string? value, string code, string message, List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(Error(code, message, actual: "missing"));
        }
    }

    private static void RequireHexColor(
        string? value,
        string code,
        string message,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || !HexColorPattern().IsMatch(value))
        {
            diagnostics.Add(Error(code, message, "manifest.color", "#rgb-or-#rrggbb", value ?? "missing"));
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
            diagnostics.Add(Error(code, message, actual: value ?? "missing"));
            return;
        }

        if (!IsUnderBasePath(target, uri.AbsolutePath))
        {
            diagnostics.Add(Error("ASPWA231", $"{message} It must also stay under the verified base path."));
        }
    }

    private static void RequireExpectedValue(
        string? expected,
        string? actual,
        string code,
        string message,
        string subject,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        diagnostics.Add(Error(
            code,
            message,
            subject,
            expected,
            actual ?? "missing",
            "Update the app PWA options or the verifier assertion so they describe the same contract."));
    }

    private static bool ResolvesToOrigin(PwaVerificationTarget target, Uri baseUri, string value, out Uri uri)
    {
        return Uri.TryCreate(baseUri, value, out uri!)
            && IsSameOrigin(target, uri);
    }

    private static void RequireStartUrlWithinScope(
        PwaVerificationTarget target,
        Uri baseUri,
        string? startUrl,
        string? scope,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (!TryResolveVerifiedPath(target, baseUri, startUrl, out var startUri)
            || !TryResolveVerifiedPath(target, baseUri, scope, out var scopeUri))
        {
            return;
        }

        if (!PwaScopePathMatcher.IsPathWithinScope(startUri.AbsolutePath, scopeUri.AbsolutePath))
        {
            diagnostics.Add(Error("ASPWA239", "Manifest start_url must stay within manifest scope."));
        }
    }

    private static bool TryResolveVerifiedPath(
        PwaVerificationTarget target,
        Uri baseUri,
        string? value,
        out Uri uri)
    {
        uri = default!;
        return !string.IsNullOrWhiteSpace(value)
            && ResolvesToOrigin(target, baseUri, value, out uri)
            && IsUnderBasePath(target, uri.AbsolutePath);
    }

    private async Task ProveServiceWorkerAbsentAsync(
        PwaVerificationTarget target,
        string serviceWorkerPath,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!ResolvesToOrigin(target, target.BaseUri, serviceWorkerPath, out var serviceWorkerUri)
            || !IsUnderBasePath(target, serviceWorkerUri.AbsolutePath))
        {
            return;
        }

        var serviceWorker = await FetchAsync(target, serviceWorkerUri, "service-worker-absence", MaxTextResponseBytes, diagnostics, cancellationToken);
        if (serviceWorker.StatusCode == HttpStatusCode.NotFound)
        {
            diagnostics.Add(Info(
                "ASPWA256",
                $"Offline is disabled and service worker {serviceWorkerPath} is not mapped."));
        }
        else
        {
            diagnostics.Add(Error(
                "ASPWA240",
                $"Diagnostics report offline disabled, but service worker {serviceWorkerPath} is still reachable.",
                "diagnostics.configuredServiceWorkerPath",
                "404",
                $"HTTP {(int)serviceWorker.StatusCode}",
                "Remove the service-worker endpoint or enable offline diagnostics intentionally."));
        }
    }

    private async Task<PwaFetchedResponse> FetchAsync(
        PwaVerificationTarget target,
        Uri requestUri,
        string subject,
        int maxBytes,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var redirects = new List<PwaRedirectEvidence>();
        var currentUri = requestUri;
        for (var i = 0; i <= MaxRedirects; i++)
        {
            var response = await _httpClient.GetAsync(currentUri, maxBytes, cancellationToken);
            if (response.BodyTruncated)
            {
                diagnostics.Add(Warning(
                    "ASPWA265",
                    $"Response body for {subject} exceeded the verifier read limit.",
                    subject,
                    $"<={maxBytes} bytes",
                    $">{maxBytes} bytes",
                    "Serve a smaller verifier-facing response or inspect the endpoint manually."));
            }

            if (!IsRedirect(response.StatusCode))
            {
                return new PwaFetchedResponse(currentUri, response, redirects);
            }

            if (string.IsNullOrWhiteSpace(response.RedirectLocation))
            {
                diagnostics.Add(Error("ASPWA260", $"Redirect response for {subject} omitted a Location header.", subject, "Location", "missing"));
                return new PwaFetchedResponse(currentUri, response, redirects);
            }

            if (!Uri.TryCreate(currentUri, response.RedirectLocation, out var nextUri))
            {
                diagnostics.Add(Error("ASPWA261", $"Redirect response for {subject} had an invalid Location header.", subject, "valid-uri", RedactUriValue(response.RedirectLocation)));
                return new PwaFetchedResponse(currentUri, response, redirects);
            }

            redirects.Add(new PwaRedirectEvidence(EvidencePath(currentUri), EvidencePath(nextUri), (int)response.StatusCode));
            diagnostics.Add(Info(
                "ASPWA266",
                $"Verifier followed redirect for {subject}.",
                subject,
                EvidencePath(currentUri),
                EvidencePath(nextUri)));
            if (!IsSameOrigin(target, nextUri))
            {
                diagnostics.Add(Error(
                    "ASPWA262",
                    $"Redirect response for {subject} leaves the verified origin.",
                    subject,
                    target.Origin.ToString().TrimEnd('/'),
                    RedactUriValue(nextUri.ToString()),
                    "Keep PWA verifier redirects on the verified app origin."));
                return new PwaFetchedResponse(currentUri, response, redirects);
            }

            if (!IsUnderBasePath(target, nextUri.AbsolutePath))
            {
                diagnostics.Add(Error(
                    "ASPWA263",
                    $"Redirect response for {subject} leaves the verified base path.",
                    subject,
                    target.BasePath,
                    nextUri.AbsolutePath,
                    "Keep PWA verifier redirects under the verified base URL path."));
                return new PwaFetchedResponse(currentUri, response, redirects);
            }

            currentUri = nextUri;
        }

        diagnostics.Add(Error("ASPWA264", $"Redirect response for {subject} exceeded {MaxRedirects} hops.", subject, $"<={MaxRedirects}", $">{MaxRedirects}"));
        return new PwaFetchedResponse(currentUri, new PwaHttpResponse(HttpStatusCode.TooManyRequests, string.Empty, [], null, false), redirects);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var value = (int)statusCode;
        return value is >= 300 and <= 399;
    }

    private static bool IsSameOrigin(PwaVerificationTarget target, Uri uri)
    {
        return uri.Scheme == target.Origin.Scheme
            && uri.Host == target.Origin.Host
            && uri.Port == target.Origin.Port;
    }

    private static void ValidateExpectedIcons(
        PwaManifestProbe manifest,
        IReadOnlyList<PwaIconEvidence> iconEvidence,
        IReadOnlyList<PwaExpectedIcon> expectedIcons,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        foreach (var expectedIcon in expectedIcons)
        {
            var matchingManifestIcon = (manifest.Icons ?? []).FirstOrDefault(icon =>
                HasIconSizeToken(icon.Sizes, expectedIcon.Size)
                && (string.IsNullOrWhiteSpace(expectedIcon.Purpose) || HasToken(icon.Purpose, expectedIcon.Purpose)));
            if (matchingManifestIcon is null)
            {
                diagnostics.Add(Error(
                    "ASPWA241",
                    "Manifest did not include an expected icon declaration.",
                    "manifest.icons",
                    expectedIcon.ToString(),
                    "missing",
                    "Add a manifest icon with the expected size and purpose."));
                continue;
            }

            var evidence = iconEvidence.FirstOrDefault(icon =>
                string.Equals(icon.Source, matchingManifestIcon.Source, StringComparison.Ordinal));
            if (evidence is { Fetched: true, Width: null })
            {
                diagnostics.Add(Warning(
                    "ASPWA243",
                    "The verifier fetched an expected icon but could not decode PNG dimensions.",
                    "manifest.icons[].src",
                    expectedIcon.Size,
                    evidence.ContentType ?? "unknown",
                    "Use PNG icons when CI needs dimension proof; SVG icons remain reachable but dimensions are not decoded."));
            }
        }
    }

    private static void ValidateDecodedIconDimensions(
        PwaIconProbe icon,
        PwaImageDimensions dimensions,
        IReadOnlyList<PwaExpectedIcon> expectedIcons,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        foreach (var expectedSize in GetIconSizeTokens(icon.Sizes))
        {
            if (!TryParseIconSize(expectedSize, out var expectedWidth, out var expectedHeight)
                || (dimensions.Width == expectedWidth && dimensions.Height == expectedHeight))
            {
                continue;
            }

            diagnostics.Add(Error(
                "ASPWA242",
                "Icon decoded dimensions do not match a declared manifest size.",
                "manifest.icons[].sizes",
                expectedSize,
                $"{dimensions.Width}x{dimensions.Height}",
                "Regenerate the icon at the declared dimensions or correct the manifest size token."));
        }

        foreach (var expectedIcon in expectedIcons.Where(expected => HasIconSizeToken(icon.Sizes, expected.Size)))
        {
            if (!TryParseIconSize(expectedIcon.Size, out var expectedWidth, out var expectedHeight)
                || (dimensions.Width == expectedWidth && dimensions.Height == expectedHeight))
            {
                continue;
            }

            diagnostics.Add(Error(
                "ASPWA242",
                "Icon decoded dimensions do not match the explicit verifier assertion.",
                "manifest.icons[].sizes",
                expectedIcon.Size,
                $"{dimensions.Width}x{dimensions.Height}",
                "Regenerate the icon at the asserted dimensions or update --expect-icon."));
        }
    }

    private static PwaImageDimensions? TryDecodePngDimensions(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (bytes.Length < 24 || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        return width > 0 && height > 0 ? new PwaImageDimensions(width, height) : null;
    }

    private static bool HasIconSizeToken(string? sizes, string expected)
    {
        return GetIconSizeTokens(sizes).Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetIconSizeTokens(string? sizes)
    {
        return string.IsNullOrWhiteSpace(sizes)
            ? []
            : sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasToken(string? value, string? expected)
    {
        return !string.IsNullOrWhiteSpace(expected)
            && !string.IsNullOrWhiteSpace(value)
            && value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseIconSize(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        var separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return separator > 0
            && int.TryParse(value[..separator], out width)
            && int.TryParse(value[(separator + 1)..], out height)
            && width > 0
            && height > 0;
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
        var head = HeadRegex().Match(html);
        if (!head.Success)
        {
            return null;
        }

        foreach (var tag in LinkTagRegex().Matches(head.Groups["content"].Value).Cast<Match>().Select(link => link.Value))
        {
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
        PwaManifestProbe? manifest,
        IReadOnlyList<PwaIconEvidence> iconEvidence,
        IReadOnlyList<PwaVerificationDiagnostic> diagnostics)
    {
        return new PwaVerificationReport(
            2,
            diagnostics.All(d => d.Severity != "error"),
            target.BaseUri.ToString().TrimEnd('/'),
            target.BaseUri.ToString().TrimEnd('/'),
            target.EntryPath,
            target.EntryUri.ToString(),
            EvidencePath(manifestUri),
            manifest?.StartUrl,
            manifest?.Scope,
            manifest?.Display,
            manifest?.ThemeColor,
            manifest?.BackgroundColor,
            iconEvidence,
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

    private static string RedactUriValue(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value.Split('?', '#')[0];
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, uri.AbsolutePath).Uri.ToString();
    }

    private static string EvidencePath(Uri uri)
    {
        return uri.AbsolutePath;
    }

    private static PwaVerificationDiagnostic Error(
        string code,
        string message,
        string? subject = null,
        string? expected = null,
        string? actual = null,
        string? fix = null) => new(code, "error", message, subject, expected, actual, fix);

    private static PwaVerificationDiagnostic Warning(
        string code,
        string message,
        string? subject = null,
        string? expected = null,
        string? actual = null,
        string? fix = null) => new(code, "warning", message, subject, expected, actual, fix);

    private static PwaVerificationDiagnostic Info(
        string code,
        string message,
        string? subject = null,
        string? expected = null,
        string? actual = null,
        string? fix = null) => new(code, "info", message, subject, expected, actual, fix);

    [GeneratedRegex("""<head\b[^>]*>(?<content>.*?)</head>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRegex();

    [GeneratedRegex("""<link\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex("""\brel\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttributeRegex();

    [GeneratedRegex("""\bhref\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttributeRegex();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColorPattern();
}

internal interface IPwaVerificationHttpClient
{
    Task<PwaHttpResponse> GetAsync(Uri uri, int maxBodyBytes, CancellationToken cancellationToken);
}

internal sealed class PwaVerificationHttpClient(HttpClient httpClient) : IPwaVerificationHttpClient
{
    public async Task<PwaHttpResponse> GetAsync(Uri uri, int maxBodyBytes, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var (bodyBytes, truncated) = await ReadBoundedBodyAsync(response.Content, maxBodyBytes, cancellationToken);
        return new PwaHttpResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            bodyBytes,
            response.Headers.Location?.OriginalString,
            truncated);
    }

    private static async Task<(byte[] Body, bool Truncated)> ReadBoundedBodyAsync(
        HttpContent content,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBodyBytes, 81920));
        var chunk = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            total += read;
            if (total > maxBodyBytes)
            {
                var allowed = read - (total - maxBodyBytes);
                if (allowed > 0)
                {
                    buffer.Write(chunk, 0, allowed);
                }

                return (buffer.ToArray(), true);
            }

            buffer.Write(chunk, 0, read);
        }
    }
}

internal sealed record PwaHttpResponse(
    HttpStatusCode StatusCode,
    string ContentType,
    byte[] BodyBytes,
    string? RedirectLocation,
    bool BodyTruncated)
{
    public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;

    public string Body => Encoding.UTF8.GetString(BodyBytes);
}

internal sealed record PwaVerificationReport(
    int SchemaVersion,
    bool Passed,
    string Origin,
    string BaseUrl,
    string EntryPath,
    string EntryUrl,
    string ManifestPath,
    string? StartUrl,
    string? Scope,
    string? Display,
    string? ThemeColor,
    string? BackgroundColor,
    IReadOnlyList<PwaIconEvidence> Icons,
    IReadOnlyList<PwaVerificationDiagnostic> Diagnostics);

internal sealed record PwaVerificationDiagnostic(
    string Code,
    string Severity,
    string Message,
    string? Subject = null,
    string? Expected = null,
    string? Actual = null,
    string? Fix = null,
    string? DocsUrl = null);

internal sealed record PwaVerificationTarget(Uri Origin, Uri BaseUri, string BasePath, string EntryPath, Uri EntryUri)
{
    public static PwaVerificationTarget Create(Uri url, string entryPath = "/")
    {
        if (!IsSafeEntryPath(entryPath))
        {
            throw new ArgumentException("--entry-path must be an app-root-relative path without query strings, fragments, traversal, or absolute URL syntax.");
        }

        if (!string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            throw new ArgumentException("--url or --base-url must not include a query string or fragment. Use --entry-path for the app page path.");
        }

        var origin = new UriBuilder(url.Scheme, url.Host, url.Port).Uri;
        var basePath = string.IsNullOrWhiteSpace(url.AbsolutePath) || url.AbsolutePath == "/"
            ? "/"
            : url.AbsolutePath.TrimEnd('/') + "/";
        var normalizedEntryPath = string.IsNullOrWhiteSpace(entryPath) ? "/" : entryPath;
        var entryPathWithBase = normalizedEntryPath == "/"
            ? basePath
            : basePath + normalizedEntryPath.TrimStart('/');
        var entryUri = new Uri(origin, entryPathWithBase);
        return new PwaVerificationTarget(origin, new Uri(origin, basePath), basePath, normalizedEntryPath, entryUri);
    }

    private static bool IsSafeEntryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        if (!string.Equals(path, value, StringComparison.Ordinal)
            || !value.StartsWith('/')
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains('?')
            || value.Contains('#')
            || value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '{' or '}'))
        {
            return false;
        }

        return !value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => ContainsMalformedEscape(segment)
                || string.Equals(Uri.UnescapeDataString(segment), "..", StringComparison.Ordinal));
    }

    private static bool ContainsMalformedEscape(string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] != '%')
            {
                continue;
            }

            if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2]))
            {
                return true;
            }

            i += 2;
        }

        return false;
    }
}

internal sealed record PwaVerificationOptions(
    Uri BaseUrl,
    string EntryPath,
    string? ExpectedStartUrl,
    string? ExpectedScope,
    string? ExpectedDisplay,
    string? ExpectedThemeColor,
    string? ExpectedBackgroundColor,
    IReadOnlyList<PwaExpectedIcon> ExpectedIcons)
{
    public static PwaVerificationOptions Create(
        Uri baseUrl,
        string entryPath = "/",
        string? expectedStartUrl = null,
        string? expectedScope = null,
        string? expectedDisplay = null,
        string? expectedThemeColor = null,
        string? expectedBackgroundColor = null,
        IReadOnlyList<string>? expectedIcons = null)
    {
        return new PwaVerificationOptions(
            baseUrl,
            string.IsNullOrWhiteSpace(entryPath) ? "/" : entryPath,
            expectedStartUrl,
            expectedScope,
            expectedDisplay,
            expectedThemeColor,
            expectedBackgroundColor,
            (expectedIcons ?? []).Select(PwaExpectedIcon.Parse).ToArray());
    }
}

internal sealed partial record PwaExpectedIcon(string Size, string? Purpose)
{
    public static PwaExpectedIcon Parse(string value)
    {
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !IconSizeAssertionPattern().IsMatch(parts[0]))
        {
            throw new ArgumentException("--expect-icon must use WIDTHxHEIGHT or WIDTHxHEIGHT:purpose, for example 192x192 or 512x512:maskable.");
        }

        if (parts.Length == 2 && string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new ArgumentException("--expect-icon purpose must not be blank.");
        }

        return new PwaExpectedIcon(parts[0], parts.Length == 2 ? parts[1] : null);
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Purpose) ? Size : $"{Size}:{Purpose}";

    [GeneratedRegex("^[1-9][0-9]*x[1-9][0-9]*$", RegexOptions.IgnoreCase)]
    private static partial Regex IconSizeAssertionPattern();
}

internal sealed record PwaFetchedResponse(Uri FinalUri, PwaHttpResponse Response, IReadOnlyList<PwaRedirectEvidence> Redirects)
{
    public HttpStatusCode StatusCode => Response.StatusCode;

    public string ContentType => Response.ContentType;

    public string Body => Response.Body;

    public byte[] BodyBytes => Response.BodyBytes;

    public bool IsSuccess => Response.IsSuccess;
}

internal sealed record PwaRedirectEvidence(string FromPath, string ToPath, int StatusCode);

internal sealed record PwaImageDimensions(int Width, int Height);

internal sealed record PwaIconEvidence(
    string? Source,
    string? Sizes,
    string? Type,
    string? Purpose,
    string? Path,
    string? ContentType,
    int? Width,
    int? Height,
    bool Fetched);

internal sealed record PwaManifestProbe(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("short_name")] string? ShortName,
    [property: JsonPropertyName("start_url")] string? StartUrl,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("display")] string? Display,
    [property: JsonPropertyName("theme_color")] string? ThemeColor,
    [property: JsonPropertyName("background_color")] string? BackgroundColor,
    [property: JsonPropertyName("icons")] IReadOnlyList<PwaIconProbe>? Icons);

internal sealed record PwaIconProbe(
    [property: JsonPropertyName("src")] string? Source,
    [property: JsonPropertyName("sizes")] string? Sizes,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("purpose")] string? Purpose);

internal sealed record PwaStatusProbe(
    bool Enabled,
    bool OfflineEnabled,
    string? ServiceWorkerPath,
    string? OfflineFallbackPath,
    string? ConfiguredServiceWorkerPath);
