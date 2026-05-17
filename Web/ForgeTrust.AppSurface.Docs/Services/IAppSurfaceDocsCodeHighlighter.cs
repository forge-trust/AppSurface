namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Highlights Markdown code fences behind AppSurface Docs' internal HTML contract.
/// </summary>
internal interface IAppSurfaceDocsCodeHighlighter
{
    /// <summary>
    /// Renders a code block as either highlighted token markup or escaped plaintext fallback.
    /// </summary>
    /// <param name="block">The code block to render.</param>
    /// <returns>AppSurface Docs-owned code block HTML.</returns>
    AppSurfaceDocsHighlightedCode Highlight(AppSurfaceDocsCodeBlock block);
}
