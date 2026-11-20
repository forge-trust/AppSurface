using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Locks the Dogfood adapter's mode-specific validation of installed v3 CLI results.</summary>
public sealed class CoverageDogfoodResultValidationTests
{
    [Theory]
    [InlineData("accept", false)]
    [InlineData("accept", true)]
    [InlineData("verify", false)]
    [InlineData("verify", true)]
    public void Aggregate_only_accepts_current_results_without_patch_applicability(string operation, bool explicitNull)
    {
        using var root = TestDirectory.Create();
        var path = ResultFile(root, operation, accepted: operation == "accept", patchOutcome: "PatchNotRequested",
            includeApplicability: explicitNull, nullApplicability: explicitNull);

        CoverageDogfoodResultValidation.RequireV3AcceptedResult(path, operation, CoverageGateMode.AggregateOnly);
    }

    [Fact]
    public void Aggregate_only_requires_acceptance_on_accept()
    {
        using var root = TestDirectory.Create();
        var path = ResultFile(root, "accept", accepted: false, patchOutcome: "PatchNotRequested");

        Assert.Throws<InvalidDataException>(() => CoverageDogfoodResultValidation.RequireV3AcceptedResult(
            path, "accept", CoverageGateMode.AggregateOnly));
    }

    [Theory]
    [InlineData("PatchAccepted")]
    [InlineData("PatchNotRequested")]
    public void Aggregate_only_rejects_forged_patch_claims(string patchOutcome)
    {
        using var root = TestDirectory.Create();
        var path = ResultFile(root, "accept", accepted: true, patchOutcome: patchOutcome, includeApplicability: true);

        Assert.Throws<InvalidDataException>(() => CoverageDogfoodResultValidation.RequireV3AcceptedResult(
            path, "accept", CoverageGateMode.AggregateOnly));
    }

    [Fact]
    public void Aggregate_and_patch_accepts_only_patch_accepted_with_applicability()
    {
        using var root = TestDirectory.Create();
        var path = ResultFile(root, "accept", accepted: true, patchOutcome: "PatchAccepted",
            includeApplicability: true, validApplicability: true);

        CoverageDogfoodResultValidation.RequireV3AcceptedResult(path, "accept", CoverageGateMode.AggregateAndPatch);
    }

    [Theory]
    [InlineData("PatchNotRequested", true, false)]
    [InlineData("PatchAccepted", false, false)]
    [InlineData("PatchAccepted", true, true)]
    public void Aggregate_and_patch_rejects_missing_or_inconsistent_patch_claims(
        string patchOutcome, bool includeApplicability, bool nullApplicability)
    {
        using var root = TestDirectory.Create();
        var path = ResultFile(root, "accept", accepted: true, patchOutcome: patchOutcome,
            includeApplicability: includeApplicability, nullApplicability: nullApplicability);

        Assert.Throws<InvalidDataException>(() => CoverageDogfoodResultValidation.RequireV3AcceptedResult(
            path, "accept", CoverageGateMode.AggregateAndPatch));
    }

    private static string ResultFile(TestDirectory root, string operation, bool accepted, string patchOutcome,
        bool includeApplicability = false, bool nullApplicability = false, bool validApplicability = false)
    {
        var result = new JsonObject
        {
            ["outcome"] = "succeeded",
            ["verified"] = true,
            ["accepted"] = accepted,
            ["patchOutcome"] = patchOutcome,
        };
        if (includeApplicability)
            result["patchApplicability"] = nullApplicability ? null : validApplicability ? ValidApplicability() : new JsonObject { ["forged"] = true };

        var envelope = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["operation"] = operation,
            ["status"] = "succeeded",
            ["result"] = result,
        };
        return root.WriteFile("result.json", envelope.ToJsonString());
    }

    private static JsonObject ValidApplicability()
    {
        static JsonObject Metric() => new()
        {
            ["outcome"] = "measured",
            ["reason"] = "reported-points",
            ["changedLines"] = 1,
            ["measurable"] = 1,
            ["covered"] = 1,
            ["percent"] = 100,
        };
        static JsonObject MetricSet() => new() { ["line"] = Metric(), ["branch"] = Metric() };

        return new JsonObject { ["primary"] = MetricSet(), ["composed"] = MetricSet() };
    }
}
