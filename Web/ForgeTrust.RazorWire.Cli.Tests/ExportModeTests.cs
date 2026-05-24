namespace ForgeTrust.RazorWire.Cli.Tests;

public class ExportModeTests
{
    [Fact]
    public void ExportModeValues_Should_Remain_Stable_For_Public_Contract()
    {
        Assert.Equal(0, (int)ExportMode.Cdn);
        Assert.Equal(1, (int)ExportMode.Hybrid);
    }

    [Fact]
    public void ExportRedirectStrategyValues_Should_Remain_Stable_For_Public_Contract()
    {
        Assert.Equal(0, (int)ExportRedirectStrategy.Html);
        Assert.Equal(1, (int)ExportRedirectStrategy.Netlify);
    }
}
