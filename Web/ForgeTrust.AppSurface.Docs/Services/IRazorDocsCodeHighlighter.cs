namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Highlights Markdown code fences behind RazorDocs' internal HTML contract.
/// </summary>
internal interface IRazorDocsCodeHighlighter
{
    /// <summary>
    /// Renders a code block as either highlighted token markup or escaped plaintext fallback.
    /// </summary>
    /// <param name="block">The code block to render.</param>
    /// <returns>RazorDocs-owned code block HTML.</returns>
    RazorDocsHighlightedCode Highlight(RazorDocsCodeBlock block);
}
