namespace ForgeTrust.Runnable.Web.Tests;

public class PublicEnumContractTests
{
    [Theory]
    [InlineData(BrowserStatusPageMode.Auto, 0)]
    [InlineData(BrowserStatusPageMode.Enabled, 1)]
    [InlineData(BrowserStatusPageMode.Disabled, 2)]
    public void BrowserStatusPageMode_NumericValues_AreStable(
        BrowserStatusPageMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(MvcSupport.None, 0)]
    [InlineData(MvcSupport.Controllers, 1)]
    [InlineData(MvcSupport.ControllersWithViews, 2)]
    [InlineData(MvcSupport.Full, 3)]
    public void MvcSupport_NumericValues_AreStable(MvcSupport value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
