using Markdig;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

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

    internal static IRazorDocsCodeHighlighter CreateDefaultHighlighter()
    {
        return new TextMateSharpRazorDocsCodeHighlighter(
            RazorDocsCodeLanguageCatalog.Shared,
            NullLogger<TextMateSharpRazorDocsCodeHighlighter>.Instance);
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

        foreach (var token in info.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            return token;
        }

        return null;
    }
}
