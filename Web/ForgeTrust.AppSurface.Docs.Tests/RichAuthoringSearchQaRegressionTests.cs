using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>
/// Regression coverage for rich-authoring search projections found through browser QA.
/// </summary>
public sealed class RichAuthoringSearchQaRegressionTests
{
    /// <summary>
    /// Keeps search-result previews reader-facing when a valid rich directive supplied the derived Markdown summary.
    /// </summary>
    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldUseReaderFacingSnippet_ForRenderedRichAuthoringSummary()
    {
        // Regression: ISSUE-001 — search snippets exposed raw :::callout control syntax.
        // Found by /qa on 2026-08-18.
        // Report: .gstack/qa-reports/qa-report-127-0-0-1-2026-08-18.md
        var harvester = A.Fake<IDocHarvester>();
        var environment = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string html) => html);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(
        [
            new DocNode(
                "Rich authoring",
                "guides/rich-authoring.md",
                """
                <section class="docs-rich-callout" data-appsurfacedocs-rich="callout"><p class="docs-rich-callout__label">Note</p><div class="docs-rich-callout__body"><p>Reader-facing callout content.</p></div></section>
                <section class="docs-rich-tabs" data-appsurfacedocs-rich="tabs"><p>Choose a path.</p><section><h3>Preview</h3><p>Use a review deployment.</p></section></section>
                """,
                Metadata: new DocMetadata
                {
                    Summary = ":::callout note Reader-facing callout content. :::"
                })
        ]);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [harvester],
            new AppSurfaceDocsOptions(),
            environment,
            new Memo(cache),
            sanitizer,
            A.Fake<ILogger<DocAggregator>>());

        var payload = await aggregator.GetSearchIndexPayloadAsync();

        var indexedDocument = Assert.Single(payload.Documents);
        Assert.Equal(indexedDocument.Snippet, indexedDocument.Summary);
        Assert.Contains("Reader-facing callout content.", indexedDocument.Summary, StringComparison.Ordinal);
        Assert.Contains("Use a review deployment.", indexedDocument.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(":::", indexedDocument.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps the authored summary when rich-authoring source intentionally remains literal.
    /// </summary>
    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldKeepAuthoredSummary_ForLiteralRichAuthoringSource()
    {
        const string summary = ":::callout note Literal source remains searchable. :::";
        var harvester = A.Fake<IDocHarvester>();
        var environment = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
        A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string html) => html);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(
        [
            new DocNode(
                "Literal rich authoring",
                "guides/literal-rich-authoring.md",
                """
                <p class="docs-rich-source"><code>:::callout note</code></p><p>Literal source remains searchable.</p>
                """,
                Metadata: new DocMetadata { Summary = summary })
        ]);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [harvester],
            new AppSurfaceDocsOptions(),
            environment,
            new Memo(cache),
            sanitizer,
            A.Fake<ILogger<DocAggregator>>());

        var payload = await aggregator.GetSearchIndexPayloadAsync();

        var indexedDocument = Assert.Single(payload.Documents);
        Assert.Equal(summary, indexedDocument.Summary);
        Assert.NotEqual(indexedDocument.Snippet, indexedDocument.Summary);
    }
}
