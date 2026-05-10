using FakeItEasy;
using ForgeTrust.Runnable.Caching;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public sealed class RepositoryRunnableEvaluatorDocsTests
{
    private static readonly string[] EvaluatorSequence =
    [
        "start-here/runnable-evaluator.md",
        "start-here/should-i-use-runnable.md",
        "start-here/first-success-path.md",
        "guides/from-program-cs-to-module.md"
    ];

    private static readonly string[] AuthoredPages =
    [
        "start-here/runnable-evaluator.md",
        "start-here/should-i-use-runnable.md",
        "start-here/first-success-path.md",
        "guides/from-program-cs-to-module.md",
        "troubleshooting/startup-and-modules.md",
        "concepts/glossary.md"
    ];

    [Fact]
    public async Task EvaluatorDocs_ShouldBeHarvestedWithMetadata_AndFeaturedFromRootLanding()
    {
        var docs = await HarvestRepositoryDocsAsync();
        var rootLanding = SingleDoc(docs, "README.md");

        foreach (var path in AuthoredPages)
        {
            var doc = SingleDoc(docs, path);

            Assert.NotNull(doc.Metadata);
            Assert.False(string.IsNullOrWhiteSpace(doc.Metadata.Title));
            Assert.False(string.IsNullOrWhiteSpace(doc.Metadata.Summary));
            Assert.False(string.IsNullOrWhiteSpace(doc.Metadata.PageType));
            Assert.False(string.IsNullOrWhiteSpace(doc.Metadata.NavGroup));
            Assert.NotEmpty(doc.Metadata.Aliases ?? []);
            Assert.NotEmpty(doc.Metadata.Keywords ?? []);
            Assert.NotNull(doc.Metadata.Order);
        }

        AssertEvaluatorSequenceMetadata(docs);

        var groups = rootLanding.Metadata?.FeaturedPageGroups ?? [];
        AssertIntentOrder(groups, "evaluate-runnable", "prove-first-service", "recover-and-read");
        Assert.Contains(groups, group => string.Equals(group.Intent, "choose-package", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, group => string.Equals(group.Intent, "evaluate-release", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, group => string.Equals(group.Intent, "build-docs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, group => string.Equals(group.Intent, "see-it-working", StringComparison.OrdinalIgnoreCase));

        AssertFeaturedPage(groups, "evaluate-runnable", "start-here/should-i-use-runnable.md");
        AssertFeaturedPage(groups, "evaluate-runnable", "start-here/runnable-evaluator.md");
        AssertFeaturedPage(groups, "prove-first-service", "start-here/first-success-path.md");
        AssertFeaturedPage(groups, "prove-first-service", "guides/from-program-cs-to-module.md");
        AssertFeaturedPage(groups, "recover-and-read", "troubleshooting/startup-and-modules.md");
        AssertFeaturedPage(groups, "recover-and-read", "concepts/glossary.md");
    }

    [Fact]
    public async Task EvaluatorDocs_ShouldResolveSequenceRelatedPages_AndSearchMetadata()
    {
        var docs = await HarvestRepositoryDocsAsync();
        var aggregator = CreateAggregator(docs);

        await AssertStartHereLandingAndOrderAsync(aggregator);
        await AssertSequenceAsync(aggregator);
        await AssertRelatedPagesAsync(aggregator);

        var searchIndex = await aggregator.GetSearchIndexPayloadAsync();

        AssertSearchMetadata(
            searchIndex,
            "start-here/should-i-use-runnable.md",
            aliases: ["plain ASP.NET Core"],
            keywords: []);
        AssertSearchMetadata(
            searchIndex,
            "guides/from-program-cs-to-module.md",
            aliases: ["Program.cs", "status pages"],
            keywords: ["WebOptions.Errors"]);
        AssertSearchMetadata(
            searchIndex,
            "start-here/runnable-evaluator.md",
            aliases: ["startup standardization"],
            keywords: []);
        AssertSearchMetadata(
            searchIndex,
            "troubleshooting/startup-and-modules.md",
            aliases: ["module did not run"],
            keywords: []);
    }

    private static async Task<IReadOnlyList<DocNode>> HarvestRepositoryDocsAsync()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var harvester = new MarkdownHarvester(A.Fake<ILogger<MarkdownHarvester>>());

        return (await harvester.HarvestAsync(repoRoot)).ToList();
    }

    private static DocAggregator CreateAggregator(IReadOnlyList<DocNode> docs)
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var harvester = A.Fake<IDocHarvester>();
        var environment = A.Fake<IWebHostEnvironment>();
        var sanitizer = A.Fake<IRazorDocsHtmlSanitizer>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var memo = new Memo(cache);

        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(docs);
        A.CallTo(() => environment.ContentRootPath).Returns(repoRoot);
        A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string input) => input);

        return new DocAggregator(
            [harvester],
            new RazorDocsOptions
            {
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = repoRoot
                }
            },
            environment,
            memo,
            sanitizer,
            A.Fake<ILogger<DocAggregator>>());
    }

    private static DocNode SingleDoc(IReadOnlyList<DocNode> docs, string path)
    {
        return Assert.Single(docs, doc => string.Equals(doc.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertEvaluatorSequenceMetadata(IReadOnlyList<DocNode> docs)
    {
        for (var index = 0; index < EvaluatorSequence.Length; index++)
        {
            var doc = SingleDoc(docs, EvaluatorSequence[index]);

            Assert.Equal("runnable-evaluator", doc.Metadata?.SequenceKey);
            Assert.Equal((index + 1) * 10, doc.Metadata?.Order);
        }

        Assert.Null(SingleDoc(docs, "troubleshooting/startup-and-modules.md").Metadata?.SequenceKey);
        Assert.Null(SingleDoc(docs, "concepts/glossary.md").Metadata?.SequenceKey);
    }

    private static async Task AssertStartHereLandingAndOrderAsync(DocAggregator aggregator)
    {
        var sections = await aggregator.GetPublicSectionsAsync();
        var startHere = Assert.Single(
            sections,
            section => section.Section == DocPublicSection.StartHere);

        Assert.Equal("start-here/runnable-evaluator.md", startHere.LandingDoc?.Path);
        Assert.Equal(
            [
                "start-here/runnable-evaluator.md",
                "start-here/should-i-use-runnable.md",
                "start-here/first-success-path.md",
                "packages/README.md",
                "Web/ForgeTrust.Runnable.Web.RazorDocs/use-razordocs.md"
            ],
            startHere.VisiblePages
                .Where(doc =>
                    doc.Path.StartsWith("start-here/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(doc.Path, "packages/README.md", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        doc.Path,
                        "Web/ForgeTrust.Runnable.Web.RazorDocs/use-razordocs.md",
                        StringComparison.OrdinalIgnoreCase))
                .Select(doc => doc.Path)
                .ToArray());
    }

    private static void AssertIntentOrder(
        IReadOnlyList<DocFeaturedPageGroupDefinition> groups,
        params string[] expectedLeadingIntents)
    {
        var actualLeadingIntents = groups
            .Take(expectedLeadingIntents.Length)
            .Select(group => group.Intent)
            .ToArray();

        Assert.Equal(expectedLeadingIntents, actualLeadingIntents);
    }

    private static void AssertFeaturedPage(
        IReadOnlyList<DocFeaturedPageGroupDefinition> groups,
        string intent,
        string path)
    {
        var group = Assert.Single(
            groups,
            item => string.Equals(item.Intent, intent, StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            group.Pages ?? [],
            page => string.Equals(page.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertSequenceAsync(DocAggregator aggregator)
    {
        var evaluator = await aggregator.GetDocDetailsAsync("start-here/runnable-evaluator.md");
        Assert.NotNull(evaluator);
        Assert.Null(evaluator!.PreviousPage);
        Assert.Equal("/docs/start-here/should-i-use-runnable.md.html", evaluator.NextPage?.Href);

        var decision = await aggregator.GetDocDetailsAsync("start-here/should-i-use-runnable.md");
        Assert.NotNull(decision);
        Assert.Equal("/docs/start-here/runnable-evaluator.md.html", decision!.PreviousPage?.Href);
        Assert.Equal("/docs/start-here/first-success-path.md.html", decision.NextPage?.Href);

        var firstSuccess = await aggregator.GetDocDetailsAsync("start-here/first-success-path.md");
        Assert.NotNull(firstSuccess);
        Assert.Equal("/docs/start-here/should-i-use-runnable.md.html", firstSuccess!.PreviousPage?.Href);
        Assert.Equal("/docs/guides/from-program-cs-to-module.md.html", firstSuccess.NextPage?.Href);

        var proof = await aggregator.GetDocDetailsAsync("guides/from-program-cs-to-module.md");
        Assert.NotNull(proof);
        Assert.Equal("/docs/start-here/first-success-path.md.html", proof!.PreviousPage?.Href);
        Assert.Null(proof.NextPage);
    }

    private static async Task AssertRelatedPagesAsync(DocAggregator aggregator)
    {
        var proof = await aggregator.GetDocDetailsAsync("guides/from-program-cs-to-module.md");
        Assert.NotNull(proof);
        AssertRelatedHref(proof!, "/docs/Web/ForgeTrust.Runnable.Web/README.md.html");
        AssertRelatedHref(proof!, "/docs/ForgeTrust.Runnable.Core/README.md.html");

        var troubleshooting = await aggregator.GetDocDetailsAsync("troubleshooting/startup-and-modules.md");
        Assert.NotNull(troubleshooting);
        AssertRelatedHref(troubleshooting!, "/docs/examples/config-validation/README.md.html");
        AssertRelatedHref(troubleshooting!, "/docs/ForgeTrust.Runnable.Core/README.md.html");
        AssertRelatedHref(troubleshooting!, "/docs/packages/README.md.html");

        var glossary = await aggregator.GetDocDetailsAsync("concepts/glossary.md");
        Assert.NotNull(glossary);
        AssertRelatedHref(glossary!, "/docs/ForgeTrust.Runnable.Core/README.md.html");
        AssertRelatedHref(glossary!, "/docs/Web/ForgeTrust.Runnable.Web/README.md.html");
    }

    private static void AssertRelatedHref(DocDetailsViewModel details, string expectedHref)
    {
        Assert.Contains(
            details.RelatedPages,
            page => string.Equals(page.Href, expectedHref, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSearchMetadata(
        DocsSearchIndexPayload payload,
        string sourcePath,
        IReadOnlyList<string> aliases,
        IReadOnlyList<string> keywords)
    {
        var document = Assert.Single(
            payload.Documents,
            doc => doc.Path.EndsWith(sourcePath, StringComparison.OrdinalIgnoreCase));

        foreach (var alias in aliases)
        {
            Assert.Contains(document.Aliases, item => string.Equals(item, alias, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var keyword in keywords)
        {
            Assert.Contains(document.Keywords, item => string.Equals(item, keyword, StringComparison.OrdinalIgnoreCase));
        }
    }
}
