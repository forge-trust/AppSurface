using ForgeTrust.AppSurface.Theming;

namespace ForgeTrust.AppSurface.Web.Theming;

internal sealed class AppSurfaceThemePreferenceDocumentProvider : IAppSurfaceThemeDocumentProvider
{
    private readonly AppSurfaceThemeDocument _document;

    public AppSurfaceThemePreferenceDocumentProvider(
        IAppSurfaceThemeRegistry registry,
        IAppSurfaceThemeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolver);

        var defaultResolution = resolver.ResolveDefault();
        var pair = registry.GetRequired(defaultResolution.Id);
        _document = AppSurfaceThemeDocumentSerializer.SerializePreference(
            new AppSurfaceThemeResolution(pair.Id, AppSurfaceThemeMode.System, pair.Light, pair.Dark));
    }

    public AppSurfaceThemeDocument GetDocument() => _document;
}
