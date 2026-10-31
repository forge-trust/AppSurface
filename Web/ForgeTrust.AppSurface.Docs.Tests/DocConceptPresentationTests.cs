using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocConceptPresentationTests
{
    private static readonly string Key = new('a', 64);
    private static readonly DocConceptDestination Target = new("Docs", "2", new string('b', 64), null, "guides/setup");

    [Fact]
    public void Guide_EncodesHtmlAndInertJson_AndSelectsOnlyKnownIncomingNotice()
    {
        var incoming = Incoming() with { Notice = "Moved <script>alert('x')</script>", SourceTitle = "Original <guide>" };
        var markup = DocConceptPresentation.Guide(View([incoming]), "sha256-actual", 4194304, Key);
        var document = new HtmlParser().ParseDocument("<html><head>" + markup.Head + "</head><body>" + markup.Body + "</body></html>");
        Assert.Equal("Moved <script>alert('x')</script>", document.QuerySelector("#docs-version-explanation")!.TextContent);
        Assert.Equal(2, document.QuerySelectorAll("script").Length);
        using var data = JsonDocument.Parse(document.QuerySelector("script[type='application/json']")!.TextContent);
        Assert.Equal(incoming.Notice, data.RootElement.GetProperty("incoming")[0].GetProperty("notice").GetString());
        Assert.Contains("&lt;title&gt;", markup.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(markup.Head + markup.Body), markup.Utf8Bytes);
        var unknown = DocConceptPresentation.Guide(View([incoming]), "sha256-actual", 4194304, new string('0', 64));
        Assert.Null(new HtmlParser().ParseDocument(unknown.Body).QuerySelector("[data-docs-version-notice]"));
    }

    [Fact]
    public void Guide_RejectsIncomingRowsBoundToAnotherDestinationOrUnsafeReturn()
    {
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Guide(View([Incoming() with { Target = Target with { Pin = new string('c', 64) } }]), "hash", 4194304));
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Guide(View([Incoming() with { ReturnUrl = "https://evil.example" }]), "hash", 4194304));
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Guide(View([Incoming() with { SourceKey = "bad" }]), "hash", 4194304));
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Guide(View([]) with { SourceKey = "bad" }, "hash", 4194304));
    }

    [Fact]
    public void Guide_BindsMultipleIncomingSourcesAndCountsTheirCompleteEncodedBytes()
    {
        var first = Incoming();
        var secondKey = new string('b', 64);
        var second = Incoming() with { SourceKey = secondKey, SourceVersion = "0.9", SourceTitle = "Earlier setup", ReturnUrl = "/docs/v/0.9/setup", Notice = "A different source moved here." };
        var view = View([first, second]) with { PageUrl = "/docs/v/2/setup" };
        var markup = DocConceptPresentation.Guide(view, "hash", 4194304, secondKey);
        var document = new HtmlParser().ParseDocument(markup.Body);
        Assert.Equal(second.Notice, document.QuerySelector("#docs-version-explanation")!.TextContent);
        using var data = JsonDocument.Parse(document.QuerySelector("[data-docs-version-data]")!.TextContent);
        Assert.Equal(2, data.RootElement.GetProperty("incoming").GetArrayLength());
        Assert.Equal(first.SourceKey, data.RootElement.GetProperty("incoming")[0].GetProperty("sourceKey").GetString());
        Assert.Equal(secondKey, data.RootElement.GetProperty("incoming")[1].GetProperty("sourceKey").GetString());
        Assert.Equal(markup, DocConceptPresentation.Guide(view, "hash", markup.Utf8Bytes, secondKey));
        Assert.Throws<DocConceptArtifactOverflowException>(() => DocConceptPresentation.Guide(view, "hash", markup.Utf8Bytes - 1, secondKey));
        var withoutIncoming = DocConceptPresentation.Guide(view with { Incoming = [] }, "hash", 4194304);
        Assert.True(withoutIncoming.Utf8Bytes < markup.Utf8Bytes);
        Assert.Throws<DocConceptArtifactOverflowException>(() => DocConceptPresentation.Guide(view, "hash", withoutIncoming.Utf8Bytes));
        var silent = DocConceptPresentation.Guide(view with { Incoming = [first with { Notice = "" }] }, "hash", 4194304, Key);
        Assert.DoesNotContain("data-docs-version-notice", silent.Body);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/path\\unsafe")]
    [InlineData("/path\nunsafe")]
    [InlineData("")]
    public void AllUrlBoundaries_RejectUnvalidatedDestinations(string url)
    {
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Guide(View([]) with { ChoicePageUrl = url }, "hash", 4194304));
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Choices("title", "1", url, "/docs/versions", "/style.css", [], 4194304));
        Assert.Throws<ArgumentException>(() => DocConceptPresentation.Recovery(url, null, 4194304, false));
    }

    [Fact]
    public void Guide_NoAlternateVersionOmitsPickerAndShowsCurrentIdentity()
    {
        var markup = DocConceptPresentation.Guide(View([]) with { Choices = [], Target = Target with { Locale = "fr" } }, "hash", 4194304);
        Assert.Contains("No other versions available", markup.Body);
        Assert.DoesNotContain("data-docs-version-fallback", markup.Body);
        Assert.DoesNotContain("data-docs-version-toggle", markup.Body);
        Assert.Contains("<span>fr</span>", markup.Body);
    }

    [Fact]
    public void Guide_EnforcesCompleteUtf8Budget_AndStopsLazyIncomingBeforeRetainingMatrix()
    {
        var view = View([Incoming()]);
        var markup = DocConceptPresentation.Guide(view, "hash", 4194304, Key);
        Assert.Equal(markup, DocConceptPresentation.Guide(view, "hash", markup.Utf8Bytes, Key));
        var overflow = Assert.Throws<DocConceptArtifactOverflowException>(() => DocConceptPresentation.Guide(view, "hash", markup.Utf8Bytes - 1, Key));
        Assert.Equal(markup.Utf8Bytes - 1, overflow.MaximumBytes);
        Assert.True(overflow.ObservedBytes > overflow.MaximumBytes);
        var count = 0;
        IEnumerable<DocConceptIncomingView> Rows()
        {
            for (var i = 0; i < 1000; i++) { count++; yield return Incoming(); }
        }
        Assert.Throws<DocConceptArtifactOverflowException>(() => DocConceptPresentation.Guide(View(Rows()), "hash", 3000));
        Assert.InRange(count, 1, 10);
    }

    [Fact]
    public void Choices_EncodeActionsAndTitles_AndBoundTheEntireStandaloneArtifact()
    {
        var choice = new DocConceptChoiceView("2<&", "fr", "Moved <guide>", "Read <this>", [new DocConceptAction("Open <guide>", "/docs/v/2/new")], "/docs/v/1/old");
        var page = DocConceptPresentation.Choices("<Setup>", "1", "/docs/v/1/old", "/docs/versions", "/style.css", [choice], 4194304);
        Assert.Contains("&lt;Setup&gt;", page);
        Assert.Contains("Open &lt;guide&gt;", page);
        Assert.Contains(DocConceptPresentation.ChoiceAnchor(choice.Version), page);
        var bytes = Encoding.UTF8.GetByteCount(page);
        Assert.Equal(page, DocConceptPresentation.Choices("<Setup>", "1", "/docs/v/1/old", "/docs/versions", "/style.css", [choice], bytes));
        Assert.Throws<DocConceptArtifactOverflowException>(() => DocConceptPresentation.Choices("<Setup>", "1", "/docs/v/1/old", "/docs/versions", "/style.css", [choice], bytes - 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_IsWholeOrAbsent_AndOptionalReturnCanBeOmitted(bool standalone)
    {
        var recovery = DocConceptPresentation.Recovery("/docs/versions", "/docs/v/1/old", 4194304, standalone)!;
        Assert.Contains("Version archive", recovery);
        Assert.Contains("Return to original guide", recovery);
        Assert.Equal(recovery, DocConceptPresentation.Recovery("/docs/versions", "/docs/v/1/old", Encoding.UTF8.GetByteCount(recovery), standalone));
        Assert.Null(DocConceptPresentation.Recovery("/docs/versions", "/docs/v/1/old", Encoding.UTF8.GetByteCount(recovery) - 1, standalone));
        Assert.DoesNotContain("Return to original guide", DocConceptPresentation.Recovery("/docs/versions", null, 4194304, standalone));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33554433)]
    public void InvalidBudget_IsRejectedWithoutPartialOutput(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocConceptPresentation.Guide(View([]), "hash", limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocConceptPresentation.Choices("title", "1", "/a", "/b", "/c", [], limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocConceptPresentation.Recovery("/a", null, limit, false));
    }

    private static DocConceptIncomingView Incoming() => new(Key, "MovedPage", "Moved guide", "/docs/v/1/original", "1", "Original", Target);
    private static DocConceptGuideView View(IEnumerable<DocConceptIncomingView> incoming) => new(Target, "<title>", Key, "/docs/versions/switch/" + Key + ".html", "/docs/versions", "/docs/versions/switch/runtime.js", "/docs/versions/switch/switch.css", [new("1", "Version <title>", "/docs/v/1/original")], incoming);
}
