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

        var defaultResolution = ResolveDefault(resolver);
        var defaultDocument = AppSurfaceThemeDocumentSerializer.Serialize(defaultResolution);
        if (!defaultDocument.IsRenderable)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME008: The configured default AppSurface theme pair could not be safely rendered by the theme selection adapter.");
        }

        var documents = new Dictionary<string, AppSurfaceThemeDocument>(StringComparer.Ordinal);
        var registeredThemeIds = new HashSet<string>(StringComparer.Ordinal);
        var registeredDefault = false;
        var themeIds = GetThemeIds(registry);

        foreach (var themeId in themeIds)
        {
            if (string.IsNullOrEmpty(themeId.Value) || !registeredThemeIds.Add(themeId.Value))
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: The sealed neutral theme registry exposed invalid theme pair identifiers.");
            }

            AppSurfaceThemePair pair;
            try
            {
                pair = registry.GetRequired(themeId);
            }
            catch (KeyNotFoundException)
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: The sealed neutral theme registry could not resolve an advertised theme pair identifier.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: The sealed neutral theme registry failed while resolving an advertised theme pair identifier.");
            }

            if (pair is null || pair.Id != themeId)
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: The sealed neutral theme registry returned a pair with a different identifier.");
            }

            var isDefault = themeId == defaultResolution.Id;
            if (isDefault
                && (pair.Light != defaultResolution.Light || pair.Dark != defaultResolution.Dark))
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: The configured default AppSurface theme pair does not match the sealed neutral registry.");
            }

            var document = isDefault
                ? defaultDocument
                : AppSurfaceThemeDocumentSerializer.Serialize(
                    new AppSurfaceThemeResolution(pair.Id, defaultResolution.Mode, pair.Light, pair.Dark));

            if (!document.IsRenderable)
            {
                throw new InvalidOperationException(
                    "ASWEBTHEME008: A registered AppSurface theme pair could not be safely rendered by the theme selection adapter.");
            }

            documents.Add(themeId.Value, document);
            registeredDefault |= isDefault;
        }

        if (!registeredDefault)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME008: The configured default AppSurface theme pair is not registered in the sealed neutral registry.");
        }

        _documents = documents;
        DefaultDocument = defaultDocument;
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

    private static AppSurfaceThemeResolution ResolveDefault(IAppSurfaceThemeResolver resolver)
    {
        try
        {
            return resolver.ResolveDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME008: The configured default AppSurface theme pair could not be resolved by the theme selection adapter.");
        }
    }

    private static AppSurfaceThemeId[] GetThemeIds(IAppSurfaceThemeRegistry registry)
    {
        IReadOnlyCollection<AppSurfaceThemeId>? themeIds;
        try
        {
            themeIds = registry.ThemeIds;
            if (themeIds is not null)
            {
                return themeIds.ToArray();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME008: The sealed neutral theme registry could not expose its theme pair identifiers.");
        }

        throw new InvalidOperationException(
            "ASWEBTHEME008: The sealed neutral theme registry did not expose its theme pair identifiers.");
    }
}
