using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocsSearchIndexProjectionCacheTests
{
    [Fact]
    public void GetPayload_ShouldPreserveDefaultPayload_ForLocaleProjectionInPhaseOne()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-16T00:00:00.0000000Z", "1", "minisearch"),
            []);
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            options,
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home"));
        var cache = new DocsSearchIndexProjectionCache(payload, graph);

        var projected = cache.GetPayload(new DocsSearchIndexProjection(Locale: "fr"));

        Assert.Same(payload, projected);
        Assert.Equal("1", projected.Metadata.Version);
    }

    [Fact]
    public void GetPayload_ShouldReturnCachedProjection_ForRepeatedLocaleProjection()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-16T00:00:00.0000000Z", "1", "minisearch"),
            []);
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            options,
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home"));
        var cache = new DocsSearchIndexProjectionCache(payload, graph);
        var projection = new DocsSearchIndexProjection(Locale: "fr");

        var firstPayload = cache.GetPayload(projection);
        var secondPayload = cache.GetPayload(projection);

        Assert.Same(payload, firstPayload);
        Assert.Same(firstPayload, secondPayload);
    }

    [Fact]
    public void GetPayload_ShouldNormalizeLocaleProjectionKeys()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-16T00:00:00.0000000Z", "1", "minisearch"),
            []);
        var options = RazorDocsLocalizationFixture.CreateOptions();
        var graph = RazorDocsLocalizationFixture.BuildGraph(
            options,
            RazorDocsLocalizationFixture.MarkdownDoc("README.md", "Home"));
        var cache = new DocsSearchIndexProjectionCache(payload, graph);

        var firstPayload = cache.GetPayload(new DocsSearchIndexProjection(Locale: " FR "));
        var secondPayload = cache.GetPayload(new DocsSearchIndexProjection(Locale: "fr"));

        Assert.Same(payload, firstPayload);
        Assert.Same(firstPayload, secondPayload);
    }

    [Fact]
    public void GetPayload_ShouldPreserveDefaultPayload_WhenLocalizationIsDisabled()
    {
        var payload = new DocsSearchIndexPayload(
            new DocsSearchIndexMetadata("2026-05-16T00:00:00.0000000Z", "1", "minisearch"),
            []);
        var cache = new DocsSearchIndexProjectionCache(
            payload,
            new LocalizedDocsGraph(false, null, [], new Dictionary<string, LocalizedDocVariant>(), []));

        var projected = cache.GetPayload(new DocsSearchIndexProjection(Locale: "fr"));

        Assert.Same(payload, projected);
    }
}
