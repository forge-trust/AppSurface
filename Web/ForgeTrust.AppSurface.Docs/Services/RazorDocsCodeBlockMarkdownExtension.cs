using Markdig;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Registers RazorDocs' fenced-code renderer with Markdig.
/// </summary>
internal sealed class RazorDocsCodeBlockMarkdownExtension : IMarkdownExtension
{
    private readonly IRazorDocsCodeHighlighter _highlighter;

    /// <summary>
    /// Initializes a new instance of the Markdown extension.
    /// </summary>
    /// <param name="highlighter">The highlighter used by fenced code blocks.</param>
    internal RazorDocsCodeBlockMarkdownExtension(IRazorDocsCodeHighlighter highlighter)
    {
        ArgumentNullException.ThrowIfNull(highlighter);
        _highlighter = highlighter;
    }

    /// <inheritdoc />
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    /// <inheritdoc />
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
        {
            htmlRenderer.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(
                new RazorDocsCodeBlockRenderer(_highlighter));
        }
    }

    /// <summary>
    /// Creates the default TextMateSharp-backed RazorDocs highlighter.
    /// </summary>
    /// <param name="logger">Logger used to emit diagnostics when grammar loading or highlighting falls back.</param>
    /// <returns>The default RazorDocs code highlighter.</returns>
    internal static IRazorDocsCodeHighlighter CreateDefaultHighlighter(
        ILogger<TextMateSharpRazorDocsCodeHighlighter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new TextMateSharpRazorDocsCodeHighlighter(
            RazorDocsCodeLanguageCatalog.Shared,
            logger);
    }
}

/// <summary>
/// Renders Markdown fenced code blocks through RazorDocs' highlighter contract.
/// </summary>
internal sealed class RazorDocsCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    private readonly IRazorDocsCodeHighlighter _highlighter;

    /// <summary>
    /// Initializes a new instance of the fenced-code renderer.
    /// </summary>
    /// <param name="highlighter">The highlighter used for fenced code blocks.</param>
    internal RazorDocsCodeBlockRenderer(IRazorDocsCodeHighlighter highlighter)
    {
        ArgumentNullException.ThrowIfNull(highlighter);
        _highlighter = highlighter;
    }

    /// <inheritdoc />
    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(obj);

        var code = obj.Lines.ToString();
        var language = obj is FencedCodeBlock fencedCodeBlock
            ? ExtractLanguage(fencedCodeBlock)
            : null;
        var highlighted = _highlighter.Highlight(new RazorDocsCodeBlock(code, language));
        renderer.Write(highlighted.Html);
    }

    /// <summary>
    /// Extracts the first language token from a fenced code block's info string.
    /// </summary>
    /// <param name="block">The fenced code block to inspect.</param>
    /// <returns>The first language token, or <see langword="null" /> when the fence has no info string.</returns>
    internal static string? ExtractLanguage(FencedCodeBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var info = block.UnescapedInfo.IsEmpty ? string.Empty : block.UnescapedInfo.ToString();
        if (string.IsNullOrWhiteSpace(info))
        {
            info = block.Info ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        return info.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
