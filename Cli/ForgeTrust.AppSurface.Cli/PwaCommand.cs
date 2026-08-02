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
/// Verifies that a running web app exposes AppSurface-compatible PWA install or push-readiness evidence.
/// </summary>
[Command("pwa verify", Description = "Verify PWA install metadata or privacy-safe server-known push readiness for a running AppSurface Web app.")]
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
    /// Gets the app-root-relative entry path whose HTML should expose the manifest link or push registration helper.
    /// </summary>
    [CommandOption("entry-path", Description = "App-root-relative page path used for manifest (install), registration-helper (push), or both (all) discovery.")]
    public string EntryPath { get; set; } = "/";

    /// <summary>
    /// Gets the verification surface. Install retains the schema-v2 default contract.
    /// </summary>
    [CommandOption("surface", Description = "Verification surface: install (default), push, or all.")]
    public string Surface { get; set; } = "install";

    /// <summary>
    /// Gets the expected server-known push posture for push or all verification.
    /// </summary>
    [CommandOption("expect-push", Description = "Expected push posture for push or all: enabled (default) or disabled.")]
    public string? ExpectedPush { get; set; }

    /// <summary>
    /// Gets the app-root-relative PWA diagnostics base path.
    /// </summary>
    [CommandOption("diagnostics-path", Description = "App-root-relative PWA diagnostics base path. Defaults to /_appsurface/pwa.")]
    public string? DiagnosticsPath { get; set; }

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
                ExpectedIcons,
                Surface,
                ExpectedPush,
                DiagnosticsPath);
            _ = PwaVerificationTarget.Create(options.BaseUrl, options.EntryPath);
        }
        catch (ArgumentException ex)
        {
            throw new CommandException(ex.Message);
        }

        if (options.Surface == PwaVerificationSurface.Install)
        {
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

            return;
        }

        var v3Report = await _verifier.VerifySurfaceAsync(options, console.RegisterCancellationHandler());
        if (Json)
        {
            await console.Output.WriteLineAsync(JsonSerializer.Serialize(v3Report, PwaVerifier.V3JsonOptions));
        }
        else
        {
            await WriteTextReportAsync(console, v3Report);
        }

        if (!v3Report.Passed)
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

    private static async Task WriteTextReportAsync(IConsole console, PwaVerificationV3Report report)
    {
        await console.Output.WriteLineAsync(report.Passed
            ? "PWA verification passed."
            : "PWA verification failed.");
        await console.Output.WriteLineAsync($"Surface: {report.Surface}");
        await console.Output.WriteLineAsync($"Entry: {report.EntryUrl}");
        await console.Output.WriteLineAsync($"Push expected: {report.Push.Expected}; observed: {FormatBoolean(report.Push.Enabled)}.");
        await console.Output.WriteLineAsync($"Push configuration: {report.Push.ConfigurationStatus}; route mapping: {report.Push.RouteMapping}.");
        foreach (var diagnostic in report.Diagnostics)
        {
            var details = string.IsNullOrWhiteSpace(diagnostic.Subject)
                ? string.Empty
                : $" [{diagnostic.Subject}]";
            await console.Output.WriteLineAsync(
                $"{diagnostic.Severity.ToUpperInvariant()} {diagnostic.Code}{details}: {diagnostic.Message}");
            if (!string.IsNullOrWhiteSpace(diagnostic.Fix))
            {
                await console.Output.WriteLineAsync($"  Fix: {diagnostic.Fix}");
            }

            if (!string.IsNullOrWhiteSpace(diagnostic.DocsUrl))
            {
                await console.Output.WriteLineAsync($"  Docs: {diagnostic.DocsUrl}");
            }
        }
    }

    private static string FormatBoolean(bool? value)
    {
        if (!value.HasValue)
        {
            return "unknown";
        }

        return value.Value ? "enabled" : "disabled";
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

    internal static readonly JsonSerializerOptions V3JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
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
        if (!entry.RedirectLimitExceeded
            && entry.IsSuccess
            && entry.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
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
        else if (!entry.RedirectLimitExceeded)
        {
            diagnostics.Add(Error(
                "ASPWA201",
                "The entry path must return HTML so browsers can discover the manifest link in the document head.",
                "entry",
                "text/html",
                entry.IsSuccess ? entry.ContentType : $"HTTP {(int)entry.StatusCode}",
                "Pass --entry-path for a real app page that renders the PWA head metadata."));
        }

        await ValidateDiagnosticsAsync(target, options.DiagnosticsPath, diagnostics, cancellationToken);

        var manifest = await FetchAsync(target, manifestUri, "manifest", MaxTextResponseBytes, diagnostics, cancellationToken);
        if (manifest.RedirectLimitExceeded)
        {
            return BuildReport(target, manifestUri, manifestDocument, iconEvidence, diagnostics);
        }

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
                if (!iconResponse.RedirectLimitExceeded && !iconResponse.IsSuccess)
                {
                    diagnostics.Add(Error(
                        "ASPWA212",
                        $"Icon {icon.Source} returned HTTP {(int)iconResponse.StatusCode}.",
                        "manifest.icons[].src",
                        "2xx",
                        $"HTTP {(int)iconResponse.StatusCode}",
                        "Publish the icon file at the declared manifest path."));
                }
                else if (!iconResponse.RedirectLimitExceeded
                    && !iconResponse.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error(
                        "ASPWA213",
                        $"Icon {icon.Source} returned non-image content type {iconResponse.ContentType}.",
                        "manifest.icons[].type",
                        "image/*",
                        iconResponse.ContentType,
                        "Serve the icon with an image content type."));
                }
                else if (!iconResponse.RedirectLimitExceeded)
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

        return BuildReport(target, manifestUri, manifestDocument, iconEvidence, diagnostics);
    }

    /// <summary>
    /// Verifies the additive schema-v3 push or combined readiness surface without modifying the default install path.
    /// </summary>
    internal async Task<PwaVerificationV3Report> VerifySurfaceAsync(
        PwaVerificationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Surface == PwaVerificationSurface.Install)
        {
            throw new ArgumentException("Schema-v3 verification requires the push or all surface.", nameof(options));
        }

        var target = PwaVerificationTarget.Create(options.BaseUrl, options.EntryPath);
        PwaVerificationReport? install = null;
        var diagnostics = new List<PwaVerificationDiagnostic>();
        if (options.Surface == PwaVerificationSurface.All)
        {
            install = await VerifyAsync(options, cancellationToken);
            diagnostics.AddRange(install.Diagnostics);
        }

        var pushResult = await VerifyPushAsync(target, options.ExpectedPush, options.DiagnosticsPath, cancellationToken);
        diagnostics.AddRange(pushResult.Diagnostics);
        return new PwaVerificationV3Report(
            3,
            diagnostics.All(diagnostic => diagnostic.Severity != "error"),
            options.Surface.ToString().ToLowerInvariant(),
            target.Origin.ToString().TrimEnd('/'),
            target.BaseUri.ToString().TrimEnd('/'),
            target.EntryPath,
            target.EntryUri.ToString(),
            install is null ? null : BuildInstallEvidence(install),
            pushResult.Evidence,
            diagnostics);
    }

    private async Task<PwaPushVerificationResult> VerifyPushAsync(
        PwaVerificationTarget target,
        PwaExpectedPush expectedPush,
        string diagnosticsPath,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<PwaVerificationDiagnostic>();
        var entry = await FetchAsync(target, target.EntryUri, "entry", MaxTextResponseBytes, diagnostics, cancellationToken);
        var statusResponse = await FetchAsync(
            target,
            GetDiagnosticsStatusUri(target, diagnosticsPath),
            "diagnostics",
            MaxTextResponseBytes,
            diagnostics,
            cancellationToken);

        PwaStatusProbe? status = null;
        if (statusResponse.RedirectLimitExceeded || !statusResponse.IsSuccess)
        {
            diagnostics.Add(PushError(
                "ASPWA270",
                "Push verification requires the existing PWA diagnostics status document.",
                "diagnostics",
                "exposed-status-json",
                statusResponse.RedirectLimitExceeded ? "redirect-limit" : $"HTTP {(int)statusResponse.StatusCode}",
                "Enable the existing PWA diagnostics endpoint through its explicit exposure policy."));
        }
        else
        {
            try
            {
                status = JsonSerializer.Deserialize<PwaStatusProbe>(statusResponse.Body, JsonOptions);
                if (status is null)
                {
                    diagnostics.Add(PushError(
                        "ASPWA271",
                        "PWA diagnostics did not contain a status document.",
                        "diagnostics.json",
                        "status-document",
                        "null",
                        "Upgrade the Web package and expose the generated PWA diagnostics endpoint."));
                }
            }
            catch (JsonException)
            {
                diagnostics.Add(PushError(
                    "ASPWA271",
                    "PWA diagnostics JSON is not a safe current readiness document.",
                    "diagnostics.json",
                    "valid-current-json",
                    "invalid-json",
                    "Upgrade the Web package and expose its generated PWA diagnostics endpoint."));
            }
        }

        var worker = new PwaWorkerEvidence(
            status?.WorkerPath,
            status?.WorkerScope,
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated");
        var helper = new PwaRegistrationHelperEvidence(
            status?.RegistrationHelperPath,
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated");
        var configurationStatus = "not-evaluated";
        var vapid = new PwaVapidEvidence(null, null);
        var routeMapping = "not-evaluated";

        if (status is null)
        {
            return new PwaPushVerificationResult(
                BuildPushEvidence(expectedPush, null, configurationStatus, worker, helper, vapid, routeMapping),
                diagnostics);
        }

        if (expectedPush == PwaExpectedPush.Disabled)
        {
            if (status.PushEnabled)
            {
                diagnostics.Add(PushError(
                    "ASPWA272",
                    "The host reports push enabled while disabled push readiness was requested.",
                    "diagnostics.pushEnabled",
                    "false",
                    "true",
                    "Disable Pwa.Push.Enabled or verify with --expect-push enabled."));
            }

            if (!string.IsNullOrWhiteSpace(status.RegistrationHelperPath))
            {
                diagnostics.Add(PushError(
                    "ASPWA273",
                    "The host reports a registration helper while disabled push readiness was requested.",
                    "diagnostics.registrationHelperPath",
                    "absent",
                    "present",
                    "Disable Pwa.Push.Enabled so the generated registration helper is not exposed."));
            }

            if (!entry.RedirectLimitExceeded
                && entry.IsSuccess
                && entry.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsPwaRegistrationHelper(entry.Body))
                {
                    diagnostics.Add(PushError(
                        "ASPWA274",
                        "The entry document still references an AppSurface push registration helper.",
                        "entry.head.registrationHelper",
                        "absent",
                        "present",
                        "Remove the generated PWA push helper by disabling Pwa.Push.Enabled."));
                }
            }
            else if (!entry.RedirectLimitExceeded)
            {
                diagnostics.Add(PushError(
                    "ASPWA275",
                    "Push verification requires an HTML entry document to prove helper absence.",
                    "entry",
                    "text/html",
                    entry.IsSuccess ? entry.ContentType : $"HTTP {(int)entry.StatusCode}",
                    "Pass --entry-path for a page rendered by the AppSurface PWA layout."));
            }

            return new PwaPushVerificationResult(
                BuildPushEvidence(expectedPush, status.PushEnabled, configurationStatus, worker, helper, vapid, routeMapping),
                diagnostics);
        }

        if (!status.PushEnabled)
        {
            diagnostics.Add(PushError(
                "ASPWA276",
                "The host does not report enabled push worker handling.",
                "diagnostics.pushEnabled",
                "true",
                "false",
                "Enable Pwa.Push.Enabled before requesting enabled push readiness."));
        }

        if (!status.WorkerEnabled)
        {
            diagnostics.Add(PushError(
                "ASPWA282",
                "Enabled push diagnostics must report enabled shared-worker handling.",
                "diagnostics.workerEnabled",
                "true",
                "false",
                "Enable the generated shared worker before requesting enabled push readiness."));
        }

        if (string.IsNullOrWhiteSpace(status.WorkerPath) || string.IsNullOrWhiteSpace(status.WorkerScope))
        {
            diagnostics.Add(PushError(
                "ASPWA277",
                "Enabled push diagnostics must include a worker path and scope.",
                "diagnostics.worker",
                "path-and-scope",
                "missing",
                "Use the generated AppSurface shared worker configuration."));
        }

        if (string.IsNullOrWhiteSpace(status.RegistrationHelperPath))
        {
            diagnostics.Add(PushError(
                "ASPWA278",
                "Enabled push diagnostics must include a registration-helper path.",
                "diagnostics.registrationHelperPath",
                "configured-path",
                "missing",
                "Use the generated AppSurface PWA head metadata on the verified entry page."));
        }

        var readiness = NormalizePushReadiness(status.PushReadiness, diagnostics);
        configurationStatus = readiness.ConfigurationStatus;
        vapid = new PwaVapidEvidence(readiness.ActiveVapidKeyId, readiness.PublicKeyFingerprint);
        routeMapping = readiness.RouteMapped switch
        {
            true => "mapped",
            false => "not-mapped",
            _ => "not-evaluated"
        };
        if (configurationStatus == "unavailable")
        {
            diagnostics.Add(PushError(
                "ASPWA279",
                "Push diagnostics expose an unavailable readiness contributor, so its safe posture cannot be trusted.",
                "diagnostics.pushReadiness.configurationStatus",
                "configured-or-not-configured",
                configurationStatus,
                "Fix the Web readiness provider or expose a current generated PWA diagnostics document."));
        }
        else if (configurationStatus == "configured" && readiness.RouteMapped is not true)
        {
            diagnostics.Add(PushError(
                "ASPWA280",
                "The optional Push subscription rail has not been mapped.",
                "diagnostics.pushReadiness.routeMapped",
                "true",
                "false",
                "Map the package-owned Push endpoints before verifying readiness."));
        }

        if (!entry.RedirectLimitExceeded
            && entry.IsSuccess
            && entry.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(status.RegistrationHelperPath)
            && !string.IsNullOrWhiteSpace(status.WorkerPath)
            && !string.IsNullOrWhiteSpace(status.WorkerScope))
        {
            helper = await VerifyRegistrationHelperAsync(
                target,
                entry,
                status.RegistrationHelperPath,
                status.WorkerPath,
                status.WorkerScope,
                diagnostics,
                cancellationToken);
        }
        else if (!entry.RedirectLimitExceeded)
        {
            diagnostics.Add(PushError(
                "ASPWA281",
                "Enabled push verification requires an HTML entry document and complete worker diagnostics.",
                "entry",
                "html-and-worker-metadata",
                entry.IsSuccess ? entry.ContentType : $"HTTP {(int)entry.StatusCode}",
                "Use a generated AppSurface PWA entry page and expose current diagnostics."));
        }

        if (!string.IsNullOrWhiteSpace(status.WorkerPath) && !string.IsNullOrWhiteSpace(status.WorkerScope))
        {
            worker = await VerifyWorkerAsync(target, status.WorkerPath, status.WorkerScope, diagnostics, cancellationToken);
        }

        return new PwaPushVerificationResult(
            BuildPushEvidence(expectedPush, status.PushEnabled, configurationStatus, worker, helper, vapid, routeMapping),
            diagnostics);
    }

    private async Task ValidateDiagnosticsAsync(
        PwaVerificationTarget target,
        string diagnosticsPath,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var diagnosticsUri = GetDiagnosticsStatusUri(target, diagnosticsPath);
        var diagnosticsResponse = await FetchAsync(
            target,
            diagnosticsUri,
            "diagnostics",
            MaxTextResponseBytes,
            diagnostics,
            cancellationToken);
        if (diagnosticsResponse.RedirectLimitExceeded)
        {
            return;
        }

        if (diagnosticsResponse.StatusCode == HttpStatusCode.NotFound)
        {
            diagnostics.Add(Info(
                "ASPWA220",
                $"AppSurface PWA diagnostics are not exposed at {EvidencePath(diagnosticsUri)}. This is expected for production defaults."));
            return;
        }

        if (!diagnosticsResponse.IsSuccess)
        {
            diagnostics.Add(Warning(
                "ASPWA221",
                $"AppSurface PWA diagnostics at {EvidencePath(diagnosticsUri)} returned HTTP {(int)diagnosticsResponse.StatusCode}."));
            return;
        }

        try
        {
            var status = JsonSerializer.Deserialize<PwaStatusProbe>(diagnosticsResponse.Body, JsonOptions);
            if (status?.PushEnabled == true)
            {
                diagnostics.Add(Info(
                    "ASPWA257",
                    "Push service-worker configuration was observed. Registration, permission, subscription, and delivery were not evaluated."));
            }

            if (status?.OfflineEnabled == true)
            {
                await ValidateEnabledOfflineDiagnosticsAsync(target, status, diagnostics, cancellationToken);
            }
            else if (status is not null
                && status.WorkerEnabled is not true
                && !string.IsNullOrWhiteSpace(status.ConfiguredServiceWorkerPath))
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
                if (!serviceWorker.RedirectLimitExceeded && !serviceWorker.IsSuccess)
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
            if (!offlineFallback.RedirectLimitExceeded && !offlineFallback.IsSuccess)
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
        RequireText(manifest.Name, "manifest.name", "ASPWA205", "Manifest name is required.", diagnostics);
        RequireText(manifest.ShortName, "manifest.short_name", "ASPWA206", "Manifest short_name is required.", diagnostics);
        RequireDisplayMode(manifest.Display, diagnostics);
        RequireSameOriginPath(target, manifestUri, manifest.StartUrl, "manifest.start_url", "ASPWA208", "Manifest start_url must resolve to the app origin.", diagnostics);
        RequireSameOriginPath(target, manifestUri, manifest.Scope, "manifest.scope", "ASPWA209", "Manifest scope must resolve to the app origin.", diagnostics);
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

    private static void RequireText(
        string? value,
        string subject,
        string code,
        string message,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(Error(code, message, subject, "non-empty", "missing"));
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
        string subject,
        string code,
        string message,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !ResolvesToOrigin(target, baseUri, value, out var uri))
        {
            diagnostics.Add(Error(code, message, subject, target.Origin.ToString().TrimEnd('/'), value ?? "missing"));
            return;
        }

        if (!IsUnderBasePath(target, uri.AbsolutePath))
        {
            diagnostics.Add(Error(
                "ASPWA231",
                $"{message} It must also stay under the verified base path.",
                subject,
                target.BasePath,
                uri.AbsolutePath));
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
        if (serviceWorker.RedirectLimitExceeded)
        {
            return;
        }

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

    private async Task<PwaWorkerEvidence> VerifyWorkerAsync(
        PwaVerificationTarget target,
        string workerPath,
        string workerScope,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!ResolvesToOrigin(target, target.BaseUri, workerPath, out var workerUri)
            || !ResolvesToOrigin(target, target.BaseUri, workerScope, out var workerScopeUri)
            || !IsUnderBasePath(target, workerUri.AbsolutePath)
            || !IsUnderBasePath(target, workerScopeUri.AbsolutePath)
            || !PwaScopePathMatcher.IsPathWithinScope(workerScopeUri.AbsolutePath, target.BasePath)
            || !PwaScopePathMatcher.IsPathWithinScope(workerUri.AbsolutePath, workerScopeUri.AbsolutePath))
        {
            diagnostics.Add(PushError(
                "ASPWA283",
                "The diagnostics worker path and scope must resolve under the verified app origin and base path.",
                "diagnostics.worker",
                "same-origin-path-and-scope",
                "out-of-base",
                "Expose the generated shared worker and its scope beneath the externally verified application base URL."));
            return new PwaWorkerEvidence(workerPath, workerScope, "failed", "failed", "failed", "failed");
        }

        var response = await FetchNoRedirectAsync(target, workerUri, "push-worker", MaxTextResponseBytes, diagnostics, cancellationToken);
        if (!response.IsSuccess || IsRedirect(response.StatusCode) || response.Response.BodyTruncated)
        {
            if (!response.IsSuccess && !IsRedirect(response.StatusCode))
            {
                diagnostics.Add(PushError(
                    "ASPWA284",
                    "The push worker did not return a successful response.",
                    "worker.fetch",
                    "2xx",
                    $"HTTP {(int)response.StatusCode}",
                    "Map the generated shared worker at the diagnostics worker path."));
            }

            return new PwaWorkerEvidence(workerPath, workerScope, "failed", "failed", "failed", "failed");
        }

        var contentType = IsJavaScript(response.ContentType) ? "passed" : "failed";
        if (contentType == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA285",
                "The push worker must be served as JavaScript.",
                "worker.contentType",
                "javascript",
                response.ContentType,
                "Serve the generated worker with a JavaScript media type."));
        }

        var nosniff = HasExactlyOneHeader(response, "X-Content-Type-Options", "nosniff") ? "passed" : "failed";
        if (nosniff == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA286",
                "The push worker must return exactly one X-Content-Type-Options: nosniff header.",
                "worker.nosniff",
                "exactly-one-nosniff",
                HeaderCount(response, "X-Content-Type-Options").ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Use the generated worker endpoint without a conflicting proxy header."));
        }

        var cacheControl = HasCacheDirective(response, "no-cache") ? "passed" : "failed";
        if (cacheControl == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA287",
                "The push worker must return Cache-Control containing no-cache.",
                "worker.cacheControl",
                "no-cache",
                "missing",
                "Preserve the generated worker cache policy through the deployed proxy."));
        }

        var allowed = HasExactlyOneHeader(response, "Service-Worker-Allowed", workerScope) ? "passed" : "failed";
        if (allowed == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA288",
                "The push worker must return one Service-Worker-Allowed header matching the diagnostics scope.",
                "worker.serviceWorkerAllowed",
                workerScope,
                "missing-or-mismatched",
                "Preserve the generated Service-Worker-Allowed header through the deployed proxy."));
        }

        return new PwaWorkerEvidence(workerPath, workerScope, "passed", contentType, nosniff, cacheControl);
    }

    private async Task<PwaRegistrationHelperEvidence> VerifyRegistrationHelperAsync(
        PwaVerificationTarget target,
        PwaFetchedResponse entry,
        string helperPath,
        string workerPath,
        string workerScope,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!TryFindRegistrationHelper(
                target,
                entry.FinalUri,
                entry.Body,
                helperPath,
                workerPath,
                workerScope,
                diagnostics,
                out var helperUri))
        {
            return new PwaRegistrationHelperEvidence(helperPath, "failed", "not-evaluated", "not-evaluated", "not-evaluated", "not-evaluated");
        }

        var response = await FetchNoRedirectAsync(target, helperUri, "push-registration-helper", MaxTextResponseBytes, diagnostics, cancellationToken);
        if (!response.IsSuccess || IsRedirect(response.StatusCode) || response.Response.BodyTruncated)
        {
            if (!response.IsSuccess && !IsRedirect(response.StatusCode))
            {
                diagnostics.Add(PushError(
                    "ASPWA289",
                    "The push registration helper did not return a successful response.",
                    "registrationHelper.fetch",
                    "2xx",
                    $"HTTP {(int)response.StatusCode}",
                    "Expose the generated registration helper on the verified entry page."));
            }

            return new PwaRegistrationHelperEvidence(helperPath, "passed", "failed", "failed", "failed", "failed");
        }

        var contentType = IsJavaScript(response.ContentType) ? "passed" : "failed";
        if (contentType == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA290",
                "The push registration helper must be served as JavaScript.",
                "registrationHelper.contentType",
                "javascript",
                response.ContentType,
                "Serve the generated helper with a JavaScript media type."));
        }

        var nosniff = HasExactlyOneHeader(response, "X-Content-Type-Options", "nosniff") ? "passed" : "failed";
        if (nosniff == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA291",
                "The push registration helper must return exactly one X-Content-Type-Options: nosniff header.",
                "registrationHelper.nosniff",
                "exactly-one-nosniff",
                HeaderCount(response, "X-Content-Type-Options").ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Use the generated helper endpoint without a conflicting proxy header."));
        }

        var cacheControl = HasCacheDirective(response, "immutable") ? "passed" : "failed";
        if (cacheControl == "failed")
        {
            diagnostics.Add(PushError(
                "ASPWA292",
                "The versioned push registration helper must return Cache-Control containing immutable.",
                "registrationHelper.cacheControl",
                "immutable",
                "missing",
                "Preserve the generated helper cache policy through the deployed proxy."));
        }

        return new PwaRegistrationHelperEvidence(helperPath, "passed", "passed", contentType, nosniff, cacheControl);
    }

    private async Task<PwaFetchedResponse> FetchNoRedirectAsync(
        PwaVerificationTarget target,
        Uri requestUri,
        string subject,
        int maxBytes,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(requestUri, maxBytes, cancellationToken);
        if (response.BodyTruncated)
        {
            diagnostics.Add(PushError(
                "ASPWA293",
                "A strict push resource response exceeded the bounded verifier read limit.",
                subject,
                $"<={maxBytes} bytes",
                $">{maxBytes} bytes",
                "Serve a smaller generated worker or helper response."));
        }

        if (IsRedirect(response.StatusCode))
        {
            diagnostics.Add(PushError(
                "ASPWA294",
                "Push worker and registration-helper proof does not permit redirects.",
                subject,
                "direct-2xx",
                $"HTTP {(int)response.StatusCode}",
                "Serve the generated resource directly at the diagnostics path."));
        }

        return new PwaFetchedResponse(requestUri, response);
    }

    private static PwaNormalizedPushReadiness NormalizePushReadiness(
        PwaPushReadinessProbe? source,
        List<PwaVerificationDiagnostic> diagnostics)
    {
        if (source is null)
        {
            diagnostics.Add(PushError(
                "ASPWA295",
                "The diagnostics status document is legacy and has no pushReadiness source object.",
                "diagnostics.pushReadiness",
                "schema-version-1",
                "missing",
                "Upgrade ForgeTrust.AppSurface.Web before requesting push readiness evidence."));
            return PwaNormalizedPushReadiness.Unavailable;
        }

        if (source.SchemaVersion != 1)
        {
            diagnostics.Add(PushError(
                "ASPWA296",
                "The pushReadiness source object has an unsupported schema version.",
                "diagnostics.pushReadiness.schemaVersion",
                "1",
                source.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Upgrade the CLI and Web packages as a compatible family."));
            return PwaNormalizedPushReadiness.Unavailable;
        }

        var status = source.ConfigurationStatus;
        var valuesAreNull = source.ActiveVapidKeyId is null
            && source.PublicKeyFingerprint is null
            && source.RouteMapped is null;
        if (string.Equals(status, "configured", StringComparison.Ordinal)
            && SafeKeyIdPattern().IsMatch(source.ActiveVapidKeyId ?? string.Empty)
            && FingerprintPattern().IsMatch(source.PublicKeyFingerprint ?? string.Empty)
            && source.RouteMapped is not null)
        {
            return new PwaNormalizedPushReadiness("configured", source.ActiveVapidKeyId, source.PublicKeyFingerprint, source.RouteMapped);
        }

        if ((string.Equals(status, "not-configured", StringComparison.Ordinal)
                || string.Equals(status, "unavailable", StringComparison.Ordinal))
            && valuesAreNull)
        {
            return new PwaNormalizedPushReadiness(status!, null, null, null);
        }

        diagnostics.Add(PushError(
            "ASPWA297",
            "The pushReadiness source object contains an unsafe or internally inconsistent state.",
            "diagnostics.pushReadiness",
            "normalized-schema-version-1",
            "malformed",
            "Upgrade the Web package or correct the safe readiness-provider implementation."));
        return PwaNormalizedPushReadiness.Unavailable;
    }

    private static PwaPushEvidence BuildPushEvidence(
        PwaExpectedPush expected,
        bool? enabled,
        string configurationStatus,
        PwaWorkerEvidence worker,
        PwaRegistrationHelperEvidence helper,
        PwaVapidEvidence vapid,
        string routeMapping)
    {
        return new PwaPushEvidence(
            expected.ToString().ToLowerInvariant(),
            enabled,
            configurationStatus,
            worker,
            helper,
            vapid,
            routeMapping,
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated",
            "not-evaluated");
    }

    private static PwaInstallEvidence BuildInstallEvidence(PwaVerificationReport report)
    {
        return new PwaInstallEvidence(
            report.ManifestPath,
            report.StartUrl,
            report.Scope,
            report.Display,
            report.ThemeColor,
            report.BackgroundColor,
            report.Icons);
    }

    private static bool TryFindRegistrationHelper(
        PwaVerificationTarget target,
        Uri entryUri,
        string html,
        string expectedHelperPath,
        string expectedWorkerPath,
        string expectedWorkerScope,
        List<PwaVerificationDiagnostic> diagnostics,
        out Uri helperUri)
    {
        helperUri = target.BaseUri;
        var head = HeadRegex().Match(html);
        if (!head.Success)
        {
            diagnostics.Add(PushError(
                "ASPWA298",
                "The entry HTML has no document head for push registration-helper discovery.",
                "entry.head.registrationHelper",
                "one-matching-script",
                "missing-head",
                "Render the generated AppSurface PWA head metadata in the verified entry layout."));
            return false;
        }

        var candidates = new List<(Uri Uri, IReadOnlyDictionary<string, string> Attributes)>();
        foreach (Match script in ScriptTagRegex().Matches(head.Groups["content"].Value))
        {
            if (!TryReadScriptAttributes(script.Groups["attributes"].Value, out var attributes))
            {
                continue;
            }

            if (!attributes.TryGetValue("src", out var source)
                || !ResolvesToOrigin(target, entryUri, source, out var resolved)
                || !IsUnderBasePath(target, resolved.AbsolutePath)
                || !string.Equals(resolved.AbsolutePath, expectedHelperPath, StringComparison.Ordinal))
            {
                continue;
            }

            candidates.Add((resolved, attributes));
        }

        if (candidates.Count != 1)
        {
            diagnostics.Add(PushError(
                "ASPWA299",
                "The entry head must contain exactly one matching push registration-helper script.",
                "entry.head.registrationHelper",
                "exactly-one-script",
                candidates.Count == 0 ? "missing" : "duplicate",
                "Render exactly one generated AppSurface PWA helper script in the entry document head."));
            return false;
        }

        var candidate = candidates[0];
        if (CountQueryParameter(candidate.Uri, "v") != 1
            || !candidate.Attributes.TryGetValue("data-appsurface-pwa-worker", out var worker)
            || !string.Equals(worker, expectedWorkerPath, StringComparison.Ordinal)
            || !candidate.Attributes.TryGetValue("data-appsurface-pwa-scope", out var scope)
            || !string.Equals(scope, expectedWorkerScope, StringComparison.Ordinal))
        {
            diagnostics.Add(PushError(
                "ASPWA300",
                "The matching registration-helper script has an invalid version or worker metadata contract.",
                "entry.head.registrationHelper",
                "one-v-and-matching-worker-scope",
                "mismatched",
                "Use the generated AppSurface PWA head metadata without rewriting helper attributes."));
            return false;
        }

        helperUri = candidate.Uri;
        return true;
    }

    private static bool ContainsPwaRegistrationHelper(string html)
    {
        var head = HeadRegex().Match(html);
        return head.Success && ScriptTagRegex().IsMatch(head.Groups["content"].Value)
            && head.Groups["content"].Value.Contains("data-appsurface-pwa-worker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadScriptAttributes(string source, out IReadOnlyDictionary<string, string> attributes)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in HtmlAttributeRegex().Matches(source))
        {
            var name = attribute.Groups["name"].Value;
            if (!parsed.TryAdd(name, WebUtility.HtmlDecode(attribute.Groups["value"].Value)))
            {
                attributes = parsed;
                return false;
            }
        }

        attributes = parsed;
        return true;
    }

    private static int CountQueryParameter(Uri uri, string name)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Count(part => string.Equals(Uri.UnescapeDataString(part.Split('=', 2)[0]), name, StringComparison.Ordinal));
    }

    private static bool IsJavaScript(string contentType)
    {
        return contentType.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactlyOneHeader(PwaFetchedResponse response, string name, string expectedValue)
    {
        return response.HeaderValues(name).Count == 1
            && string.Equals(response.HeaderValues(name)[0].Trim(), expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static int HeaderCount(PwaFetchedResponse response, string name) => response.HeaderValues(name).Count;

    private static bool HasCacheDirective(PwaFetchedResponse response, string expectedDirective)
    {
        return response.HeaderValues("Cache-Control")
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(directive => string.Equals(directive.Split('=', 2)[0], expectedDirective, StringComparison.OrdinalIgnoreCase));
    }

    private static PwaVerificationDiagnostic PushError(
        string code,
        string message,
        string? subject = null,
        string? expected = null,
        string? actual = null,
        string? fix = null)
    {
        return new PwaVerificationDiagnostic(
            code,
            "error",
            message,
            subject,
            expected,
            actual,
            fix,
            "https://forge-trust.com/docs/pwa-install#push-readiness-evidence");
    }

    private async Task<PwaFetchedResponse> FetchAsync(
        PwaVerificationTarget target,
        Uri requestUri,
        string subject,
        int maxBytes,
        List<PwaVerificationDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var currentUri = requestUri;
        var redirectsFollowed = 0;
        while (true)
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
                return new PwaFetchedResponse(currentUri, response);
            }

            if (redirectsFollowed == MaxRedirects)
            {
                diagnostics.Add(Error("ASPWA264", $"Redirect response for {subject} exceeded {MaxRedirects} hops.", subject, $"<={MaxRedirects}", $">{MaxRedirects}"));
                return new PwaFetchedResponse(currentUri, response, true);
            }

            if (string.IsNullOrWhiteSpace(response.RedirectLocation))
            {
                diagnostics.Add(Error("ASPWA260", $"Redirect response for {subject} omitted a Location header.", subject, "Location", "missing"));
                return new PwaFetchedResponse(currentUri, response);
            }

            if (!Uri.TryCreate(currentUri, response.RedirectLocation, out var nextUri))
            {
                diagnostics.Add(Error("ASPWA261", $"Redirect response for {subject} had an invalid Location header.", subject, "valid-uri", RedactUriValue(response.RedirectLocation)));
                return new PwaFetchedResponse(currentUri, response);
            }

            if (!IsSameOrigin(target, nextUri))
            {
                diagnostics.Add(Error(
                    "ASPWA262",
                    $"Redirect response for {subject} leaves the verified origin.",
                    subject,
                    target.Origin.ToString().TrimEnd('/'),
                    RedactUriValue(nextUri.ToString()),
                    "Keep PWA verifier redirects on the verified app origin."));
                return new PwaFetchedResponse(currentUri, response);
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
                return new PwaFetchedResponse(currentUri, response);
            }

            diagnostics.Add(Info(
                "ASPWA266",
                $"Verifier followed redirect for {subject}.",
                subject,
                EvidencePath(currentUri),
                EvidencePath(nextUri)));
            redirectsFollowed++;
            currentUri = nextUri;
        }
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
            target.Origin.ToString().TrimEnd('/'),
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

    private static Uri GetDiagnosticsStatusUri(PwaVerificationTarget target, string diagnosticsPath)
    {
        var relativePath = diagnosticsPath.Trim('/');
        return new Uri(
            target.BaseUri,
            string.IsNullOrEmpty(relativePath) ? "status.json" : relativePath + "/status.json");
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

    [GeneratedRegex("""<script\b(?<attributes>[^>]*)>""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex("""(?<name>[A-Za-z_:][A-Za-z0-9_:.\-]*)\s*=\s*["'](?<value>[^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex("""\brel\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex RelAttributeRegex();

    [GeneratedRegex("""\bhref\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttributeRegex();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColorPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeKeyIdPattern();

    [GeneratedRegex("^sha256-[A-Za-z0-9_-]{43}$")]
    private static partial Regex FingerprintPattern();
}

/// <summary>
/// Fetches verifier resources without automatically following redirects.
/// </summary>
/// <remarks>
/// Redirect custody stays with <see cref="PwaVerifier"/> so origin and path-base boundaries are checked before each hop.
/// </remarks>
internal interface IPwaVerificationHttpClient
{
    /// <summary>
    /// Fetches one response and reads no more than the requested body limit.
    /// </summary>
    /// <param name="uri">The absolute resource URI.</param>
    /// <param name="maxBodyBytes">The maximum response-body bytes retained for evidence.</param>
    /// <param name="cancellationToken">Cancels the network request and bounded body read.</param>
    /// <returns>The response metadata and bounded body.</returns>
    Task<PwaHttpResponse> GetAsync(Uri uri, int maxBodyBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts <see cref="HttpClient"/> to the verifier's bounded, redirect-aware fetch contract.
/// </summary>
/// <param name="httpClient">A client configured with automatic redirect handling disabled.</param>
/// <remarks>
/// Enabling automatic redirects bypasses the verifier's same-origin and path-base checks.
/// </remarks>
internal sealed class PwaVerificationHttpClient(HttpClient httpClient) : IPwaVerificationHttpClient
{
    /// <inheritdoc />
    public async Task<PwaHttpResponse> GetAsync(Uri uri, int maxBodyBytes, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await BoundedHttpBodyReader.ReadAsync(response.Content, maxBodyBytes, cancellationToken);
        return new PwaHttpResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            body.Bytes,
            response.Headers.Location?.OriginalString,
            body.Truncated,
            CollectRequiredHeaders(response));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectRequiredHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "X-Content-Type-Options", "Cache-Control", "Service-Worker-Allowed" })
        {
            var values = new List<string>();
            if (response.Headers.TryGetValues(name, out var responseValues))
            {
                values.AddRange(responseValues);
            }

            if (response.Content.Headers.TryGetValues(name, out var contentValues))
            {
                values.AddRange(contentValues);
            }

            if (values.Count > 0)
            {
                headers[name] = values;
            }
        }

        return headers;
    }

}

/// <summary>
/// Captures one bounded HTTP response before verifier-managed redirect handling.
/// </summary>
/// <param name="StatusCode">The actual server status code.</param>
/// <param name="ContentType">The response media type, or an empty string when absent.</param>
/// <param name="BodyBytes">The retained response bytes, capped by the requested read limit.</param>
/// <param name="RedirectLocation">The unmodified Location header value, when present.</param>
/// <param name="BodyTruncated">Whether bytes beyond the configured read limit were discarded.</param>
/// <param name="Headers">Only the three response-header observations required by strict push verification, preserving duplicate values.</param>
internal sealed record PwaHttpResponse(
    HttpStatusCode StatusCode,
    string ContentType,
    byte[] BodyBytes,
    string? RedirectLocation,
    bool BodyTruncated,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Headers = null)
{
    /// <summary>
    /// Gets whether the actual response status is in the HTTP 2xx range.
    /// </summary>
    public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;

    /// <summary>
    /// Gets the retained body decoded as UTF-8 text.
    /// </summary>
    /// <remarks>Binary consumers should use <see cref="BodyBytes"/> instead.</remarks>
    public string Body => Encoding.UTF8.GetString(BodyBytes);

    /// <summary>Gets bounded captured values for one strict-verification response header.</summary>
    public IReadOnlyList<string> HeaderValues(string name)
    {
        return Headers is not null && Headers.TryGetValue(name, out var values) ? values : [];
    }
}

/// <summary>
/// Represents schema-versioned PWA verification evidence written by the CLI.
/// </summary>
/// <param name="SchemaVersion">The JSON evidence schema version.</param>
/// <param name="Passed">Whether no error-severity diagnostics were recorded.</param>
/// <param name="Origin">The verified scheme, host, and port without a path base.</param>
/// <param name="BaseUrl">The verified application root, including its path base.</param>
/// <param name="EntryPath">The app-root-relative entry path supplied to the verifier.</param>
/// <param name="EntryUrl">The absolute entry URL resolved under <paramref name="BaseUrl"/>.</param>
/// <param name="ManifestPath">The app-origin-relative manifest path discovered or probed.</param>
/// <param name="StartUrl">The manifest start_url value, when parsed.</param>
/// <param name="Scope">The manifest scope value, when parsed.</param>
/// <param name="Display">The manifest display value, when parsed.</param>
/// <param name="ThemeColor">The manifest theme_color value, when parsed.</param>
/// <param name="BackgroundColor">The manifest background_color value, when parsed.</param>
/// <param name="Icons">Bounded fetch and dimension evidence for manifest icons.</param>
/// <param name="Diagnostics">Stable diagnostics supporting the pass or failure result.</param>
/// <remarks>
/// Consumers should branch on <paramref name="SchemaVersion"/> and diagnostic codes instead of parsing human-readable messages.
/// </remarks>
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

/// <summary>
/// Represents the schema-v3 report used only for explicitly requested push or combined PWA readiness verification.
/// </summary>
internal sealed record PwaVerificationV3Report(
    int SchemaVersion,
    bool Passed,
    string Surface,
    string Origin,
    string BaseUrl,
    string EntryPath,
    string EntryUrl,
    PwaInstallEvidence? Install,
    PwaPushEvidence Push,
    IReadOnlyList<PwaVerificationDiagnostic> Diagnostics);

/// <summary>Captures install observations embedded in a schema-v3 combined report.</summary>
internal sealed record PwaInstallEvidence(
    string ManifestPath,
    string? StartUrl,
    string? Scope,
    string? Display,
    string? ThemeColor,
    string? BackgroundColor,
    IReadOnlyList<PwaIconEvidence> Icons);

/// <summary>Captures only server-known, privacy-safe push-readiness evidence.</summary>
internal sealed record PwaPushEvidence(
    string Expected,
    bool? Enabled,
    string ConfigurationStatus,
    PwaWorkerEvidence Worker,
    PwaRegistrationHelperEvidence RegistrationHelper,
    PwaVapidEvidence Vapid,
    string RouteMapping,
    string BrowserSupport,
    string Installation,
    string Permission,
    string Subscription,
    string NotificationDisplay,
    string NotificationClick,
    string Unsubscribe,
    string Delivery);

/// <summary>Captures bounded shared-worker fetch evidence.</summary>
internal sealed record PwaWorkerEvidence(
    string? Path,
    string? Scope,
    string Fetch,
    string ContentType,
    string Nosniff,
    string CacheControl);

/// <summary>Captures bounded registration-helper discovery and fetch evidence.</summary>
internal sealed record PwaRegistrationHelperEvidence(
    string? Path,
    string HeadReference,
    string Fetch,
    string ContentType,
    string Nosniff,
    string CacheControl);

/// <summary>Captures the safe VAPID identity contributed by the optional Push package.</summary>
internal sealed record PwaVapidEvidence(string? ActiveKeyId, string? PublicKeyFingerprint);

/// <summary>Pairs schema-v3 push evidence with diagnostics collected while verifying it.</summary>
/// <param name="Evidence">The bounded server-known push evidence.</param>
/// <param name="Diagnostics">Diagnostics emitted while collecting the evidence.</param>
internal sealed record PwaPushVerificationResult(
    PwaPushEvidence Evidence,
    IReadOnlyList<PwaVerificationDiagnostic> Diagnostics);

/// <summary>Holds validated readiness evidence or the sanitized unavailable state.</summary>
/// <param name="ConfigurationStatus">The configured, not-configured, or unavailable readiness state.</param>
/// <param name="ActiveVapidKeyId">The safe active VAPID key identifier, when configured.</param>
/// <param name="PublicKeyFingerprint">The safe SHA-256 public-key fingerprint, when configured.</param>
/// <param name="RouteMapped">Whether the package-owned route is mapped, or null when not evaluated.</param>
internal sealed record PwaNormalizedPushReadiness(
    string ConfigurationStatus,
    string? ActiveVapidKeyId,
    string? PublicKeyFingerprint,
    bool? RouteMapped)
{
    /// <summary>Gets the fixed redacted result used when readiness evidence cannot be trusted.</summary>
    public static PwaNormalizedPushReadiness Unavailable { get; } = new("unavailable", null, null, null);
}

/// <summary>
/// Represents one stable, structured PWA verification observation.
/// </summary>
/// <param name="Code">The stable ASPWA2xx identifier.</param>
/// <param name="Severity">The lowercase error, warning, or info token.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="Subject">The bounded manifest field or verifier surface involved.</param>
/// <param name="Expected">The expected bounded value, when applicable.</param>
/// <param name="Actual">The observed redacted value, when applicable.</param>
/// <param name="Fix">A concise remediation, when known.</param>
/// <param name="DocsUrl">Canonical documentation for the diagnostic, when available.</param>
/// <remarks>Do not place query strings, fragments, response bodies, or other secrets in structured evidence.</remarks>
internal sealed record PwaVerificationDiagnostic(
    string Code,
    string Severity,
    string Message,
    string? Subject = null,
    string? Expected = null,
    string? Actual = null,
    string? Fix = null,
    string? DocsUrl = null);

/// <summary>
/// Normalizes the trusted origin, application path base, and real entry route used by one verification run.
/// </summary>
/// <param name="Origin">The scheme, host, and port boundary.</param>
/// <param name="BaseUri">The application root including its normalized path base.</param>
/// <param name="BasePath">The normalized path base ending in a slash.</param>
/// <param name="EntryPath">The validated app-root-relative entry path.</param>
/// <param name="EntryUri">The entry path resolved beneath <paramref name="BaseUri"/>.</param>
/// <remarks>
/// Query strings and fragments belong in neither the base URL nor entry path because verifier evidence intentionally excludes them.
/// </remarks>
internal sealed record PwaVerificationTarget(Uri Origin, Uri BaseUri, string BasePath, string EntryPath, Uri EntryUri)
{
    /// <summary>
    /// Creates a normalized verification target after enforcing the URL and entry-path boundaries.
    /// </summary>
    /// <param name="url">An absolute HTTP or HTTPS application root without a query or fragment.</param>
    /// <param name="entryPath">An app-root-relative path without traversal, query, fragment, or absolute URL syntax.</param>
    /// <returns>The normalized target used for all verifier requests.</returns>
    /// <exception cref="ArgumentException">The URL or entry path violates a verifier boundary.</exception>
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

/// <summary>
/// Carries normalized CLI assertions into a PWA verification run.
/// </summary>
/// <param name="BaseUrl">The absolute app root to verify.</param>
/// <param name="EntryPath">The app-root-relative HTML entry route.</param>
/// <param name="ExpectedStartUrl">An optional exact manifest start_url assertion.</param>
/// <param name="ExpectedScope">An optional exact manifest scope assertion.</param>
/// <param name="ExpectedDisplay">An optional exact manifest display assertion.</param>
/// <param name="ExpectedThemeColor">An optional exact manifest theme_color assertion.</param>
/// <param name="ExpectedBackgroundColor">An optional exact manifest background_color assertion.</param>
/// <param name="ExpectedIcons">Parsed icon size and purpose assertions.</param>
/// <param name="Surface">The requested public verification surface.</param>
/// <param name="ExpectedPush">The expected server-known push posture for schema-v3 surfaces.</param>
/// <param name="DiagnosticsPath">The normalized app-root-relative PWA diagnostics base path.</param>
/// <remarks>Target safety is enforced by <see cref="PwaVerificationTarget.Create(Uri, string)"/> before network access.</remarks>
internal sealed record PwaVerificationOptions(
    Uri BaseUrl,
    string EntryPath,
    string? ExpectedStartUrl,
    string? ExpectedScope,
    string? ExpectedDisplay,
    string? ExpectedThemeColor,
    string? ExpectedBackgroundColor,
    IReadOnlyList<PwaExpectedIcon> ExpectedIcons,
    PwaVerificationSurface Surface,
    PwaExpectedPush ExpectedPush,
    string DiagnosticsPath)
{
    /// <summary>
    /// Creates verification options and parses repeated icon assertions.
    /// </summary>
    /// <param name="baseUrl">The absolute app root to verify.</param>
    /// <param name="entryPath">The app-root-relative entry route; blank values normalize to root.</param>
    /// <param name="expectedStartUrl">An optional exact start_url assertion.</param>
    /// <param name="expectedScope">An optional exact scope assertion.</param>
    /// <param name="expectedDisplay">An optional exact display assertion.</param>
    /// <param name="expectedThemeColor">An optional exact theme_color assertion.</param>
    /// <param name="expectedBackgroundColor">An optional exact background_color assertion.</param>
    /// <param name="expectedIcons">Repeated WIDTHxHEIGHT or WIDTHxHEIGHT:purpose assertions.</param>
    /// <param name="surface">Install (default), push, or all.</param>
    /// <param name="expectedPush">Enabled (default) or disabled for push/all surfaces.</param>
    /// <param name="diagnosticsPath">The app-root-relative PWA diagnostics base path.</param>
    /// <returns>Normalized options for <see cref="PwaVerifier"/>.</returns>
    /// <exception cref="ArgumentException">An icon assertion is malformed.</exception>
    public static PwaVerificationOptions Create(
        Uri baseUrl,
        string entryPath = "/",
        string? expectedStartUrl = null,
        string? expectedScope = null,
        string? expectedDisplay = null,
        string? expectedThemeColor = null,
        string? expectedBackgroundColor = null,
        IReadOnlyList<string>? expectedIcons = null,
        string? surface = null,
        string? expectedPush = null,
        string? diagnosticsPath = null)
    {
        var parsedSurface = PwaVerificationSurfaceParser.Parse(surface);
        var parsedExpectedPush = PwaExpectedPushParser.Parse(expectedPush);
        var hasInstallExpectation = !string.IsNullOrWhiteSpace(expectedStartUrl)
            || !string.IsNullOrWhiteSpace(expectedScope)
            || !string.IsNullOrWhiteSpace(expectedDisplay)
            || !string.IsNullOrWhiteSpace(expectedThemeColor)
            || !string.IsNullOrWhiteSpace(expectedBackgroundColor)
            || (expectedIcons?.Count ?? 0) > 0;
        if (parsedSurface == PwaVerificationSurface.Install && !string.IsNullOrWhiteSpace(expectedPush))
        {
            throw new ArgumentException("--expect-push is valid only with --surface push or --surface all.");
        }

        if (parsedSurface == PwaVerificationSurface.Push && hasInstallExpectation)
        {
            throw new ArgumentException("Install expectation options are valid only with --surface install or --surface all.");
        }

        var normalizedDiagnosticsPath = PwaVerificationTarget.Create(
            baseUrl,
            string.IsNullOrWhiteSpace(diagnosticsPath) ? "/_appsurface/pwa" : diagnosticsPath).EntryPath;
        if (normalizedDiagnosticsPath.Contains('%'))
        {
            throw new ArgumentException("--diagnostics-path must be an app-root-relative endpoint path without percent escapes.");
        }

        return new PwaVerificationOptions(
            baseUrl,
            string.IsNullOrWhiteSpace(entryPath) ? "/" : entryPath,
            expectedStartUrl,
            expectedScope,
            expectedDisplay,
            expectedThemeColor,
            expectedBackgroundColor,
            (expectedIcons ?? []).Select(PwaExpectedIcon.Parse).ToArray(),
            parsedSurface,
            parsedExpectedPush,
            normalizedDiagnosticsPath);
    }
}

/// <summary>Identifies the public PWA verification surface.</summary>
internal enum PwaVerificationSurface
{
    /// <summary>Verifies the schema-v2 PWA install surface.</summary>
    Install,

    /// <summary>Verifies the schema-v3 server-known push-readiness surface.</summary>
    Push,

    /// <summary>Verifies both install and push-readiness surfaces.</summary>
    All
}

/// <summary>Identifies the expected server-known push posture.</summary>
internal enum PwaExpectedPush
{
    /// <summary>Requires diagnostics to report enabled push handling.</summary>
    Enabled,

    /// <summary>Requires diagnostics to report disabled push handling.</summary>
    Disabled
}

/// <summary>Parses the accepted install, push, and all PWA verification surface values.</summary>
internal static class PwaVerificationSurfaceParser
{
    /// <summary>Parses an optional PWA verification surface value.</summary>
    /// <param name="value">An optional install, push, or all value.</param>
    /// <returns>The parsed surface; a missing value selects install.</returns>
    /// <exception cref="ArgumentException">The value is not install, push, or all.</exception>
    public static PwaVerificationSurface Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "install" => PwaVerificationSurface.Install,
        "push" => PwaVerificationSurface.Push,
        "all" => PwaVerificationSurface.All,
        _ => throw new ArgumentException("--surface must be install, push, or all.")
    };
}

/// <summary>Parses the accepted enabled and disabled push-expectation values.</summary>
internal static class PwaExpectedPushParser
{
    /// <summary>Parses an optional expected push posture value.</summary>
    /// <param name="value">An optional enabled or disabled value.</param>
    /// <returns>The parsed posture; a missing value selects enabled.</returns>
    /// <exception cref="ArgumentException">The value is not enabled or disabled.</exception>
    public static PwaExpectedPush Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "enabled" => PwaExpectedPush.Enabled,
        "disabled" => PwaExpectedPush.Disabled,
        _ => throw new ArgumentException("--expect-push must be enabled or disabled.")
    };
}

/// <summary>
/// Represents one explicit manifest icon size and optional purpose assertion.
/// </summary>
/// <param name="Size">A positive WIDTHxHEIGHT token.</param>
/// <param name="Purpose">An optional manifest purpose token such as maskable.</param>
internal sealed partial record PwaExpectedIcon(string Size, string? Purpose)
{
    /// <summary>
    /// Parses a command-line icon assertion.
    /// </summary>
    /// <param name="value">WIDTHxHEIGHT or WIDTHxHEIGHT:purpose.</param>
    /// <returns>The parsed assertion.</returns>
    /// <exception cref="ArgumentException">The size or purpose token is malformed.</exception>
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

    /// <summary>
    /// Formats the assertion using its command-line token shape.
    /// </summary>
    /// <returns>WIDTHxHEIGHT or WIDTHxHEIGHT:purpose.</returns>
    public override string ToString() => string.IsNullOrWhiteSpace(Purpose) ? Size : $"{Size}:{Purpose}";

    [GeneratedRegex("^[1-9][0-9]*x[1-9][0-9]*$", RegexOptions.IgnoreCase)]
    private static partial Regex IconSizeAssertionPattern();
}

/// <summary>
/// Captures the final verifier-managed fetch state after zero or more accepted redirects.
/// </summary>
/// <param name="FinalUri">The URI that produced <paramref name="Response"/>.</param>
/// <param name="Response">The actual final response without fabricated sentinel status codes.</param>
/// <param name="RedirectLimitExceeded">Whether this response was an unfollowed redirect beyond the hop limit.</param>
/// <remarks>
/// Callers must not reinterpret <paramref name="Response"/> as a terminal HTTP failure when <paramref name="RedirectLimitExceeded"/> is true; ASPWA264 is the authoritative failure.
/// </remarks>
internal sealed record PwaFetchedResponse(Uri FinalUri, PwaHttpResponse Response, bool RedirectLimitExceeded = false)
{
    /// <summary>Gets the actual final response status code.</summary>
    public HttpStatusCode StatusCode => Response.StatusCode;

    /// <summary>Gets the actual final response media type.</summary>
    public string ContentType => Response.ContentType;

    /// <summary>Gets the retained final response body decoded as UTF-8.</summary>
    public string Body => Response.Body;

    /// <summary>Gets the retained final response bytes.</summary>
    public byte[] BodyBytes => Response.BodyBytes;

    /// <summary>Gets whether the actual final response is in the HTTP 2xx range.</summary>
    public bool IsSuccess => Response.IsSuccess;

    /// <summary>Gets bounded captured values for one strict-verification response header.</summary>
    public IReadOnlyList<string> HeaderValues(string name) => Response.HeaderValues(name);
}

/// <summary>
/// Represents dimensions decoded directly from a bounded PNG response.
/// </summary>
/// <param name="Width">The positive pixel width.</param>
/// <param name="Height">The positive pixel height.</param>
internal sealed record PwaImageDimensions(int Width, int Height);

/// <summary>
/// Represents privacy-safe fetch and optional PNG dimension evidence for one manifest icon.
/// </summary>
/// <param name="Source">The manifest src value.</param>
/// <param name="Sizes">The manifest sizes token list.</param>
/// <param name="Type">The declared media type.</param>
/// <param name="Purpose">The declared purpose token list.</param>
/// <param name="Path">The fetched app-origin-relative path without query or fragment.</param>
/// <param name="ContentType">The observed response media type.</param>
/// <param name="Width">The decoded PNG width, when available.</param>
/// <param name="Height">The decoded PNG height, when available.</param>
/// <param name="Fetched">Whether the icon returned an HTTP 2xx response.</param>
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

/// <summary>
/// Models the manifest fields required for install-readiness verification.
/// </summary>
/// <param name="Name">The manifest name.</param>
/// <param name="ShortName">The manifest short_name.</param>
/// <param name="StartUrl">The manifest start_url.</param>
/// <param name="Scope">The manifest scope.</param>
/// <param name="Display">The manifest display mode.</param>
/// <param name="ThemeColor">The manifest theme_color.</param>
/// <param name="BackgroundColor">The manifest background_color.</param>
/// <param name="Icons">The manifest icon declarations.</param>
internal sealed record PwaManifestProbe(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("short_name")] string? ShortName,
    [property: JsonPropertyName("start_url")] string? StartUrl,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("display")] string? Display,
    [property: JsonPropertyName("theme_color")] string? ThemeColor,
    [property: JsonPropertyName("background_color")] string? BackgroundColor,
    [property: JsonPropertyName("icons")] IReadOnlyList<PwaIconProbe>? Icons);

/// <summary>
/// Models one manifest icon declaration without trusting it as fetched evidence.
/// </summary>
/// <param name="Source">The manifest src value.</param>
/// <param name="Sizes">The space-delimited manifest sizes tokens.</param>
/// <param name="Type">The declared media type.</param>
/// <param name="Purpose">The space-delimited manifest purpose tokens.</param>
internal sealed record PwaIconProbe(
    [property: JsonPropertyName("src")] string? Source,
    [property: JsonPropertyName("sizes")] string? Sizes,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("purpose")] string? Purpose);

/// <summary>
/// Models the server-known AppSurface PWA diagnostics used for install, worker, offline, and push posture checks.
/// </summary>
/// <param name="Enabled">Whether AppSurface PWA metadata is enabled.</param>
/// <param name="OfflineEnabled">Whether the offline strategy is enabled.</param>
/// <param name="ServiceWorkerPath">The active service-worker path when offline is enabled.</param>
/// <param name="OfflineFallbackPath">The active offline fallback path when offline is enabled.</param>
/// <param name="ConfiguredServiceWorkerPath">The configured worker path used to prove absence when offline is disabled.</param>
/// <param name="WorkerEnabled">Whether either offline or push configuration activates the shared service worker.</param>
/// <param name="WorkerPath">The active shared service-worker path when a worker capability is enabled.</param>
/// <param name="PushEnabled">Whether push event handling is enabled in the shared service worker.</param>
/// <param name="WorkerScope">The effective registration scope for the shared service worker.</param>
/// <param name="RegistrationHelperPath">The registration-helper path exposed when push is enabled.</param>
/// <param name="PushReadiness">The additive schema-versioned safe readiness contribution, when supported by the server.</param>
/// <remarks>These server-known values do not prove browser runtime capability or registration state.</remarks>
internal sealed record PwaStatusProbe(
    bool Enabled,
    bool OfflineEnabled,
    string? ServiceWorkerPath,
    string? OfflineFallbackPath,
    string? ConfiguredServiceWorkerPath,
    bool WorkerEnabled = false,
    string? WorkerPath = null,
    bool PushEnabled = false,
    string? WorkerScope = null,
    string? RegistrationHelperPath = null,
    PwaPushReadinessProbe? PushReadiness = null);

/// <summary>Models the versioned, sanitized push-readiness source object emitted by current Web servers.</summary>
internal sealed record PwaPushReadinessProbe(
    int SchemaVersion,
    string? ConfigurationStatus,
    string? ActiveVapidKeyId,
    string? PublicKeyFingerprint,
    bool? RouteMapped);
