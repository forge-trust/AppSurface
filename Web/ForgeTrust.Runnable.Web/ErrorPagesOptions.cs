namespace ForgeTrust.Runnable.Web;

/// <summary>
/// Represents configuration options for Runnable's conventional browser status pages.
/// </summary>
/// <remarks>
/// The default configuration keeps <see cref="BrowserStatusPageMode"/> at <see cref="BrowserStatusPageMode.Auto"/>,
/// which enables conventional 401, 403, and 404 browser pages only when MVC support already includes Razor
/// views. Apps that need the HTML status-page experience regardless of their starting MVC mode can opt into
/// <see cref="BrowserStatusPageMode.Enabled"/>, while API-only or custom error handling stacks should use
/// <see cref="BrowserStatusPageMode.Disabled"/>.
/// </remarks>
public record ErrorPagesOptions
{
    /// <summary>
    /// Gets a default instance of <see cref="ErrorPagesOptions"/> with <see cref="BrowserStatusPageMode.Auto"/>.
    /// </summary>
    public static ErrorPagesOptions Default => new();

    /// <summary>
    /// Gets or sets the conventional browser status page behavior for the application.
    /// </summary>
    /// <remarks>
    /// <see cref="BrowserStatusPageMode.Auto"/> is the default and turns the feature on only when the
    /// app's MVC support reaches <see cref="MvcSupport.ControllersWithViews"/>. Choosing
    /// <see cref="BrowserStatusPageMode.Enabled"/> can cause Runnable startup to upgrade MVC support so
    /// the conventional Razor views can render. Choosing <see cref="BrowserStatusPageMode.Disabled"/>
    /// prevents the reserved framework routes and browser-oriented status handling from activating.
    /// </remarks>
    public BrowserStatusPageMode BrowserStatusPageMode { get; set; } = BrowserStatusPageMode.Auto;

    /// <summary>
    /// Explicitly enables Runnable's conventional browser status pages.
    /// </summary>
    /// <remarks>
    /// Use this when an app must always render the conventional HTML 401, 403, and 404 pages. Runnable may
    /// effectively require controllers with views at startup so the configured Razor pages can execute.
    /// </remarks>
    public void UseConventionalBrowserStatusPages()
    {
        BrowserStatusPageMode = BrowserStatusPageMode.Enabled;
    }

    /// <summary>
    /// Explicitly disables Runnable's conventional browser status pages.
    /// </summary>
    /// <remarks>
    /// Use this for APIs, custom status-code middleware, or any app that wants to keep conventional browser
    /// status routes and handling out of the pipeline even when MVC view support is available.
    /// </remarks>
    public void DisableBrowserStatusPages()
    {
        BrowserStatusPageMode = BrowserStatusPageMode.Disabled;
    }

    /// <summary>
    /// Determines whether Runnable should enable conventional browser status pages for the supplied MVC support level.
    /// </summary>
    /// <param name="mvcSupportLevel">The MVC capability currently configured for the app.</param>
    /// <returns>
    /// <see langword="true"/> when the conventional page should be active; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This helper is used by Runnable startup after module and app options are applied. In
    /// <see cref="BrowserStatusPageMode.Auto"/>, the feature only turns on when MVC already includes
    /// views. In <see cref="BrowserStatusPageMode.Enabled"/>, the feature is active regardless of the
    /// incoming MVC level because startup may upgrade the app to support Razor views.
    /// </remarks>
    internal bool AreConventionalBrowserStatusPagesEnabled(MvcSupport mvcSupportLevel)
    {
        return BrowserStatusPageMode switch
        {
            BrowserStatusPageMode.Enabled => true,
            BrowserStatusPageMode.Disabled => false,
            _ => mvcSupportLevel >= MvcSupport.ControllersWithViews
        };
    }
}
