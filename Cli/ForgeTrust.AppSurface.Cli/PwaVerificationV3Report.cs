namespace ForgeTrust.AppSurface.Cli;

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
