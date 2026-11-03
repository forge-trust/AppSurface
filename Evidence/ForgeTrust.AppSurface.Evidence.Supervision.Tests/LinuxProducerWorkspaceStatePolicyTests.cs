using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision.Tests;

/// <summary>Direct state-gate checks; no workspace, owner, lease, handle or native authority is created.</summary>
public sealed class LinuxProducerWorkspaceStatePolicyTests
{
    [Fact]
    public void ProducerStateGatesAcceptAdjacentValidFacts()
    {
        var active = Facts();
        LinuxProducerWorkspaceStatePolicy.RequireRetainProducer(active);
        LinuxProducerWorkspaceStatePolicy.RequireOwnedBy(active);
        LinuxProducerWorkspaceStatePolicy.RequireControlOwnedBy(active);
        LinuxProducerWorkspaceStatePolicy.RequireResultsTokenPreparation(active);
        LinuxProducerWorkspaceStatePolicy.RequireSealedForAccountRelease(active with { Sealed = true });
    }

    [Theory]
    [InlineData("quarantined")]
    [InlineData("sealed")]
    [InlineData("seal")]
    [InlineData("retention")]
    public void RetainProducerRejectsEachStateField(string field) => AssertInvalid(() =>
        LinuxProducerWorkspaceStatePolicy.RequireRetainProducer(Change(field, Facts())));

    [Theory]
    [InlineData("quarantined")]
    [InlineData("sealed")]
    [InlineData("seal")]
    [InlineData("handles")]
    public void OwnedByRejectsEachStateField(string field) => AssertInvalid(() =>
        LinuxProducerWorkspaceStatePolicy.RequireOwnedBy(Change(field, Facts())));

    [Theory]
    [InlineData("handles")]
    [InlineData("slash")]
    [InlineData("run")]
    public void ControlOwnedByRejectsEachRequiredField(string field) => AssertInvalid(() =>
        LinuxProducerWorkspaceStatePolicy.RequireControlOwnedBy(Change(field, Facts())));

    [Fact]
    public void ResultsTokenPreparationRejectsOnlyASecondAttempt()
    {
        AssertInvalid(() => LinuxProducerWorkspaceStatePolicy.RequireResultsTokenPreparation(
            Facts() with { ResultsTokenAttempted = true }));
        LinuxProducerWorkspaceStatePolicy.RequireResultsTokenPreparation(Facts() with
        { ProducerRetentionAttempted = true, ResultsTokenAttempted = false });
    }

    [Theory]
    [InlineData("sealed")]
    [InlineData("quarantined")]
    [InlineData("handles")]
    public void SealedForReleaseRejectsEachRequiredField(string field) => AssertInvalid(() =>
        LinuxProducerWorkspaceStatePolicy.RequireSealedForAccountRelease(
            Change(field, Facts() with { Sealed = true })));

    private static LinuxProducerWorkspaceStateFacts Facts() => new(
        Quarantined: false, Sealed: false, HandlesClosed: false, SealAttempted: false,
        ProducerRetentionAttempted: false, ResultsTokenAttempted: false,
        SlashPresent: true, RunPresent: true);

    private static LinuxProducerWorkspaceStateFacts Change(string field, LinuxProducerWorkspaceStateFacts facts) => field switch
    {
        "quarantined" => facts with { Quarantined = true },
        "sealed" => facts with { Sealed = !facts.Sealed },
        "seal" => facts with { SealAttempted = true },
        "retention" => facts with { ProducerRetentionAttempted = true },
        "handles" => facts with { HandlesClosed = true },
        "slash" => facts with { SlashPresent = false },
        "run" => facts with { RunPresent = false },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(action);
        Assert.Equal("ASEVD402", error.Code);
        Assert.Null(error.InnerException);
    }
}
