using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceRevisionPlanBuilderTests
{
    [Fact]
    public async Task VerifyAsync_ShouldRejectPlansWithoutRevisionBoundV2FieldsBeforeReadingGit()
    {
        var planner = new EvidencePlanner();
        var policy = new EvidencePolicy(
            "gate",
            "1",
            "targeted",
            [new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [], [])],
            []);
        var plans = new[]
        {
            CreatePlan(contractVersion: "1.0"),
            CreatePlan(baseRevision: null),
            CreatePlan(headRevision: null),
        };

        foreach (var plan in plans)
        {
            var exception = await Assert.ThrowsAsync<EvidencePlanningException>(() =>
                EvidenceRevisionPlanBuilder.VerifyAsync(planner, policy, string.Empty, plan));

            Assert.Equal("ASEVD139", exception.Code);
            Assert.Contains("not revision-bound v2 evidence", exception.Message, StringComparison.Ordinal);
        }
    }

    private static EvidencePlan CreatePlan(
        string contractVersion = "2.0",
        string? baseRevision = "base",
        string? headRevision = "head") =>
        new(
            ContractVersion: contractVersion,
            PolicyId: "gate",
            PolicyDigest: new string('a', 64),
            DiffDigest: new string('b', 64),
            Profile: new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, [], [], []),
            ChangedPaths: [new NormalizedDiffPath("src/Feature.cs")],
            MatchedRuleIds: [],
            PlanDigest: new string('c', 64),
            BaseRevision: baseRevision,
            HeadRevision: headRevision);
}
