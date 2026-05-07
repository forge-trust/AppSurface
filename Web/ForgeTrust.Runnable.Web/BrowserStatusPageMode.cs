namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Controls how Runnable applies its conventional browser-friendly status pages.
/// </summary>
/// <remarks>
/// <see cref="Auto"/> is the default and is the safest choice for most applications because it only
/// enables the conventional pages when MVC view support is already available. Switch to
/// <see cref="Enabled"/> when an app must always expose the conventional HTML 401, 403, and 404
/// experience, even if
/// Runnable needs to upgrade MVC support to controllers with views during startup. Use
/// <see cref="Disabled"/> for API-first applications or when another status-code handling strategy should
/// remain fully in control.
/// The numeric values are explicit because this public enum may be persisted, serialized, or bound by
/// applications. New values should be appended without changing the values documented here.
/// </remarks>
public enum BrowserStatusPageMode
{
    /// <summary>
    /// Enables the conventional pages automatically when MVC support already includes views.
    /// </summary>
    /// <remarks>
    /// This is the default mode. Runnable keeps the feature off for controller-only or API-only apps, and
    /// turns it on for apps whose MVC support is already <see cref="MvcSupport.ControllersWithViews"/> or
    /// higher.
    /// </remarks>
    Auto = 0,

    /// <summary>
    /// Always enables the conventional pages and allows Runnable to upgrade MVC support when needed.
    /// </summary>
    /// <remarks>
    /// Choose this when an app must guarantee the shared HTML 401, 403, and 404 experience, even if it
    /// originally started from a controller-only configuration. This mode may increase MVC support to
    /// <see cref="MvcSupport.ControllersWithViews"/> so Razor views can render.
    /// </remarks>
    Enabled = 1,

    /// <summary>
    /// Always disables Runnable's conventional pages.
    /// </summary>
    /// <remarks>
    /// Choose this when an app wants blank, JSON, API, or custom middleware-driven status responses without
    /// Runnable injecting the reserved <c>/_runnable/errors/{status}</c> routes or browser-only status handling.
    /// </remarks>
    Disabled = 2
}
