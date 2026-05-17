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

    public static string? NormalizeTextOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    public static string NormalizeDisplayName(string? displayName)
    {
        return NormalizeTextOrNull(displayName) ?? AppSurfaceDocsIdentityOptions.DefaultDisplayName;
    }

    public static string? NormalizeBrowserPathOrNull(string? value)
    {
        return TryNormalizeBrowserPath(value, out var normalizedPath, out _)
            ? normalizedPath
            : NormalizeTextOrNull(value);
    }

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
            if (IsValidPathTail(trimmed[2..], out error))
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
