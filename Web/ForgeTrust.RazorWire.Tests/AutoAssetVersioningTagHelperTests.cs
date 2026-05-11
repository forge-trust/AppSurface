using FakeItEasy;
using ForgeTrust.RazorWire.TagHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ForgeTrust.RazorWire.Tests;

public class AutoAssetVersioningTagHelperTests
{
    private readonly IFileVersionProvider _fileVersionProvider;
    private readonly AutoAssetVersioningTagHelper _helper;
    private readonly TagHelperContext _context;
    private readonly TagHelperOutput _output;
    private readonly ViewContext _viewContext;

    public AutoAssetVersioningTagHelperTests()
    {
        _fileVersionProvider = A.Fake<IFileVersionProvider>();
        _helper = new AutoAssetVersioningTagHelper(_fileVersionProvider);

        _context = new TagHelperContext(
            new TagHelperAttributeList(),
            new Dictionary<object, object>(),
            Guid.NewGuid().ToString());

        _output = new TagHelperOutput(
            "script",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.PathBase = "/app";

        _viewContext = new ViewContext { HttpContext = httpContext };
        _helper.ViewContext = _viewContext;
    }

    [Fact]
    public void Constructor_WithNullProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new AutoAssetVersioningTagHelper(null!));
    }

    [Fact]
    public void Process_WithAspAppendVersion_DoesNothing()
    {
        var attributes = new TagHelperAttributeList { new TagHelperAttribute("asp-append-version", "true") };
        var context = new TagHelperContext(attributes, new Dictionary<object, object>(), Guid.NewGuid().ToString());

        _output.Attributes.Add("src", "/js/site.js");

        _helper.Process(context, _output);

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void Process_WithRemoteUrl_DoesNothing()
    {
        _output.Attributes.SetAttribute("src", "https://cdn.example.com/lib.js");

        _helper.Process(_context, _output);

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .MustNotHaveHappened();
        Assert.Equal("https://cdn.example.com/lib.js", _output.Attributes["src"].Value);
    }

    [Fact]
    public void Process_WithProtocolRelativeUrl_DoesNothing()
    {
        // This targets the bug where strings starting with "//" were treated as local because they start with "/"
        _output.Attributes.SetAttribute("src", "//cdn.example.com/lib.js");

        _helper.Process(_context, _output);

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .MustNotHaveHappened();
        Assert.Equal("//cdn.example.com/lib.js", _output.Attributes["src"].Value);
    }

    [Fact]
    public void Process_WithLocalScript_AppendsVersion()
    {
        _output.TagName = "script";
        _output.Attributes.SetAttribute("src", "/js/site.js");

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath("/app", "/js/site.js"))
            .Returns("/js/site.js?v=hashed");

        _helper.Process(_context, _output);

        Assert.Equal("/js/site.js?v=hashed", _output.Attributes["src"].Value);
    }

    [Fact]
    public void Process_WithNoOrEmptyRel_DoesNothing()
    {
        _output.TagName = "link";
        // Case 1: No rel attribute
        _helper.Process(_context, _output);
        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .MustNotHaveHappened();

        // Case 2: Empty rel attribute
        _output.Attributes.SetAttribute("rel", "");
        _helper.Process(_context, _output);

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void Process_WithMultiTokenRel_AppendsVersion()
    {
        _output.TagName = "link";
        _output.Attributes.SetAttribute("rel", "preload stylesheet");
        _output.Attributes.SetAttribute("href", "~/css/site.css");

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath("/app", "~/css/site.css"))
            .Returns("~/css/site.css?v=hashed");

        _helper.Process(_context, _output);

        Assert.Equal("~/css/site.css?v=hashed", _output.Attributes["href"].Value);
    }

    [Fact]
    public void Process_WithLocalLinkStylesheet_AppendsVersion()
    {
        _output.TagName = "link";
        _output.Attributes.SetAttribute("rel", "stylesheet");
        _output.Attributes.SetAttribute("href", "~/css/site.css");

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath("/app", "~/css/site.css"))
            .Returns("~/css/site.css?v=hashed");

        _helper.Process(_context, _output);

        Assert.Equal("~/css/site.css?v=hashed", _output.Attributes["href"].Value);
    }

    [Fact]
    public void Process_WithNonStylesheetLink_DoesNothing()
    {
        _output.TagName = "link";
        _output.Attributes.SetAttribute("rel", "icon");
        _output.Attributes.SetAttribute("href", "/favicon.ico");

        _helper.Process(_context, _output);

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath(A<PathString>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void Process_WithTabSeparatedRel_AppendsVersion()
    {
        _output.TagName = "link";
        _output.Attributes.SetAttribute("rel", "stylesheet\tpreload");
        _output.Attributes.SetAttribute("href", "/css/site.css");

        A.CallTo(() => _fileVersionProvider.AddFileVersionToPath("/app", "/css/site.css"))
            .Returns("/css/site.css?v=tab");

        _helper.Process(_context, _output);

        Assert.Equal("/css/site.css?v=tab", _output.Attributes["href"].Value);
    }
}
