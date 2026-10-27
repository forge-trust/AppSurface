using ForgeTrust.AppSurface.Evidence.Supervision;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Ordinary data-only state-policy controls; these create no account holder or native evidence.</summary>
public sealed class LinuxAccountMeasurementPolicyTests
{
    [Fact]
    public void SuccessfulCreationStateAcceptsOnlyTheOriginalSettledThreeUtilityShape()
    {
        Assert.True(SupervisionAccountStatePolicy.IsSuccessfulCreationState(CreatedState));

        foreach (var invalid in new[]
        {
            CreatedState with { Utilities = 2 },
            CreatedState with { Utilities = 4 },
            CreatedState with { SettledUtilities = 2 },
            CreatedState with { FailedUtilities = 1 },
            CreatedState with { CloseCompleted = true },
            CreatedState with { Quarantined = true },
            CreatedState with { ReservationFailed = true },
            CreatedState with { IdentitiesPublished = false },
            CreatedState with { FirstFailurePresent = true },
        }) Assert.False(SupervisionAccountStatePolicy.IsSuccessfulCreationState(invalid));
    }

    [Fact]
    public void SuccessfulClosedStateAcceptsBothOriginalSixAndEightUtilityNeighbors()
    {
        Assert.True(SupervisionAccountStatePolicy.IsSuccessfulClosedState(ClosedState(6)));
        Assert.True(SupervisionAccountStatePolicy.IsSuccessfulClosedState(ClosedState(8)));
    }

    [Fact]
    public void SuccessfulClosedStateRejectsEachGuardedStateMutationAndAdjacentCount()
    {
        var closed = ClosedState(7);
        Assert.True(SupervisionAccountStatePolicy.IsSuccessfulClosedState(closed));
        foreach (var invalid in new[]
        {
            closed with { CloseCompleted = false },
            closed with { Quarantined = true },
            closed with { ReservationFailed = true },
            closed with { FirstFailurePresent = true },
            closed with { FailedUtilities = 1 },
            closed with { SettledUtilities = 6 },
            ClosedState(5),
            ClosedState(9),
        }) Assert.False(SupervisionAccountStatePolicy.IsSuccessfulClosedState(invalid));
    }

    private static SupervisionAccountStatePolicy.Snapshot CreatedState =>
        new(3, 3, 0, false, false, false, true, false);

    private static SupervisionAccountStatePolicy.Snapshot ClosedState(int utilities) =>
        new(utilities, utilities, 0, true, false, false, true, false);
}
