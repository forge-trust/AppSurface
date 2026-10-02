using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceCommandModeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("production-canary", false)]
    [InlineData("trusted", true)]
    public void MissingUnknownOrConflictingModeRejectsWithoutEcho(string? mode, bool legacy)
    {
        var error = Assert.Throws<EvidenceAdmissionException>(() => EvidenceModeSelection.Select(mode, legacy));
        Assert.Equal("ASEVD401", error.Code);
        Assert.DoesNotContain("production-canary", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trusted", false, EvidenceExecutionMode.Trusted)]
    [InlineData("TRUSTED", false, EvidenceExecutionMode.Trusted)]
    [InlineData("observation", false, EvidenceExecutionMode.Observation)]
    [InlineData("observation", true, EvidenceExecutionMode.Observation)]
    [InlineData(null, true, EvidenceExecutionMode.Observation)]
    public void ExplicitModeOrLegacyTrueHasOnlyItsDeclaredMeaning(string? mode, bool legacy, EvidenceExecutionMode expected) =>
        Assert.Equal(expected, EvidenceModeSelection.Select(mode, legacy));
}
