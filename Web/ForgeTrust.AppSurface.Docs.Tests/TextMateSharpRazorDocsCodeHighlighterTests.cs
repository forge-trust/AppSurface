using System.Text;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TextMateSharp.Grammars;

namespace ForgeTrust.AppSurface.Docs.Tests;

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
    public void Highlight_ShouldTreatNullCodeAsEmptyPlainText()
    {
        var result = _highlighter.Highlight(new RazorDocsCodeBlock(null, "plaintext"));

        Assert.False(result.IsHighlighted);
        Assert.Equal("plaintext", result.NormalizedLanguage);
        Assert.Contains("doc-code--plain doc-code--language-plaintext language-plaintext", result.Html);
        Assert.EndsWith("<code></code></pre>", result.Html, StringComparison.Ordinal);
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
    public void Highlight_ShouldRenderKnownLanguageWithoutGrammarAsPlainText()
    {
        var highlighter = new TextMateSharpRazorDocsCodeHighlighter(
            new RazorDocsCodeLanguageCatalog(),
            NullLogger<TextMateSharpRazorDocsCodeHighlighter>.Instance,
            _ => null);

        var result = highlighter.Highlight(new RazorDocsCodeBlock("@page", "csharp"));

        Assert.False(result.IsHighlighted);
        Assert.Equal("csharp", result.NormalizedLanguage);
        Assert.Contains("doc-code--plain doc-code--language-csharp language-csharp", result.Html);
        Assert.Contains("@page", result.Html);
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
    public void Highlight_ShouldApplyLineLimitWithoutDoubleCountingCrLf()
    {
        var atLimit = string.Join("\r\n", Enumerable.Repeat("x", TextMateSharpRazorDocsCodeHighlighter.MaxHighlightedCodeBlockLines));
        var atLimitResult = _highlighter.Highlight(new RazorDocsCodeBlock(atLimit, "csharp"));

        Assert.True(atLimitResult.IsHighlighted);
        Assert.Equal("csharp", atLimitResult.NormalizedLanguage);
        Assert.Contains("x\nx", atLimitResult.Html);

        var overLimit = string.Join("\r\n", Enumerable.Repeat("x", TextMateSharpRazorDocsCodeHighlighter.MaxHighlightedCodeBlockLines + 1));
        var overLimitResult = _highlighter.Highlight(new RazorDocsCodeBlock(overLimit, "csharp"));

        Assert.False(overLimitResult.IsHighlighted);
        Assert.Equal("csharp", overLimitResult.NormalizedLanguage);
        Assert.Contains("x\nx", overLimitResult.Html);
    }

    [Fact]
    public void Highlight_ShouldReuseCachedGrammarAcrossRepeatedBlocks()
    {
        _highlighter.Highlight(new RazorDocsCodeBlock("public class One { }", "csharp"));
        _highlighter.Highlight(new RazorDocsCodeBlock("public class Two { }", "cs"));

        Assert.Equal(1, _highlighter.CachedGrammarCount);
    }

    [Fact]
    public void LoadGrammar_ShouldReturnNull_WhenTextMateLanguageIdIsMissing()
    {
        var language = new RazorDocsCodeLanguage(
            "custom",
            "custom",
            "Custom",
            TextMateLanguageId: null,
            IsKnown: true,
            IsPlainText: false);

        Assert.Null(_highlighter.LoadGrammar(language));
    }

    [Fact]
    public void LoadGrammar_ShouldReturnNull_WhenTextMateScopeIsUnknown()
    {
        var language = new RazorDocsCodeLanguage(
            "custom",
            "custom",
            "Custom",
            "not-a-textmate-language",
            IsKnown: true,
            IsPlainText: false);

        Assert.Null(_highlighter.LoadGrammar(language));
    }

    [Fact]
    public void AppendTokens_ShouldEscapeUntokenizedLines()
    {
        var builder = new StringBuilder();

        TextMateSharpRazorDocsCodeHighlighter.AppendTokens(builder, "<empty>", []);

        Assert.Equal("&lt;empty&gt;", builder.ToString());
    }

    [Fact]
    public void AppendTokens_ShouldPreserveGapsAndTrailingText()
    {
        var builder = new StringBuilder();
        IToken[] tokens =
        [
            new TestToken(2, 5, ["string.quoted"]),
            new TestToken(7, 9, ["source.operator"])
        ];

        TextMateSharpRazorDocsCodeHighlighter.AppendTokens(builder, "ab<cde>fg&", tokens);

        Assert.Equal(
            "ab<span class=\"doc-token doc-token--string\">&lt;cd</span>e&gt;<span class=\"doc-token doc-token--operator\">fg</span>&amp;",
            builder.ToString());
    }

    [Theory]
    [InlineData("markup.deleted.diff", "deleted")]
    [InlineData("markup.inserted.diff", "inserted")]
    [InlineData("keyword.operator.assignment", "keyword")]
    [InlineData("source.unknown", null)]
    public void ResolveTokenClass_ShouldMapDiffAndFallbackScopes(string scope, string? expected)
    {
        Assert.Equal(expected, TextMateSharpRazorDocsCodeHighlighter.ResolveTokenClass([scope]));
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

    private sealed class TestToken : IToken
    {
        public TestToken(int startIndex, int endIndex, IEnumerable<string> scopes)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
            Scopes = scopes.ToList();
        }

        public int StartIndex { get; set; }

        public int EndIndex { get; }

        public int Length => EndIndex - StartIndex;

        public List<string> Scopes { get; }
    }
}
