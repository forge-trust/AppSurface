namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Normalizes browser-facing metadata hrefs that render as plain anchors.
/// </summary>
internal static class AppSurfaceDocsMetadataHrefPolicy
{
    /// <summary>
    /// Normalizes <c>trust.migration.href</c> and classifies it as absent, safe to render, or rejected.
    /// </summary>
    /// <param name="href">The authored migration href from Markdown front matter or sidecar metadata.</param>
    /// <returns>An explicit policy result that distinguishes missing optional metadata from rejected unsafe metadata.</returns>
    internal static AppSurfaceDocsMetadataHrefPolicyResult NormalizeTrustMigrationHref(string? href)
    {
        var normalized = string.IsNullOrWhiteSpace(href) ? null : href.Trim();
        if (normalized is null)
        {
            return AppSurfaceDocsMetadataHrefPolicyResult.Absent();
        }

        if (ContainsControlCharacter(normalized))
        {
            return AppSurfaceDocsMetadataHrefPolicyResult.Rejected(normalized);
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return AppSurfaceDocsMetadataHrefPolicyResult.Rejected(normalized);
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("#", StringComparison.Ordinal))
        {
            return AppSurfaceDocsMetadataHrefPolicyResult.Allowed(normalized);
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? AppSurfaceDocsMetadataHrefPolicyResult.Allowed(normalized)
                : AppSurfaceDocsMetadataHrefPolicyResult.Rejected(normalized);
        }

        if (HasSchemeLikePrefix(normalized))
        {
            return AppSurfaceDocsMetadataHrefPolicyResult.Rejected(normalized);
        }

        return AppSurfaceDocsMetadataHrefPolicyResult.Allowed(normalized);
    }

    private static bool ContainsControlCharacter(string value)
    {
        return value.Any(char.IsControl);
    }

    private static bool HasSchemeLikePrefix(string value)
    {
        var delimiterIndex = value.IndexOfAny([':', '/', '?', '#']);
        return delimiterIndex > 0 && value[delimiterIndex] == ':';
    }
}

internal enum AppSurfaceDocsMetadataHrefPolicyState
{
    /// <summary>
    /// No nonblank href was authored.
    /// </summary>
    Absent,

    /// <summary>
    /// The href is safe to render as a plain browser anchor.
    /// </summary>
    Allowed,

    /// <summary>
    /// The href was nonblank but failed the safe metadata-link policy.
    /// </summary>
    Rejected
}

/// <summary>
/// Result returned by metadata href normalization.
/// </summary>
/// <param name="State">Explicit policy state for the authored value.</param>
/// <param name="Href">The trimmed href when the value was allowed or rejected; <see langword="null"/> when absent.</param>
internal readonly record struct AppSurfaceDocsMetadataHrefPolicyResult(
    AppSurfaceDocsMetadataHrefPolicyState State,
    string? Href)
{
    /// <summary>
    /// Creates an absent href result for missing optional metadata.
    /// </summary>
    /// <returns>An absent result with no href.</returns>
    public static AppSurfaceDocsMetadataHrefPolicyResult Absent()
    {
        return new AppSurfaceDocsMetadataHrefPolicyResult(AppSurfaceDocsMetadataHrefPolicyState.Absent, null);
    }

    /// <summary>
    /// Creates an allowed href result.
    /// </summary>
    /// <param name="href">The normalized safe href.</param>
    /// <returns>An allowed result.</returns>
    public static AppSurfaceDocsMetadataHrefPolicyResult Allowed(string href)
    {
        return new AppSurfaceDocsMetadataHrefPolicyResult(AppSurfaceDocsMetadataHrefPolicyState.Allowed, href);
    }

    /// <summary>
    /// Creates a rejected href result.
    /// </summary>
    /// <param name="href">The normalized href that failed policy validation.</param>
    /// <returns>A rejected result.</returns>
    public static AppSurfaceDocsMetadataHrefPolicyResult Rejected(string href)
    {
        return new AppSurfaceDocsMetadataHrefPolicyResult(AppSurfaceDocsMetadataHrefPolicyState.Rejected, href);
    }
}
