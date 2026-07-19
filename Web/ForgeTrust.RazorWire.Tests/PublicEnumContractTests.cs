using ForgeTrust.RazorWire.Auth;
using ForgeTrust.RazorWire.Bridge;

namespace ForgeTrust.RazorWire.Tests;

public class PublicEnumContractTests
{
    [Theory]
    [InlineData(RazorWireFormFailureMode.Auto, 0)]
    [InlineData(RazorWireFormFailureMode.Manual, 1)]
    [InlineData(RazorWireFormFailureMode.Off, 2)]
    public void RazorWireFormFailureMode_NumericValues_AreStable(
        RazorWireFormFailureMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(RazorWireStreamAuthorizationMode.DenyAll, 0)]
    [InlineData(RazorWireStreamAuthorizationMode.AllowAll, 1)]
    public void RazorWireStreamAuthorizationMode_NumericValues_AreStable(
        RazorWireStreamAuthorizationMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(RazorWireTurboRuntimeMode.Bundled, 0)]
    [InlineData(RazorWireTurboRuntimeMode.Custom, 1)]
    [InlineData(RazorWireTurboRuntimeMode.HostManaged, 2)]
    public void RazorWireTurboRuntimeMode_NumericValues_AreStable(
        RazorWireTurboRuntimeMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(RazorWireHybridCredentialsMode.Auto, 0)]
    [InlineData(RazorWireHybridCredentialsMode.Include, 1)]
    [InlineData(RazorWireHybridCredentialsMode.Omit, 2)]
    public void RazorWireHybridCredentialsMode_NumericValues_AreStable(
        RazorWireHybridCredentialsMode value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(RazorWireVisitAction.Advance, 0)]
    [InlineData(RazorWireVisitAction.Replace, 1)]
    public void RazorWireVisitAction_NumericValues_AreStable(
        RazorWireVisitAction value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }

    [Theory]
    [InlineData(RazorWireAuthProjectionState.Unknown, 0)]
    [InlineData(RazorWireAuthProjectionState.Allowed, 1)]
    [InlineData(RazorWireAuthProjectionState.Anonymous, 2)]
    [InlineData(RazorWireAuthProjectionState.Forbidden, 3)]
    [InlineData(RazorWireAuthProjectionState.SetupFailure, 4)]
    [InlineData(RazorWireAuthProjectionState.UnsafeNavigation, 5)]
    [InlineData(RazorWireAuthProjectionState.StaleOrUnknownSession, 6)]
    public void RazorWireAuthProjectionState_NumericValues_AreStable(
        RazorWireAuthProjectionState value,
        int expected)
    {
        Assert.Equal(expected, (int)value);
    }
}
