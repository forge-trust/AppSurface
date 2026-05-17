using System.Reflection;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Resolves stylesheet paths for AppSurface Docs hosts.
/// </summary>
/// <remarks>
/// When the current application's root module lives in the AppSurface Docs assembly, AppSurface Docs layouts preserve the
/// historical root stylesheet URL at <c>~/css/site.gen.css</c>. Published and exported hosts may only materialize
/// the packaged Razor Class Library asset path, so <see cref="AppSurfaceDocsWebModule"/> also preserves the root URL via
/// a compatibility redirect to <c>~/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css</c>.
/// When AppSurface Docs is consumed from another host assembly, layouts link directly to that packaged asset path.
/// </remarks>
internal sealed class AppSurfaceDocsAssetPathResolver
{
    internal const string RootStylesheetPath = "~/css/site.gen.css";
    internal const string PackagedStylesheetPath = "~/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css";
    internal const string PackagedBrandIconPath = "~/_content/ForgeTrust.AppSurface.Docs/docs/appsurface-docs-icon.svg";

    private static readonly Assembly AppSurfaceDocsAssembly = typeof(AppSurfaceDocsWebModule).Assembly;

    private AppSurfaceDocsAssetPathResolver(string stylesheetPath)
    {
        StylesheetPath = stylesheetPath;
    }

    /// <summary>
    /// Gets the application-relative stylesheet path to use from AppSurface Docs layouts.
    /// </summary>
    public string StylesheetPath { get; }

    /// <summary>
    /// Gets the application-relative path for the AppSurface brand mark static asset.
    /// </summary>
    /// <remarks>
    /// The value resolves to <see cref="PackagedBrandIconPath"/>, the bundled static-web-assets location
    /// <c>~/_content/ForgeTrust.AppSurface.Docs/docs/appsurface-docs-icon.svg</c>. Razor views should use this
    /// property when referencing the AppSurface brand mark served by the docs host so the packaged asset path is
    /// maintained in one place instead of being repeated in layout markup. The returned path is application-relative;
    /// callers that render under a path base should pass it through URL helpers such as <c>Url.Content</c>, and hosts
    /// that rewrite assets through a CDN or a non-standard static-web-assets layout should use their explicit asset URL
    /// instead.
    /// </remarks>
    public string BrandIconPath => PackagedBrandIconPath;

    /// <summary>
    /// Creates the default asset-path resolver used when only the AppSurface Docs services are registered.
    /// </summary>
    /// <returns>A resolver that assumes AppSurface Docs is embedded in another host.</returns>
    internal static AppSurfaceDocsAssetPathResolver CreateDefault()
    {
        return new AppSurfaceDocsAssetPathResolver(PackagedStylesheetPath);
    }

    /// <summary>
    /// Creates the asset-path resolver for the supplied root module assembly.
    /// </summary>
    /// <param name="rootModuleAssembly">The assembly that owns the current host's root module.</param>
    /// <returns>The resolver that matches the current host's AppSurface Docs asset layout.</returns>
    internal static AppSurfaceDocsAssetPathResolver CreateForRootModule(Assembly rootModuleAssembly)
    {
        return new AppSurfaceDocsAssetPathResolver(
            IsRootModuleAssembly(rootModuleAssembly)
                ? RootStylesheetPath
                : PackagedStylesheetPath);
    }

    /// <summary>
    /// Determines whether the supplied root module assembly belongs to the AppSurface Docs standalone host.
    /// </summary>
    /// <param name="rootModuleAssembly">The assembly that owns the current host's root module.</param>
    /// <returns><see langword="true"/> when AppSurface Docs is the root module; otherwise, <see langword="false"/>.</returns>
    internal static bool IsRootModuleAssembly(Assembly rootModuleAssembly)
    {
        return rootModuleAssembly == AppSurfaceDocsAssembly;
    }
}
