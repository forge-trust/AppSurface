using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Normalizes and validates browser paths used by AppSurface Docs identity options.
/// </summary>
/// <remarks>
/// Keep this helper as the single policy point for brand asset and home-link paths. The options post-configurator,
/// validator, and resolved identity service all depend on the same rules so path behavior cannot drift between
/// appsettings, environment variables, startup validation, and Razor rendering.
/// </remarks>
internal static class AppSurfaceDocsIdentityPath
{
    private static readonly char[] QueryOrFragmentChars = ['?', '#'];
    private static readonly Regex CssHexColorRegex = new(
        @"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <summary>
    /// Trims a text option value and treats blank text as omitted.
    /// </summary>
    /// <param name="value">Raw configured text value.</param>
    /// <returns>The trimmed text, or <see langword="null"/> when the value is null, empty, or whitespace.</returns>
    public static string? NormalizeTextOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    /// <summary>
    /// Resolves the visible AppSurface Docs display name.
    /// </summary>
    /// <param name="displayName">Configured display name.</param>
    /// <returns>The trimmed display name, or <see cref="AppSurfaceDocsIdentityOptions.DefaultDisplayName"/> when blank.</returns>
    public static string NormalizeDisplayName(string? displayName)
    {
        return NormalizeTextOrNull(displayName) ?? AppSurfaceDocsIdentityOptions.DefaultDisplayName;
    }

    /// <summary>
    /// Normalizes a browser path when it is valid and preserves invalid non-blank text for later validation errors.
    /// </summary>
    /// <param name="value">Configured app-root or application-relative browser path.</param>
    /// <returns>
    /// A normalized path when valid, the trimmed original value when invalid, or <see langword="null"/> when blank.
    /// </returns>
    public static string? NormalizeBrowserPathOrNull(string? value)
    {
        return TryNormalizeBrowserPath(value, out var normalizedPath, out _)
            ? normalizedPath
            : NormalizeTextOrNull(value);
    }

    /// <summary>
    /// Normalizes a CSS hex color and preserves invalid non-blank text for later validation errors.
    /// </summary>
    /// <param name="value">Configured CSS hex color.</param>
    /// <returns>A lower-invariant CSS hex color when valid, the trimmed original value when invalid, or null when blank.</returns>
    public static string? NormalizeCssHexColorOrNull(string? value)
    {
        return TryNormalizeCssHexColor(value, out var normalizedColor, out _)
            ? normalizedColor
            : NormalizeTextOrNull(value);
    }

    /// <summary>
    /// Validates and normalizes a CSS hex color suitable for the package-owned wordmark style variable.
    /// </summary>
    /// <param name="value">Configured color value.</param>
    /// <param name="normalizedColor">Lower-invariant color when valid or blank; otherwise null.</param>
    /// <param name="error">Validation error message when invalid; otherwise an empty string.</param>
    /// <returns>True when the value is blank, a three-digit hex color, or a six-digit hex color; otherwise false.</returns>
    public static bool TryNormalizeCssHexColor(string? value, out string? normalizedColor, out string error)
    {
        normalizedColor = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (!CssHexColorRegex.IsMatch(trimmed))
        {
            error = "must be a CSS hex color such as #3b82f6.";
            return false;
        }

        normalizedColor = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Validates and normalizes an app-root or application-relative browser path.
    /// </summary>
    /// <param name="value">Configured path value.</param>
    /// <param name="normalizedPath">Normalized path when the value is valid or blank; otherwise <see langword="null"/>.</param>
    /// <param name="error">Validation error message when invalid; otherwise an empty string.</param>
    /// <returns>
    /// <see langword="true"/> when the value is blank, starts with <c>/</c>, or starts with <c>~/</c> and does not
    /// contain remote URL, data URL, query string, fragment, backslash, or traversal segments; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryNormalizeBrowserPath(string? value, out string? normalizedPath, out string error)
    {
        normalizedPath = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            var pathTail = trimmed[2..];
            if (pathTail.StartsWith("/", StringComparison.Ordinal))
            {
                error = "must not be protocol-relative.";
                return false;
            }

            if (IsValidPathTail(pathTail, out error))
            {
                normalizedPath = trimmed;
                return true;
            }

            return false;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                error = "must not be protocol-relative.";
                return false;
            }

            if (IsValidPathTail(trimmed[1..], out error))
            {
                normalizedPath = trimmed;
                return true;
            }

            return false;
        }

        error = "must be an app-root browser path like '/docs/logo.svg' or an application-relative path like '~/docs/logo.svg'.";
        return false;
    }

    private static bool IsValidPathTail(string pathTail, out string error)
    {
        error = string.Empty;

        if (pathTail.Length == 0)
        {
            return true;
        }

        if (pathTail.Contains("://", StringComparison.Ordinal)
            || pathTail.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            error = "must not be a remote URL or data URL.";
            return false;
        }

        if (pathTail.IndexOfAny(QueryOrFragmentChars) >= 0)
        {
            error = "must not include a query string or fragment.";
            return false;
        }

        if (pathTail.Contains('\\', StringComparison.Ordinal))
        {
            error = "must use forward slashes.";
            return false;
        }

        if (pathTail.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            error = "must not include traversal segments.";
            return false;
        }

        return true;
    }
}
