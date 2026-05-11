namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Represents configuration options for serving static files and web assets.
/// </summary>
public record StaticFilesOptions
{
    /// <summary>
    /// Gets a default instance of <see cref="StaticFilesOptions"/> with default configuration settings.
    /// </summary>
    public static StaticFilesOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether static files are enabled.
    /// This is automatically enabled when <see cref="MvcSupport.ControllersWithViews"/> or higher is used.
    /// </summary>
    public bool EnableStaticFiles { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether static web assets (from RCLs) are enabled.
    /// This is automatically enabled in the development environment.
    /// </summary>
    public bool EnableStaticWebAssets { get; set; } = false;
}
