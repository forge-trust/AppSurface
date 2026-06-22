using ForgeTrust.AppSurface.Docs.Controllers;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DocsControllerHarvestReturnUrlTests
{
    [Theory]
    [InlineData("/docs")]
    [InlineData("/docs/search?q=api")]
    [InlineData("/base/docs/packages/README.md.html")]
    public void IsSafeAppRelativeUrl_WhenUrlIsAppRelative_ReturnsTrue(string url)
    {
        Assert.True(DocsController.IsSafeAppRelativeUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("docs")]
    [InlineData("https://example.com/docs")]
    [InlineData("//example.com/docs")]
    [InlineData("/\\evil")]
    [InlineData("/docs\r\nLocation:%20https://example.com")]
    public void IsSafeAppRelativeUrl_WhenUrlCouldLeaveTheApp_ReturnsFalse(string? url)
    {
        Assert.False(DocsController.IsSafeAppRelativeUrl(url));
    }

    [Theory]
    [InlineData("/docs", null, "/docs")]
    [InlineData("/docs/search?q=api", null, "/docs")]
    [InlineData("/base/docs/packages/README.md.html", "/base", "/docs")]
    [InlineData("/search?q=api", null, "/")]
    public void IsSafeDocsHarvestReturnUrl_WhenUrlStaysInDocsAndAvoidsHarvestLoop_ReturnsTrue(
        string url,
        string? pathBase,
        string docsRootPath)
    {
        Assert.True(DocsController.IsSafeDocsHarvestReturnUrl(url, pathBase, docsRootPath));
    }

    [Theory]
    [InlineData("/admin", null, "/docs")]
    [InlineData("/base/admin", "/base", "/docs")]
    [InlineData("/docs/_harvest", null, "/docs")]
    [InlineData("/docs/_harvest/rebuild", null, "/docs")]
    [InlineData("/docs/_harvest/extra", null, "/docs")]
    [InlineData("/_harvest", null, "/")]
    public void IsSafeDocsHarvestReturnUrl_WhenUrlLeavesDocsOrLoopsToHarvest_ReturnsFalse(
        string url,
        string? pathBase,
        string docsRootPath)
    {
        Assert.False(DocsController.IsSafeDocsHarvestReturnUrl(url, pathBase, docsRootPath));
    }
}
