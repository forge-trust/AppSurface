using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NorthstarBrochureStarter.Tests;

public sealed class NorthstarBrochureStarterQaRegressionTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public NorthstarBrochureStarterQaRegressionTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/journal/field-guide")]
    // Regression: ISSUE-001 — Turbo was emitted in the document body and warned on page navigation.
    // Found by /qa on 2026-08-13
    // Report: .gstack/qa-reports/qa-report-127-0-0-1-5233-2026-08-13.md
    public async Task PageNavigationRuntime_RendersInDocumentHead(string path)
    {
        var html = await _client.GetStringAsync(path);
        var head = ExtractElement(html, "head");
        var body = ExtractElement(html, "body");

        Assert.Contains("turbo.es2017-umd.js", head, StringComparison.Ordinal);
        Assert.Contains("page-navigation.js", head, StringComparison.Ordinal);
        Assert.DoesNotContain("turbo.es2017-umd.js", body, StringComparison.Ordinal);
        Assert.DoesNotContain("page-navigation.js", body, StringComparison.Ordinal);
    }

    [Fact]
    // Regression: ISSUE-002 — GET form submission discarded the demo marker from its action URL.
    // Found by /qa on 2026-08-13
    // Report: .gstack/qa-reports/qa-report-127-0-0-1-5233-2026-08-13.md
    public async Task ContactPreviewSubmit_ProvidesTheDemoQueryMarker()
    {
        var html = await _client.GetStringAsync("/contact");
        var form = ExtractElement(html, "form");

        Assert.Contains("action=\"/thank-you.html?demo=1\"", form, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\" name=\"demo\" value=\"1\"", form, StringComparison.Ordinal);
    }

    private static string ExtractElement(string html, string elementName)
    {
        var match = Regex.Match(
            html,
            $@"<{elementName}\b.*?</{elementName}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(match.Success, $"Expected a {elementName} element in the rendered document.");
        return match.Value;
    }
}
