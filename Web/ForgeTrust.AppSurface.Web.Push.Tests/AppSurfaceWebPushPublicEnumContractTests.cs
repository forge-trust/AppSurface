using ForgeTrust.AppSurface.Web.Push;

namespace ForgeTrust.AppSurface.Web.Push.Tests;

public sealed class AppSurfaceWebPushPublicEnumContractTests
{
    [Theory]
    [InlineData(AppSurfaceWebPushUrgency.VeryLow, 0)]
    [InlineData(AppSurfaceWebPushUrgency.Low, 1)]
    [InlineData(AppSurfaceWebPushUrgency.Normal, 2)]
    [InlineData(AppSurfaceWebPushUrgency.High, 3)]
    public void Urgency_NumericValues_AreStable(AppSurfaceWebPushUrgency value, int expected) =>
        Assert.Equal(expected, (int)value);

    [Theory]
    [InlineData(AppSurfaceWebPushSendOutcome.Accepted, 0)]
    [InlineData(AppSurfaceWebPushSendOutcome.TerminalSubscription, 1)]
    [InlineData(AppSurfaceWebPushSendOutcome.TransientFailure, 2)]
    [InlineData(AppSurfaceWebPushSendOutcome.Rejected, 3)]
    [InlineData(AppSurfaceWebPushSendOutcome.VapidKeyUnavailable, 4)]
    [InlineData(AppSurfaceWebPushSendOutcome.PushServiceNotAllowed, 5)]
    [InlineData(AppSurfaceWebPushSendOutcome.ProtocolFailure, 6)]
    public void SendOutcome_NumericValues_AreStable(AppSurfaceWebPushSendOutcome value, int expected) =>
        Assert.Equal(expected, (int)value);

    [Theory]
    [InlineData(AppSurfaceWebPushCleanupState.NotRequired, 0)]
    [InlineData(AppSurfaceWebPushCleanupState.Completed, 1)]
    [InlineData(AppSurfaceWebPushCleanupState.AlreadyTerminal, 2)]
    [InlineData(AppSurfaceWebPushCleanupState.Rejected, 3)]
    [InlineData(AppSurfaceWebPushCleanupState.Failed, 4)]
    public void CleanupState_NumericValues_AreStable(AppSurfaceWebPushCleanupState value, int expected) =>
        Assert.Equal(expected, (int)value);

    [Theory]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Created, 0)]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Updated, 1)]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Unchanged, 2)]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Conflict, 3)]
    [InlineData(AppSurfaceWebPushRegistrationDisposition.Rejected, 4)]
    public void RegistrationDisposition_NumericValues_AreStable(
        AppSurfaceWebPushRegistrationDisposition value,
        int expected) =>
        Assert.Equal(expected, (int)value);

    [Theory]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.Removed, 0)]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.NotFound, 1)]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.Conflict, 2)]
    [InlineData(AppSurfaceWebPushUnregistrationDisposition.Rejected, 3)]
    public void UnregistrationDisposition_NumericValues_AreStable(
        AppSurfaceWebPushUnregistrationDisposition value,
        int expected) =>
        Assert.Equal(expected, (int)value);

    [Theory]
    [InlineData(AppSurfaceWebPushTerminalDisposition.Completed, 0)]
    [InlineData(AppSurfaceWebPushTerminalDisposition.AlreadyTerminal, 1)]
    [InlineData(AppSurfaceWebPushTerminalDisposition.Rejected, 2)]
    public void TerminalDisposition_NumericValues_AreStable(
        AppSurfaceWebPushTerminalDisposition value,
        int expected) =>
        Assert.Equal(expected, (int)value);

    [Theory]
    [InlineData(AppSurfaceWebPushTerminalReason.NotFound, 0)]
    [InlineData(AppSurfaceWebPushTerminalReason.Gone, 1)]
    public void TerminalReason_NumericValues_AreStable(AppSurfaceWebPushTerminalReason value, int expected) =>
        Assert.Equal(expected, (int)value);
}
