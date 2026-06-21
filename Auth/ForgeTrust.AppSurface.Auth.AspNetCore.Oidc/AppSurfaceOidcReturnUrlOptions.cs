namespace ForgeTrust.AppSurface.Auth.AspNetCore.Oidc;

/// <summary>
/// Configures return-url validation for AppSurface OIDC passive prompt helpers.
/// </summary>
/// <remarks>
/// The initial package only allows local app-relative return targets. It does not URL-decode, redirect, challenge, or
/// call an identity provider.
/// </remarks>
public sealed class AppSurfaceOidcReturnUrlOptions
{
    private bool _allowLocalOnly = true;

    /// <summary>
    /// Requires prompt targets to be local app-relative paths.
    /// </summary>
    /// <returns>The current options instance for chaining.</returns>
    public AppSurfaceOidcReturnUrlOptions AllowLocalOnly()
    {
        _allowLocalOnly = true;
        return this;
    }

    internal string? Normalize(string? targetPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        if (_allowLocalOnly && !IsSafeAppRelativePath(targetPath))
        {
            throw new ArgumentException("Return URLs must be safe app-relative paths.", parameterName);
        }

        return targetPath;
    }

    internal static bool IsSafeAppRelativePath(string targetPath)
    {
        if (targetPath[0] != '/'
            || targetPath.Length > 1 && (targetPath[1] == '/' || targetPath[1] == '\\')
            || targetPath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return !targetPath.Any(char.IsControl);
    }
}
