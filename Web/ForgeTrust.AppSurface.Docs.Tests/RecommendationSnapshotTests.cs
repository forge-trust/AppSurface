using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Controllers;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RecommendationSnapshotTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthoredTitleLookup_ShouldPreserveVisibilityOrderPathAndRoutePrecedence(
        bool reverseHarvest,
        bool preferLaterPathByOrder)
    {
        using var fixture = new Fixture();
        var sources = new[] { "source-one", "source-two" }.Select(id => Page(id, id + ".md", "guide",
            new DocMetadata { AutoRelatedPages = false, RelatedPages = ["  DUPLICATE TITLE  ", "route-target.md", "Missing title"] }));
        var destinations = new[]
        {
            Page("Duplicate Title", "a-hidden.md", "example", new DocMetadata { HideFromPublicNav = true, Order = -10 }),
            Page("Duplicate Title", "b-empty.md", "example", new DocMetadata { Order = -9 }) with { Content = "" },
            Page("Duplicate Title", "c-first.md", "example", new DocMetadata { Order = 5, HideFromSearch = true }),
            Page("Duplicate Title", "d-later.md", "example", new DocMetadata { Order = preferLaterPathByOrder ? 4 : 5 }),
            Page("Route winner", "route-target.md", "example"),
            Page("route-target.md", "title-only.md", "example", new DocMetadata { Order = -20 }),
            new DocNode(null!, "null-title.md", "<p>Readable</p>", Metadata: new DocMetadata { AutoRelatedPages = false })
        };
        var docs = sources.Concat(destinations).ToArray();
        fixture.Set(reverseHarvest ? docs.Reverse().ToArray() : docs);
        var expected = preferLaterPathByOrder ? "d-later" : "c-first";

        foreach (var sourceId in new[] { "source-one", "source-two" })
        {
            var details = Assert.IsType<DocDetailsViewModel>(await fixture.Aggregator.GetDocDetailsAsync(sourceId));
            Assert.Equal(["/docs/" + expected, "/docs/route-target"], details.RelatedPages.Select(page => page.Href));
            Assert.Equal(["Duplicate Title", "Route winner"], details.RecommendedPages.Select(row => row.Page.Title));
            Assert.All(details.RecommendedPages, row => Assert.Equal(DocRecommendationSource.Authored, row.Source));
            var indexed = Assert.Single((await fixture.Aggregator.GetSearchIndexPayloadAsync()).Documents, doc => doc.Id == sourceId);
            Assert.Equal([expected, "route-target"], indexed.RecommendedPages.Select(row => row.TargetId));
        }
    }

    [Fact]
    public async Task AuthoredTitleLookup_ShouldRebuildWhenTheCachedGenerationChanges()
    {
        using var fixture = new Fixture();
        var source = Page("Source", "source.md", "guide",
            new DocMetadata { AutoRelatedPages = false, RelatedPages = ["Selected destination"] });
        fixture.Set([source, Page("Selected destination", "old.md", "example")]);
        var oldDetails = Assert.IsType<DocDetailsViewModel>(await fixture.Aggregator.GetDocDetailsAsync("source"));
        Assert.Equal("/docs/old", Assert.Single(oldDetails.RelatedPages).Href);

        fixture.Set([source, Page("Selected destination", "new.md", "example")]);
        fixture.Aggregator.InvalidateCache();
        var newDetails = Assert.IsType<DocDetailsViewModel>(await fixture.Aggregator.GetDocDetailsAsync("source"));
        Assert.Equal("/docs/new", Assert.Single(newDetails.RelatedPages).Href);
        Assert.Equal("new", Assert.Single((await fixture.Aggregator.GetSearchIndexPayloadAsync()).Documents,
            doc => doc.Id == "source").RecommendedPages[0].TargetId);
        Assert.Equal("/docs/old", Assert.Single(oldDetails.RelatedPages).Href);
    }

    [Fact]
    public async Task CompleteRows_ShouldPreserveAuthoredNonsearchAndRawReferences_BeforeAutomaticReads()
    {
        using var fixture = new Fixture();
        fixture.Set([
            Page("Source", "source.md", "guide", new DocMetadata { RelatedPages = ["hidden.md", "Missing", "source.md"] }),
            Page("Hidden", "hidden.md", "api-reference", new DocMetadata { HideFromSearch = true, Summary = "Author exception" }),
            Page("Example", "example.md", "example"),
            Page("Trouble", "trouble.md", "troubleshooting")]);
        var details = Assert.IsType<DocDetailsViewModel>(await fixture.Aggregator.GetDocDetailsAsync("source"));
        Assert.Equal(["Hidden"], details.RelatedPages.Select(page => page.Title));
        Assert.Equal(["Hidden", "Example", "Trouble"], details.RecommendedPages.Select(row => row.Page.Title));
        Assert.Equal([DocRecommendationSource.Authored, DocRecommendationSource.Automatic, DocRecommendationSource.Automatic],
            details.RecommendedPages.Select(row => row.Source));
        Assert.True(details.HasWayfinding);
        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        Assert.DoesNotContain(payload.Documents, doc => doc.Id == "hidden");
        var source = Assert.Single(payload.Documents, doc => doc.Id == "source");
        Assert.Equal(["hidden.md", "Missing", "source.md"], source.RelatedPages);
        Assert.Equal(["hidden", "example", "trouble"], source.RecommendedPages.Select(row => row.TargetId));
        var hidden = source.RecommendedPages[0];
        Assert.Equal("/docs/hidden", hidden.Path);
        Assert.Equal("Author exception", hidden.Summary);
        Assert.Equal("authored", hidden.Source);
        Assert.Equal(["authored"], hidden.ReasonCodes);
        var json = JsonSerializer.SerializeToElement(payload);
        var jsonSource = json.GetProperty("documents").EnumerateArray().Single(doc => doc.GetProperty("id").GetString() == "source");
        Assert.Equal("automatic", jsonSource.GetProperty("recommendedPages")[1].GetProperty("source").GetString());
    }

    [Fact]
    public async Task ReturnedRowsAndTwoMounts_ShouldNeverMutateAnotherCallerOrSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Set([Page("Source", "source.md", "guide"), Page("Example", "example.md", "example")]);
        var first = (await fixture.Aggregator.GetDocDetailsAsync("source"))!;
        var second = (await fixture.Aggregator.GetDocDetailsAsync("source"))!;
        Assert.NotSame(first.RecommendedPages, second.RecommendedPages);
        Assert.NotSame(first.RecommendedPages[0], second.RecommendedPages[0]);
        Assert.NotSame(first.RecommendedPages[0].Page, second.RecommendedPages[0].Page);
        ((string[])first.RecommendedPages[0].ReasonCodes)[0] = "mutated";
        ((DocPageRecommendationViewModel[])first.RecommendedPages)[0] = new();
        Assert.Equal("try-example", second.RecommendedPages[0].PrimaryReasonCode);
        Assert.Equal("try-example", second.RecommendedPages[0].ReasonCodes[0]);
        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        var a = DocsController.PrefixSearchIndexPathsForPathBase(payload, "/a");
        var b = DocsController.PrefixSearchIndexPathsForPathBase(payload, "/b/");
        var rowA = Assert.Single(a.Documents.Single(doc => doc.Id == "source").RecommendedPages);
        var rowB = Assert.Single(b.Documents.Single(doc => doc.Id == "source").RecommendedPages);
        Assert.Equal("/a/docs/example", rowA.Path);
        Assert.Equal("/b/docs/example", rowB.Path);
        Assert.Equal("example", rowA.TargetId);
        ((string[])rowA.ReasonCodes)[0] = "mutated";
        Assert.Equal("try-example", rowB.ReasonCodes[0]);
        var fresh = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        Assert.Equal("try-example", fresh.Documents.Single(doc => doc.Id == "source").RecommendedPages[0].ReasonCodes[0]);
        Assert.Equal("/docs/example", fresh.Documents.Single(doc => doc.Id == "source").RecommendedPages[0].Path);
        Assert.Equal(2, (await fixture.Aggregator.GetRecommendationMeasurementsAsync()).AutomaticResults);
        A.CallTo(() => fixture.Harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CallerCancellationAndLateObsoleteBuild_ShouldPublishOnlyCompleteCurrentGeneration()
    {
        using var fixture = new Fixture();
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRelease = new TaskCompletionSource<IReadOnlyList<DocNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        A.CallTo(() => fixture.Harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref count) == 1) { oldStarted.SetResult(); return oldRelease.Task; }
            return Task.FromResult<IReadOnlyList<DocNode>>([Page("Source", "source.md", "guide"), Page("New", "new.md", "example")]);
        });
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Aggregator.GetDocDetailsAsync("source", cancellation.Token);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldReader = fixture.Aggregator.GetDocDetailsAsync("source");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        fixture.Aggregator.InvalidateCache();
        var current = (await fixture.Aggregator.GetDocDetailsAsync("source"))!;
        Assert.Equal("New", Assert.Single(current.RecommendedPages).Page.Title);
        oldRelease.SetResult([Page("Source", "source.md", "guide"), Page("Old", "old.md", "example")]);
        Assert.Equal("Old", Assert.Single((await oldReader)!.RecommendedPages).Page.Title);
        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        Assert.Equal("new", Assert.Single(payload.Documents.Single(doc => doc.Id == "source").RecommendedPages).TargetId);
        Assert.DoesNotContain(payload.Documents, doc => doc.Id == "old");
        Assert.Equal("New", Assert.Single((await fixture.Aggregator.GetDocDetailsAsync("source"))!.RecommendedPages).Page.Title);
    }

    [Fact]
    public async Task OptOutAndEmptyCorpus_ShouldEmitEmptyArraysAndLeaveLegacyModelCompatibility()
    {
        using var fixture = new Fixture();
        fixture.Set([Page("Source", "source.md", "guide", new DocMetadata { AutoRelatedPages = false, RelatedPages = [] }),
            Page("Example", "example.md", "example")]);
        var source = (await fixture.Aggregator.GetDocDetailsAsync("source"))!;
        Assert.Empty(source.RecommendedPages);
        Assert.False(source.HasWayfinding);
        var json = JsonSerializer.SerializeToElement(await fixture.Aggregator.GetSearchIndexPayloadAsync());
        Assert.Equal(0, json.GetProperty("documents").EnumerateArray().Single(doc => doc.GetProperty("id").GetString() == "source")
            .GetProperty("recommendedPages").GetArrayLength());
        fixture.Set([]);
        fixture.Aggregator.InvalidateCache();
        Assert.Empty((await fixture.Aggregator.GetSearchIndexPayloadAsync()).Documents);
        Assert.Null(await fixture.Aggregator.GetDocDetailsAsync("source"));
        var legacy = new DocDetailsViewModel { Document = Page("Legacy", "legacy.md", "guide"), RelatedPages = [new() { Href = "/docs/x" }] };
        Assert.Empty(legacy.RecommendedPages);
        Assert.True(legacy.HasWayfinding);
    }

    [Fact]
    public async Task FinalSourceIdentity_ShouldSurviveDuplicateSourcesAndSourceShapedSlugCollisions()
    {
        using var fixture = new Fixture();
        fixture.Set([
            Page("Intro", "docs/intro.md", "example", new DocMetadata { CanonicalSlug = "start-here/intro" }),
            Page("Literal", "literal.md", "guide", new DocMetadata { CanonicalSlug = "docs/intro.md" }),
            Page("First", "duplicate.md", "example"), Page("Second", "duplicate.md", "example")]);
        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        Assert.Equal("start-here/intro", payload.Documents.Single(doc => doc.SourcePath == "docs/intro.md").Id);
        Assert.Equal("docs/intro.md", payload.Documents.Single(doc => doc.SourcePath == "literal.md").Id);
        Assert.Equal("First", payload.Documents.Single(doc => doc.Id == "duplicate").Title);
        var details = (await fixture.Aggregator.GetDocDetailsAsync("literal.md"))!;
        Assert.Contains(details.RecommendedPages, row => row.Page.Href == "/docs/start-here/intro" && row.Page.Title == "Intro");
    }

    [Fact]
    public async Task CaseDistinctSources_ShouldKeepTheirOwnCanonicalIdentityAndRecommendations()
    {
        using var fixture = new Fixture();
        fixture.Set([
            Page("Upper source", "Guide.md", "guide", new DocMetadata { CanonicalSlug = "upper-guide" }),
            Page("Lower source", "guide.md", "example", new DocMetadata { CanonicalSlug = "lower-example" })]);

        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        Assert.Equal("upper-guide", payload.Documents.Single(doc => doc.SourcePath == "Guide.md").Id);
        Assert.Equal("lower-example", payload.Documents.Single(doc => doc.SourcePath == "guide.md").Id);
        var upper = (await fixture.Aggregator.GetDocDetailsAsync("upper-guide"))!;
        var lower = (await fixture.Aggregator.GetDocDetailsAsync("lower-example"))!;
        Assert.Equal("Lower source", Assert.Single(upper.RecommendedPages).Page.Title);
        Assert.Equal("Upper source", Assert.Single(lower.RecommendedPages).Page.Title);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/docs")]
    [InlineData("/foo/bar/next")]
    public async Task CustomRootsAndLocaleProjections_ShouldKeepCanonicalIdentityAndProductIsolation(string root)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
        DocAggregator Create(string target)
        {
            var harvester = A.Fake<IDocHarvester>();
            A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
                .Returns([Page("Source", "source.md", "guide"), Page(target, target + ".md", "example")]);
            return new DocAggregator([harvester], new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions { DocsRootPath = root, RouteRootPath = root }
            }, environment, memo, new AppSurfaceDocsHtmlSanitizer(), NullLogger<DocAggregator>.Instance);
        }
        var a = Create("a");
        var b = Create("b");
        var aPayload = await a.GetSearchIndexPayloadAsync(new DocsSearchIndexProjection(Locale: "fr"));
        var bPayload = await b.GetSearchIndexPayloadAsync();
        var aRow = Assert.Single(aPayload.Documents.Single(doc => doc.Id == "source").RecommendedPages);
        var bRow = Assert.Single(bPayload.Documents.Single(doc => doc.Id == "source").RecommendedPages);
        Assert.Equal("a", aRow.TargetId);
        Assert.Equal("b", bRow.TargetId);
        Assert.Equal(root.TrimEnd('/') + "/a", aRow.Path);
        Assert.Equal(root.TrimEnd('/') + "/b", bRow.Path);
        Assert.Equal("try-example", aRow.ReasonCodes[0]);
    }

    [Fact]
    public async Task DistinctAuthoredFragments_ShouldRemainDistinctAndSuppressAutomaticOwner()
    {
        using var fixture = new Fixture();
        var first = new DocNode("First anchor", "example.md#first", "", "example.md");
        var second = new DocNode("Second anchor", "example.md#second", "", "example.md");
        fixture.Set([
            Page("Source", "source.md", "guide", new DocMetadata { RelatedPages = ["example.md#first", "example.md#second", "example.md#first"] }),
            Page("Example", "example.md", "example"), first, second,
            Page("Trouble", "trouble.md", "troubleshooting")]);
        var details = (await fixture.Aggregator.GetDocDetailsAsync("source"))!;
        Assert.Equal(["/docs/example#first", "/docs/example#second"], details.RelatedPages.Select(page => page.Href));
        Assert.Equal(["/docs/example#first", "/docs/example#second", "/docs/trouble"], details.RecommendedPages.Select(row => row.Page.Href));
        var rows = (await fixture.Aggregator.GetSearchIndexPayloadAsync()).Documents.Single(doc => doc.Id == "source").RecommendedPages;
        Assert.Equal(["example#first", "example#second", "trouble"], rows.Select(row => row.TargetId));
        Assert.All(rows.Take(2), row => Assert.Equal("authored", row.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStaleRefreshAndExpiredFailure_ShouldPreserveTheExistingFiniteCacheContract(bool expireBeforeRefresh)
    {
        var clock = new SnapshotClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        using var memo = new Memo(cache, TimeSpan.FromMilliseconds(100), clock);
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
        var harvester = A.Fake<IDocHarvester>();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource<IReadOnlyList<DocNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fail = false;
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).ReturnsLazily(() =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                refreshStarted.TrySetResult();
                return refreshRelease.Task;
            }
            return Task.FromResult<IReadOnlyList<DocNode>>([Page("Source", "source.md", "guide"), Page("Example", "example.md", "example")]);
        });
        var sanitizer = A.Fake<IAppSurfaceDocsHtmlSanitizer>();
        var failingSanitize = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => sanitizer.Sanitize(A<string>._)).ReturnsLazily((string html) =>
        {
            if (Volatile.Read(ref fail)) { failingSanitize.TrySetResult(); throw new InvalidOperationException("Controlled snapshot failure"); }
            return html;
        });
        var aggregator = new DocAggregator([harvester], new AppSurfaceDocsOptions { CacheExpirationMinutes = 1d / 60 },
            environment, memo, sanitizer, NullLogger<DocAggregator>.Instance);
        var first = await aggregator.GetDocDetailsAsync("source");
        Assert.NotNull(first);
        clock.Advance(TimeSpan.FromMilliseconds(expireBeforeRefresh ? 2200 : 1100));
        var second = aggregator.GetDocDetailsAsync("source");
        try
        {
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (expireBeforeRefresh)
            {
                Assert.False(second.IsCompleted); // Expired callers must wait; the held factory cannot return stale data.
            }
            else
            {
                var stale = await second.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotNull(stale);
                Assert.Equal(first.RecommendedPages.Select(row => row.Page.Href), stale.RecommendedPages.Select(row => row.Page.Href));
            }
            Volatile.Write(ref fail, true);
            refreshRelease.SetResult([Page("Source", "source.md", "guide"), Page("New", "new.md", "example")]);
            await failingSanitize.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await memo.DrainBackgroundRefreshesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (expireBeforeRefresh)
                await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                var staleAfterFailure = await aggregator.GetDocDetailsAsync("source");
                Assert.NotNull(staleAfterFailure);
                Assert.Equal("Example", Assert.Single(staleAfterFailure.RecommendedPages).Page.Title);
                clock.Advance(TimeSpan.FromMilliseconds(1100));
                await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.GetDocDetailsAsync("source"));
            }
            // Invalidation starts a new generation, preserving no unlimited fallback across explicit rebuilds.
            aggregator.InvalidateCache();
            await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.GetSearchIndexPayloadAsync());
            Volatile.Write(ref fail, false);
            aggregator.InvalidateCache();
            var recovered = await aggregator.GetDocDetailsAsync("source");
            Assert.NotNull(recovered);
            Assert.Equal("Example", Assert.Single(recovered.RecommendedPages).Page.Title);
        }
        finally
        {
            refreshRelease.TrySetResult([Page("Source", "source.md", "guide")]);
            try { await second.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) when (expireBeforeRefresh) { }
            finally { await memo.DrainBackgroundRefreshesAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private sealed class SnapshotClock : TimeProvider, Microsoft.Extensions.Internal.ISystemClock
    {
        private long _ticks = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public DateTimeOffset UtcNow => GetUtcNow();
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            Interlocked.Add(ref _ticks, elapsed.Ticks);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamespaceIntroControl_ShouldSurviveMetadataPreparationAndKeepAuthoredLinks(bool enabled)
    {
        using var fixture = new Fixture();
        var api = Page("ForgeTrust.Foo", "Namespaces/ForgeTrust.Foo", "api-reference");
        var intro = new DocNode("Foo intro", "Web/ForgeTrust.Foo/NAMESPACE.md", "<p>Intro</p>", Metadata: new DocMetadata
        {
            Namespace = "ForgeTrust.Foo",
            AutoRelatedPages = enabled,
            RelatedPages = ["trouble.md"],
            Component = "Snapshot component"
        });
        fixture.Set([api, intro, Page("Example", "example.md", "example"), Page("Trouble", "trouble.md", "troubleshooting")]);
        var details = (await fixture.Aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Foo"))!;
        Assert.Equal(enabled, details.Document.Metadata?.AutoRelatedPages);
        Assert.Equal("Trouble", Assert.Single(details.RelatedPages).Title);
        Assert.Equal(enabled ? 2 : 1, details.RecommendedPages.Count);
        Assert.Equal(DocRecommendationSource.Authored, details.RecommendedPages[0].Source);
        if (enabled) Assert.Equal(DocRecommendationSource.Automatic, details.RecommendedPages[1].Source);
    }

    [Fact]
    public async Task CollisionLoserAndCompetingLanding_ShouldNotEnterTheAutomaticTargetCorpus()
    {
        using var fixture = new Fixture();
        fixture.Set([
            Page("Source", "source.md", "guide", new DocMetadata { NavGroup = "Guides" }),
            Page("A", "a.md", "example", new DocMetadata { CanonicalSlug = "shared" }),
            Page("B", "b.md", "example", new DocMetadata { CanonicalSlug = "shared" }),
            Page("Landing first", "landing-first.md", "how-to", new DocMetadata { NavGroup = "Guides", SectionLanding = true, Order = 1 }),
            Page("Landing second", "landing-second.md", "how-to", new DocMetadata { NavGroup = "Guides", SectionLanding = true, Order = 2 })]);
        var payload = await fixture.Aggregator.GetSearchIndexPayloadAsync();
        var shared = Assert.Single(payload.Documents, doc => doc.Id == "shared");
        var source = Assert.Single(payload.Documents, doc => doc.Id == "source");
        Assert.Equal(["shared", "landing-first"], source.RecommendedPages.Select(row => row.TargetId));
        Assert.Equal(shared.Title, source.RecommendedPages[0].Title);
        Assert.Equal("section-overview", source.RecommendedPages[1].PrimaryReasonCode);
        Assert.Equal(3, (await fixture.Aggregator.GetRecommendationMeasurementsAsync()).EligibleTargets);
    }

    private static DocNode Page(string title, string path, string role, DocMetadata? metadata = null) =>
        new(title, path, "<p>Readable</p>", Metadata: (metadata ?? new()) with { Title = title, PageType = role, Component = "Snapshot component" });

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly Memo _memo;
        public IDocHarvester Harvester { get; } = A.Fake<IDocHarvester>();
        public DocAggregator Aggregator { get; }
        public Fixture()
        {
            _memo = new Memo(_cache);
            var environment = A.Fake<IWebHostEnvironment>();
            A.CallTo(() => environment.ContentRootPath).Returns(Path.GetTempPath());
            Aggregator = new DocAggregator([Harvester], new AppSurfaceDocsOptions(), environment, _memo,
                new AppSurfaceDocsHtmlSanitizer(), NullLogger<DocAggregator>.Instance);
        }
        public void Set(IReadOnlyList<DocNode> pages) => A.CallTo(() => Harvester.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(pages);
        public void Dispose() { _memo.Dispose(); _cache.Dispose(); }
    }
}
