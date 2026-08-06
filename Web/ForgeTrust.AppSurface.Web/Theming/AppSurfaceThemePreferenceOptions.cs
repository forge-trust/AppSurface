namespace ForgeTrust.AppSurface.Web.Theming;

/// <summary>Configures the browser-local System/Light/Dark preference enhancement for Web theme documents.</summary>
/// <remarks>
/// The enhancement stores only an explicit Light or Dark selection in browser local storage for the current origin;
/// selecting System removes that value and returns to the operating-system preference. It neither reads nor writes
/// cookies, server-side preference state, or additional document URLs.
/// </remarks>
public sealed class AppSurfaceThemePreferenceOptions
{
    /// <summary>Gets or sets the origin-scoped local-storage key used for explicit Light or Dark selection.</summary>
    /// <remarks>
    /// The default is <c>as_theme</c>. Keys must contain 1 to 64 non-whitespace characters and cannot include quote
    /// or control characters because the value is emitted as an encoded HTML data attribute.
    /// </remarks>
    public string StorageKey { get; set; } = "as_theme";

    /// <summary>Validates and copies this configuration for service registration.</summary>
    /// <returns>A validated snapshot detached from the caller's mutable options instance.</returns>
    /// <exception cref="ArgumentException"><see cref="StorageKey"/> is blank, unsafe for an HTML data attribute, or longer than 64 characters.</exception>
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
