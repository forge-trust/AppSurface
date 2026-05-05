namespace ForgeTrust.Runnable.Web;

using Microsoft.AspNetCore.Routing;

/// <summary>
/// Represents configuration options for the web application, including MVC, CORS, and static file settings.
/// </summary>
public record WebOptions
{
    /// <summary>
    /// Gets a default instance of <see cref="WebOptions"/> with default configuration settings.
    /// </summary>
    public static readonly WebOptions Default = new();

    /// <summary>
    /// Gets or sets MVC-specific configuration options, such as support levels and custom MVC configuration.
    /// </summary>
    public MvcOptions Mvc { get; set; } = MvcOptions.Default;

    /// <summary>
    /// Gets or sets CORS configuration options for defining cross-origin resource sharing policies.
    /// </summary>
    public CorsOptions Cors { get; set; } = CorsOptions.Default;

    /// <summary>
    /// Gets or sets configuration options for serving static files within the web application.
    /// </summary>
    public StaticFilesOptions StaticFiles { get; set; } = StaticFilesOptions.Default;

    /// <summary>
    /// Gets or sets configuration options for conventional framework browser status pages.
    /// </summary>
    /// <remarks>
    /// The default value is <see cref="ErrorPagesOptions.Default"/>, which leaves browser status pages in
    /// <see cref="BrowserStatusPageMode.Auto"/>. In that mode, Runnable only enables the conventional browser
    /// 401, 403, and 404 experience when MVC support already includes views. Use explicit modes when an app
    /// must always force or always suppress the conventional pages. When enabled, Runnable reserves
    /// <c>/_runnable/errors/401</c>, <c>/_runnable/errors/403</c>, and <c>/_runnable/errors/404</c> for direct
    /// rendering, and ignores that path prefix when deciding whether to apply browser-oriented status-page
    /// middleware. Static export tooling still consumes only the 404 route and writes only <c>404.html</c>.
    /// </remarks>
    public ErrorPagesOptions Errors { get; set; } = ErrorPagesOptions.Default;

    /// <summary>
    /// Gets or sets an optional delegate to configure endpoint routing for the application.
    /// </summary>
    public Action<IEndpointRouteBuilder>? MapEndpoints { get; set; }
}
