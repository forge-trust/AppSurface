namespace ForgeTrust.AppSurface.Config.Tests;

public class DefaultConfigFileLocationProviderTests
{
    [Fact]
    public void Directory_ReturnsAppContextBaseDirectory()
    {
        var provider = new DefaultConfigFileLocationProvider();

        Assert.Equal(AppContext.BaseDirectory, provider.Directory);
    }
}
