namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures AppSurface Progressive Web App install support.
/// </summary>
/// <remarks>
/// Set <see cref="Enabled"/> to <see langword="true"/> when AppSurface should emit and serve install metadata for a
/// web app. Required metadata mirrors the browser-facing manifest fields that make install posture understandable:
/// names, start URL, scope, display mode, theme/background colors, and 192x192 plus 512x512 icons. AppSurface maps
/// diagnostics separately from install metadata so production apps can hide diagnostics while still serving the manifest.
/// </remarks>
public sealed class PwaOptions
{
    /// <summary>
    /// Gets a default disabled PWA options instance.
    /// </summary>
    public static PwaOptions Default => new();

    /// <summary>
    /// Gets or sets a value indicating whether AppSurface should map PWA install metadata endpoints.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the app-root-relative generated manifest endpoint path.
    /// </summary>
    public string ManifestPath { get; set; } = "/manifest.webmanifest";

    /// <summary>
    /// Gets or sets when the PWA diagnostics endpoint should be mapped.
    /// </summary>
    public PwaDiagnosticEndpointExposure DiagnosticsExposure { get; set; } =
        PwaDiagnosticEndpointExposure.DevelopmentOnly;

    /// <summary>
    /// Gets or sets the app-root-relative diagnostics endpoint path.
    /// </summary>
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
    /// Gets or sets the app-root-relative scope emitted into the manifest.
    /// </summary>
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
    /// Gets explicit offline strategy settings.
    /// </summary>
    public PwaOfflineOptions Offline { get; } = new();
}
