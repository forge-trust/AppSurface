using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Provides one host-selected, prevalidated theme document for a Web request scope.</summary>
internal sealed class AppSurfaceThemeSelectionDocumentProvider : IAppSurfaceThemeDocumentProvider
{
    private readonly IAppSurfaceWebThemeSelectionPolicy _policy;
    private readonly AppSurfaceThemeSelectionDocumentCache _cache;
    private readonly Lazy<AppSurfaceThemeDocument> _document;

    /// <summary>Initializes a scoped provider without invoking the policy during application startup.</summary>
    public AppSurfaceThemeSelectionDocumentProvider(
        IAppSurfaceWebThemeSelectionPolicy policy,
        AppSurfaceThemeSelectionDocumentCache cache)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(cache);

        _policy = policy;
        _cache = cache;
        _document = new Lazy<AppSurfaceThemeDocument>(SelectDocument, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public AppSurfaceThemeDocument GetDocument() => _document.Value;

    private AppSurfaceThemeDocument SelectDocument()
    {
        AppSurfaceThemeId themeId;
        try
        {
            if (!_policy.TrySelect(out themeId))
            {
                return _cache.DefaultDocument;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "ASWEBTHEME009: The AppSurface theme selection policy failed before a theme document could be rendered.");
        }

        if (_cache.TryGet(themeId, out var document))
        {
            return document;
        }

        throw new InvalidOperationException(
            "ASWEBTHEME008: The AppSurface theme selection policy returned an empty or unregistered theme pair.");
    }
}
