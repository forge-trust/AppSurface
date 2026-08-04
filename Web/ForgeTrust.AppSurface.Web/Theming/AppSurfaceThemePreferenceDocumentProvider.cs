using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

internal sealed class AppSurfaceThemePreferenceDocumentProvider : IAppSurfaceThemeDocumentProvider
{
    private readonly AppSurfaceThemeDocument _document;

    public AppSurfaceThemePreferenceDocumentProvider(IAppSurfaceThemeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        _document = AppSurfaceThemeDocumentSerializer.SerializePreference(resolver.ResolveDefault());
    }

    public AppSurfaceThemeDocument GetDocument() => _document;
}
