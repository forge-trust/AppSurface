namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Configures the browser-local preference enhancement for Web theme documents.</summary>
public sealed class AppSurfaceThemePreferenceOptions
{
    /// <summary>Gets or sets the origin-scoped local-storage key used for explicit Light or Dark selection.</summary>
    public string StorageKey { get; set; } = "as_theme";

    internal AppSurfaceThemePreferenceOptions Snapshot()
    {
        if (string.IsNullOrWhiteSpace(StorageKey)
            || StorageKey.Length > 64
            || StorageKey.Any(char.IsWhiteSpace)
            || StorageKey.Any(value => char.IsControl(value) || value is '\'' or '\"'))
        {
            throw new ArgumentException(
                "ASWEBTHEME001: AppSurfaceThemePreferenceOptions.StorageKey must be 1-64 non-whitespace characters without quotes or control characters.",
                nameof(StorageKey));
        }

        return new AppSurfaceThemePreferenceOptions { StorageKey = StorageKey };
    }
}
