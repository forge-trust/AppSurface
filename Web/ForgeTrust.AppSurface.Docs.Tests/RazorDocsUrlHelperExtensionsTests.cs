using FakeItEasy;
using Microsoft.AspNetCore.Mvc;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class RazorDocsUrlHelperExtensionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PathBaseAware_ShouldReturnEmptyString_ForBlankInputs(string? href)
    {
        var urlHelper = A.Fake<IUrlHelper>();

        var rewritten = urlHelper.PathBaseAware(href);

        Assert.Equal(string.Empty, rewritten);
        A.CallTo(() => urlHelper.Content(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public void PathBaseAware_ShouldRewriteSingleSlashAppRelativeHrefs()
    {
        var urlHelper = A.Fake<IUrlHelper>();
        A.CallTo(() => urlHelper.Content("~/docs/guide"))
            .Returns("/some-base/docs/guide");

        var rewritten = urlHelper.PathBaseAware("/docs/guide");

        Assert.Equal("/some-base/docs/guide", rewritten);
        A.CallTo(() => urlHelper.Content("~/docs/guide")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void PathBaseAware_ShouldPreserveProtocolRelativeUrls()
    {
        var urlHelper = A.Fake<IUrlHelper>();

        var rewritten = urlHelper.PathBaseAware("//cdn.example.com/app.css");

        Assert.Equal("//cdn.example.com/app.css", rewritten);
        A.CallTo(() => urlHelper.Content(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public void PathBaseAware_ShouldLeaveNonRootedRelativeUrlsUnchanged()
    {
        var urlHelper = A.Fake<IUrlHelper>();

        var rewritten = urlHelper.PathBaseAware("assets/style.css");

        Assert.Equal("assets/style.css", rewritten);
        A.CallTo(() => urlHelper.Content(A<string>._)).MustNotHaveHappened();
    }
}
