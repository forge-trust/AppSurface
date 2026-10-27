using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageArtifactPlanningTests
{
    [Theory]
    [InlineData("managed")]
    [InlineData("full")]
    public void ValidRetentionPreservesCoverageRequirementsInThePlannedProducer(string retention)
    {
        var policy = Policy(retention);
        EvidencePlanner.ValidatePolicy(policy);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Feature.cs")]);
        var producer = Assert.Single(plan.Profile.Producers);
        Assert.Equal(retention, producer.CoverageArtifactRetention);
        Assert.Equal(95, producer.CoverageGate!.MinLinePercent);
        Assert.Equal(85, producer.CoverageGate.MinBranchPercent);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("MANAGED")]
    [InlineData("")]
    public void InvalidRetentionIsRejectedBeforeAnEvidencePlanCanBeExecuted(string retention)
    {
        var exception = Assert.Throws<EvidencePlanningException>(() => EvidencePlanner.ValidatePolicy(Policy(retention)));
        Assert.Equal("ASEVD132", exception.Code);
        Assert.Contains("coverageArtifactRetention", exception.Message, StringComparison.Ordinal);
    }

    private static EvidencePolicy Policy(string retention) => new(
        "sample", "1", "coverage",
        [new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [], ["coverage/assertion@1"], [], 60,
                new EvidenceCoverageGateRequirements(95, 85)) { CoverageArtifactRetention = retention }],
            [new EvidenceObligation("behavior", "behavior", "Changed behavior needs evidence.", ["coverage"], "coverage/assertion@1")])],
        []);
}
