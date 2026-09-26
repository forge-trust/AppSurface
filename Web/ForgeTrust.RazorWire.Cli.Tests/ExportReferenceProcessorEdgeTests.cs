using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ExportReferenceProcessorEdgeTests
{
    private readonly ExportReferenceProcessor _sut = new(A.Fake<ILogger>());

    [Fact]
    public void ExtractReferences_StopsAtUnterminatedCommentAndQuotedTag()
    {
        var unterminatedComment = _sut.ExtractReferences(
            "<a href='/before'>ok</a><!-- <a href='/hidden'>hidden</a>", "/index.html", htmlScope: true);
        var unterminatedTag = _sut.ExtractReferences(
            "<a href='/before'>ok</a><img src='/never.png' title='unterminated", "/index.html", htmlScope: true);

        Assert.Equal("/before", Assert.Single(unterminatedComment).Path);
        Assert.Equal("/before", Assert.Single(unterminatedTag).Path);
    }

    [Fact]
    public void ExtractReferences_HandlesMalformedAttributesAndSkipsTagTextInRawScript()
    {
        const string html = "<img / src='/image.png' disabled data-x=one broken=>"
            + "<script>const sample = \"<img src='/fake.png'>\";</script>"
            + "<a href='/real'>real</a>";

        var references = _sut.ExtractReferences(html, "/index.html", htmlScope: true);

        Assert.Contains(references, item => item.Path == "/image.png");
        Assert.Contains(references, item => item.Path == "/real");
        Assert.DoesNotContain(references, item => item.Path == "/fake.png");
    }

    [Fact]
    public void ExtractReferences_ParsesSrcSetDataUrlsAndIgnoresIncompleteCandidates()
    {
        const string html = "<img srcset='data:image/png;base64,AAAA 1x, /small.png 2x, , /large.png 3x'>";

        var paths = _sut.ExtractReferences(html, "/index.html", htmlScope: true)
            .Select(item => item.Path)
            .ToArray();

        Assert.Equal(["/small.png", "/large.png"], paths);
    }

    [Fact]
    public void ExtractReferences_DecodesCssEscapesAndFiltersMalformedOrExternalTokens()
    {
        const string css = "a{a:url(\\2f images\\2f icon.png);b:url(https://cdn.test/a.png);c:url('/broken.png'}"
            + "d{e:url(\\0.png);f:url(\\D800.png);g:url(\\110000.png);h:url(\\41\nasset.png)}";

        var references = _sut.ExtractReferences(css, "/css/site.css", htmlScope: false);
        var paths = references.Select(item => item.Path).ToArray();

        Assert.Contains("/images/icon.png", paths);
        Assert.Contains("/css/Aasset.png", paths);
        Assert.DoesNotContain(paths, path => path.Contains("cdn.test", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains('\uFFFD'));
        Assert.DoesNotContain(paths, path => path.Contains("broken", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("const source = '/custom/navigation.js';", "/custom/navigation.js")]
    [InlineData("const resource = '/wrong.js'; const source = '/right.js';", "/right.js")]
    [InlineData("const source = '/bad\\xQ1.js';", "/badxQ1.js")]
    [InlineData("const source = '/unterminated.js;", null)]
    [InlineData("const source = '/trailing\\", null)]
    public void ExtractReferences_UsesOnlyWellFormedAutoloadSourceAssignments(string assignment, string? expectedPath)
    {
        var html = $"<a data-rw-page-nav></a><script>data-rw-page-navigation-runtime [data-rw-page-nav] {assignment}</script>";

        var references = _sut.ExtractReferences(html, "/index.html", htmlScope: true);

        if (expectedPath is null)
        {
            Assert.Empty(references);
            return;
        }

        Assert.Equal(expectedPath, Assert.Single(references).Path);
    }

    [Fact]
    public void RewriteManagedReferences_ChangesOnlyResolvedReferencesAndPreservesMalformedText()
    {
        const string html = "<img src='/asset.png'><img src='/unresolved.png'><script>\"<img src='/fake.png'>\"</script>";

        var rewritten = _sut.RewriteManagedReferences(
            html,
            "/index.html",
            htmlScope: true,
            reference => reference.Path == "/asset.png" ? "/export/asset.png" : null);

        Assert.Equal(
            "<img src='/export/asset.png'><img src='/unresolved.png'><script>\"<img src='/fake.png'>\"</script>",
            rewritten);
    }
}
