using ForgeTrust.AppSurface.Docs.Services;

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
}
