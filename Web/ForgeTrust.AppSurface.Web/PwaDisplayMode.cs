namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Display modes AppSurface can emit into the generated web app manifest.
/// </summary>
/// <remarks>
/// Values match the standard web app manifest display modes. AppSurface serializes
/// <see cref="MinimalUi"/> as <c>minimal-ui</c>.
/// The numeric values are explicit because this public enum may be persisted, serialized, or bound by
/// applications. New values should be appended without changing the values documented here.
/// </remarks>
public enum PwaDisplayMode
{
    /// <summary>
    /// Opens the app in a browser tab or normal browser surface.
    /// </summary>
    Browser = 0,

    /// <summary>
    /// Opens the app with minimal browser controls.
    /// </summary>
    MinimalUi = 1,

    /// <summary>
    /// Opens the app in a standalone app-like window.
    /// </summary>
    Standalone = 2,

    /// <summary>
    /// Opens the app fullscreen when supported by the platform.
    /// </summary>
    Fullscreen = 3
}
