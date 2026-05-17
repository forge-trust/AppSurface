using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RazorDocsAssetVersionerTests
{
    [Fact]
    public void BuildVersionedDocsAssetUrl_ShouldAppendContentVersionToCurrentDocsAssetUrl()
    {
        var versioner = new RazorDocsAssetVersioner();
        var docsUrlBuilder = new DocsUrlBuilder(new RazorDocsOptions());

        var url = versioner.BuildVersionedDocsAssetUrl(docsUrlBuilder, "search.css");

        Assert.Matches("^/docs/search\\.css\\?v=[A-Za-z0-9_-]+$", url);
    }

    [Fact]
    public void BuildVersionedDocsAssetUrl_ShouldAppendContentVersionToRootMountedAssetUrl()
    {
        var versioner = new RazorDocsAssetVersioner();
        var docsUrlBuilder = new DocsUrlBuilder(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/"
                }
            });

        var url = versioner.BuildVersionedDocsAssetUrl(docsUrlBuilder, "search.css");

        Assert.Matches("^/search\\.css\\?v=[A-Za-z0-9_-]+$", url);
    }

    [Fact]
    public void BuildVersionedDocsAssetUrl_ShouldRejectNullDocsUrlBuilder()
    {
        var versioner = new RazorDocsAssetVersioner();

        Assert.Throws<ArgumentNullException>(
            "docsUrlBuilder",
            () => versioner.BuildVersionedDocsAssetUrl(null!, "search.css"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BuildVersionedDocsAssetUrl_ShouldRejectBlankAssetName(string assetName)
    {
        var versioner = new RazorDocsAssetVersioner();
        var docsUrlBuilder = new DocsUrlBuilder(new RazorDocsOptions());

        Assert.Throws<ArgumentException>(
            "assetName",
            () => versioner.BuildVersionedDocsAssetUrl(docsUrlBuilder, assetName));
    }

    [Fact]
    public void AppendVersion_ShouldNormalizeEmbeddedAssetPathBeforeHashing()
    {
        var versioner = new RazorDocsAssetVersioner();

        var url = versioner.AppendVersion("/docs/search.css", "/docs\\search.css");

        Assert.Matches("^/docs/search\\.css\\?v=[A-Za-z0-9_-]+$", url);
    }

    [Fact]
    public void AppendVersion_ShouldProduceDifferentVersionsForDifferentAssets()
    {
        var versioner = new RazorDocsAssetVersioner();
        var docsUrlBuilder = new DocsUrlBuilder(new RazorDocsOptions());

        var searchUrl = versioner.BuildVersionedDocsAssetUrl(docsUrlBuilder, "search.css");
        var outlineUrl = versioner.BuildVersionedDocsAssetUrl(docsUrlBuilder, "outline-client.js");

        Assert.NotEqual(ExtractVersionParameter(searchUrl), ExtractVersionParameter(outlineUrl));
    }

    [Fact]
    public void AppendVersion_ShouldPreserveExistingQueryAndFragment()
    {
        var versioner = new RazorDocsAssetVersioner();

        var url = versioner.AppendVersion("/docs/search.css?cache=local#theme", "docs/search.css");

        Assert.Matches("^/docs/search\\.css\\?cache=local&v=[A-Za-z0-9_-]+#theme$", url);
    }

    [Fact]
    public void AppendVersion_ShouldIgnoreQuestionMarksInsideFragments()
    {
        var versioner = new RazorDocsAssetVersioner();

        var url = versioner.AppendVersion("/docs/search.css#theme?mode=dark", "docs/search.css");

        Assert.Matches("^/docs/search\\.css\\?v=[A-Za-z0-9_-]+#theme\\?mode=dark$", url);
    }

    [Fact]
    public void AppendVersion_ShouldNotDuplicateExistingVersionParameter()
    {
        var versioner = new RazorDocsAssetVersioner();

        var url = versioner.AppendVersion("/docs/search.css?v=existing#theme", "docs/search.css");

        Assert.Equal("/docs/search.css?v=existing#theme", url);
    }

    [Fact]
    public void AppendVersion_ShouldLeaveUrlUnchanged_WhenAssetIsMissing()
    {
        var versioner = new RazorDocsAssetVersioner();

        var url = versioner.AppendVersion("/docs/missing.css", "docs/missing.css");

        Assert.Equal("/docs/missing.css", url);
    }

    [Fact]
    public void AppendVersion_ShouldRejectNullUrl()
    {
        var versioner = new RazorDocsAssetVersioner();

        Assert.Throws<ArgumentNullException>(
            "url",
            () => versioner.AppendVersion(null!, "docs/search.css"));
    }

    [Fact]
    public void AppendVersion_ShouldRejectNullEmbeddedAssetPath()
    {
        var versioner = new RazorDocsAssetVersioner();

        Assert.Throws<ArgumentNullException>(
            "embeddedAssetPath",
            () => versioner.AppendVersion("/docs/search.css", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AppendVersion_ShouldRejectBlankUrl(string url)
    {
        var versioner = new RazorDocsAssetVersioner();

        Assert.Throws<ArgumentException>(
            "url",
            () => versioner.AppendVersion(url, "docs/search.css"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AppendVersion_ShouldRejectBlankEmbeddedAssetPath(string embeddedAssetPath)
    {
        var versioner = new RazorDocsAssetVersioner();

        Assert.Throws<ArgumentException>(
            "embeddedAssetPath",
            () => versioner.AppendVersion("/docs/search.css", embeddedAssetPath));
    }

    private static string ExtractVersionParameter(string url)
    {
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        Assert.True(queryStart >= 0, $"Expected '{url}' to contain a query string.");

        var fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
        var query = fragmentStart > queryStart
            ? url[(queryStart + 1)..fragmentStart]
            : url[(queryStart + 1)..];
        var parsed = QueryHelpers.ParseQuery(query);

        Assert.True(
            parsed.TryGetValue(RazorDocsAssetVersioner.VersionParameterName, out var values),
            $"Expected '{url}' to contain a '{RazorDocsAssetVersioner.VersionParameterName}' query parameter.");
        return Assert.Single(values)!;
    }
}
