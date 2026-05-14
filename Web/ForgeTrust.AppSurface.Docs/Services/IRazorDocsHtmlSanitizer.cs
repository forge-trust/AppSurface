namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Sanitizes rendered RazorDocs HTML using the package's docs-specific allowlist.
/// </summary>
/// <remarks>
/// <see cref="IRazorDocsHtmlSanitizer"/> exists to normalize rendered package documentation fragments before RazorDocs
/// displays them. It is not a general-purpose user-generated-content sanitizer, JavaScript policy, or replacement for
/// a host-owned Content Security Policy. Unsupported elements or attributes may be removed.
/// </remarks>
public interface IRazorDocsHtmlSanitizer
{
    /// <summary>
    /// Sanitizes the provided HTML fragment.
    /// </summary>
    /// <remarks>
    /// <see cref="Sanitize"/> expects a non-null rendered RazorDocs HTML fragment, not a complete document or
    /// unrendered template. Implementations should throw <see cref="ArgumentNullException"/> when
    /// <paramref name="html"/> is null and should preserve already-encoded text rather than double-encoding it.
    /// </remarks>
    /// <param name="html">The rendered RazorDocs HTML fragment to sanitize.</param>
    /// <returns>The sanitized HTML fragment.</returns>
    string Sanitize(string html);
}
