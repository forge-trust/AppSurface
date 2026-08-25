using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public class AppSurfaceDocsHtmlSanitizerTests
{
    [Theory]
    [InlineData("text/html", "title")]
    [InlineData("text/html", "style")]
    [InlineData("application/xhtml+xml", "title")]
    [InlineData("application/xhtml+xml", "style")]
    public void Sanitize_ShouldRemoveAnnotationXmlAttributeBreakoutPayload(string encoding, string rcdataElement)
    {
        var sanitizer = new AppSurfaceDocsHtmlSanitizer();
        var html = $"<math><annotation-xml encoding=\"{encoding}\"><{rcdataElement}><a encoding=\"</{rcdataElement}><img src=x onerror=alert()>\"></annotation-xml></math>";

        var sanitized = sanitizer.Sanitize(html);

        Assert.Equal(string.Empty, sanitized);
    }

    [Fact]
    public void Sanitize_ShouldPreserveCodeHighlighterClassOnlyMarkup()
    {
        var sanitizer = new AppSurfaceDocsHtmlSanitizer();
        var html = """
            <pre class="doc-code doc-code--highlighted doc-code--language-csharp language-csharp" data-doc-code-language="C#">
              <code class="language-csharp">
                <span class="doc-token doc-token--keyword">public</span>
              </code>
            </pre>
            """;

        var sanitized = sanitizer.Sanitize(html);

        Assert.Contains("<pre class=\"doc-code doc-code--highlighted doc-code--language-csharp language-csharp\" data-doc-code-language=\"C#\">", sanitized);
        Assert.Contains("<code class=\"language-csharp\">", sanitized);
        Assert.Contains("<span class=\"doc-token doc-token--keyword\">public</span>", sanitized);
        Assert.DoesNotContain("doc-code__language", sanitized);
    }

    [Fact]
    public void Sanitize_ShouldRejectStyleDataAndEventAttributesOnHighlighterMarkup()
    {
        var sanitizer = new AppSurfaceDocsHtmlSanitizer();
        var html = """
            <pre class="doc-code" style="color:red" data-doc-code-language="C#" data-language="csharp" onclick="alert(1)">
              <code style="color:blue" data-x="1">
                <span class="doc-token" style="color:green" data-token="keyword" onmouseover="alert(1)">public</span>
              </code>
            </pre>
            """;

        var sanitized = sanitizer.Sanitize(html);

        Assert.Contains("class=\"doc-code\"", sanitized);
        Assert.Contains("class=\"doc-token\"", sanitized);
        Assert.Contains("data-doc-code-language=\"C#\"", sanitized);
        Assert.DoesNotContain("style=", sanitized);
        Assert.DoesNotContain("data-language", sanitized);
        Assert.DoesNotContain("data-token", sanitized);
        Assert.DoesNotContain("onclick", sanitized);
        Assert.DoesNotContain("onmouseover", sanitized);
    }

    [Fact]
    public void Sanitize_ShouldPreservePackageOwnedRichAuthoringStructureAndRejectUnknownDataAttributes()
    {
        var sanitizer = new AppSurfaceDocsHtmlSanitizer();
        var html = """
            <section class="docs-rich-tabs" data-appsurfacedocs-rich="tabs" data-appsurfacedocs-rich-tabs="true" data-appsurfacedocs-rich-tabs-token="token" onclick="alert(1)">
              <p class="docs-rich-tabs__baseline" data-appsurfacedocs-rich-tabs-baseline="true">All paths are available below.</p>
              <section class="docs-rich-tabs__panel" data-appsurfacedocs-rich-tab-panel="true" data-appsurfacedocs-rich-tab-label="Local proof" data-untrusted="nope" style="display:none">Body</section>
            </section>
            """;

        var sanitized = sanitizer.Sanitize(html);

        Assert.Contains("data-appsurfacedocs-rich=\"tabs\"", sanitized);
        Assert.Contains("data-appsurfacedocs-rich-tabs=\"true\"", sanitized);
        Assert.Contains("data-appsurfacedocs-rich-tabs-token=\"token\"", sanitized);
        Assert.Contains("data-appsurfacedocs-rich-tabs-baseline=\"true\"", sanitized);
        Assert.Contains("data-appsurfacedocs-rich-tab-panel=\"true\"", sanitized);
        Assert.Contains("data-appsurfacedocs-rich-tab-label=\"Local proof\"", sanitized);
        Assert.DoesNotContain("data-untrusted", sanitized);
        Assert.DoesNotContain("onclick", sanitized);
        Assert.DoesNotContain("style=", sanitized);
    }
}
