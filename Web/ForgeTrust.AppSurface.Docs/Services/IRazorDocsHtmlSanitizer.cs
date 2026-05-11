namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Sanitizes rendered RazorDocs HTML using the package's docs-specific allowlist.
/// </summary>
public interface IRazorDocsHtmlSanitizer
{
    /// <summary>
    /// Sanitizes the provided HTML fragment.
    /// </summary>
    /// <param name="html">The HTML fragment to sanitize.</param>
    /// <returns>The sanitized HTML fragment.</returns>
    string Sanitize(string html);
}
