using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInApplicabilityTests
{
    [Fact]
    public async Task V2ManifestOnlyDiffRefusesWhenPrimaryAndComposedHaveNoChangedLineMeasurements()
    {
        // Hand-authored v3 oracle for this one-line Git diff: all four primary/composed
        // line/branch metrics have changed=1, measurable=0, covered=0, percent=null.
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectManifestOnlyPatchAsync();
        await fixture.CapturePrimaryAsync(Report(aggregate: true));
        await fixture.ComposeAsync([]);
        var diff = await fixture.ReadDiffAsync();
        Assert.Single(AddedLines(diff));
        Assert.Contains("+enabled=true", diff, StringComparison.Ordinal);

        var primary = await fixture.EvaluatePatchAsync(
            Path.Join(fixture.PrimaryCapture, "coverage.cobertura.xml"), diff);
        var composedPrimary = await fixture.EvaluatePatchAsync(
            Path.Join(fixture.Composed, "primary-paths.cobertura.xml"), diff);
        var composed = await fixture.EvaluatePatchAsync(
            Path.Join(fixture.Composed, "coverage.cobertura.xml"), diff);
        AssertV2EmptyPatchMetric(primary, changedLines: 1);
        AssertV2EmptyPatchMetric(composedPrimary, changedLines: 1);
        AssertV2EmptyPatchMetric(composed, changedLines: 1);

        var result = await fixture.AcceptAsync([], diff);

        AssertV2PositivePrimaryLineRefusal(result);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Fact]
    public async Task V2SyntheticNativeCaptureCannotReplaceTheRequiredPrimaryChangedLineMeasurement()
    {
        // Hand-authored v3 oracle: primary line/branch are N/A; composed line is 1/1 and
        // branch is 2/2. Primary aggregate points remain valid and nonzero.
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        // The label and report are synthetic; they do not claim a Linux execution occurred.
        var supplement = new CoverageSupplement("native-synthetic", Fixture.CurrentPlatform, "Category=applicability");
        fixture.SetSupplements([supplement]);
        await fixture.CapturePrimaryAsync(Report(aggregate: true));
        await fixture.CaptureSupplementAsync(supplement, Report(changedSource: true));
        await fixture.ComposeAsync([fixture.NativeCapture]);
        var diff = await fixture.ReadDiffAsync();
        Assert.Single(AddedLines(diff));
        Assert.Contains("+++ b/src/Example.cs", diff, StringComparison.Ordinal);

        var primary = await fixture.EvaluatePatchAsync(
            Path.Join(fixture.PrimaryCapture, "coverage.cobertura.xml"), diff);
        var composed = await fixture.EvaluatePatchAsync(
            Path.Join(fixture.Composed, "coverage.cobertura.xml"), diff);
        AssertV2EmptyPatchMetric(primary, changedLines: 1);
        Assert.Equal(1, composed.PatchLineCoverage!.MeasurableLines);
        Assert.Equal(1, composed.PatchLineCoverage.CoveredLines);
        Assert.Equal(100m, composed.PatchLineCoverage.Percent);
        Assert.Equal(2, composed.PatchBranchCoverage!.MeasurableBranches);
        Assert.Equal(2, composed.PatchBranchCoverage.CoveredBranches);
        Assert.Equal(100m, composed.PatchBranchCoverage.Percent);
        Assert.True(composed.Passed);

        var result = await fixture.AcceptAsync([fixture.NativeCapture], diff);

        AssertV2PositivePrimaryLineRefusal(result);
        Assert.False(Directory.Exists(fixture.Accepted));
    }

    [Fact]
    public async Task V2BranchlessChangedLinePassesWithTheExistingZeroBranchHundredPercentConvention()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        await fixture.CapturePrimaryAsync(Report(aggregate: true, changedSource: true, branchlessChangedLine: true));
        await fixture.ComposeAsync([]);
        var diff = await fixture.ReadDiffAsync();

        var primary = await fixture.EvaluatePatchAsync(
            Path.Join(fixture.PrimaryCapture, "coverage.cobertura.xml"), diff);
        Assert.Equal(1, primary.PatchLineCoverage!.MeasurableLines);
        Assert.Equal(1, primary.PatchLineCoverage.CoveredLines);
        Assert.Equal(100m, primary.PatchLineCoverage.Percent);
        Assert.Equal(0, primary.PatchBranchCoverage!.MeasurableBranches);
        Assert.Equal(0, primary.PatchBranchCoverage.CoveredBranches);
        Assert.Equal(100m, primary.PatchBranchCoverage.Percent);

        var result = await fixture.AcceptAsync([], diff);

        Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal("PatchAccepted", result.PatchOutcome);
        using (var primaryGate = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "primary-gate.json"))))
            AssertV2BranchlessMetrics(primaryGate.RootElement);
        using (var composedGate = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "composed-gate.json"))))
            AssertV2BranchlessMetrics(composedGate.RootElement);

        var beforeVerify = Snapshot(fixture.Accepted);
        var verified = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest([]));
        Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
        Assert.True(verified.Verified);
        Assert.False(verified.Accepted);
        Assert.False(verified.Effects.OutputCreated);
        Assert.False(verified.Effects.GenerationCommitted);
        Assert.Equal(beforeVerify, Snapshot(fixture.Accepted));
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public async Task V3ManifestOnlyDiffAcceptsFourNotApplicableMetricsAndVerifiesReadOnly(string objectFormat)
    {
        // Applicability describes admitted report measurements; silent omissions can also yield N/A or a mixed pass.
        using var fixture = await Fixture.CreateAsync(objectFormat);
        await fixture.SelectManifestOnlyPatchAsync();
        fixture.UseV3Contract();
        await fixture.CapturePrimaryAsync(Report(aggregate: true));
        await fixture.ComposeAsync([]);
        var input = await fixture.ReadV3DiffAsync();
        var parsed = CoverageFanInDiffParser.Parse(input);
        Assert.Single(parsed);
        Assert.Single(parsed[0].AddedRanges);
        Assert.Single(AddedLines(Encoding.UTF8.GetString(input.Patch)));

        var result = await fixture.AcceptV3Async([], input);

        AssertV3Accepted(result);
        var applicability = Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability);
        AssertV3NotApplicable(applicability.Primary.Line, changedLines: 1);
        AssertV3NotApplicable(applicability.Primary.Branch, changedLines: 1);
        AssertV3NotApplicable(applicability.Composed.Line, changedLines: 1);
        AssertV3NotApplicable(applicability.Composed.Branch, changedLines: 1);
        AssertValidNonzeroPrimaryAggregate(fixture);
        await AssertV3ReadOnlyVerifyAsync(fixture, [], result);
    }

    [Fact]
    public async Task V3SyntheticNativeCaptureAddsComposedMeasurementsWhilePrimaryRemainsNotApplicable()
    {
        // Synthetic Linux labeling proves report semantics only; it makes no native-execution claim.
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        fixture.UseV3Contract();
        var supplement = new CoverageSupplement("native-synthetic", Fixture.CurrentPlatform, "Category=applicability");
        fixture.SetSupplements([supplement]);
        await fixture.CapturePrimaryAsync(Report(aggregate: true));
        await fixture.CaptureSupplementAsync(supplement, Report(changedSource: true));
        await fixture.ComposeAsync([fixture.NativeCapture]);
        var input = await fixture.ReadV3DiffAsync();
        Assert.Single(CoverageFanInDiffParser.Parse(input).Single().AddedRanges);

        var result = await fixture.AcceptV3Async([fixture.NativeCapture], input);

        AssertV3Accepted(result);
        var applicability = Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability);
        AssertV3NotApplicable(applicability.Primary.Line, changedLines: 1);
        AssertV3NotApplicable(applicability.Primary.Branch, changedLines: 1);
        AssertV3Measured(applicability.Composed.Line, changedLines: 1, measurable: 1, covered: 1, percent: 100m);
        AssertV3Measured(applicability.Composed.Branch, changedLines: 1, measurable: 2, covered: 2, percent: 100m);
        AssertValidNonzeroPrimaryAggregate(fixture);
        await AssertV3ReadOnlyVerifyAsync(fixture, [fixture.NativeCapture], result);
    }

    [Fact]
    public async Task V3BranchlessChangedLineIsMeasuredWhileBranchesAreNotApplicable()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        fixture.UseV3Contract();
        await fixture.CapturePrimaryAsync(Report(aggregate: true, changedSource: true, branchlessChangedLine: true));
        await fixture.ComposeAsync([]);
        var input = await fixture.ReadV3DiffAsync();

        var result = await fixture.AcceptV3Async([], input);

        AssertV3Accepted(result);
        var applicability = Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability);
        AssertV3Measured(applicability.Primary.Line, changedLines: 1, measurable: 1, covered: 1, percent: 100m);
        AssertV3NotApplicable(applicability.Primary.Branch, changedLines: 1);
        AssertV3Measured(applicability.Composed.Line, changedLines: 1, measurable: 1, covered: 1, percent: 100m);
        AssertV3NotApplicable(applicability.Composed.Branch, changedLines: 1);
        await AssertV3ReadOnlyVerifyAsync(fixture, [], result);
    }

    [Theory]
    [InlineData("empty", true, "", "", "")]
    [InlineData("delete", false, "deleted", "src/Example.cs", "")]
    [InlineData("rename", false, "renamed", "src/Example.cs", "docs/Example.cs")]
    [InlineData("empty-file", false, "added", null, "docs/Empty.cs")]
    public async Task V3NoAddedLinesAndExactEmptyDiffShapesAreAcceptedAsNotApplicable(
        string shape, bool empty, string kind, string? oldPath, string newPath)
    {
        using var fixture = await Fixture.CreateAsync();
        switch (shape)
        {
            case "empty": fixture.UseV3Contract(diffBase: fixture.Policy.Source.Commit); break;
            case "delete": await fixture.SelectDeletePatchAsync(); fixture.UseV3Contract(); break;
            case "rename": await fixture.SelectRenamePatchAsync(); fixture.UseV3Contract(); break;
            case "empty-file": await fixture.SelectEmptyFileAdditionPatchAsync(); fixture.UseV3Contract(); break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
        await fixture.CapturePrimaryAsync(Report(aggregate: true));
        await fixture.ComposeAsync([]);
        var input = await fixture.ReadV3DiffAsync();

        var result = await fixture.AcceptV3Async([], input);

        AssertV3Accepted(result);
        var applicability = Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability);
        AssertV3NotApplicable(applicability.Primary.Line, changedLines: 0);
        AssertV3NotApplicable(applicability.Primary.Branch, changedLines: 0);
        AssertV3NotApplicable(applicability.Composed.Line, changedLines: 0);
        AssertV3NotApplicable(applicability.Composed.Branch, changedLines: 0);
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "diff-metadata.json")));
        Assert.Equal(empty, metadata.RootElement.GetProperty("empty").GetBoolean());
        var changes = metadata.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        if (empty) Assert.Empty(changes);
        else
        {
            var change = Assert.Single(changes);
            Assert.Equal(kind, change.GetProperty("kind").GetString());
            Assert.Equal(oldPath, change.GetProperty("oldPath").ValueKind == JsonValueKind.Null
                ? null : change.GetProperty("oldPath").GetString());
            Assert.Equal(newPath.Length == 0 ? null : newPath, change.GetProperty("newPath").ValueKind == JsonValueKind.Null
                ? null : change.GetProperty("newPath").GetString());
            Assert.Empty(change.GetProperty("addedRanges").EnumerateArray());
        }
        await AssertV3ReadOnlyVerifyAsync(fixture, [], result);
    }

    [Fact]
    public async Task V3ReadOnlyVerifyRejectsFullyRehashedForgedNotApplicableMetric()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        fixture.UseV3Contract();
        await fixture.CapturePrimaryAsync(Report(aggregate: true, changedSource: true, branchlessChangedLine: true));
        await fixture.ComposeAsync([]);
        var input = await fixture.ReadV3DiffAsync();
        var accepted = await fixture.AcceptV3Async([], input);
        AssertV3Accepted(accepted);
        await fixture.ForgeComposedLineAsNotApplicableAndRepublishAsync();
        var beforeVerify = Snapshot(fixture.Accepted);

        var result = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest([]));

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV302", diagnostic.Code);
        Assert.Equal("applicability-mismatch", diagnostic.Cause);
        Assert.Equal(beforeVerify, Snapshot(fixture.Accepted));
    }

    [Fact]
    public async Task V3UncoveredReportedPointIsMeasuredAndRetainedAsNumericGateRejection()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        fixture.UseV3Contract();
        await fixture.CapturePrimaryAsync(Report(aggregate: true, changedSource: true,
            changedSourceHit: false, aggregateClasses: 20));
        await fixture.ComposeAsync([]);
        var input = await fixture.ReadV3DiffAsync();

        var result = await fixture.AcceptV3Async([], input);

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal("NotEvaluated", result.PatchOutcome);
        Assert.Equal(CoveragePatchApplicabilityOutcome.Measured, result.PatchApplicability!.Composed.Line.Outcome);
        AssertV3Measured(result.PatchApplicability.Primary.Line, changedLines: 1, measurable: 1, covered: 0, percent: 0m);
        AssertV3Measured(result.PatchApplicability.Primary.Branch, changedLines: 1, measurable: 2, covered: 0, percent: 0m);
        AssertV3Measured(result.PatchApplicability.Composed.Line, changedLines: 1, measurable: 1, covered: 0, percent: 0m);
        AssertV3Measured(result.PatchApplicability.Composed.Branch, changedLines: 1, measurable: 2, covered: 0, percent: 0m);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV307", diagnostic.Code);
        Assert.Equal("threshold-failed", diagnostic.Cause);
        Assert.True(result.Effects.GenerationCommitted);
        Assert.True(result.Effects.CleanupComplete);
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
        using (var gate = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "composed-gate.json"))))
        {
            Assert.False(gate.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(0, gate.RootElement.GetProperty("patchLine").GetProperty("covered").GetInt32());
            Assert.Equal(1, gate.RootElement.GetProperty("patchLine").GetProperty("measurable").GetInt32());
            Assert.Equal(0, gate.RootElement.GetProperty("patchLine").GetProperty("percent").GetDecimal());
        }

        var beforeVerify = Snapshot(fixture.Accepted);
        var verify = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest([]));

        Assert.Equal(CoverageOperationOutcome.Rejected, verify.Outcome);
        Assert.False(verify.Verified);
        Assert.Contains(verify.Diagnostics, item => item.Code == "ASCOV302");
        Assert.False(verify.Effects.OutputCreated);
        Assert.False(verify.Effects.GenerationCommitted);
        Assert.Equal(beforeVerify, Snapshot(fixture.Accepted));
    }

    [Fact]
    public async Task V3DetectedPathCaseAliasReturnsTypedUnverifiedAndPublishesSafeRejection()
    {
        using var fixture = await Fixture.CreateAsync();
        var input = await fixture.PreparePathCaseAliasAsync();

        var result = await fixture.AcceptV3Async([], input);

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal("NotEvaluated", result.PatchOutcome);
        AssertV3Unverified(Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV302", diagnostic.Code);
        Assert.Equal("report-invalid", diagnostic.Cause);
        Assert.True(result.Effects.GenerationCommitted);
        Assert.True(result.Effects.CleanupComplete);
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "generation.json")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "patch-applicability.json")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
        var primaryGatePath = Path.Join(fixture.Accepted, "primary-gate.json");
        var composedGatePath = Path.Join(fixture.Accepted, "composed-gate.json");
        Assert.True(File.Exists(primaryGatePath));
        Assert.True(File.Exists(composedGatePath));
        using (var rejection = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "rejection.json"))))
        {
            Assert.Equal(Hash(File.ReadAllBytes(primaryGatePath)), rejection.RootElement.GetProperty("primaryGateSha256").GetString());
            Assert.Equal(Hash(File.ReadAllBytes(composedGatePath)), rejection.RootElement.GetProperty("composedGateSha256").GetString());
        }
        using (var claim = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "patch-applicability.json"))))
            AssertUnverifiedClaim(claim.RootElement);

        var beforeVerify = Snapshot(fixture.Accepted);
        var verify = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest([]));

        Assert.Equal(CoverageOperationOutcome.Rejected, verify.Outcome);
        Assert.False(verify.Verified);
        Assert.Contains(verify.Diagnostics, item => item.Code == "ASCOV302");
        Assert.Equal(beforeVerify, Snapshot(fixture.Accepted));
    }

    [Fact]
    public async Task V3DetectedPathCaseAliasWithForeignOutputReturnsTypedResultWithoutReplacingIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var input = await fixture.PreparePathCaseAliasAsync();
        Directory.CreateDirectory(fixture.Accepted);
        var foreign = fixture.Temp.WriteFile("accepted/foreign.txt", "preserve caller-owned output\n");
        var before = Snapshot(fixture.Accepted);

        var result = await fixture.AcceptV3Async([], input);

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        AssertV3Unverified(Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability));
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.Equal(before, Snapshot(fixture.Accepted));
        Assert.Equal("preserve caller-owned output\n", File.ReadAllText(foreign));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "generation.json")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public async Task V3ExcludedOrSilentlyOmittedChangedSourceMayBeNAAcrossZeroAndMixedPatches(
        bool hasOtherReportedAddition, int changedLines)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectExcludedPatchAsync(hasOtherReportedAddition);
        fixture.UseV3Contract();
        await fixture.CapturePrimaryAsync(Report(aggregate: true, aggregateAddedLine: hasOtherReportedAddition));
        await fixture.ComposeAsync([]);
        var input = await fixture.ReadV3DiffAsync();

        var result = await fixture.AcceptV3Async([], input);

        AssertV3Accepted(result);
        Assert.Equal(3, fixture.Policy.ContractVersion);
        Assert.Equal(CoveragePolicyEncoding.InitialReportProfileId, fixture.Policy.ReportProfileId);
        var applicability = Assert.IsType<CoveragePatchApplicability>(result.PatchApplicability);
        if (!hasOtherReportedAddition)
        {
            AssertV3NotApplicable(applicability.Primary.Line, changedLines);
            AssertV3NotApplicable(applicability.Primary.Branch, changedLines);
            AssertV3NotApplicable(applicability.Composed.Line, changedLines);
            AssertV3NotApplicable(applicability.Composed.Branch, changedLines);
        }
        else
        {
            AssertV3Measured(applicability.Primary.Line, changedLines, measurable: 1, covered: 1, percent: 100m);
            AssertV3NotApplicable(applicability.Primary.Branch, changedLines);
            AssertV3Measured(applicability.Composed.Line, changedLines, measurable: 1, covered: 1, percent: 100m);
            AssertV3NotApplicable(applicability.Composed.Branch, changedLines);
        }
        AssertValidNonzeroPrimaryAggregate(fixture);
        await AssertV3ReadOnlyVerifyAsync(fixture, [], result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ContractVersionPreservesItsAcceptanceToNumericRejectionRetentionPurpose(int contractVersion)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SelectSourcePatchAsync();
        if (contractVersion == 3) fixture.UseV3Contract();
        await fixture.CapturePrimaryAsync(Report(aggregate: true, changedSource: true));
        await fixture.ComposeAsync([]);
        string? v2Diff = null;
        CoverageFanInDiffInput? v3Input = null;
        if (contractVersion == 2) v2Diff = await fixture.ReadDiffAsync();
        else v3Input = await fixture.ReadV3DiffAsync();

        var accepted = contractVersion == 2
            ? await fixture.AcceptAsync([], v2Diff!)
            : await fixture.AcceptV3Async([], v3Input!);
        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Accepted);
        var beforeReplacement = Snapshot(fixture.Accepted);

        await fixture.CapturePrimaryAsync(Report(aggregate: true, changedSource: true,
            changedSourceHit: false, aggregateClasses: 20), CoverageOutputMode.ReplaceOwned);
        await fixture.ComposeAsync([], CoverageOutputMode.ReplaceOwned);
        var rejected = contractVersion == 2
            ? await fixture.AcceptAsync([], v2Diff!, CoverageOutputMode.ReplaceOwned)
            : await fixture.AcceptV3Async([], v3Input!, CoverageOutputMode.ReplaceOwned);

        Assert.Equal(CoverageOperationOutcome.Rejected, rejected.Outcome);
        Assert.False(rejected.Accepted);
        Assert.False(rejected.Verified);
        Assert.True(rejected.PatchApplicability is null == (contractVersion == 2));
        if (contractVersion == 2)
        {
            Assert.True(rejected.Effects.GenerationCommitted);
            Assert.True(File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
            Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
            using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "generation.json")));
            Assert.Equal("rejected-gates", marker.RootElement.GetProperty("stage").GetString());
        }
        else
        {
            AssertV3Measured(rejected.PatchApplicability!.Composed.Line,
                changedLines: 1, measurable: 1, covered: 0, percent: 0m);
            Assert.False(rejected.Effects.GenerationCommitted);
            Assert.Equal(beforeReplacement, Snapshot(fixture.Accepted));
            Assert.True(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
            Assert.False(File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
        }
    }

    private static void AssertV3Accepted(CoverageFanInResult result)
    {
        Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Accepted);
        Assert.True(result.Verified);
        Assert.Equal("PatchAccepted", result.PatchOutcome);
        Assert.NotNull(result.PatchApplicability);
    }

    private static async Task AssertV3ReadOnlyVerifyAsync(Fixture fixture, IReadOnlyList<string> supplements, CoverageFanInResult accepted)
    {
        var beforeVerify = Snapshot(fixture.Accepted);
        var verified = await CoverageFanIn.VerifyAsync(fixture.VerifyRequest(supplements));
        Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
        Assert.True(verified.Verified);
        Assert.False(verified.Accepted);
        Assert.True(verified.PatchApplicability is not null);
        Assert.Equal(accepted.PatchApplicability, verified.PatchApplicability);
        Assert.False(verified.Effects.OutputCreated);
        Assert.False(verified.Effects.GenerationCommitted);
        Assert.Equal(beforeVerify, Snapshot(fixture.Accepted));
    }

    private static void AssertV3NotApplicable(CoveragePatchMetricResult metric, int changedLines)
    {
        Assert.Equal(CoveragePatchApplicabilityOutcome.NotApplicable, metric.Outcome);
        Assert.Equal("no-reported-patch-points", metric.Reason);
        Assert.Equal(changedLines, metric.ChangedLines);
        Assert.Equal(0, metric.Measurable);
        Assert.Equal(0, metric.Covered);
        Assert.Null(metric.Percent);
    }

    private static void AssertV3Measured(CoveragePatchMetricResult metric, int changedLines,
        int measurable, int covered, decimal percent)
    {
        Assert.Equal(CoveragePatchApplicabilityOutcome.Measured, metric.Outcome);
        Assert.Equal("reported-points", metric.Reason);
        Assert.Equal(changedLines, metric.ChangedLines);
        Assert.Equal(measurable, metric.Measurable);
        Assert.Equal(covered, metric.Covered);
        Assert.Equal(percent, metric.Percent);
    }

    private static void AssertV3Unverified(CoveragePatchApplicability applicability)
    {
        AssertV3Unverified(applicability.Primary.Line);
        AssertV3Unverified(applicability.Primary.Branch);
        AssertV3Unverified(applicability.Composed.Line);
        AssertV3Unverified(applicability.Composed.Branch);
    }

    private static void AssertV3Unverified(CoveragePatchMetricResult metric)
    {
        Assert.Equal(CoveragePatchApplicabilityOutcome.Unverified, metric.Outcome);
        Assert.Equal("invalid-evidence", metric.Reason);
        Assert.Null(metric.ChangedLines);
        Assert.Null(metric.Measurable);
        Assert.Null(metric.Covered);
        Assert.Null(metric.Percent);
    }

    private static void AssertUnverifiedClaim(JsonElement claim)
    {
        foreach (var stage in new[] { "primary", "composed" })
        {
            var metrics = claim.GetProperty(stage);
            foreach (var metric in new[] { metrics.GetProperty("line"), metrics.GetProperty("branch") })
            {
                Assert.Equal("unverified", metric.GetProperty("outcome").GetString());
                Assert.Equal("invalid-evidence", metric.GetProperty("reason").GetString());
                Assert.Equal(JsonValueKind.Null, metric.GetProperty("changedLines").ValueKind);
                Assert.Equal(JsonValueKind.Null, metric.GetProperty("measurable").ValueKind);
                Assert.Equal(JsonValueKind.Null, metric.GetProperty("covered").ValueKind);
                Assert.Equal(JsonValueKind.Null, metric.GetProperty("percent").ValueKind);
            }
        }
    }

    private static void AssertValidNonzeroPrimaryAggregate(Fixture fixture)
    {
        using var primaryGate = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Accepted, "primary-gate.json")));
        var gate = primaryGate.RootElement;
        Assert.True(gate.GetProperty("passed").GetBoolean());
        Assert.True(gate.GetProperty("line").GetProperty("valid").GetInt32() > 0);
        Assert.True(gate.GetProperty("branch").GetProperty("valid").GetInt32() > 0);
    }

    private static void AssertV2PositivePrimaryLineRefusal(CoverageFanInResult result)
    {
        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.Equal("NotEvaluated", result.PatchOutcome);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("ASCOV302", diagnostic.Code);
        Assert.Equal("accept", diagnostic.Stage);
        Assert.Equal("accept", diagnostic.Field);
        Assert.Empty(result.Artifacts);
    }

    private static void AssertV2EmptyPatchMetric(CoverageGateResult result, int changedLines)
    {
        Assert.True(result.Passed);
        Assert.NotNull(result.PatchLineCoverage);
        Assert.Equal(changedLines, result.PatchLineCoverage.ChangedLines);
        Assert.Equal(0, result.PatchLineCoverage.MeasurableLines);
        Assert.Equal(0, result.PatchLineCoverage.CoveredLines);
        Assert.Equal(100m, result.PatchLineCoverage.Percent);
        Assert.NotNull(result.PatchBranchCoverage);
        Assert.Equal(changedLines, result.PatchBranchCoverage.ChangedLines);
        Assert.Equal(0, result.PatchBranchCoverage.MeasurableBranches);
        Assert.Equal(0, result.PatchBranchCoverage.CoveredBranches);
        Assert.Equal(100m, result.PatchBranchCoverage.Percent);
        Assert.True(result.LineCoverage.Valid > 0);
        Assert.True(result.BranchCoverage.Valid > 0);
    }

    private static void AssertV2BranchlessMetrics(JsonElement gate)
    {
        var line = gate.GetProperty("patchLine");
        Assert.Equal(1, line.GetProperty("changed").GetInt32());
        Assert.Equal(1, line.GetProperty("measurable").GetInt32());
        Assert.Equal(1, line.GetProperty("covered").GetInt32());
        Assert.Equal(100m, line.GetProperty("percent").GetDecimal());

        var branch = gate.GetProperty("patchBranch");
        Assert.Equal(1, branch.GetProperty("changed").GetInt32());
        Assert.Equal(0, branch.GetProperty("measurable").GetInt32());
        Assert.Equal(0, branch.GetProperty("covered").GetInt32());
        Assert.Equal(100m, branch.GetProperty("percent").GetDecimal());
    }

    private static string Report(bool aggregate = false, bool changedSource = false,
        bool branchlessChangedLine = false, bool changedSourceHit = true, int aggregateClasses = 1,
        bool aggregateAddedLine = false, string changedSourceFile = "src/Example.cs")
    {
        var classes = new XElement("classes");
        var lineValid = 0;
        var lineCovered = 0;
        var branchValid = 0;
        var branchCovered = 0;
        void AddClass(string name, string file, bool branch, int lineNumber = 1, bool hit = true,
            int? coveredBranches = null)
        {
            var line = new XElement("line", new XAttribute("number", lineNumber), new XAttribute("hits", hit ? 1 : 0));
            lineValid++;
            if (hit) lineCovered++;
            if (branch)
            {
                var covered = coveredBranches ?? (hit ? 2 : 0);
                line.Add(new XAttribute("branch", "true"));
                line.Add(new XAttribute("condition-coverage", $"{covered * 50}% ({covered}/2)"));
                branchValid += 2;
                branchCovered += covered;
            }
            classes.Add(new XElement("class", new XAttribute("name", name), new XAttribute("filename", file),
                new XElement("lines", line)));
        }

        if (aggregate)
            for (var index = 0; index < aggregateClasses; index++)
                AddClass(index == 0 ? "Aggregate" : $"Aggregate{index}", "src/Aggregate.cs", branch: true);
        if (aggregateAddedLine) AddClass("AggregateAdded", "src/Aggregate.cs", branch: false, lineNumber: 2);
        if (changedSource)
            AddClass("Example", changedSourceFile, branch: !branchlessChangedLine, hit: changedSourceHit);

        var document = new XDocument(new XElement("coverage",
            new XAttribute("lines-covered", lineCovered),
            new XAttribute("lines-valid", lineValid),
            new XAttribute("branches-covered", branchCovered),
            new XAttribute("branches-valid", branchValid),
            new XAttribute("line-rate", Rate(lineCovered, lineValid)),
            new XAttribute("branch-rate", Rate(branchCovered, branchValid)),
            new XElement("packages", new XElement("package", new XAttribute("name", "fixture"), classes))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string Rate(int covered, int valid) => valid == 0 ? "0"
        : (covered / (decimal)valid).ToString("0.################", CultureInfo.InvariantCulture);

    private static IEnumerable<string> AddedLines(string diff) => diff.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(directory, path) + ":" + Hash(File.ReadAllBytes(path)))
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string? _priorArtifactHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");

        private Fixture()
        {
            Temp = TestDirectory.Create();
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(Temp.Path, "store"));
        }

        internal TestDirectory Temp { get; }
        internal string Repository => Path.Join(Temp.Path, "repo");
        internal string PrimaryReport => Path.Join(Temp.Path, "producer", "primary.xml");
        internal string NativeReport => Path.Join(Temp.Path, "producer", "native.xml");
        internal string Timings => Path.Join(Temp.Path, "producer", "timings.json");
        internal string PrimaryCapture => Path.Join(Temp.Path, "primary-capture");
        internal string NativeCapture => Path.Join(Temp.Path, "native-capture");
        internal string Composed => Path.Join(Temp.Path, "composed");
        internal string Accepted => Path.Join(Temp.Path, "accepted");
        internal CoverageFanInPolicy Policy { get; private set; } = null!;
        internal string BaseCommit { get; private set; } = null!;
        internal static string CurrentPlatform => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux";

        internal static async Task<Fixture> CreateAsync(string objectFormat = "sha1")
        {
            var fixture = new Fixture();
            fixture.Temp.WriteFile("repo/checks/Verification.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            fixture.Temp.WriteFile("repo/src/Aggregate.cs", "public sealed class Aggregate { }\n");
            fixture.Temp.WriteFile("repo/src/Example.cs", "public sealed class Example { }\n");
            fixture.Temp.WriteFile("repo/coverage.manifest", "version=1\n");
            await Command(fixture.Repository, "git", "init", $"--object-format={objectFormat}");
            await Command(fixture.Repository, "git", "config", "user.email", "coverage-tests@example.invalid");
            await Command(fixture.Repository, "git", "config", "user.name", "AppSurface applicability tests");
            await Command(fixture.Repository, "git", "add", ".");
            await Command(fixture.Repository, "git", "commit", "-m", "applicability fixture baseline");
            fixture.BaseCommit = await Command(fixture.Repository, "git", "rev-parse", "HEAD");
            fixture.Policy = new(new(fixture.BaseCommit,
                    await Command(fixture.Repository, "git", "rev-parse", "HEAD^{tree}"),
                    await Command(fixture.Repository, "dotnet", "--version"), "Release"),
                new("local", "workflow-tests", Guid.NewGuid().ToString("N"), 1),
                ["checks/Verification.csproj"], [], new(CoverageGateMode.AggregateOnly));
            return fixture;
        }

        internal void SetSupplements(IReadOnlyList<CoverageSupplement> supplements) =>
            Policy = Policy with { Supplements = supplements };

        internal void UseV3Contract(string? diffBase = null) =>
            Policy = Policy with
            {
                ContractVersion = 3,
                ReportProfileId = CoveragePolicyEncoding.InitialReportProfileId,
                Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: diffBase ?? BaseCommit)
            };

        internal async Task SelectManifestOnlyPatchAsync()
        {
            File.WriteAllText(Path.Join(Repository, "coverage.manifest"), "version=1\nenabled=true\n");
            await CommitPatchAsync();
        }

        internal async Task SelectSourcePatchAsync()
        {
            File.WriteAllText(Path.Join(Repository, "src/Example.cs"),
                "public sealed class Example { public int Value => 1; }\n");
            await CommitPatchAsync();
        }

        internal async Task SelectExcludedPatchAsync(bool addReportedPoint)
        {
            File.WriteAllText(Path.Join(Repository, "src/Example.cs"),
                "[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]\npublic sealed class Example { public int Value => 1; }\n");
            if (addReportedPoint)
                File.WriteAllText(Path.Join(Repository, "src/Aggregate.cs"),
                    "public sealed class Aggregate { }\npublic sealed class AggregateAdded { }\n");
            await CommitPatchAsync();
        }

        internal async Task<CoverageFanInDiffInput> PreparePathCaseAliasAsync()
        {
            await SelectSourcePatchAsync();
            UseV3Contract();
            await CapturePrimaryAsync(Report(aggregate: true, changedSource: true, changedSourceFile: "src/example.cs"));
            await ComposeAsync([]);
            return await ReadV3DiffAsync();
        }

        internal async Task SelectDeletePatchAsync()
        {
            File.Delete(Path.Join(Repository, "src/Example.cs"));
            await CommitPatchAsync();
        }

        internal async Task SelectRenamePatchAsync()
        {
            Directory.CreateDirectory(Path.Join(Repository, "docs"));
            await Command(Repository, "git", "mv", "src/Example.cs", "docs/Example.cs");
            await CommitPatchAsync();
        }

        internal async Task SelectEmptyFileAdditionPatchAsync()
        {
            Directory.CreateDirectory(Path.Join(Repository, "docs"));
            File.WriteAllBytes(Path.Join(Repository, "docs/Empty.cs"), []);
            await CommitPatchAsync();
        }

        private async Task CommitPatchAsync()
        {
            await Command(Repository, "git", "add", "-A", "--", ".");
            await Command(Repository, "git", "commit", "-m", "applicability fixture patch");
            Policy = Policy with
            {
                Source = Policy.Source with
                {
                    Commit = await Command(Repository, "git", "rev-parse", "HEAD"),
                    Tree = await Command(Repository, "git", "rev-parse", "HEAD^{tree}")
                },
                Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: "HEAD^")
            };
        }

        internal async Task<CoverageFanInDiffInput> ReadV3DiffAsync()
        {
            var frozen = CoveragePolicyEncoding.Freeze(Policy);
            var expected = new CoverageFanInExpected(CoveragePolicyEncoding.ToElement(frozen),
                CoveragePolicyEncoding.Digest(frozen));
            var budget = new CoverageFanInBudget(expected.Bounds.TotalBytes);
            var context = new CoverageFanInDiffContext(new(expected, Repository), budget);
            await context.InitializeAsync(CancellationToken.None);
            return await context.ReadAsync(CancellationToken.None);
        }

        internal async Task<CoverageFanInResult> AcceptV3Async(IReadOnlyList<string> supplements, CoverageFanInDiffInput input,
            CoverageOutputMode outputMode = CoverageOutputMode.Fresh) =>
            await CoverageFanIn.AcceptAsync(new(Policy, Repository, PrimaryCapture, supplements,
                Composed, Accepted, outputMode)
            { ExpectedPatchSha256 = Hash(input.Patch) });

        internal async Task ForgeComposedLineAsNotApplicableAndRepublishAsync()
        {
            var files = Directory.EnumerateFiles(Accepted)
                .Where(path => !Path.GetFileName(path).StartsWith('.') && Path.GetFileName(path) != "generation.json")
                .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

            var applicability = ReadObject(files["patch-applicability.json"]);
            var composedClaim = (JsonObject)applicability["composed"]!;
            composedClaim["line"] = NotApplicableMetric(changedLines: 1);
            files["patch-applicability.json"] = Canonical(applicability);

            var composedGate = ReadObject(files["composed-gate.json"]);
            composedGate["patchLine"] = NotApplicableMetric(changedLines: 1);
            files["composed-gate.json"] = Canonical(composedGate);

            var receipt = ReadObject(files["acceptance.json"]);
            receipt["composedGateSha256"] = Hash(files["composed-gate.json"]);
            receipt["applicabilitySha256"] = Hash(files["patch-applicability.json"]);
            files["acceptance.json"] = Canonical(receipt);

            var limits = CoveragePolicyEncoding.Freeze(Policy).Limits!;
            var bounds = new CoverageFanInBounds(limits.XmlBytes, limits.JsonBytes, limits.TotalBytes, limits.Depth,
                limits.LineRecordsPerReport, limits.XmlElementsPerReport, limits.AttributesPerElement,
                limits.TokenCharacters, limits.BranchRecordsPerReport, limits.TotalRecords);
            await CoverageFanInPublication.PublishAsync(Accepted, Repository, "acceptance",
                Guid.NewGuid().ToString("N"), CoveragePolicyEncoding.Digest(Policy), files, true,
                bounds, CancellationToken.None, contractVersion: 3);
        }

        private static JsonObject ReadObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

        private static JsonObject NotApplicableMetric(int changedLines) => new()
        {
            ["outcome"] = "notApplicable",
            ["reason"] = "no-reported-patch-points",
            ["changedLines"] = changedLines,
            ["measurable"] = 0,
            ["covered"] = 0,
            ["percent"] = null
        };

        private static byte[] Canonical(JsonNode value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
            return CoverageFanInV3Encoding.Canonical(document.RootElement);
        }

        internal async Task CapturePrimaryAsync(string report, CoverageOutputMode outputMode = CoverageOutputMode.Fresh)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrimaryReport)!);
            File.WriteAllText(PrimaryReport, report);
            File.WriteAllText(Timings, JsonSerializer.Serialize(new
            {
                configuration = Policy.Source.Configuration,
                coverageDriver = "collector",
                merge = new { exitCode = 0 },
                artifacts = new { coverageFiles = 1 },
                projects = new[]
                {
                    new
                    {
                        project = "checks/Verification.csproj",
                        exitCode = 0,
                        executionStatus = "completed",
                        coverageArtifactStatus = "produced"
                    }
                }
            }));
            var result = await CoverageFanIn.CaptureAsync(new(Policy, Repository, PrimaryCapture,
                "primary", PrimaryReport, Timings, OutputMode: outputMode));
            Assert.True(result.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(result));
        }

        internal async Task CaptureSupplementAsync(CoverageSupplement supplement, string report)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NativeReport)!);
            File.WriteAllText(NativeReport, report);
            var result = await CoverageFanIn.CaptureAsync(new(Policy, Repository, NativeCapture,
                supplement.Id, CoveragePath: NativeReport, RawCoveragePath: NativeReport));
            Assert.True(result.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(result));
            Assert.Equal(CoverageObservation.TrustedProducer, result.Observation);
        }

        internal async Task ComposeAsync(IReadOnlyList<string> supplements, CoverageOutputMode outputMode = CoverageOutputMode.Fresh)
        {
            var result = await CoverageFanIn.ComposeAsync(new(Policy, Repository, PrimaryCapture,
                supplements, Composed, outputMode));
            Assert.True(result.Outcome == CoverageOperationOutcome.Succeeded, JsonSerializer.Serialize(result));
        }

        internal async Task<string> ReadDiffAsync() =>
            await GitDiffReader.ReadDiffAsync(Repository, Policy.Gate.DiffBase!, CancellationToken.None);

        internal async Task<CoverageGateResult> EvaluatePatchAsync(string coveragePath, string diff)
        {
            var patch = new CoveragePatchRequest(Repository, Policy.Gate.DiffBase!, 95m,
                _ => Task.FromResult(diff), 85m, PatchLineMode.Codecov);
            return await CoverageGateEvaluator.EvaluateAsync(new CoverageGateRequest(coveragePath,
                Path.Join(Temp.Path, "gate-evaluation"), 95m, 85m, patch, 0.5m), CancellationToken.None);
        }

        internal async Task<CoverageFanInResult> AcceptAsync(IReadOnlyList<string> supplements, string diff,
            CoverageOutputMode outputMode = CoverageOutputMode.Fresh)
        {
            var expectedPatchHash = Hash(Encoding.UTF8.GetBytes(diff));
            return await CoverageFanIn.AcceptAsync(new(Policy, Repository, PrimaryCapture, supplements,
                Composed, Accepted, outputMode)
            { ExpectedPatchSha256 = expectedPatchHash });
        }

        internal CoverageVerifyRequest VerifyRequest(IReadOnlyList<string> supplements) =>
            new(Policy, Repository, PrimaryCapture, supplements, Composed, Accepted);

        private static async Task<string> Command(string root, string executable, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new(executable)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return (await output).Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _priorArtifactHome);
            Temp.Dispose();
        }
    }
}
