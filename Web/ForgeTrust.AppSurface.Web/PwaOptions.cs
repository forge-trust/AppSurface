namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures independent AppSurface Progressive Web App install, offline, and push-worker capabilities.
/// </summary>
/// <remarks>
/// Set <see cref="Enabled"/> to <see langword="true"/> when AppSurface should emit and serve install metadata for a
/// web app. Required metadata mirrors the browser-facing manifest fields that make install posture understandable:
/// names, start URL, scope, display mode, theme/background colors, and 192x192 plus 512x512 icons. AppSurface maps
/// diagnostics separately from install metadata so production apps can hide diagnostics while still serving the manifest.
/// Offline and push capabilities activate one shared worker without requiring install metadata.
/// </remarks>
public sealed class PwaOptions
{
    private readonly PwaWorkerPathState _workerPathState = new();

    /// <summary>
    /// Initializes a disabled PWA options instance with shared worker, offline, and push settings.
    /// </summary>
    public PwaOptions()
    {
        Worker = new PwaWorkerOptions(_workerPathState);
        Offline = new PwaOfflineOptions(_workerPathState);
    }

    /// <summary>
    /// Gets a default disabled PWA options instance.
    /// </summary>
    public static PwaOptions Default => new();

    /// <summary>
    /// Gets or sets a value indicating whether AppSurface should map PWA install-manifest metadata endpoints.
    /// </summary>
    /// <remarks>This setting does not suppress <see cref="Offline"/> or <see cref="Push"/> when either capability is enabled.</remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the app-root-relative generated manifest endpoint path.
    /// </summary>
    /// <remarks>
    /// Generated endpoint paths reject percent escapes so routing and emitted metadata use one representation. This
    /// path remains validated and appears PathBase-adjusted in diagnostics whenever any PWA surface is active, although
    /// the manifest endpoint is mapped only when <see cref="Enabled"/> is enabled.
    /// </remarks>
    public string ManifestPath { get; set; } = "/manifest.webmanifest";

    /// <summary>
    /// Gets or sets when the PWA diagnostics endpoint should be mapped.
    /// </summary>
    public PwaDiagnosticEndpointExposure DiagnosticsExposure { get; set; } =
        PwaDiagnosticEndpointExposure.DevelopmentOnly;

    /// <summary>
    /// Gets or sets the app-root-relative diagnostics endpoint path.
    /// </summary>
    /// <remarks>Generated endpoint paths reject percent escapes so routing and emitted metadata use one representation.</remarks>
    public string DiagnosticsPath { get; set; } = "/_appsurface/pwa";

    /// <summary>
    /// Gets or sets the full application name emitted into the manifest.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short application name emitted into the manifest.
    /// </summary>
    public string ShortName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the app-root-relative start URL emitted into the manifest.
    /// </summary>
    public string StartUrl { get; set; } = "/";

    /// <summary>
    /// Gets or sets the app-root-relative manifest and service-worker scope.
    /// </summary>
    /// <remarks>
    /// The default is <c>/</c>. The scope is validated whenever install metadata or a worker capability is active.
    /// Browsers use raw URL-prefix matching, so <c>/app</c> also covers <c>/application</c>; use <c>/app/</c> when a
    /// path-segment boundary is intended.
    /// </remarks>
    public string Scope { get; set; } = "/";

    /// <summary>
    /// Gets or sets the display mode emitted into the manifest.
    /// </summary>
    public PwaDisplayMode Display { get; set; } = PwaDisplayMode.Standalone;

    /// <summary>
    /// Gets or sets the manifest and browser theme color as a CSS hex color.
    /// </summary>
    public string ThemeColor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the manifest background color as a CSS hex color.
    /// </summary>
    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icon entries emitted into the generated manifest.
    /// </summary>
    public IList<PwaIcon> Icons { get; } = new List<PwaIcon>();

    /// <summary>
    /// Gets shared service-worker and registration-helper endpoint settings.
    /// </summary>
    public PwaWorkerOptions Worker { get; }

    /// <summary>
    /// Gets explicit offline strategy settings.
    /// </summary>
    public PwaOfflineOptions Offline { get; }

    /// <summary>
    /// Gets opt-in Web Push worker settings.
    /// </summary>
    public PwaPushOptions Push { get; } = new();

    /// <summary>Gets whether at least one capability requires the shared service worker.</summary>
    internal bool IsWorkerEnabled => Offline.Enabled || Push.Enabled;

    /// <summary>Gets whether AppSurface should validate and map any PWA surface.</summary>
    internal bool HasAnySurfaceEnabled => Enabled || IsWorkerEnabled;

    /// <summary>
    /// Gets whether the configured PWA surfaces require AppSurface to enable static-file middleware.
    /// </summary>
    /// <remarks>
    /// Install metadata preserves the existing static-icon behavior, offline support may reference static fallback
    /// assets, and a custom push handler may be deployed from the web root. The generated default push worker and
    /// registration helper are endpoints and do not require static-file middleware.
    /// </remarks>
    internal bool RequiresStaticFileMiddleware => Enabled
        || Offline.Enabled
        || (Push.Enabled && Push.HandlerScriptPath is not null);
}
