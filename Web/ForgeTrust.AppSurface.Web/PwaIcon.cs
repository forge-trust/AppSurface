namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Declares one icon entry emitted into the generated PWA manifest and optional page head metadata.
/// </summary>
/// <remarks>
/// <see cref="Source"/> must be an app-root-relative path such as <c>/icons/app-192.png</c>. AppSurface validates the
/// declared <see cref="Sizes"/> token values and exposes them in diagnostics, but it does not decode image dimensions
/// at runtime. Use <c>appsurface pwa verify</c> to prove icon URLs are reachable and decode PNG dimensions from a
/// running app.
/// </remarks>
public sealed class PwaIcon
{
    /// <summary>
    /// Gets or sets the app-root-relative icon URL.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the manifest size token list, for example <c>192x192</c> or <c>192x192 512x512</c>.
    /// </summary>
    public string Sizes { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icon content type. Defaults to <c>image/png</c>.
    /// </summary>
    public string Type { get; set; } = "image/png";

    /// <summary>
    /// Gets or sets the optional manifest purpose, for example <c>any</c> or <c>maskable</c>.
    /// </summary>
    public string? Purpose { get; set; }
}
