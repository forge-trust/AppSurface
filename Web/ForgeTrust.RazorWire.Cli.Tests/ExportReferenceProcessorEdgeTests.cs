using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli.Tests;

public sealed class ExportReferenceProcessorEdgeTests
{
    private readonly ExportReferenceProcessor _sut = new(A.Fake<ILogger>());

    [Fact]
    public void AttributeLineProvenanceRemainsExactAcrossMultilineTagsAndRepeatedOperations()
    {
        const string html = "<!-- ignored\n<tag> -->\r\n<a disabled\n href='/first'\n data-note='value\ncontinued'>one</a>\n<script>\n\"<a href='/hidden'>\";\n</script>\n<img\n src='/second.png'>";
        var extracted = _sut.ExtractReferences(html, "/index.html", htmlScope: true);
        Assert.Equal([4, 11], extracted.Select(reference => reference.Provenance!.Line));
        var observedLines = new List<int?>();
        var rewritten = _sut.RewriteManagedReferences(html, "/index.html", htmlScope: true, reference =>
        {
            observedLines.Add(reference.Provenance!.Line);
            return "/export" + reference.Path;
        });
        Assert.Equal([4, 11], observedLines);
        Assert.Contains("href='/export/first'", rewritten, StringComparison.Ordinal);
        Assert.Contains("src='/export/second.png'", rewritten, StringComparison.Ordinal);
        Assert.Contains("href='/hidden'", rewritten, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(_sut.ExtractReferences("<a href='/new'>new</a>", "/new", true)).Provenance!.Line);
    }

    [Fact]
    public void LargeHtmlNavigationRetainsExactAttributeLinesWhenExtractedAndRewritten()
    {
        const int count = 10000;
        var html = string.Join('\n', Enumerable.Range(0, count)
            .Select(i => $"<a data-label='Page {i}' href='/page-{i}' aria-label='Page {i}'>Page {i}</a>"));
        var extracted = _sut.ExtractReferences(html, "/index.html", true);
        Assert.Equal(count, extracted.Count);
        Assert.Equal(Enumerable.Range(1, count), extracted.Select(reference => reference.Provenance!.Line!.Value));
        var observed = new List<int>();
        var rewritten = _sut.RewriteManagedReferences(html, "/index.html", true, reference =>
        {
            observed.Add(reference.Provenance!.Line!.Value);
            return "/export" + reference.Path;
        });
        Assert.Equal(Enumerable.Range(1, count), observed);
        Assert.Contains("href='/export/page-9999'", rewritten, StringComparison.Ordinal);
    }

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
