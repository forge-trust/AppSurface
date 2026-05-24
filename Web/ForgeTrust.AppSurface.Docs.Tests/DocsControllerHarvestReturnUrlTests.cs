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
}
