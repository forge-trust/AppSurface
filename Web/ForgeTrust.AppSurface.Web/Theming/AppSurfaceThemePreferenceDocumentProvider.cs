using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Provides a cached System-first document for browser-local theme preferences.</summary>
/// <remarks>
/// The neutral resolver is evaluated exactly once during construction. The resulting document is cached for every
/// request and is empty when the resolved pair cannot safely be serialized. This provider does not inspect HTTP
/// state or browser storage; the deterministic bootstrap performs browser-local selection after rendering.
/// </remarks>
internal sealed class AppSurfaceThemePreferenceDocumentProvider : IAppSurfaceThemeDocumentProvider
{
    private readonly AppSurfaceThemeDocument _document;

    /// <summary>Resolves and serializes the configured pair once.</summary>
    /// <param name="resolver">The registered neutral theme resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
    public AppSurfaceThemePreferenceDocumentProvider(IAppSurfaceThemeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        _document = AppSurfaceThemeDocumentSerializer.SerializePreference(resolver.ResolveDefault());
    }

    /// <summary>Gets the cached document without re-resolving the configured theme pair.</summary>
    /// <returns>The cached renderable document, or an empty document when the resolved pair was unsafe.</returns>
    public AppSurfaceThemeDocument GetDocument() => _document;
}
