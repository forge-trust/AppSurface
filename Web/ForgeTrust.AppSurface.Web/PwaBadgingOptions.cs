namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures the opt-in AppSurface browser adapter for the Progressive Web App Badging API.
/// </summary>
/// <remarks>
/// Badging is a write-only progressive enhancement. AppSurface validates calls and normalizes value-free outcomes,
/// but it does not own the badge count, request permission, persist state, or prove that an operating system displayed
/// a badge. Applications must keep an accessible in-app representation of the same attention state.
/// </remarks>
public sealed class PwaBadgingOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether AppSurface should expose the page badging helper and compose the same
    /// adapter into an already-enabled AppSurface worker.
    /// </summary>
    /// <remarks>Enabling badging alone does not create a service worker or enable static-file middleware.</remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the app-root-relative generated page-helper endpoint path.
    /// </summary>
    /// <remarks>
    /// The generated endpoint is served only when <see cref="Enabled"/> is enabled. Paths reject percent escapes and
    /// other ambiguous URL forms so routing, emitted head markup, and diagnostics use one representation.
    /// </remarks>
    public string HelperPath { get; set; } = "/_appsurface/pwa/badging.js";
}
