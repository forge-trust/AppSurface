namespace ForgeTrust.AppSurface.Core.Tests;

public sealed class AppSurfaceActivitySourcesTests
{
    [Fact]
    public void ActivitySourceName_IsCanonical()
    {
        Assert.Equal("ForgeTrust.AppSurface", AppSurfaceActivitySources.ActivitySourceName);
    }

    [Fact]
    public void ActivitySource_InstanceUsesCanonicalName()
    {
        var source = AppSurfaceActivitySources.Instance;

        Assert.Equal(AppSurfaceActivitySources.ActivitySourceName, source.Name);
    }

    [Fact]
    public void StandardActivitySourceNames_AreReadOnlyAndCanonical()
    {
        var sourceNames = Assert.IsAssignableFrom<IList<string>>(AppSurfaceActivitySources.StandardActivitySourceNames);

        Assert.True(sourceNames.IsReadOnly);
        Assert.Equal([AppSurfaceActivitySources.ActivitySourceName], sourceNames);

        Assert.Throws<NotSupportedException>(() => sourceNames[0] = "changed");
    }
}
