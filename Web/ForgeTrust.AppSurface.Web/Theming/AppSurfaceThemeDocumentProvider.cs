using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Provides the immutable Web document for the configured default AppSurface theme.</summary>
public interface IAppSurfaceThemeDocumentProvider
{
    /// <summary>Gets the current safe document snapshot.</summary>
    /// <returns>A renderable document, or <see cref="AppSurfaceThemeDocument.Empty"/> when it is unsafe.</returns>
    AppSurfaceThemeDocument GetDocument();
}

/// <summary>Builds and retains the immutable Web document from the neutral default-theme resolver.</summary>
public sealed class AppSurfaceThemeDocumentProvider : IAppSurfaceThemeDocumentProvider
{
    private readonly AppSurfaceThemeDocument _document;

    /// <summary>Initializes a provider from the neutral default-theme resolver.</summary>
    /// <param name="resolver">The configured neutral theme resolver.</param>
    public AppSurfaceThemeDocumentProvider(IAppSurfaceThemeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _document = AppSurfaceThemeDocumentSerializer.Serialize(resolver.ResolveDefault());
    }

    /// <inheritdoc />
    public AppSurfaceThemeDocument GetDocument() => _document;
}
