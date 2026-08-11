using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Builds immutable Web documents for every pair in the sealed neutral registry.</summary>
/// <remarks>
/// The cache is keyed only by registered pair id because its values contain package-owned CSS and HTML derived from
/// immutable pairs. A pair id is never a host response-cache key: applications remain responsible for partitioning
/// or disabling any cache that contains tenant-specific content, authorization, or other host-owned state.
/// </remarks>
internal sealed class AppSurfaceThemeSelectionDocumentCache
{
    private readonly IReadOnlyDictionary<string, AppSurfaceThemeDocument> _documents;

    /// <summary>Creates snapshots for the configured default and every registered pair.</summary>
    public AppSurfaceThemeSelectionDocumentCache(
        IAppSurfaceThemeRegistry registry,
        IAppSurfaceThemeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolver);

        var defaultResolution = resolver.ResolveDefault();
        DefaultDocument = AppSurfaceThemeDocumentSerializer.Serialize(defaultResolution);

        var documents = new Dictionary<string, AppSurfaceThemeDocument>(StringComparer.Ordinal);
        foreach (var themeId in registry.ThemeIds)
        {
            var pair = registry.GetRequired(themeId);
            var document = pair.Id == defaultResolution.Id
                ? DefaultDocument
                : AppSurfaceThemeDocumentSerializer.Serialize(
                    new AppSurfaceThemeResolution(pair.Id, defaultResolution.Mode, pair.Light, pair.Dark));

            if (!document.IsRenderable)
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: A registered AppSurface theme pair could not be safely rendered by the theme selection adapter.");
            }

            documents.Add(pair.Id.Value, document);
        }

        _documents = documents;
    }

    /// <summary>Gets the ordinary configured-default document used when a policy has no selection.</summary>
    public AppSurfaceThemeDocument DefaultDocument { get; }

    /// <summary>Attempts to get the prevalidated document for a selected registered pair.</summary>
    public bool TryGet(AppSurfaceThemeId themeId, out AppSurfaceThemeDocument document)
    {
        if (!string.IsNullOrEmpty(themeId.Value) && _documents.TryGetValue(themeId.Value, out document!))
        {
            return true;
        }

        document = AppSurfaceThemeDocument.Empty;
        return false;
    }
}
