using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class RazorDocsHtmlSanitizerTests
{
    [Fact]
    public void Sanitize_ShouldPreserveCodeHighlighterClassOnlyMarkup()
    {
        var sanitizer = new RazorDocsHtmlSanitizer();
        var html = """
            <pre class="doc-code doc-code--highlighted doc-code--language-csharp language-csharp">
              <span class="doc-code__language">C#</span>
              <code class="language-csharp">
                <span class="doc-token doc-token--keyword">public</span>
              </code>
            </pre>
            """;

        var sanitized = sanitizer.Sanitize(html);

        Assert.Contains("<pre class=\"doc-code doc-code--highlighted doc-code--language-csharp language-csharp\">", sanitized);
        Assert.Contains("<code class=\"language-csharp\">", sanitized);
        Assert.Contains("<span class=\"doc-code__language\">C#</span>", sanitized);
        Assert.Contains("<span class=\"doc-token doc-token--keyword\">public</span>", sanitized);
    }

    [Fact]
    public void Sanitize_ShouldRejectStyleDataAndEventAttributesOnHighlighterMarkup()
    {
        var sanitizer = new RazorDocsHtmlSanitizer();
        var html = """
            <pre class="doc-code" style="color:red" data-language="csharp" onclick="alert(1)">
              <code style="color:blue" data-x="1">
                <span class="doc-token" style="color:green" data-token="keyword" onmouseover="alert(1)">public</span>
              </code>
            </pre>
            """;

        var sanitized = sanitizer.Sanitize(html);

        Assert.Contains("class=\"doc-code\"", sanitized);
        Assert.Contains("class=\"doc-token\"", sanitized);
        Assert.DoesNotContain("style=", sanitized);
        Assert.DoesNotContain("data-language", sanitized);
        Assert.DoesNotContain("data-token", sanitized);
        Assert.DoesNotContain("onclick", sanitized);
        Assert.DoesNotContain("onmouseover", sanitized);
    }
}
