namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Resolves normalized AppSurface Docs identity options into render-ready browser chrome values.
/// </summary>
/// <remarks>
/// The resolved identity intentionally stores only app-relative or app-root paths. Razor views apply the current
/// request path base at render time with <c>Url.PathBaseAware(...)</c>, so singleton identity resolution stays safe for
/// virtual-directory deployments and tests that render the same service provider under multiple path bases.
/// </remarks>
public sealed class AppSurfaceDocsIdentityResolver
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsIdentityResolver"/> class.
    /// </summary>
    /// <param name="options">The normalized AppSurface Docs options.</param>
    /// <param name="urlBuilder">The docs URL builder used for default docs home links.</param>
    public AppSurfaceDocsIdentityResolver(AppSurfaceDocsOptions options, DocsUrlBuilder urlBuilder)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(urlBuilder);

        Identity = Resolve(options.Identity, urlBuilder);
    }

    /// <summary>
    /// Gets the render-ready identity used by AppSurface Docs layouts.
    /// </summary>
    public AppSurfaceDocsResolvedIdentity Identity { get; }

    private static AppSurfaceDocsResolvedIdentity Resolve(AppSurfaceDocsIdentityOptions identity, DocsUrlBuilder urlBuilder)
    {
        var logoOptions = identity.Logo ?? new AppSurfaceDocsLogoOptions();
        var faviconOptions = identity.Favicon ?? new AppSurfaceDocsFaviconOptions();
        var displayName = AppSurfaceDocsIdentityPath.NormalizeDisplayName(identity.DisplayName);
        var homeHref = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(identity.HomeHref) ?? urlBuilder.Routes.Home;
        var logoPath = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(logoOptions.Path);
        var logo = logoPath is null
            ? null
            : new AppSurfaceDocsResolvedLogo(
                logoPath,
                AppSurfaceDocsIdentityPath.NormalizeTextOrNull(logoOptions.AltText) ?? displayName);

        List<AppSurfaceDocsResolvedFavicon> favicons = [];
        AddFavicon(favicons, faviconOptions.SvgPath, "image/svg+xml");
        AddFavicon(favicons, faviconOptions.IcoPath, "image/x-icon");
        AddFavicon(favicons, faviconOptions.PngPath, "image/png");

        return new AppSurfaceDocsResolvedIdentity(displayName, homeHref, logo, favicons.AsReadOnly());
    }

    private static void AddFavicon(List<AppSurfaceDocsResolvedFavicon> favicons, string? path, string type)
    {
        var normalizedPath = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(path);
        if (normalizedPath is not null)
        {
            favicons.Add(new AppSurfaceDocsResolvedFavicon(normalizedPath, type));
        }
    }
}

/// <summary>
/// Render-ready AppSurface Docs identity.
/// </summary>
/// <param name="DisplayName">Visible docs product name.</param>
/// <param name="HomeHref">App-root or application-relative brand home link.</param>
/// <param name="Logo">Optional resolved logo.</param>
/// <param name="Favicons">Resolved favicon link entries.</param>
public sealed record AppSurfaceDocsResolvedIdentity(
    string DisplayName,
    string HomeHref,
    AppSurfaceDocsResolvedLogo? Logo,
    IReadOnlyList<AppSurfaceDocsResolvedFavicon> Favicons);

/// <summary>
/// Render-ready AppSurface Docs logo.
/// </summary>
/// <param name="Path">App-root or application-relative image path.</param>
/// <param name="AltText">Logo alt text.</param>
public sealed record AppSurfaceDocsResolvedLogo(string Path, string AltText);

/// <summary>
/// Render-ready AppSurface Docs favicon entry.
/// </summary>
/// <param name="Path">App-root or application-relative favicon path.</param>
/// <param name="Type">Favicon MIME type.</param>
public sealed record AppSurfaceDocsResolvedFavicon(string Path, string Type);
