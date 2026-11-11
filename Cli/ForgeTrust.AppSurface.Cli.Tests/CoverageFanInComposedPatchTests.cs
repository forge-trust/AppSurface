using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInComposedPatchTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("t")]
    [InlineData("n")]
    [InlineData("v")]
    [InlineData("f")]
    [InlineData("r")]
    [InlineData("000")]
    public async Task DirectGateNeverAliasesEscapedControlSuffixToCoveredSource(string escape)
    {
        using var temp = TestDirectory.Create();
        var path = temp.WriteFile("report.xml", """
            <coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2">
              <packages><package name="P"><classes><class name="Widget" filename="src/Widget.cs">
                <lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" /></lines>
              </class></classes></package></packages>
            </coverage>
            """);
        var diff = "diff --git a/src/Widget.cs b/src/Widget.cs\n--- a/src/Widget.cs\n+++ \"b/src/Widget.cs\\"
            + escape + "\"\n@@ -0,0 +1 @@\n+changed\n";
        CoverageGateResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await CoverageGateEvaluator.EvaluateAsync(
            new CoverageGateRequest(path, temp.Path, 95, 85,
                new CoveragePatchRequest(temp.Path, "origin/main", 95,
                    _ => Task.FromResult(diff), 85, PatchLineMode.Codecov), 0.5m), CancellationToken.None));

        if (error is not null)
        {
            Assert.Equal("ASCOV015", Assert.IsType<CoverageExecutionException>(error).Code);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(0, result.PatchLineCoverage!.MeasurableLines);
            Assert.Equal(0, result.PatchLineCoverage.CoveredLines);
            Assert.Equal(0, result.PatchBranchCoverage!.MeasurableBranches);
        }
    }

    [Fact]
    public async Task RetainedNativeMethodBranchesRequireRealFullObservationBeforePatchAcceptance()
    {
        using var temp = TestDirectory.Create();
        const string root = "/repo";
        var primary = XDocument.Parse("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2">
              <packages><package name="Example"><classes>
                <class name="Widget" filename="src/Widget.cs">
                  <methods><method name="RunAsync" signature="()"><lines>
                    <line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" />
                  </lines></method></methods>
                  <lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);
        XDocument Native(string coverage) => XDocument.Parse($"""
            <coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2">
              <packages><package name="Example"><classes>
                <class name="Widget" filename="src/Widget.cs">
                  <lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" /></lines>
                </class>
                <class name="Widget/&lt;RunAsync&gt;d__1" filename="src/Widget.cs">
                  <methods><method name="MoveNext" signature="()"><lines>
                    <line number="1" hits="1" branch="true" condition-coverage="{coverage}" />
                  </lines></method></methods><lines />
                </class>
              </classes></package></packages>
            </coverage>
            """);

        const string diff = """
            diff --git a/src/Widget.cs b/src/Widget.cs
            index 0000000..1111111 100644
            --- a/src/Widget.cs
            +++ b/src/Widget.cs
            @@ -0,0 +1 @@
            +changed source
            """;
        async Task<CoverageGateResult> Gate(XDocument document)
        {
            var path = Path.Join(temp.Path, "report.xml");
            document.Save(path);
            return await CoverageGateEvaluator.EvaluateAsync(new CoverageGateRequest(
                path, temp.Path, 95, 85,
                new CoveragePatchRequest(temp.Path, "origin/main", 95,
                    _ => Task.FromResult(diff), 85, PatchLineMode.Codecov), 0.5m), CancellationToken.None);
        }

        var baseline = await Gate(primary);
        Assert.True(baseline.Passed);
        Assert.Equal(1, baseline.PatchLineCoverage!.CoveredLines);
        Assert.Equal(4, baseline.PatchBranchCoverage!.MeasurableBranches);

        // A folded primary method cannot erase an additional native state-machine record.
        // Two partial native observations cannot manufacture full branch coverage either.
        var partialInputs = new[] { (primary, root), (Native("0% (0/2)"), root),
            (Native("50% (1/2)"), root), (Native("50% (1/2)"), root) };
        var partial = CoverageFanInComposer.Compose(partialInputs);
        CoverageFanInIndependentVerifier.VerifyComposition(partialInputs, partial, primary);
        var rejected = await Gate(partial);
        Assert.False(rejected.Passed);
        Assert.Equal(1, rejected.PatchLineCoverage!.MeasurableLines);
        Assert.Equal(0, rejected.PatchLineCoverage.CoveredLines);
        Assert.Equal(6, rejected.PatchBranchCoverage!.MeasurableBranches);
        Assert.Equal(4, rejected.PatchBranchCoverage.CoveredBranches);
        Assert.Equal(2, partial.Descendants("class").Count());
        Assert.Equal(2, partial.Descendants("method").Count());

        foreach (var differentDenominator in new[] { "100% (1/1)", "100% (3/3)" })
        {
            var mismatched = CoverageFanInComposer.Compose(partialInputs.Append((Native(differentDenominator), root)).ToArray());
            var stillRejected = await Gate(mismatched);
            Assert.False(stillRejected.Passed);
            Assert.Equal(0, stillRejected.PatchLineCoverage!.CoveredLines);
            Assert.Equal(6, stillRejected.PatchBranchCoverage!.MeasurableBranches);
            Assert.Equal(4, stillRejected.PatchBranchCoverage.CoveredBranches);
        }

        var fullInputs = partialInputs.Append((Native("100% (2/2)"), root)).ToArray();
        var complete = CoverageFanInComposer.Compose(fullInputs);
        CoverageFanInIndependentVerifier.VerifyComposition(fullInputs, complete, primary);
        var accepted = await Gate(complete);
        Assert.True(accepted.Passed);
        Assert.Equal(rejected.PatchLineCoverage.MeasurableLines, accepted.PatchLineCoverage!.MeasurableLines);
        Assert.Equal(rejected.PatchBranchCoverage.MeasurableBranches, accepted.PatchBranchCoverage!.MeasurableBranches);
        Assert.Equal(1, accepted.PatchLineCoverage.CoveredLines);
        Assert.Equal(6, accepted.PatchBranchCoverage.CoveredBranches);
        Assert.Equal(2, complete.Descendants("class").Count());
        Assert.Equal(2, complete.Descendants("method").Count());

        var fullFirstInputs = new[] { (primary, root), (Native("100% (2/2)"), root) }.Concat(partialInputs.Skip(1)).ToArray();
        var fullFirst = CoverageFanInComposer.Compose(fullFirstInputs);
        CoverageFanInIndependentVerifier.VerifyComposition(fullFirstInputs, fullFirst, primary);
        var stillAccepted = await Gate(fullFirst);
        Assert.True(stillAccepted.Passed);
        Assert.Equal(6, stillAccepted.PatchBranchCoverage!.MeasurableBranches);
        Assert.Equal(6, stillAccepted.PatchBranchCoverage.CoveredBranches);

        // Verify actual evaluator report arithmetic independently, including a forged pass flag.
        async Task<JsonDocument> GateReport(XDocument coverage, bool primaryOnly)
        {
            var path = Path.Join(temp.Path, "report.xml");
            coverage.Save(path);
            var request = new CoverageGateRequest(path, temp.Path,
                primaryOnly ? 95 : 0, primaryOnly ? 85 : 0,
                new CoveragePatchRequest(temp.Path, "origin/main", primaryOnly ? null : 95,
                    _ => Task.FromResult(diff), primaryOnly ? null : 85, PatchLineMode.Codecov), 0.5m);
            var result = await CoverageGateEvaluator.EvaluateAsync(request, CancellationToken.None);
            await CoverageGateReportWriter.WriteAsync(result, request, CancellationToken.None);
            var node = JsonNode.Parse(await File.ReadAllBytesAsync(result.JsonReportPath))!;
            // Acceptance stores logical artifact names while the direct evaluator receives a physical path.
            node["coverage"] = primaryOnly ? "primary-paths.cobertura.xml" : "coverage.cobertura.xml";
            return JsonDocument.Parse(node.ToJsonString());
        }
        using var policy = JsonDocument.Parse("""
            {"gate":{"mode":"aggregateAndPatch","minLine":95,"minBranch":85,"minPatchLine":95,
              "minPatchBranch":85,"tolerance":0.5,"patchLineMode":"codecov","diffBase":"origin/main"}}
            """);
        using var primaryGate = await GateReport(primary, true);
        using var partialGate = await GateReport(partial, false);
        var forgedNode = JsonNode.Parse(partialGate.RootElement.GetRawText())!;
        forgedNode["passed"] = true;
        using var forged = JsonDocument.Parse(forgedNode.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, forged.RootElement, policy.RootElement, primary, partial, diff));
        using var completeGate = await GateReport(complete, false);
        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, completeGate.RootElement,
            policy.RootElement, primary, complete, diff);
    }
}
