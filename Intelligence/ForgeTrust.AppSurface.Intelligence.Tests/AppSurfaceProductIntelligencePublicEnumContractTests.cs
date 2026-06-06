using ForgeTrust.AppSurface.Intelligence;

namespace ForgeTrust.AppSurface.Intelligence.Tests;

public sealed class AppSurfaceProductIntelligencePublicEnumContractTests
{
    [Theory]
    [InlineData(AppSurfaceProductEventLifecycle.Experimental, 0)]
    [InlineData(AppSurfaceProductEventLifecycle.Recommended, 1)]
    [InlineData(AppSurfaceProductEventLifecycle.Stable, 2)]
    [InlineData(AppSurfaceProductEventLifecycle.Deprecated, 3)]
    public void AppSurfaceProductEventLifecycle_NumericValues_AreStable(
        AppSurfaceProductEventLifecycle value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AppSurfaceProductEventCardinality.Low, 0)]
    [InlineData(AppSurfaceProductEventCardinality.Medium, 1)]
    [InlineData(AppSurfaceProductEventCardinality.High, 2)]
    public void AppSurfaceProductEventCardinality_NumericValues_AreStable(
        AppSurfaceProductEventCardinality value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AppSurfaceProductEventSensitivity.Operational, 0)]
    [InlineData(AppSurfaceProductEventSensitivity.Behavioral, 1)]
    [InlineData(AppSurfaceProductEventSensitivity.Sensitive, 2)]
    public void AppSurfaceProductEventSensitivity_NumericValues_AreStable(
        AppSurfaceProductEventSensitivity value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
