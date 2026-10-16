using System.Text.Json;
using AngleSharp.Html.Parser;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RecommendationDocumentationTests
{
    private const string PackageReadme = "Web/ForgeTrust.AppSurface.Docs/README.md";
    private const string ConsumerGuide = "Web/ForgeTrust.AppSurface.Docs/use-appsurface-docs.md";
    private const string RecommendationHeading = "### Recommended next reads";
    private const string RecommendationEndHeading = "## Metadata-Driven Page Type Display";

    [Fact]
    public async Task DocumentedJsonSnapshot_ShouldMatchTheControlledSearchIndexResult()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var readme = File.ReadAllText(TestPathUtils.PathUnder(repositoryRoot, PackageReadme));
        var documentedJson = ReadCodeFenceBetweenHeadings(
            readme,
            "#### Consumer JSON snapshot example",
            "#### Older and future payloads",
            "json");

        using var fixture = new RecommendationFixture();
        fixture.Set(
        [
            Page("Source", "source.md", "guide", new DocMetadata
            {
                RelatedPages = ["hidden.md", "Missing", "source.md"]
            }),
            Page("Hidden", "hidden.md", "api-reference", new DocMetadata
            {
                HideFromSearch = true,
                Summary = "Author exception"
            }),
            Page("Example", "example.md", "example"),
            Page("Trouble", "trouble.md", "troubleshooting")
        ]);

        using var expected = JsonDocument.Parse(documentedJson);
        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        var source = Assert.Single(payload.Documents, document => document.Id == "source");
        var actual = JsonSerializer.SerializeToElement(
            new
            {
                relatedPages = source.RelatedPages,
                recommendedPages = source.RecommendedPages
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        AssertJsonEquivalent(expected.RootElement, actual);
        Assert.Equal(3, actual.GetProperty("recommendedPages").GetArrayLength());
        Assert.Equal(
            ["hidden.md", "Missing", "source.md"],
            actual.GetProperty("relatedPages").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(payload.Documents, document => document.Id == "hidden");
        Assert.Equal("ForgeTrust.RazorWire", source.Component);
        A.CallTo(() => fixture.Harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void ConsumerExamples_ShouldPreserveAccessibleLinksAndFuturePayloadCompatibility()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var readme = File.ReadAllText(TestPathUtils.PathUnder(repositoryRoot, PackageReadme));
        var recommendationSection = ReadSectionBetweenHeadings(readme, RecommendationHeading, RecommendationEndHeading);
        var razor = ReadCodeFenceBetweenHeadings(
            readme,
            "#### Render a compatible read-next link",
            "#### Consumer JSON snapshot example",
            "razor");
        var parsedRazor = new HtmlParser().ParseDocument(razor);
        var link = Assert.Single(parsedRazor.QuerySelectorAll("a"));
        var title = Assert.Single(parsedRazor.QuerySelectorAll("[id]"));
        var caption = Assert.Single(parsedRazor.QuerySelectorAll(".read-next-reason"));

        Assert.Contains("var titleId = $\"read-next-title-{rowIndex++}\";", razor, StringComparison.Ordinal);
        Assert.Contains("var href = Url.Content(\"~\" + row.Page.Href);", razor, StringComparison.Ordinal);
        Assert.Equal("@href", link.GetAttribute("href"));
        Assert.Equal("@titleId", link.GetAttribute("aria-labelledby"));
        Assert.Equal(link.GetAttribute("aria-labelledby"), title.GetAttribute("id"));
        Assert.Equal("a", title.ParentElement?.LocalName);
        Assert.Equal("@row.Page.Title", title.TextContent.Trim());
        Assert.Empty(link.QuerySelectorAll(".read-next-reason"));
        Assert.Equal("li", caption.ParentElement?.LocalName);
        Assert.Equal("@row.ReasonText", caption.TextContent.Trim());

        Assert.Contains(
            "| `try-example` | Try an example for this component | Try an example for this topic |",
            recommendationSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Custom views may use the provided `ReasonText` unchanged or localize from the primary and supporting codes",
            recommendationSection,
            StringComparison.Ordinal);
        Assert.Contains("Unknown valid source or reason values keep a generic link and caption", recommendationSection,
            StringComparison.Ordinal);

        var oldIndexJson = ReadCodeFenceBetweenHeadings(
            readme,
            "##### An older index without recommendations",
            "##### A future recommendation row",
            "json");
        using var oldIndex = JsonDocument.Parse(oldIndexJson);
        Assert.False(oldIndex.RootElement.GetProperty("documents")[0].TryGetProperty("recommendedPages", out _));

        var futureRowJson = ReadCodeFenceBetweenHeadings(
            readme,
            "##### A future recommendation row",
            "#### Diagnostics, recovery, and release compatibility",
            "json");
        using var futureRow = JsonDocument.Parse(futureRowJson);
        Assert.Equal("/docs/next.html", futureRow.RootElement.GetProperty("path").GetString());
        Assert.Equal("Next page", futureRow.RootElement.GetProperty("title").GetString());
        Assert.Equal("future-source", futureRow.RootElement.GetProperty("source").GetString());
        Assert.Equal("future-action", futureRow.RootElement.GetProperty("primaryReasonCode").GetString());
        Assert.Equal("future-evidence", futureRow.RootElement.GetProperty("reasonCodes")[1].GetString());
        Assert.Equal("Read this page next", futureRow.RootElement.GetProperty("reasonText").GetString());

        var diagnostics = ReadSectionBetweenHeadings(
            recommendationSection,
            "#### Diagnostics, recovery, and release compatibility",
            "");
        Assert.Contains("RWEXPORT011", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickstartMetadataAndSourceLinks_ShouldUseRealParserFixturesAndPublishableLinks()
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var guidePath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Web",
            "ForgeTrust.RazorWire",
            "Docs",
            "stream-authorization.md");
        var examplePagePath = TestPathUtils.PathUnder(repositoryRoot, "examples", "razorwire-mvc", "README.md");
        var exampleSidecarPath = TestPathUtils.PathUnder(repositoryRoot, "examples", "razorwire-mvc", "README.md.yml");
        var antiForgeryPagePath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Web",
            "ForgeTrust.RazorWire",
            "Docs",
            "antiforgery.md");
        var antiForgerySidecarPath = TestPathUtils.PathUnder(
            repositoryRoot,
            "Web",
            "ForgeTrust.RazorWire",
            "Docs",
            "antiforgery.md.yml");

        var (guideBody, guideResult) = MarkdownFrontMatterParser.ExtractWithDiagnostics(File.ReadAllText(guidePath));
        var guide = Assert.IsType<DocMetadata>(guideResult.Metadata);
        Assert.Empty(guideResult.Diagnostics);
        Assert.StartsWith("# RazorWire Stream Authorization", guideBody.TrimStart(), StringComparison.Ordinal);
        Assert.Equal("RazorWire Stream Authorization", guide.Title);
        Assert.Equal("guide", guide.PageType);
        Assert.Equal("ForgeTrust.RazorWire", guide.Component);
        Assert.Equal(["Web/ForgeTrust.RazorWire/Docs/antiforgery.md"], guide.RelatedPages);

        Assert.True(File.Exists(examplePagePath));
        var exampleResult = MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics(File.ReadAllText(exampleSidecarPath));
        var example = Assert.IsType<DocMetadata>(exampleResult.Metadata);
        Assert.Empty(exampleResult.Diagnostics);
        Assert.Equal("RazorWire MVC Example", example.Title);
        Assert.Equal("example", example.PageType);
        Assert.Equal("ForgeTrust.RazorWire", example.Component);
        Assert.Equal("razorwire-proof", example.SequenceKey);
        Assert.Equal(20, example.Order);
        Assert.Equal(
            [
                "Web/ForgeTrust.RazorWire/README.md",
                "Web/ForgeTrust.RazorWire/Docs/page-navigation.md",
                "Web/ForgeTrust.RazorWire/Docs/form-failures.md",
                "Web/ForgeTrust.RazorWire/Docs/form-loading.md",
                "Web/ForgeTrust.RazorWire/Docs/antiforgery.md",
                "Web/ForgeTrust.RazorWire/Docs/dialog-responses.md"
            ],
            example.RelatedPages);

        Assert.True(File.Exists(antiForgeryPagePath));
        var antiForgeryResult = MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics(File.ReadAllText(antiForgerySidecarPath));
        var antiForgery = Assert.IsType<DocMetadata>(antiForgeryResult.Metadata);
        Assert.Empty(antiForgeryResult.Diagnostics);
        Assert.Equal("Security & Anti-Forgery", antiForgery.Title);
        Assert.Equal("troubleshooting", antiForgery.PageType);
        Assert.Equal("ForgeTrust.RazorWire", antiForgery.Component);
        Assert.Equal("razorwire-proof", antiForgery.SequenceKey);
        Assert.Equal(30, antiForgery.Order);

        var consumerGuideMarkdown = File.ReadAllText(TestPathUtils.PathUnder(repositoryRoot, ConsumerGuide));
        var walkthrough = ReadSectionBetweenHeadings(
            consumerGuideMarkdown,
            "### Build a three-page next-read example",
            "### Fix a poorly ranked page");
        Assert.Contains(
            "[`Web/ForgeTrust.RazorWire/Docs/stream-authorization.md`](../ForgeTrust.RazorWire/Docs/stream-authorization.md)",
            walkthrough,
            StringComparison.Ordinal);
        Assert.Contains(
            "](../../examples/razorwire-mvc/README.md)",
            walkthrough,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/forge-trust/AppSurface/blob/main/examples/razorwire-mvc/README.md.yml",
            walkthrough,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.RazorWire/Docs/antiforgery.md.yml",
            walkthrough,
            StringComparison.Ordinal);
        Assert.DoesNotContain("](../../examples/razorwire-mvc/README.md.yml)", walkthrough, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "](../ForgeTrust.RazorWire/Docs/antiforgery.md.yml)",
            walkthrough,
            StringComparison.Ordinal);
        Assert.Contains("RWEXPORT011", walkthrough, StringComparison.Ordinal);
    }

    private static string ReadCodeFenceBetweenHeadings(
        string markdown,
        string startHeading,
        string endHeading,
        string language)
    {
        var section = ReadSectionBetweenHeadings(markdown, startHeading, endHeading);
        var marker = $"```{language}";
        var opening = section.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(opening >= 0, $"Expected one {language} code fence between {startHeading} and {endHeading}.");
        Assert.Equal(opening, section.LastIndexOf(marker, StringComparison.Ordinal));

        var contentStart = opening + marker.Length;
        if (contentStart < section.Length && section[contentStart] == '\r')
        {
            contentStart++;
        }

        if (contentStart < section.Length && section[contentStart] == '\n')
        {
            contentStart++;
        }

        var closing = section.IndexOf("```", contentStart, StringComparison.Ordinal);
        Assert.True(closing >= 0, $"Expected a closing code fence for {language} between {startHeading} and {endHeading}.");
        return section[contentStart..closing].TrimEnd('\r', '\n');
    }

    private static string ReadSectionBetweenHeadings(string markdown, string startHeading, string endHeading)
    {
        var start = markdown.IndexOf(startHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected documentation heading: {startHeading}");
        Assert.Equal(start, markdown.LastIndexOf(startHeading, StringComparison.Ordinal));

        var contentStart = start + startHeading.Length;
        var end = endHeading.Length == 0
            ? markdown.Length
            : markdown.IndexOf(endHeading, contentStart, StringComparison.Ordinal);
        Assert.True(end >= contentStart, $"Expected documentation heading: {endHeading}");
        if (endHeading.Length > 0)
        {
            Assert.Equal(end, markdown.LastIndexOf(endHeading, StringComparison.Ordinal));
        }

        return markdown[contentStart..end];
    }

    private static void AssertJsonEquivalent(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        if (expected.ValueKind == JsonValueKind.Object)
        {
            var expectedProperties = expected.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            var actualProperties = actual.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            Assert.Equal(
                expectedProperties.Keys.OrderBy(name => name, StringComparer.Ordinal),
                actualProperties.Keys.OrderBy(name => name, StringComparer.Ordinal));
            foreach (var (name, expectedValue) in expectedProperties)
            {
                Assert.True(actualProperties.TryGetValue(name, out var actualValue), $"Missing JSON property: {name}");
                AssertJsonEquivalent(expectedValue, actualValue);
            }

            return;
        }

        if (expected.ValueKind == JsonValueKind.Array)
        {
            var expectedItems = expected.EnumerateArray().ToArray();
            var actualItems = actual.EnumerateArray().ToArray();
            Assert.Equal(expectedItems.Length, actualItems.Length);
            for (var index = 0; index < expectedItems.Length; index++)
            {
                AssertJsonEquivalent(expectedItems[index], actualItems[index]);
            }

            return;
        }

        if (expected.ValueKind == JsonValueKind.String)
        {
            Assert.Equal(expected.GetString(), actual.GetString());
            return;
        }

        if (expected.ValueKind == JsonValueKind.Number)
        {
            Assert.Equal(expected.GetDecimal(), actual.GetDecimal());
            return;
        }

        Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }

    private static DocNode Page(string title, string path, string role, DocMetadata? metadata = null) =>
        new(title, path, "<p>Readable</p>", Metadata: (metadata ?? new()) with
        {
            Title = title,
            PageType = role,
            Component = "ForgeTrust.RazorWire"
        });

    private sealed class RecommendationFixture : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly Memo _memo;

        public IDocHarvester Harvester { get; } = A.Fake<IDocHarvester>();
        public DocAggregator Aggregator { get; }

        public RecommendationFixture()
        {
            _memo = new Memo(_cache);
            var environment = A.Fake<IWebHostEnvironment>();
            A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
            Aggregator = new DocAggregator(
                [Harvester],
                new AppSurfaceDocsOptions(),
                environment,
                _memo,
                new AppSurfaceDocsHtmlSanitizer(),
                NullLogger<DocAggregator>.Instance);
        }

        public void Set(IReadOnlyList<DocNode> pages) =>
            A.CallTo(() => Harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(pages);

        public void Dispose()
        {
            _memo.Dispose();
            _cache.Dispose();
        }
    }
}
