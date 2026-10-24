using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Pure workspace state checks; no workspace, owner, handle, lock or native sample is created.</summary>
public sealed class LinuxApplicationWorkspaceStatePolicyTests
{
    [Fact]
    public void OperationControlsAcceptAdjacentValidStates()
    {
        LinuxApplicationWorkspaceStatePolicy.RequireBundleUse(ActiveFacts());
        LinuxApplicationWorkspaceStatePolicy.RequireRetainApplication(RetentionFacts());
        LinuxApplicationWorkspaceStatePolicy.RequireControlOwnedBy(ActiveFacts());
        LinuxApplicationWorkspaceStatePolicy.RequireSealCore(SealFacts());
        LinuxApplicationWorkspaceStatePolicy.RequireActive(ActiveFacts());
        LinuxApplicationWorkspaceStatePolicy.RequireSealedForRelease(SealedFacts());
        LinuxApplicationWorkspaceStatePolicy.RequireDispose(DisposedFacts());
    }

    [Fact]
    public void BundleUseRejectsARequiredStateAndIgnoresNeighbors()
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireBundleUse(
            ActiveFacts() with { Quarantined = true }));
        LinuxApplicationWorkspaceStatePolicy.RequireBundleUse(ActiveFacts() with
        {
            ApplicationRetentionAttempted = true, ScratchMounted = false, ReleaseGuardPassed = true,
        });
    }

    [Theory]
    [InlineData("initialized")]
    [InlineData("quarantined")]
    [InlineData("seal-attempted")]
    [InlineData("retention")]
    public void RetainApplicationRejectsEachRequiredState(string field)
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireRetainApplication(
            Change(field, RetentionFacts())));
    }

    [Fact]
    public void RetainApplicationIgnoresSealedAndClosedNeighbors()
    {
        LinuxApplicationWorkspaceStatePolicy.RequireRetainApplication(RetentionFacts() with
        {
            Sealed = true, HandlesClosed = true,
        });
    }

    [Theory]
    [InlineData("initialized")]
    [InlineData("quarantined")]
    [InlineData("sealed")]
    [InlineData("handles")]
    public void ControlOwnedByRejectsEachRequiredState(string field)
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireControlOwnedBy(
            Change(field, ActiveFacts())));
    }

    [Theory]
    [InlineData("initialized")]
    [InlineData("quarantined")]
    [InlineData("handles")]
    [InlineData("scratch")]
    public void SealCoreRejectsEachRequiredState(string field)
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireSealCore(
            Change(field, SealFacts())));
    }

    [Theory]
    [InlineData("quarantined")]
    [InlineData("seal-attempted")]
    [InlineData("sealed")]
    [InlineData("handles")]
    public void ActiveRejectsEachRequiredState(string field)
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireActive(
            Change(field, ActiveFacts())));
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("quarantined")]
    [InlineData("handles")]
    [InlineData("scratch")]
    public void SealedForReleaseRejectsEachRequiredState(string field)
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireSealedForRelease(
            Change(field, SealedFacts())));
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("quarantined")]
    [InlineData("release")]
    public void DisposeRejectsEachRequiredState(string field)
    {
        AssertInvalid(() => LinuxApplicationWorkspaceStatePolicy.RequireDispose(
            Change(field, DisposedFacts())));
    }

    private static LinuxApplicationWorkspaceStateFacts ActiveFacts() => new(
        Initialized: true, Quarantined: false, Sealed: false, HandlesClosed: false,
        SealAttempted: false, ApplicationRetentionAttempted: false, ScratchMounted: true,
        ReleaseGuardPassed: false);

    private static LinuxApplicationWorkspaceStateFacts RetentionFacts() => ActiveFacts();

    private static LinuxApplicationWorkspaceStateFacts SealFacts() => ActiveFacts();

    private static LinuxApplicationWorkspaceStateFacts SealedFacts() => new(
        Initialized: true, Quarantined: false, Sealed: true, HandlesClosed: false,
        SealAttempted: false, ApplicationRetentionAttempted: true, ScratchMounted: false,
        ReleaseGuardPassed: false);

    private static LinuxApplicationWorkspaceStateFacts DisposedFacts() => SealedFacts() with { ReleaseGuardPassed = true };

    private static LinuxApplicationWorkspaceStateFacts Change(string field, LinuxApplicationWorkspaceStateFacts facts) => field switch
    {
        "initialized" => facts with { Initialized = false },
        "quarantined" => facts with { Quarantined = true },
        "sealed" => facts with { Sealed = !facts.Sealed },
        "handles" => facts with { HandlesClosed = true },
        "seal-attempted" => facts with { SealAttempted = true },
        "retention" => facts with { ApplicationRetentionAttempted = true },
        "scratch" => facts with { ScratchMounted = !facts.ScratchMounted },
        "release" => facts with { ReleaseGuardPassed = false },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD410", error.Code);
        Assert.Null(error.InnerException);
    }
}
