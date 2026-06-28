namespace ForgeTrust.AppSurface.Web.Tests;

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

    [Theory]
    [InlineData(PwaDisplayMode.Browser, 0)]
    [InlineData(PwaDisplayMode.MinimalUi, 1)]
    [InlineData(PwaDisplayMode.Standalone, 2)]
    [InlineData(PwaDisplayMode.Fullscreen, 3)]
    public void PwaDisplayMode_NumericValues_AreStable(PwaDisplayMode value, int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(PwaDiagnosticEndpointExposure.DevelopmentOnly, 0)]
    [InlineData(PwaDiagnosticEndpointExposure.Always, 1)]
    [InlineData(PwaDiagnosticEndpointExposure.Never, 2)]
    public void PwaDiagnosticEndpointExposure_NumericValues_AreStable(
        PwaDiagnosticEndpointExposure value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
