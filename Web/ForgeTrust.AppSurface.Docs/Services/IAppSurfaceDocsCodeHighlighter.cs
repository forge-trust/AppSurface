namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Highlights Markdown code fences behind AppSurface Docs' internal HTML contract.
/// </summary>
/// <remarks>
/// Use this abstraction when Markdown rendering needs package-owned code HTML without coupling callers to a specific
/// highlighter implementation. Implementations must return HTML-safe output for direct insertion into docs rendering
/// templates, including escaped plaintext fallback for unsupported languages or failed highlighting.
/// </remarks>
internal interface IAppSurfaceDocsCodeHighlighter
{
    /// <summary>
    /// Renders a code block as either highlighted token markup or escaped plaintext fallback.
    /// </summary>
    /// <param name="block">Non-null code block metadata and source text to render.</param>
    /// <returns>
    /// AppSurface Docs-owned code block HTML. Callers treat the returned value as immutable render output and insert it
    /// without additional escaping.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="block"/> is <see langword="null"/>.</exception>
    AppSurfaceDocsHighlightedCode Highlight(AppSurfaceDocsCodeBlock block);
}
