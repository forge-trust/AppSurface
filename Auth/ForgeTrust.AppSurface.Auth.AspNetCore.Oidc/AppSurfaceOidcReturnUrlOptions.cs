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

    /// <summary>
    /// Normalizes an optional passive prompt target path according to the configured return-url policy.
    /// </summary>
    /// <param name="targetPath">Optional prompt target path supplied by the host.</param>
    /// <param name="parameterName">Parameter name used when reporting validation failures.</param>
    /// <returns>
    /// <see langword="null"/> when <paramref name="targetPath"/> is null, empty, or whitespace; otherwise the original
    /// target path after validation.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when local-only validation is enabled and <paramref name="targetPath"/> is not a safe app-relative path.
    /// </exception>
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

    /// <summary>
    /// Determines whether a target path is safe for local app-relative prompt metadata.
    /// </summary>
    /// <param name="targetPath">Target path to validate.</param>
    /// <returns>
    /// <see langword="true"/> when the path starts with a single forward slash, contains no backslashes, and contains no
    /// control characters; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Protocol-relative paths such as <c>//example.com</c>, backslash-prefixed paths, and paths containing backslashes
    /// are rejected because browsers and proxies can interpret them as cross-origin redirects.
    /// </remarks>
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
