using ForgeTrust.AppSurface.Intelligence;

namespace ForgeTrust.AppSurface.Intelligence.Tests;

public sealed class AppSurfaceProductIntelligencePublicEnumContractTests
{
    [Fact]
    public void AppSurfaceProductEventPropertyContract_LegacyConstructorSignature_IsPreserved()
    {
        var constructor = typeof(AppSurfaceProductEventPropertyContract).GetConstructor(
            [
                typeof(string),
                typeof(string),
                typeof(AppSurfaceProductEventSensitivity),
                typeof(AppSurfaceProductEventCardinality),
                typeof(bool),
                typeof(IEnumerable<string>),
                typeof(int)
            ]);

        Assert.NotNull(constructor);
    }

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

    [Theory]
    [InlineData(AppSurfaceProductEventValueShape.Token, 0)]
    [InlineData(AppSurfaceProductEventValueShape.BoundedText, 1)]
    [InlineData(AppSurfaceProductEventValueShape.NonNegativeInteger, 2)]
    [InlineData(AppSurfaceProductEventValueShape.Boolean, 3)]
    [InlineData(AppSurfaceProductEventValueShape.AllowedValue, 4)]
    public void AppSurfaceProductEventValueShape_NumericValues_AreStable(
        AppSurfaceProductEventValueShape value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(AppSurfaceProductEventValidationFailureReason.EventNotRegistered, 0)]
    [InlineData(AppSurfaceProductEventValidationFailureReason.PropertyNotRegistered, 1)]
    [InlineData(AppSurfaceProductEventValidationFailureReason.ForbiddenPropertyName, 2)]
    [InlineData(AppSurfaceProductEventValidationFailureReason.InvalidPropertyValue, 3)]
    [InlineData(AppSurfaceProductEventValidationFailureReason.RequiredPropertyMissing, 4)]
    [InlineData(AppSurfaceProductEventValidationFailureReason.ExperimentalEventNotEnabled, 5)]
    public void AppSurfaceProductEventValidationFailureReason_NumericValues_AreStable(
        AppSurfaceProductEventValidationFailureReason value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
