using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class TextMateSharpRazorDocsCodeHighlighterTests
{
    private readonly TextMateSharpRazorDocsCodeHighlighter _highlighter = new(
        new RazorDocsCodeLanguageCatalog(),
        NullLogger<TextMateSharpRazorDocsCodeHighlighter>.Instance);

    [Fact]
    public void Highlight_ShouldRenderCSharpWithTokenSpans()
    {
        var result = _highlighter.Highlight(new RazorDocsCodeBlock(
            "public sealed class Demo { public string Name { get; init; } = \"RazorDocs\"; }",
            "cs"));

        Assert.True(result.IsHighlighted);
        Assert.Equal("csharp", result.NormalizedLanguage);
        Assert.Contains("doc-code doc-code--highlighted doc-code--language-csharp language-csharp", result.Html);
        Assert.Contains("language-csharp\"><span class=\"doc-code__language\">C#</span><code>", result.Html);
        Assert.Contains("doc-token doc-token--keyword", result.Html);
        Assert.Contains("RazorDocs", result.Html);
    }

    [Fact]
    public void Highlight_ShouldRenderUnknownLanguageAsEscapedPlainText()
    {
        var result = _highlighter.Highlight(new RazorDocsCodeBlock(
            "<script>alert('x')</script>",
            "madeup"));

        Assert.False(result.IsHighlighted);
        Assert.Equal("unknown", result.NormalizedLanguage);
        Assert.Contains("doc-code doc-code--plain doc-code--language-unknown language-madeup", result.Html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", result.Html);
        Assert.DoesNotContain("<script>", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Highlight_ShouldNotCopyMaliciousLanguageIntoClassAttribute()
    {
        var result = _highlighter.Highlight(new RazorDocsCodeBlock(
            "safe",
            "\"><script>"));

        Assert.False(result.IsHighlighted);
        Assert.Contains("doc-code--language-unknown language-plaintext", result.Html);
        Assert.DoesNotContain("script", GetPreClassAttribute(result.Html), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Highlight_ShouldRenderNoLanguageAsPlainText()
    {
        var result = _highlighter.Highlight(new RazorDocsCodeBlock("plain <b>code</b>", null));

        Assert.False(result.IsHighlighted);
        Assert.Equal("plaintext", result.NormalizedLanguage);
        Assert.Contains("doc-code--language-plaintext language-plaintext", result.Html);
        Assert.Contains("plain &lt;b&gt;code&lt;/b&gt;", result.Html);
    }

    [Fact]
    public void Highlight_ShouldRenderOversizedBlockAsPlainText()
    {
        var code = new string('x', TextMateSharpRazorDocsCodeHighlighter.MaxHighlightedCodeBlockCharacters + 1);

        var result = _highlighter.Highlight(new RazorDocsCodeBlock(code, "csharp"));

        Assert.False(result.IsHighlighted);
        Assert.Equal("csharp", result.NormalizedLanguage);
        Assert.Contains("doc-code--plain doc-code--language-csharp language-csharp", result.Html);
        Assert.DoesNotContain("doc-token", result.Html);
    }

    [Fact]
    public void Highlight_ShouldApplyLineLimitBeforeNormalizingCarriageReturns()
    {
        var code = string.Join('\r', Enumerable.Repeat("x", TextMateSharpRazorDocsCodeHighlighter.MaxHighlightedCodeBlockLines + 1));

        var result = _highlighter.Highlight(new RazorDocsCodeBlock(code, "csharp"));

        Assert.False(result.IsHighlighted);
        Assert.Equal("csharp", result.NormalizedLanguage);
        Assert.DoesNotContain("doc-token", result.Html);
    }

    [Fact]
    public void Highlight_ShouldReuseCachedGrammarAcrossRepeatedBlocks()
    {
        _highlighter.Highlight(new RazorDocsCodeBlock("public class One { }", "csharp"));
        _highlighter.Highlight(new RazorDocsCodeBlock("public class Two { }", "cs"));

        Assert.Equal(1, _highlighter.CachedGrammarCount);
    }

    private static string GetPreClassAttribute(string html)
    {
        const string prefix = "<pre class=\"";
        var start = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end > start);
        return html[start..end];
    }
}
