using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class PublishedSearchIndexDocumentPathPolicyTests
{
    [Theory]
    [InlineData("/docs/guide.html")]
    [InlineData("/docs/packages/README.md.html")]
    [InlineData("/docs")]
    [InlineData("/docs/%41pi.html")]
    [InlineData("/docs/v/1.2.3/guide.html")]
    [InlineData("/docs/v/1.2.3")]
    [InlineData("/docs/versions/1.2.3/guide.html")]
    [InlineData("/docs/versions/1.2.3")]
    [InlineData("/docs/guide.html?q=term#section-2")]
    public void ValidateArchivePath_ShouldAcceptCanonicalDocsDocumentPaths(string path)
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateArchivePath(
            path,
            new PublishedSearchIndexArchivePathContext("1.2.3"));

        Assert.True(result.IsValid);
        Assert.Equal(PublishedSearchIndexPathRejectionReason.None, result.Reason);
        Assert.NotNull(result.NormalizedPath);
    }

    [Theory]
    [InlineData("", "missing")]
    [InlineData("docs/guide.html", "not-root-relative")]
    [InlineData(" /docs/guide.html", "whitespace")]
    [InlineData("javascript:alert(1)", "scheme-url")]
    [InlineData("data:text/html,hi", "scheme-url")]
    [InlineData("https://evil.example/docs/guide.html", "absolute-url")]
    [InlineData("//evil.example/docs/guide.html", "protocol-relative")]
    [InlineData("/admin", "outside-docs-root")]
    [InlineData("/foo/bar/guide.html", "outside-docs-root")]
    [InlineData("/tenant/docs/guide.html", "outside-docs-root")]
    [InlineData("/docs/search", "reserved-route")]
    [InlineData("/docs/search-index.json", "reserved-route")]
    [InlineData("/docs/search.css", "reserved-route")]
    [InlineData("/docs/search-client.js", "reserved-route")]
    [InlineData("/docs/_health", "reserved-route")]
    [InlineData("/docs/_routes", "reserved-route")]
    [InlineData("/docs/_search-index/refresh", "reserved-route")]
    [InlineData("/docs/versions", "reserved-route")]
    [InlineData("/docs/v", "reserved-route")]
    [InlineData("/docs/v/9.9.9/guide.html", "wrong-version")]
    [InlineData("/docs/../admin", "encoded-traversal")]
    [InlineData("/docs/%2e%2e/admin", "encoded-traversal")]
    [InlineData("/docs/%2E%2E/admin", "encoded-traversal")]
    [InlineData("/docs/%252e%252e/admin", "encoded-traversal")]
    [InlineData("/docs/%2fadmin", "encoded-separator")]
    [InlineData("/docs/%2Fadmin", "encoded-separator")]
    [InlineData("/docs/%252fadmin", "encoded-separator")]
    [InlineData("/docs/%5cadmin", "encoded-separator")]
    [InlineData("/docs/guide.html\\evil", "backslash")]
    [InlineData("/docs/guide.html?q=bad\\path", "backslash")]
    [InlineData("/docs/%", "malformed-percent-encoding")]
    [InlineData("/docs/%0a", "control-character")]
    [InlineData("/docs/guide.html?q=%0a", "control-character")]
    [InlineData("/docs/guide.html?q=%250a", "control-character")]
    public void ValidateArchivePath_ShouldRejectUnsafeDocumentPaths(
        string path,
        string reason)
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateArchivePath(
            path,
            new PublishedSearchIndexArchivePathContext("1.2.3"));

        Assert.False(result.IsValid);
        Assert.Equal(reason, PublishedSearchIndexDocumentPathPolicy.ToDiagnosticCode(result.Reason));
        Assert.StartsWith("<redacted:", result.RedactedValue);
    }

    [Fact]
    public void ValidateArchivePath_ShouldRejectRawControlCharacters()
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateArchivePath(
            "/docs/guide.html\r\n",
            new PublishedSearchIndexArchivePathContext("1.2.3"));

        Assert.False(result.IsValid);
        Assert.Equal(PublishedSearchIndexPathRejectionReason.ControlCharacter, result.Reason);
    }

    [Theory]
    [InlineData("/docs/guide.html", "", null)]
    [InlineData("/guide.html", "/", null)]
    [InlineData("/docs/versions/1.2.3/guide.html", "docs", "/docs/versions/")]
    [InlineData("/docs/versions/1.2.3/guide.html", "/docs", " /docs/versions ")]
    [InlineData("/some-base/docs/v/1.2.3/guide.html", "/some-base/docs/v/1.2.3", null)]
    [InlineData("/some-base/docs/versions/1.2.3/guide.html", "/some-base/docs/v/1.2.3", "/some-base/docs/versions")]
    [InlineData("/foo/bar/guide.html", "/foo/bar", null)]
    [InlineData("/foo/bar/versions/1.2.3/guide.html", "/foo/bar/v/1.2.3", "/foo/bar/versions")]
    [InlineData("/versions/1.2.3/guide.html", "/v/1.2.3", "/versions")]
    public void ValidateServedPath_ShouldAcceptPathsUnderExplicitServedRoots(string path, string docsRootPath, string? archiveRootPath)
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateServedPath(
            path,
            new PublishedSearchIndexServedPathContext(docsRootPath, archiveRootPath));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateServedPath_ShouldPreferLongerArchiveRoot_WhenRootsOverlap()
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateServedPath(
            "/docs/versions/1.2.3/guide.html",
            new PublishedSearchIndexServedPathContext("/docs", "/docs/versions"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateServedPath_ShouldNotInferArchiveRootFromVersionLikeDocsRoot()
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateServedPath(
            "/api/versions/docs/guide.html",
            new PublishedSearchIndexServedPathContext("/api/v/docs"));

        Assert.False(result.IsValid);
        Assert.Equal(PublishedSearchIndexPathRejectionReason.OutsideDocsRoot, result.Reason);
    }

    [Theory]
    [InlineData("/docs/v/9.9.9/guide.html")]
    [InlineData("/docs/versions/1.2.3/guide.html")]
    public void ValidateServedPath_ShouldRejectVersionFamilyChildrenWithoutExplicitRoots(string path)
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateServedPath(
            path,
            new PublishedSearchIndexServedPathContext("/docs"));

        Assert.False(result.IsValid);
        Assert.Equal(PublishedSearchIndexPathRejectionReason.ReservedRoute, result.Reason);
    }

    [Fact]
    public void ValidateServedPath_ShouldRejectReservedRoutesInsideExplicitArchiveRoot()
    {
        foreach (var path in new[]
        {
            "/docs/versions/search",
            "/docs/versions/_health",
            "/docs/versions/_search-index/refresh",
            "/docs/versions/1.2.3/_search-index/refresh"
        })
        {
            var result = PublishedSearchIndexDocumentPathPolicy.ValidateServedPath(
                path,
                new PublishedSearchIndexServedPathContext("/docs", "/docs/versions"));

            Assert.False(result.IsValid);
            Assert.Equal(PublishedSearchIndexPathRejectionReason.ReservedRoute, result.Reason);
        }
    }

    [Theory]
    [InlineData("/docs/versions")]
    [InlineData("/docs/versions/search")]
    public void ValidateServedPath_ShouldRejectArchiveRootWithoutDocumentChild(string path)
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ValidateServedPath(
            path,
            new PublishedSearchIndexServedPathContext("/docs", "/docs/versions"));

        Assert.False(result.IsValid);
        Assert.Equal(PublishedSearchIndexPathRejectionReason.ReservedRoute, result.Reason);
    }

    [Fact]
    public void ToDiagnosticCode_ShouldReturnUnknown_ForUnsupportedEnumValue()
    {
        var result = PublishedSearchIndexDocumentPathPolicy.ToDiagnosticCode((PublishedSearchIndexPathRejectionReason)999);

        Assert.Equal("unknown", result);
    }
}
