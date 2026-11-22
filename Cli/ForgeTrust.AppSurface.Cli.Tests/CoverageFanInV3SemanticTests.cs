using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Hand-authored semantic oracles protect replay against rehashed writer claims, without claiming native execution.</summary>
public sealed class CoverageFanInV3SemanticTests
{
    private const string VisualBaselineDiffPolicyId = "appsurface-visual-baseline-png-v1";
    private const string StrictDiffPolicyId = "appsurface-git-patch-v1";
    private const string VisualBaselinePath = "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/home.png";

    [Fact]
    public void HandAuthoredMeasuredClaimsPassIndependentReplay() => new Oracle().Verify();

    [Fact]
    public void IndependentReplayAcceptsQualifiedPngOnlyWithModifiedMetadataAndNoAddedRanges()
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly();

        oracle.Verify();

        var change = oracle.Metadata["changes"]![0]!;
        Assert.Equal(VisualBaselineDiffPolicyId, oracle.Expected.DiffPolicyId);
        Assert.Equal(VisualBaselineDiffPolicyId, oracle.Metadata["diffPolicyId"]!.GetValue<string>());
        Assert.Equal("modified", change["kind"]!.GetValue<string>());
        Assert.Equal(VisualBaselinePath, change["oldPath"]!.GetValue<string>());
        Assert.Equal(VisualBaselinePath, change["newPath"]!.GetValue<string>());
        Assert.Empty(change["addedRanges"]!.AsArray());
    }

    [Fact]
    public void IndependentReplayAcceptsVisualBaselineAlongsideMeasuredSourceChanges()
    {
        var oracle = new Oracle();
        oracle.UseMixedVisualBaselinePng();

        oracle.Verify();

        Assert.Equal(1, oracle.ComposedGate["patchLine"]!["changedLines"]!.GetValue<int>());
        Assert.Equal(1, oracle.ComposedGate["patchLine"]!["measurable"]!.GetValue<int>());
        Assert.Equal(2, oracle.ComposedGate["patchBranch"]!["measurable"]!.GetValue<int>());
    }

    [Fact]
    public void VisualBaselinePolicyRefusesNulInTextHunks()
    {
        var oracle = new Oracle();
        oracle.UseMixedVisualBaselinePng();
        var replacement = Encoding.UTF8.GetString(oracle.Input.Patch).Replace("+after", "+a\0fter", StringComparison.Ordinal);
        oracle.Input = oracle.Input with { Patch = Encoding.UTF8.GetBytes(replacement) };
        oracle.RehashDiffEvidence();

        Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify);
        Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(oracle.Input));
    }

    [Fact]
    public void IndependentReplayRetainsFailedMeasuredSourceGateWithVisualBaselineChange()
    {
        var oracle = new Oracle();
        oracle.UseMixedVisualBaselinePng();
        foreach (var report in new[] { oracle.Primary, oracle.Composed })
        {
            report.Root!.SetAttributeValue("lines-covered", 0);
            report.Root.SetAttributeValue("branches-covered", 0);
            var line = report.Descendants("line").Single();
            line.SetAttributeValue("hits", 0);
            line.SetAttributeValue("condition-coverage", "0% (0/2)");
        }
        foreach (var gate in new[] { oracle.PrimaryGate, oracle.ComposedGate })
        {
            gate["passed"] = false;
            foreach (var dimension in new[] { "line", "branch", "patchLine", "patchBranch" })
            {
                gate[dimension]!["covered"] = 0;
                gate[dimension]!["percent"] = 0;
            }
        }
        foreach (var stage in new[] { "primary", "composed" })
        {
            foreach (var dimension in new[] { "line", "branch" })
            {
                oracle.Claim[stage]![dimension]!["covered"] = 0;
                oracle.Claim[stage]![dimension]!["percent"] = 0;
            }
        }
        oracle.RebindReports();

        oracle.Verify();

        Assert.False(oracle.ComposedGate["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void StrictDefaultPolicyRefusesBinaryPngMarker()
    {
        var oracle = new Oracle();
        oracle.UseUnqualifiedBinaryPngOnly();

        Assert.Equal(StrictDiffPolicyId, oracle.Expected.DiffPolicyId);
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void IndependentReplayRejectsPolicyAndTrustedInputMismatch()
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly();
        oracle.Input = oracle.Input with { DiffPolicyId = StrictDiffPolicyId };

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void IndependentReplayRejectsUnknownDiffPolicyEvenWhenTrustedInputAgrees()
    {
        const string unsupportedPolicyId = "appsurface-unrecognized-diff-v1";
        var oracle = new Oracle();
        oracle.UseUnknownDiffPolicy(unsupportedPolicyId);

        Assert.Equal(unsupportedPolicyId, oracle.Expected.DiffPolicyId);
        Assert.Equal(unsupportedPolicyId, oracle.Input.DiffPolicyId);
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("index-mode")]
    [InlineData("index-object")]
    [InlineData("missing-index-mode")]
    [InlineData("marker-path")]
    [InlineData("other-binary-marker")]
    [InlineData("duplicate-marker")]
    [InlineData("extra-header")]
    [InlineData("extra-hunk")]
    public void IndependentReplayRejectsTamperedQualifiedPngPatchArtifacts(string mutation)
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly();
        var patch = Encoding.UTF8.GetString(oracle.Input.Patch);
        var originalPatch = patch;
        var marker = $"Binary files a/{VisualBaselinePath} and b/{VisualBaselinePath} differ";
        switch (mutation)
        {
            case "index-mode": patch = patch.Replace(" 100644\n" + marker, " 100755\n" + marker, StringComparison.Ordinal); break;
            case "index-object": patch = patch.Replace(new string('a', 20) + "0" + new string('a', 19), new string('c', 40), StringComparison.Ordinal); break;
            case "missing-index-mode": patch = patch.Replace(".." + new string('b', 20) + "0" + new string('b', 19) + " 100644", ".." + new string('b', 20) + "0" + new string('b', 19), StringComparison.Ordinal); break;
            case "marker-path": patch = patch.Replace(marker, marker.Replace("home.png", "elsewhere.png", StringComparison.Ordinal), StringComparison.Ordinal); break;
            case "other-binary-marker": patch = patch.Replace(marker, marker.Replace("Binary files", "Binary file", StringComparison.Ordinal), StringComparison.Ordinal); break;
            case "duplicate-marker": patch = patch.Replace(marker + "\n", marker + "\n" + marker + "\n", StringComparison.Ordinal); break;
            case "extra-header": patch = patch.Replace(marker + "\n", marker + "\n--- a/home.png\n", StringComparison.Ordinal); break;
            case "extra-hunk": patch = patch.Replace(marker + "\n", marker + "\n@@ -1 +1 @@\n-old\n+new\n", StringComparison.Ordinal); break;
        }
        Assert.NotEqual(originalPatch, patch);
        oracle.Input = oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch) };
        oracle.RehashDiffEvidence();

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void IndependentReplayRejectsQualifiedPngMetadataWithAddedRanges()
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly();
        oracle.Metadata["changes"]![0]!["addedRanges"] = new JsonArray(new JsonObject { ["start"] = 1, ["count"] = 1 });
        oracle.RehashMetadata();

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/nested/home.png")]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/home.PNG")]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsLight/caf\u00e9.png")]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/Other/home.png")]
    public void IndependentReplayRejectsQualifiedPathsOutsideDirectPngBaselines(string path)
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly(path);

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/home.png", "Web/ForgeTrust.RazorWire.IntegrationTests/VisualBaselines/AppSurfaceDocsGraphite/HOME.PNG")]
    public void IndependentReplayRejectsQualifiedReportPathAliasesWithoutLineRecords(string qualifiedPath, string reportPath)
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly(qualifiedPath);
        foreach (var report in new[] { oracle.Primary, oracle.Composed })
            report.Descendants("classes").Single().Add(new XElement("class",
                new XAttribute("name", "reported screenshot"), new XAttribute("filename", reportPath), new XElement("lines")));
        oracle.RebindReports();

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("deleted")]
    [InlineData("mode-changed")]
    [InlineData("executable")]
    [InlineData("regular-to-executable")]
    public void IndependentReplayRejectsStructuralChangesAtQualifiedPngPaths(string changeKind)
    {
        var oracle = new Oracle();
        oracle.UseQualifiedPngOnly();
        var oldId = new string('a', 20) + "0" + new string('a', 19);
        var newId = new string('b', 20) + "0" + new string('b', 19);
        var zeros = new string('0', 40);
        var raw = changeKind switch
        {
            "added" => $":000000 100644 {zeros} {newId} A\0{VisualBaselinePath}\0",
            "deleted" => $":100644 000000 {oldId} {zeros} D\0{VisualBaselinePath}\0",
            "mode-changed" => $":100644 100755 {oldId} {oldId} M\0{VisualBaselinePath}\0",
            "executable" => $":100755 100755 {oldId} {newId} M\0{VisualBaselinePath}\0",
            "regular-to-executable" => $":100644 100755 {oldId} {newId} M\0{VisualBaselinePath}\0",
            _ => throw new ArgumentOutOfRangeException(nameof(changeKind))
        };
        oracle.Input = oracle.Input with { Raw = raw };

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void FailedLineThresholdCannotSkipBranchRecordValidation()
    {
        var oracle = new Oracle();
        oracle.Primary.Root!.SetAttributeValue("lines-covered", 0);
        oracle.PrimaryGate["line"]!["covered"] = 0;
        oracle.PrimaryGate["line"]!["percent"] = 0;
        oracle.PrimaryGate["branch"]!["valid"] = 3;

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void IndependentReplayAcceptsQuotedEscapedPathAndNoNewlineHunkMarker()
    {
        const string path = "F space.cs";
        var oracle = new Oracle();
        oracle.UseQuotedPath(path);
        var patch = Encoding.UTF8.GetString(oracle.Input.Patch) + "\\ No newline at end of file\n";
        oracle.Input = oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch) };
        oracle.RehashDiffEvidence();

        oracle.Verify(new HashSet<string>([path], StringComparer.Ordinal));
    }

    [Fact]
    public void IndependentReplayRejectsMismatchedAndUnterminatedQuotedGitHeaders()
    {
        const string path = "F space.cs";
        var encodedPath = path.Replace(" ", "\\040", StringComparison.Ordinal);

        var mismatched = new Oracle();
        mismatched.UseQuotedPath(path);
        var mismatchPatch = Encoding.UTF8.GetString(mismatched.Input.Patch)
            .Replace("\"b/F\\040space.cs\"", "\"b/G\\040space.cs\"", StringComparison.Ordinal);
        mismatched.Input = mismatched.Input with { Patch = Encoding.UTF8.GetBytes(mismatchPatch) };
        mismatched.RehashDiffEvidence();
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(
            () => mismatched.Verify(new HashSet<string>([path], StringComparer.Ordinal))).Cause);

        var unterminated = new Oracle();
        unterminated.UseQuotedPath(path);
        var originalPatch = Encoding.UTF8.GetString(unterminated.Input.Patch);
        var headerEnd = originalPatch.IndexOf('\n');
        var malformedHeader = "diff --git \"a/" + encodedPath + " b/" + encodedPath + originalPatch[headerEnd..];
        unterminated.Input = unterminated.Input with { Patch = Encoding.UTF8.GetBytes(malformedHeader) };
        unterminated.RehashDiffEvidence();
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(
            () => unterminated.Verify(new HashSet<string>([path], StringComparer.Ordinal))).Cause);
    }

    [Fact]
    public void IndependentReplayAcceptsHandAuthoredAddedDeletedRenamedAndModeChangedObjects()
    {
        var zeros = new string('0', 40);
        var oldId = new string('d', 40);
        var newId = new string('e', 40);
        var cases = new (string Raw, string Patch, JsonObject Change, int ChangedLines)[]
        {
            ($":000000 100644 {zeros} {newId} A\0New.cs\0",
                $"diff --git a/New.cs b/New.cs\nnew file mode 100644\nindex {zeros}..{newId}\n--- /dev/null\n+++ b/New.cs\n@@ -0,0 +1 @@\n+added\n",
                Change("added", null, "New.cs", 1), 1),
            ($":100644 000000 {oldId} {zeros} D\0Gone.cs\0",
                $"diff --git a/Gone.cs b/Gone.cs\ndeleted file mode 100644\nindex {oldId}..{zeros}\n--- a/Gone.cs\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n",
                Change("deleted", "Gone.cs", null), 0),
            ($":100644 100644 {oldId} {newId} R100\0F.cs\0G.cs\0",
                $"diff --git a/F.cs b/G.cs\nsimilarity index 100%\nrename from F.cs\nrename to G.cs\nindex {oldId}..{newId} 100644\n--- a/F.cs\n+++ b/G.cs\n",
                Change("renamed", "F.cs", "G.cs"), 0),
            ($":100644 100755 {oldId} {oldId} M\0F.cs\0",
                "diff --git a/F.cs b/F.cs\nold mode 100644\nnew mode 100755\n",
                Change("modeChanged", "F.cs", "F.cs"), 0)
        };

        foreach (var item in cases)
        {
            var oracle = new Oracle();
            oracle.UseHandAuthoredDiff(item.Raw, item.Patch, new JsonArray(item.Change), item.ChangedLines);
            oracle.Verify(new HashSet<string>(["F.cs", "G.cs", "New.cs", "Gone.cs"], StringComparer.Ordinal));
        }
    }

    [Fact]
    public void IndependentReplayAcceptsEmptyDiffOnlyWithNotApplicableMetrics()
    {
        var oracle = new Oracle();
        oracle.UseHandAuthoredDiff(string.Empty, string.Empty, new JsonArray(), changedLines: 0);

        oracle.Verify();

        oracle.Claim["composed"]!["line"]!["changedLines"] = 1;
        oracle.RehashMetadata();
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void CanonicalV3EncodingOrdersPropertiesAndNormalizesDecimals()
    {
        using var document = JsonDocument.Parse("""{"z":1.2300,"a":2}""");

        Assert.Equal("{\"a\":2,\"z\":1.23}", Encoding.UTF8.GetString(
            CoverageFanInV3Encoding.Canonical(document.RootElement)));
    }

    [Fact]
    public void CanonicalV3EncodingRejectsPayloadBeyondTheWireLimit()
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload = new string('x', CoverageFanInDiffContext.StreamLimit)
        }));

        var error = Assert.Throws<CoverageFanInEvidenceException>(
            () => CoverageFanInV3Encoding.Canonical(document.RootElement));

        Assert.Equal("limit-exceeded", error.Cause);
    }

    [Fact]
    public void IndependentReplayBindsContextAndDisjointAddedRangesToTheExactHunks()
    {
        var oracle = new Oracle();
        oracle.UseContextAndDisjointHunks();

        oracle.Verify();

        oracle.Metadata["changes"]![0]!["addedRanges"]![1]!["start"] = 6;
        oracle.RehashMetadata();
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void AccurateNumericRefusalKeepsMeasuredMetricsInsteadOfPretendingUnverified()
    {
        var oracle = new Oracle();
        foreach (var document in new[] { oracle.Primary, oracle.Composed })
        {
            document.Root!.SetAttributeValue("lines-covered", 0); document.Root.SetAttributeValue("branches-covered", 0);
            var point = document.Descendants("line").Single(); point.SetAttributeValue("hits", 0); point.SetAttributeValue("condition-coverage", "0% (0/2)");
        }
        foreach (var gate in new[] { oracle.PrimaryGate, oracle.ComposedGate })
        {
            gate["passed"] = false;
            foreach (var dimension in new[] { "line", "branch", "patchLine", "patchBranch" })
            { gate[dimension]!["covered"] = 0; gate[dimension]!["percent"] = 0; }
        }
        foreach (var set in new[] { "primary", "composed" })
            foreach (var dimension in new[] { "line", "branch" })
            { oracle.Claim[set]![dimension]!["covered"] = 0; oracle.Claim[set]![dimension]!["percent"] = 0; }
        oracle.Verify();
        oracle.ComposedGate["passed"] = true;
        Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify);
    }

    [Fact]
    public void DuplicateLineIdentityStillRetainsEveryMethodBranchOccurrence()
    {
        var oracle = new Oracle();
        foreach (var document in new[] { oracle.Primary, oracle.Composed })
        {
            document.Descendants("class").Single().AddFirst(XElement.Parse("""<methods><method name="Run" signature="()"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></method></methods>"""));
        }
        // Primary aggregate retains its supplied root summary, while composed totals derive from
        // every serialized occurrence. Patch lines dedupe to one and branch occurrences sum to four.
        oracle.Composed.Root!.SetAttributeValue("branches-valid", 4); oracle.Composed.Root.SetAttributeValue("branches-covered", 4);
        oracle.ComposedGate["branch"]!["valid"] = 4; oracle.ComposedGate["branch"]!["covered"] = 4;
        foreach (var set in new[] { "primary", "composed" })
        { oracle.Claim[set]!["branch"]!["measurable"] = 4; oracle.Claim[set]!["branch"]!["covered"] = 4; }
        foreach (var gate in new[] { oracle.PrimaryGate, oracle.ComposedGate })
        { gate["patchBranch"]!["measurable"] = 4; gate["patchBranch"]!["covered"] = 4; }
        oracle.Verify();
        oracle.Claim["composed"]!["branch"]!["measurable"] = 2;
        oracle.Claim["composed"]!["branch"]!["covered"] = 2;
        oracle.ComposedGate["patchBranch"] = oracle.Claim["composed"]!["branch"]!.DeepClone();
        Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify);
    }

    [Theory]
    [InlineData(189, 200, 94.5, true)]
    [InlineData(188, 200, 94, false)]
    [InlineData(1, 3, 33.3333, false)]
    [InlineData(2, 3, 66.6667, false)]
    public void RoundedBranchMetricRetainsToleranceBoundaryAndCodecovLineSemantics(
        int covered, int valid, double percentValue, bool branchPasses)
    {
        var oracle = new Oracle();
        var percent = (decimal)percentValue;
        foreach (var document in new[] { oracle.Primary, oracle.Composed })
            document.Descendants("line").Single().SetAttributeValue("condition-coverage", $"{percent}% ({covered}/{valid})");
        foreach (var gate in new[] { oracle.PrimaryGate, oracle.ComposedGate })
        {
            gate["patchLine"]!["covered"] = 0; gate["patchLine"]!["percent"] = 0;
            gate["patchBranch"]!["covered"] = covered; gate["patchBranch"]!["measurable"] = valid; gate["patchBranch"]!["percent"] = percent;
        }
        foreach (var set in new[] { "primary", "composed" })
        {
            oracle.Claim[set]!["line"] = oracle.PrimaryGate["patchLine"]!.DeepClone();
            oracle.Claim[set]!["branch"] = oracle.PrimaryGate["patchBranch"]!.DeepClone();
        }
        // Primary root aggregate is independent; composed aggregate accounts for reported occurrences.
        oracle.Composed.Root!.SetAttributeValue("branches-valid", valid); oracle.Composed.Root.SetAttributeValue("branches-covered", covered);
        oracle.ComposedGate["branch"]!["valid"] = valid; oracle.ComposedGate["branch"]!["covered"] = covered; oracle.ComposedGate["branch"]!["percent"] = percent;
        oracle.ComposedGate["passed"] = false; // A partially covered branch makes the Codecov line uncovered.
        oracle.Verify();
        // Isolate the branch threshold using the existing policy's allowed measurable-line mode.
        oracle.UseMeasurableMode();
        oracle.ComposedGate["passed"] = branchPasses;
        oracle.Verify();
        oracle.ComposedGate["passed"] = !branchPasses;
        Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify);
    }

    [Theory]
    [InlineData("forged-n-a")]
    [InlineData("count")]
    [InlineData("reason")]
    [InlineData("percent")]
    [InlineData("null")]
    [InlineData("overflow")]
    [InlineData("unknown-outcome")]
    [InlineData("unknown-field")]
    [InlineData("copy")]
    [InlineData("threshold")]
    [InlineData("passed")]
    [InlineData("base")]
    [InlineData("profile")]
    [InlineData("report")]
    [InlineData("capture")]
    [InlineData("version")]
    [InlineData("ranges")]
    [InlineData("rename-origin")]
    [InlineData("missing-metric-field")]
    [InlineData("metric-type")]
    [InlineData("count-negative")]
    [InlineData("covered-exceeds-measurable")]
    [InlineData("metric-null")]
    [InlineData("wrong-outcome")]
    [InlineData("wrong-reason")]
    [InlineData("metadata-schema")]
    [InlineData("metadata-head")]
    [InlineData("metadata-changes")]
    [InlineData("source-identity")]
    [InlineData("run-identity")]
    [InlineData("primary-report")]
    [InlineData("capture-count")]
    [InlineData("gate-metric-field")]
    [InlineData("gate-type")]
    public void RehashedClaimChangesCannotBecomeAcceptance(string mutation)
    {
        var oracle = new Oracle();
        var metric = oracle.Claim["composed"]!["line"]!;
        switch (mutation)
        {
            case "forged-n-a": metric["outcome"] = "notApplicable"; metric["reason"] = "no-reported-patch-points"; metric["measurable"] = 0; metric["covered"] = 0; metric["percent"] = null; break;
            case "count": metric["covered"] = 0; break;
            case "reason": metric["reason"] = "excluded-by-policy"; break;
            case "percent": metric["percent"] = 99.9999m; break;
            case "null": metric["changedLines"] = null; break;
            case "overflow": metric["measurable"] = 2147483648L; break;
            case "unknown-outcome": metric["outcome"] = "trustedProducer"; break;
            case "unknown-field": metric["asserted"] = true; break;
            case "copy": oracle.ComposedGate["patchLine"]!["percent"] = 99m; break;
            case "threshold": oracle.ComposedGate["thresholds"]!["patchLine"] = 0; break;
            case "passed": oracle.ComposedGate["passed"] = false; break;
            case "base": oracle.Metadata["requestedBaseCommit"] = new string('f', 40); break;
            case "profile": oracle.Claim["reportProfileId"] = "microsoft-converted"; break;
            case "report": oracle.Claim["composedReportSha256"] = new string('f', 64); break;
            case "capture": oracle.Claim["captureManifestHashes"]![0] = new string('f', 64); break;
            case "version": oracle.Claim["schemaVersion"] = 2; break;
            case "ranges": oracle.Metadata["changes"]![0]!["addedRanges"]![0]!["start"] = 2; break;
            case "rename-origin": oracle.Metadata["changes"]![0]!["oldPath"] = "different.cs"; break;
            case "missing-metric-field": metric.AsObject().Remove("reason"); break;
            case "metric-type": metric["changedLines"] = "1"; break;
            case "count-negative": metric["measurable"] = -1; break;
            case "covered-exceeds-measurable": metric["covered"] = 2; break;
            case "metric-null": metric["percent"] = null; break;
            case "wrong-outcome": metric["outcome"] = "unverified"; metric["reason"] = "invalid-evidence"; break;
            case "wrong-reason": metric["reason"] = "excluded-by-policy"; break;
            case "metadata-schema": oracle.Metadata["schemaVersion"] = 2; break;
            case "metadata-head": oracle.Metadata["headCommit"] = new string('f', 40); break;
            case "metadata-changes": oracle.Metadata["changes"] = new JsonArray(); break;
            case "source-identity": oracle.Claim["source"] = new JsonObject(); break;
            case "run-identity": oracle.Claim["run"] = new JsonObject(); break;
            case "primary-report": oracle.Claim["primaryReportSha256"] = new string('f', 64); break;
            case "capture-count": oracle.Claim["captureManifestHashes"] = new JsonArray(); break;
            case "gate-metric-field": oracle.ComposedGate["patchLine"]!.AsObject().Remove("reason"); break;
            case "gate-type": oracle.ComposedGate["passed"] = "true"; break;
        }
        // Rebind all portable hashes/copies except the explicit copy-disagreement case. These refusals
        // must follow from semantic replay, never a stale transport hash or missing private registration.
        if (mutation is not ("copy" or "gate-metric-field" or "gate-type"))
            oracle.ComposedGate["patchLine"] = metric.DeepClone();
        oracle.RehashMetadata();
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("raw-mode")]
    [InlineData("raw-object")]
    [InlineData("raw-path")]
    [InlineData("missing-raw")]
    [InlineData("missing-patch")]
    [InlineData("hunk-count")]
    [InlineData("hunk-old-count")]
    [InlineData("hunk-context")]
    [InlineData("bad-header-pair")]
    [InlineData("duplicate-raw-path")]
    [InlineData("invalid-raw-id")]
    [InlineData("raw-traversal-path")]
    [InlineData("unsupported-binary")]
    [InlineData("record-limit")]
    public void ContradictoryRawObjectsOrHunksAreNotCollectorOmission(string mutation)
    {
        var oracle = new Oracle();
        var raw = oracle.Input.Raw; var patch = Encoding.UTF8.GetString(oracle.Input.Patch);
        oracle.Input = mutation switch
        {
            "raw-mode" => oracle.Input with { Raw = raw.Replace(":100644", ":120000", StringComparison.Ordinal) },
            "raw-object" => oracle.Input with { Raw = raw.Replace(new string('d', 40), new string('f', 40), StringComparison.Ordinal) },
            "raw-path" => oracle.Input with { Raw = raw.Replace("F.cs", "G.cs", StringComparison.Ordinal) },
            "missing-raw" => oracle.Input with { Raw = string.Empty },
            "missing-patch" => oracle.Input with { Patch = [] },
            "hunk-count" => oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch.Replace("+1 @@", "+1,2 @@", StringComparison.Ordinal)) },
            "hunk-old-count" => oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch.Replace("-1 +1", "-1,2 +1", StringComparison.Ordinal)) },
            "hunk-context" => oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch.Replace("+after", " context\n+after", StringComparison.Ordinal)) },
            "bad-header-pair" => oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch.Replace("a/F.cs b/F.cs", "a/F.cs b/G.cs", StringComparison.Ordinal)) },
            "duplicate-raw-path" => oracle.Input with { Raw = raw + raw },
            "invalid-raw-id" => oracle.Input with { Raw = raw.Replace(new string('d', 40), "xyz", StringComparison.Ordinal) },
            "raw-traversal-path" => oracle.Input with { Raw = raw.Replace("F.cs", "../F.cs", StringComparison.Ordinal) },
            "unsupported-binary" => oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch[..patch.IndexOf("@@", StringComparison.Ordinal)] + "Binary files a/F.cs and b/F.cs differ\n") },
            _ => oracle.Input with { MaximumRecords = 0 }
        };
        if (mutation == "missing-raw") oracle.Metadata["changes"] = new JsonArray();
        if (mutation == "raw-path")
        {
            oracle.Metadata["changes"]![0]!["oldPath"] = "G.cs";
            oracle.Metadata["changes"]![0]!["newPath"] = "G.cs";
        }
        if (mutation is "missing-patch" or "unsupported-binary")
            oracle.Metadata["changes"]![0]!["addedRanges"] = new JsonArray();
        oracle.RehashDiffEvidence();
        Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify);
    }

    [Theory]
    [InlineData("f.cs")]
    [InlineData("missing/F.cs")]
    public void KnownCaseAliasFailsButGeneratedReportedPathsRemainAdmitted(string filename)
    {
        var oracle = new Oracle();
        oracle.Primary.Descendants("class").Single().SetAttributeValue("filename", filename);
        if (filename == "f.cs") Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify);
        else
        {
            // Absence of a tracked equivalent is allowed for generated report paths; a changed point
            // silently attributed elsewhere is the deliberately accepted upstream trust limitation.
            oracle.MakePrimaryNotApplicable();
            oracle.Verify();
        }
    }

    [Fact]
    public void UnicodeNormalizationAliasCannotBypassTrackedPathAdmission()
    {
        const string trackedPath = "Café.cs";
        const string decomposedPath = "Cafe\u0301.cs";
        var oracle = new Oracle();
        foreach (var document in new[] { oracle.Primary, oracle.Composed })
            document.Descendants("class").Single().SetAttributeValue("filename", decomposedPath);

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(
            () => oracle.Verify(new HashSet<string>([trackedPath], StringComparer.Ordinal))).Cause);
    }

    [Theory]
    [InlineData("capture-array-kind")]
    [InlineData("aggregate-valid")]
    [InlineData("aggregate-covered")]
    [InlineData("aggregate-percent")]
    [InlineData("composed-loses-lines")]
    [InlineData("composed-loses-branches")]
    [InlineData("na-outcome")]
    [InlineData("na-reason")]
    [InlineData("na-percent")]
    public void IndependentReplayRejectsConsistentlyRehashedCountAndApplicabilityContradictions(string mutation)
    {
        var oracle = new Oracle();
        switch (mutation)
        {
            case "capture-array-kind": oracle.Claim["captureManifestHashes"] = new JsonObject(); break;
            case "aggregate-valid": oracle.PrimaryGate["line"]!["valid"] = 2; break;
            case "aggregate-covered": oracle.PrimaryGate["line"]!["covered"] = 0; break;
            case "aggregate-percent": oracle.PrimaryGate["line"]!["percent"] = 99; break;
            case "composed-loses-lines": oracle.Composed.Descendants("line").Single().Remove(); break;
            case "composed-loses-branches": oracle.Composed.Descendants("line").Single().SetAttributeValue("condition-coverage", "100% (1/1)"); break;
            default:
                oracle.UseHandAuthoredDiff(string.Empty, string.Empty, new JsonArray(), 0);
                var metric = oracle.Claim["composed"]!["line"]!;
                if (mutation == "na-outcome") metric["outcome"] = "measured";
                else if (mutation == "na-reason") metric["reason"] = "reported-points";
                else metric["percent"] = 100;
                oracle.ComposedGate["patchLine"] = metric.DeepClone();
                break;
        }
        oracle.RehashDiffEvidence();

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Fact]
    public void IndependentReplayRejectsLostChangedRecordWithConsistentNonemptyComposedEvidence()
    {
        var oracle = new Oracle();
        oracle.UseContextAndDisjointHunks();
        oracle.RebindReports();
        oracle.Verify();

        oracle.Composed.Descendants("line").Single(line => (string?)line.Attribute("number") == "2").Remove();
        oracle.Composed.Root!.SetAttributeValue("lines-valid", 3);
        oracle.Composed.Root.SetAttributeValue("lines-covered", 3);
        oracle.Composed.Root.SetAttributeValue("branches-valid", 6);
        oracle.Composed.Root.SetAttributeValue("branches-covered", 6);
        oracle.ComposedGate["line"]!["valid"] = 3;
        oracle.ComposedGate["line"]!["covered"] = 3;
        oracle.ComposedGate["branch"]!["valid"] = 6;
        oracle.ComposedGate["branch"]!["covered"] = 6;
        foreach (var metric in new[] { oracle.ComposedGate["patchLine"]!, oracle.Claim["composed"]!["line"]! })
        { metric["measurable"] = 2; metric["covered"] = 2; }
        foreach (var metric in new[] { oracle.ComposedGate["patchBranch"]!, oracle.Claim["composed"]!["branch"]! })
        { metric["measurable"] = 4; metric["covered"] = 4; }
        oracle.RebindReports();
        oracle.RehashDiffEvidence();

        Assert.Equal(3, oracle.Composed.Descendants("line").Count());
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition(
            [(oracle.Primary, "/source")], oracle.Composed, oracle.Primary));
    }

    [Theory]
    [InlineData("raw-header")]
    [InlineData("raw-prefix")]
    [InlineData("raw-short-mode")]
    [InlineData("raw-missing-path")]
    [InlineData("raw-missing-nul")]
    [InlineData("raw-rename-score")]
    [InlineData("raw-rename-missing-destination")]
    [InlineData("raw-rename-same-path")]
    [InlineData("old-zero-id")]
    [InlineData("new-zero-id")]
    [InlineData("raw-added-mode")]
    [InlineData("raw-deleted-mode")]
    [InlineData("hunk-missing-index")]
    [InlineData("hunk-missing-headers")]
    [InlineData("hunk-close")]
    [InlineData("hunk-prefix")]
    [InlineData("hunk-zero-counts")]
    [InlineData("hunk-zero-start")]
    [InlineData("hunk-body-marker")]
    [InlineData("hunk-empty-body")]
    [InlineData("hunk-trailing-metadata")]
    [InlineData("hunk-misplaced-newline")]
    [InlineData("index-duplicate")]
    [InlineData("index-mode")]
    [InlineData("old-header")]
    [InlineData("new-header")]
    [InlineData("new-before-old")]
    [InlineData("rename-origin")]
    [InlineData("rename-destination")]
    [InlineData("similarity")]
    [InlineData("new-mode")]
    [InlineData("old-mode")]
    public void IndependentReplayRejectsMalformedRawAndHunkGrammarBeforeTrustingClaims(string mutation)
    {
        var oracle = new Oracle();
        var oldId = new string('d', 40); var newId = new string('e', 40); var zeros = new string('0', 40);
        var raw = oracle.Input.Raw; var patch = Encoding.UTF8.GetString(oracle.Input.Patch);
        switch (mutation)
        {
            case "raw-header": raw = ":\0"; break;
            case "raw-prefix": raw = raw.Replace(":100644", "x100644", StringComparison.Ordinal); break;
            case "raw-short-mode": raw = raw.Replace(":100644", ":10064", StringComparison.Ordinal); break;
            case "raw-missing-path": raw = raw[..(raw.IndexOf('\0') + 1)]; break;
            case "raw-missing-nul": raw = raw[..^1]; break;
            case "raw-rename-score": raw = raw.Replace(" M\0", " R49\0", StringComparison.Ordinal); break;
            case "raw-rename-missing-destination": raw = raw.Replace(" M\0", " R100\0", StringComparison.Ordinal); break;
            case "raw-rename-same-path": raw = raw.Replace(" M\0", " R100\0", StringComparison.Ordinal) + "F.cs\0"; break;
            case "old-zero-id": raw = raw.Replace(oldId, zeros, StringComparison.Ordinal); break;
            case "new-zero-id": raw = raw.Replace(newId, zeros, StringComparison.Ordinal); break;
            case "raw-added-mode": raw = raw.Replace(" M\0", " A\0", StringComparison.Ordinal); break;
            case "raw-deleted-mode": raw = raw.Replace(" M\0", " D\0", StringComparison.Ordinal); break;
            case "hunk-missing-index": patch = patch.Replace($"index {oldId}..{newId} 100644\n", "", StringComparison.Ordinal); break;
            case "hunk-missing-headers": patch = patch.Replace("--- a/F.cs\n+++ b/F.cs\n", "", StringComparison.Ordinal); break;
            case "hunk-close": patch = patch.Replace("@@ -1 +1 @@", "@@ -1 +1", StringComparison.Ordinal); break;
            case "hunk-prefix": patch = patch.Replace("@@ -1 +1 @@", "@@ +1 +1 @@", StringComparison.Ordinal); break;
            case "hunk-zero-counts": patch = patch.Replace("@@ -1 +1 @@", "@@ -1,0 +1,0 @@", StringComparison.Ordinal); break;
            case "hunk-zero-start": patch = patch.Replace("@@ -1 +1 @@", "@@ -0 +1 @@", StringComparison.Ordinal); break;
            case "hunk-body-marker": patch = patch.Replace("-before\n+after\n", "?invalid\n", StringComparison.Ordinal); break;
            case "hunk-empty-body": patch = patch.Replace("-before\n+after\n", "\n", StringComparison.Ordinal); break;
            case "hunk-trailing-metadata": patch += "old mode 100644\n"; break;
            case "hunk-misplaced-newline": patch = "diff --git a/F.cs b/F.cs\n\\ No newline at end of file\n"; break;
            case "index-duplicate": patch = patch.Replace("--- a/F.cs", $"index {oldId}..{newId} 100644\n--- a/F.cs", StringComparison.Ordinal); break;
            case "index-mode": patch = patch.Replace(" 100644\n", " 100755\n", StringComparison.Ordinal); break;
            case "old-header": patch = patch.Replace("--- a/F.cs", "--- a/G.cs", StringComparison.Ordinal); break;
            case "new-header": patch = patch.Replace("+++ b/F.cs", "+++ b/G.cs", StringComparison.Ordinal); break;
            case "new-before-old": patch = patch.Replace("--- a/F.cs\n+++ b/F.cs", "+++ b/F.cs\n--- a/F.cs", StringComparison.Ordinal); break;
            case "rename-origin": patch = patch.Replace("index ", "rename from F.cs\nindex ", StringComparison.Ordinal); break;
            case "rename-destination": patch = patch.Replace("index ", "rename to G.cs\nindex ", StringComparison.Ordinal); break;
            case "similarity": patch = patch.Replace("index ", "similarity index 49%\nindex ", StringComparison.Ordinal); break;
            case "new-mode": patch = patch.Replace("index ", "new mode 100755\nindex ", StringComparison.Ordinal); break;
            case "old-mode": patch = patch.Replace("index ", "old mode 100644\nindex ", StringComparison.Ordinal); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        oracle.Input = oracle.Input with { Raw = raw, Patch = Encoding.UTF8.GetBytes(patch) };
        oracle.RehashDiffEvidence();

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("duplicate-old-header")]
    [InlineData("duplicate-new-header")]
    [InlineData("index-extra-field")]
    [InlineData("index-malformed-ids")]
    [InlineData("hunk-only-old-header")]
    [InlineData("before-first-header")]
    [InlineData("same-object-same-mode")]
    [InlineData("wrong-rename-origin")]
    [InlineData("duplicate-rename-origin")]
    [InlineData("wrong-rename-destination")]
    [InlineData("duplicate-rename-destination")]
    [InlineData("rename-without-destination")]
    [InlineData("old-mode-mismatch")]
    [InlineData("new-mode-mismatch")]
    public void BothParsersRejectDuplicateOrContradictoryGitMetadata(string mutation)
    {
        var oracle = new Oracle();
        var oldId = new string('d', 40); var newId = new string('e', 40);
        var raw = oracle.Input.Raw; var patch = Encoding.UTF8.GetString(oracle.Input.Patch);
        switch (mutation)
        {
            case "duplicate-old-header": patch = patch.Replace("--- a/F.cs", "--- a/F.cs\n--- a/F.cs", StringComparison.Ordinal); break;
            case "duplicate-new-header": patch = patch.Replace("+++ b/F.cs", "+++ b/F.cs\n+++ b/F.cs", StringComparison.Ordinal); break;
            case "index-extra-field": patch = patch.Replace(" 100644\n", " 100644 extra\n", StringComparison.Ordinal); break;
            case "index-malformed-ids": patch = patch.Replace($"{oldId}..{newId}", oldId, StringComparison.Ordinal); break;
            case "hunk-only-old-header": patch = patch.Replace("+++ b/F.cs\n", "", StringComparison.Ordinal); break;
            case "before-first-header": patch = "unexpected\n" + patch; break;
            case "same-object-same-mode": raw = raw.Replace(newId, oldId, StringComparison.Ordinal); break;
            case "old-mode-mismatch":
            case "new-mode-mismatch":
                raw = $":100644 100755 {oldId} {oldId} M\0F.cs\0";
                patch = mutation == "old-mode-mismatch" ? "diff --git a/F.cs b/F.cs\nold mode 100755\nnew mode 100755\n"
                    : "diff --git a/F.cs b/F.cs\nold mode 100644\nnew mode 100644\n";
                break;
            default:
                raw = $":100644 100644 {oldId} {oldId} R100\0F.cs\0G.cs\0";
                var origin = mutation == "wrong-rename-origin" ? "other.cs" : "F.cs";
                var destination = mutation == "wrong-rename-destination" ? "other.cs" : "G.cs";
                patch = $"diff --git a/F.cs b/G.cs\nsimilarity index 100%\nrename from {origin}\n";
                if (mutation == "duplicate-rename-origin") patch += "rename from F.cs\n";
                if (mutation != "rename-without-destination") patch += $"rename to {destination}\n";
                if (mutation == "duplicate-rename-destination") patch += "rename to G.cs\n";
                break;
        }
        oracle.Input = oracle.Input with { Raw = raw, Patch = Encoding.UTF8.GetBytes(patch) };
        oracle.RehashDiffEvidence();

        Assert.Equal("malformed-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(oracle.Input)).Cause);
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothParsersRejectNewAndDeletedFileModesThatDisagreeWithRawObjects(bool added)
    {
        var oracle = new Oracle();
        var id = new string('d', 40); var zeros = new string('0', 40);
        var raw = added ? $":000000 100644 {zeros} {id} A\0F.cs\0" : $":100644 000000 {id} {zeros} D\0F.cs\0";
        var patch = "diff --git a/F.cs b/F.cs\n" + (added ? $"new file mode 100755\nindex {zeros}..{id}\n" : $"deleted file mode 100755\nindex {id}..{zeros}\n");
        oracle.Input = oracle.Input with { Raw = raw, Patch = Encoding.UTF8.GetBytes(patch) };
        oracle.RehashDiffEvidence();

        Assert.Equal("malformed-diff", Assert.Throws<CoverageFanInEvidenceException>(() => CoverageFanInDiffParser.Parse(oracle.Input)).Cause);
        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("PRN.cs")]
    [InlineData("CON.cs")]
    [InlineData("AUX.cs")]
    [InlineData("NUL.cs")]
    [InlineData("COM1.cs")]
    [InlineData("LPT9.cs")]
    [InlineData("name?.cs")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("/rooted.cs")]
    [InlineData("folder\\file.cs")]
    [InlineData("folder//file.cs")]
    [InlineData("name\u200B.cs")]
    [InlineData("name\n.cs")]
    [InlineData("./name.cs")]
    [InlineData("../name.cs")]
    public void IndependentReplayRejectsPortableDeviceAndPathAliasesBeforeApplicability(string path)
    {
        var oracle = new Oracle();
        oracle.UseQuotedPath(path);

        Assert.Equal("applicability-mismatch", Assert.Throws<CoverageFanInEvidenceException>(oracle.Verify).Cause);
    }

    [Theory]
    [InlineData("COM0.cs")]
    [InlineData("LPT0.cs")]
    [InlineData("COMX.cs")]
    [InlineData("LPTX.cs")]
    [InlineData("COM10.cs")]
    public void IndependentReplayPreservesOrdinaryDeviceLikeReportedPathNames(string path)
    {
        var oracle = new Oracle();
        oracle.UseQuotedPath(path);

        oracle.Verify(new HashSet<string>([path], StringComparer.Ordinal));
    }

    private sealed class Oracle
    {
        internal CoverageFanInExpected Expected { get; private set; }
        internal CoverageFanInDiffInput Input { get; set; }
        internal JsonNode Metadata { get; }
        internal JsonNode Claim { get; }
        internal JsonNode PrimaryGate { get; }
        internal JsonNode ComposedGate { get; }
        internal XDocument Primary { get; } = XDocument.Parse("""<coverage lines-valid="1" lines-covered="1" branches-valid="2" branches-covered="2"><packages><package name="P"><classes><class name="F" filename="F.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class></classes></package></packages></coverage>""");
        internal XDocument Composed { get; }
        private string _primaryHash = new('1', 64);
        private string _composedHash = new('2', 64);
        private readonly string[] _captures = [new('3', 64)];

        internal Oracle()
        {
            var policy = CoverageFanInPolicyTests.Policy() with
            {
                ContractVersion = 3,
                ReportProfileId = "coverlet-collector-cobertura-v1",
                Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: new string('c', 40))
            };
            Expected = new(CoveragePolicyEncoding.ToElement(policy), CoveragePolicyEncoding.Digest(policy));
            var oldId = new string('d', 40); var newId = new string('e', 40);
            var patch = Encoding.UTF8.GetBytes($"diff --git a/F.cs b/F.cs\nindex {oldId}..{newId} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1 +1 @@\n-before\n+after\n");
            Input = new(new string('c', 40), new string('c', 40), policy.Source.Commit, 40, patch, $":100644 100644 {oldId} {newId} M\0F.cs\0");
            Composed = new(Primary);
            Metadata = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 3,
                requestedBaseCommit = Input.RequestedBase,
                mergeBaseCommit = Input.MergeBase,
                headCommit = Input.Head,
                diffPolicyId = "appsurface-git-patch-v1",
                diffSha256 = CoverageFanInIO.Hash(patch),
                empty = false,
                changes = new[] { new { kind = "modified", oldPath = "F.cs", newPath = "F.cs", addedRanges = new[] { new { start = 1, count = 1 } } } }
            })!;
            var set = new { line = Metric(1, 1), branch = Metric(2, 2) };
            Claim = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 3,
                reportProfileId = "coverlet-collector-cobertura-v1",
                policyDigest = Expected.Digest,
                source = Expected.Source,
                run = Expected.Run,
                diffSha256 = CoverageFanInIO.Hash(patch),
                diffMetadataSha256 = "",
                captureManifestHashes = _captures,
                primaryReportSha256 = _primaryHash,
                composedReportSha256 = _composedHash,
                primary = set,
                composed = set
            })!;
            PrimaryGate = Gate(false); ComposedGate = Gate(true); RehashMetadata();
        }

        private static object Metric(int valid, int covered) => new
        { outcome = "measured", reason = "reported-points", changedLines = 1, measurable = valid, covered, percent = 100m };

        internal void UseQualifiedPngOnly(string path = VisualBaselinePath)
        {
            SelectVisualBaselinePolicy();
            var oldId = new string('a', 20) + "0" + new string('a', 19);
            var newId = new string('b', 20) + "0" + new string('b', 19);
            var patch = Encoding.UTF8.GetBytes(
                $"diff --git a/{path} b/{path}\nindex {oldId}..{newId} 100644\nBinary files a/{path} and b/{path} differ\n");
            Input = Input with
            {
                Raw = $":100644 100644 {oldId} {newId} M\0{path}\0",
                Patch = patch,
                DiffPolicyId = VisualBaselineDiffPolicyId,
                QualifiedPngPaths = new HashSet<string>([path], StringComparer.Ordinal)
            };
            Metadata["diffPolicyId"] = VisualBaselineDiffPolicyId;
            Metadata["changes"] = new JsonArray(Change("modified", path, path));
            SetPatchNotApplicable(0);
            RehashDiffEvidence();
        }

        internal void UseUnqualifiedBinaryPngOnly(string path = VisualBaselinePath)
        {
            var oldId = new string('a', 20) + "0" + new string('a', 19);
            var newId = new string('b', 20) + "0" + new string('b', 19);
            var patch = Encoding.UTF8.GetBytes(
                $"diff --git a/{path} b/{path}\nindex {oldId}..{newId} 100644\nBinary files a/{path} and b/{path} differ\n");
            Input = Input with
            {
                Raw = $":100644 100644 {oldId} {newId} M\0{path}\0",
                Patch = patch,
                DiffPolicyId = StrictDiffPolicyId,
                QualifiedPngPaths = new HashSet<string>(StringComparer.Ordinal)
            };
            Metadata["diffPolicyId"] = StrictDiffPolicyId;
            Metadata["changes"] = new JsonArray(Change("modified", path, path));
            SetPatchNotApplicable(0);
            RehashDiffEvidence();
        }

        internal void UseUnknownDiffPolicy(string diffPolicyId)
        {
            var policy = JsonNode.Parse(Expected.Policy.GetRawText())!.AsObject();
            policy["diffPolicyId"] = diffPolicyId;
            using var document = JsonDocument.Parse(policy.ToJsonString());
            Expected = new(document.RootElement.Clone(), Expected.Digest);
            Input = Input with { DiffPolicyId = diffPolicyId };
        }

        internal void UseMixedVisualBaselinePng(string path = VisualBaselinePath)
        {
            SelectVisualBaselinePolicy();
            var oldId = new string('a', 20) + "0" + new string('a', 19);
            var newId = new string('b', 20) + "0" + new string('b', 19);
            var patch = Encoding.UTF8.GetString(Input.Patch)
                + $"diff --git a/{path} b/{path}\nindex {oldId}..{newId} 100644\nBinary files a/{path} and b/{path} differ\n";
            Input = Input with
            {
                Raw = Input.Raw + $":100644 100644 {oldId} {newId} M\0{path}\0",
                Patch = Encoding.UTF8.GetBytes(patch),
                DiffPolicyId = VisualBaselineDiffPolicyId,
                QualifiedPngPaths = new HashSet<string>([path], StringComparer.Ordinal)
            };
            Metadata["diffPolicyId"] = VisualBaselineDiffPolicyId;
            Metadata["changes"]!.AsArray().Add(Change("modified", path, path));
            RehashDiffEvidence();
        }

        private void SelectVisualBaselinePolicy()
        {
            var policy = CoveragePolicyEncoding.Decode<CoverageFanInPolicy>(
                Encoding.UTF8.GetBytes(Expected.Policy.GetRawText())) with
            { DiffPolicyId = VisualBaselineDiffPolicyId };
            Expected = new(CoveragePolicyEncoding.ToElement(policy), CoveragePolicyEncoding.Digest(policy));
            Claim["policyDigest"] = Expected.Digest;
        }

        private void SetPatchNotApplicable(int changedLines)
        {
            var metric = JsonSerializer.SerializeToNode(new
            {
                outcome = "notApplicable",
                reason = "no-reported-patch-points",
                changedLines,
                measurable = 0,
                covered = 0,
                percent = (decimal?)null
            })!;
            foreach (var gate in new[] { PrimaryGate, ComposedGate })
            {
                gate["patchLine"] = metric.DeepClone();
                gate["patchBranch"] = metric.DeepClone();
            }
            foreach (var stage in new[] { "primary", "composed" })
            {
                Claim[stage]!["line"] = metric.DeepClone();
                Claim[stage]!["branch"] = metric.DeepClone();
            }
        }

        internal void UseContextAndDisjointHunks()
        {
            var patch = Encoding.UTF8.GetBytes($"diff --git a/F.cs b/F.cs\nindex {new string('d', 40)}..{new string('e', 40)} 100644\n--- a/F.cs\n+++ b/F.cs\n@@ -1,2 +1,3 @@\n context\n-before\n+after\n+after2\n@@ -4 +5 @@\n-before4\n+after4\n");
            Input = Input with { Patch = patch };
            foreach (var document in new[] { Primary, Composed })
            {
                foreach (var number in new[] { 2, 3, 5 })
                    document.Descendants("lines").Single().Add(new XElement("line",
                        new XAttribute("number", number), new XAttribute("hits", 1), new XAttribute("branch", "true"),
                        new XAttribute("condition-coverage", "100% (2/2)")));
                document.Root!.SetAttributeValue("lines-valid", 4);
                document.Root.SetAttributeValue("lines-covered", 4);
                document.Root.SetAttributeValue("branches-valid", 8);
                document.Root.SetAttributeValue("branches-covered", 8);
            }
            foreach (var gate in new[] { PrimaryGate, ComposedGate })
            {
                gate["line"]!["valid"] = 4; gate["line"]!["covered"] = 4;
                gate["branch"]!["valid"] = 8; gate["branch"]!["covered"] = 8;
                gate["patchLine"] = JsonSerializer.SerializeToNode(Metric(3, 3));
                gate["patchBranch"] = JsonSerializer.SerializeToNode(Metric(6, 6));
                gate["patchLine"]!["changedLines"] = 3;
                gate["patchBranch"]!["changedLines"] = 3;
            }
            foreach (var stage in new[] { "primary", "composed" })
            {
                Claim[stage]!["line"] = JsonSerializer.SerializeToNode(Metric(3, 3));
                Claim[stage]!["line"]!["changedLines"] = 3;
                Claim[stage]!["branch"] = JsonSerializer.SerializeToNode(Metric(6, 6));
                Claim[stage]!["branch"]!["changedLines"] = 3;
            }
            Metadata["changes"]![0]!["addedRanges"] = new JsonArray(
                JsonSerializer.SerializeToNode(new { start = 2, count = 2 }),
                JsonSerializer.SerializeToNode(new { start = 5, count = 1 }));
            RehashDiffEvidence();
        }

        internal void UseQuotedPath(string path)
        {
            var encodedPath = path.Replace(" ", "\\040", StringComparison.Ordinal);
            var oldId = new string('d', 40); var newId = new string('e', 40);
            var patch = Encoding.UTF8.GetBytes(
                $"diff --git \"a/{encodedPath}\" \"b/{encodedPath}\"\nindex {oldId}..{newId} 100644\n--- \"a/{encodedPath}\"\n+++ \"b/{encodedPath}\"\n@@ -1 +1 @@\n-before\n+after\n");
            Input = Input with { Raw = Input.Raw.Replace("F.cs", path, StringComparison.Ordinal), Patch = patch };
            foreach (var document in new[] { Primary, Composed })
                document.Descendants("class").Single().SetAttributeValue("filename", path);
            Metadata["changes"]![0]!["oldPath"] = path;
            Metadata["changes"]![0]!["newPath"] = path;
            RehashDiffEvidence();
        }

        internal void UseHandAuthoredDiff(string raw, string patch, JsonArray changes, int changedLines)
        {
            Input = Input with { Raw = raw, Patch = Encoding.UTF8.GetBytes(patch) };
            Metadata["changes"] = changes;
            var notApplicable = JsonSerializer.SerializeToNode(new
            {
                outcome = "notApplicable",
                reason = "no-reported-patch-points",
                changedLines,
                measurable = 0,
                covered = 0,
                percent = (decimal?)null
            })!;
            foreach (var gate in new[] { PrimaryGate, ComposedGate })
            {
                gate["patchLine"] = notApplicable.DeepClone();
                gate["patchBranch"] = notApplicable.DeepClone();
            }
            foreach (var stage in new[] { "primary", "composed" })
            {
                Claim[stage]!["line"] = notApplicable.DeepClone();
                Claim[stage]!["branch"] = notApplicable.DeepClone();
            }
            RehashDiffEvidence();
        }

        private JsonNode Gate(bool composed) => JsonSerializer.SerializeToNode(new
        {
            passed = true,
            coverage = composed ? "coverage.cobertura.xml" : "primary-paths.cobertura.xml",
            tolerancePercent = 0.5m,
            thresholds = new { line = composed ? 0 : 95, branch = composed ? 0 : 85, patchLine = composed ? (int?)95 : null, patchBranch = composed ? (int?)85 : null },
            effectiveThresholds = new { line = composed ? 0m : 94.5m, branch = composed ? 0m : 84.5m, patchLine = composed ? (decimal?)94.5m : null, patchBranch = composed ? (decimal?)84.5m : null },
            patchLineMode = "codecov",
            patchDiffSource = new { kind = "git-base", label = Input.RequestedBase, strictness = "local-git", diffBase = Input.RequestedBase, path = (string?)null, bytes = Input.Patch.Length, sha256 = CoverageFanInIO.Hash(Input.Patch), empty = false },
            line = new { covered = 1, valid = 1, percent = 100 },
            branch = new { covered = 2, valid = 2, percent = 100 },
            patchLine = Metric(1, 1),
            patchBranch = Metric(2, 2)
        })!;

        internal void UseMeasurableMode()
        {
            var policy = CoverageFanInPolicyTests.Policy() with
            {
                ContractVersion = 3,
                ReportProfileId = "coverlet-collector-cobertura-v1",
                Gate = new(CoverageGateMode.AggregateAndPatch, MinPatchBranch: 95, PatchLineMode: "measurable", DiffBase: Input.RequestedBase)
            };
            Expected = new(CoveragePolicyEncoding.ToElement(policy), CoveragePolicyEncoding.Digest(policy));
            Claim["policyDigest"] = Expected.Digest;
            foreach (var gate in new[] { PrimaryGate, ComposedGate })
            { gate["patchLineMode"] = "measurable"; gate["patchLine"]!["covered"] = 1; gate["patchLine"]!["percent"] = 100; }
            ComposedGate["thresholds"]!["patchBranch"] = 95; ComposedGate["effectiveThresholds"]!["patchBranch"] = 94.5m;
            foreach (var set in new[] { "primary", "composed" }) Claim[set]!["line"] = PrimaryGate["patchLine"]!.DeepClone();
        }

        internal void MakePrimaryNotApplicable()
        {
            var absent = JsonSerializer.SerializeToNode(new { outcome = "notApplicable", reason = "no-reported-patch-points", changedLines = 1, measurable = 0, covered = 0, percent = (decimal?)null })!;
            Claim["primary"]!["line"] = absent.DeepClone(); Claim["primary"]!["branch"] = absent.DeepClone();
            PrimaryGate["patchLine"] = absent.DeepClone(); PrimaryGate["patchBranch"] = absent.DeepClone();
        }

        internal void RebindReports()
        {
            _primaryHash = CoverageFanInIO.Hash(Encoding.UTF8.GetBytes(Primary.ToString(SaveOptions.DisableFormatting)));
            _composedHash = CoverageFanInIO.Hash(Encoding.UTF8.GetBytes(Composed.ToString(SaveOptions.DisableFormatting)));
            Claim["primaryReportSha256"] = _primaryHash;
            Claim["composedReportSha256"] = _composedHash;
        }

        internal void RehashMetadata() => Claim["diffMetadataSha256"] = CoverageFanInIO.Hash(CoverageFanInV3Encoding.Canonical(JsonSerializer.SerializeToElement(Metadata)));
        internal void RehashDiffEvidence()
        {
            var hash = CoverageFanInIO.Hash(Input.Patch);
            Claim["diffSha256"] = hash;
            Metadata["diffSha256"] = hash;
            Metadata["empty"] = Input.Patch.Length == 0;
            foreach (var gate in new[] { PrimaryGate, ComposedGate })
            {
                gate["patchDiffSource"]!["bytes"] = Input.Patch.Length;
                gate["patchDiffSource"]!["sha256"] = hash;
                gate["patchDiffSource"]!["empty"] = Input.Patch.Length == 0;
            }
            RehashMetadata();
        }
        internal void Verify() => Verify(null);

        internal void Verify(IReadOnlySet<string>? trackedPaths) => CoverageFanInV3Verifier.Verify(JsonSerializer.SerializeToElement(PrimaryGate), JsonSerializer.SerializeToElement(ComposedGate),
            JsonSerializer.SerializeToElement(Claim), JsonSerializer.SerializeToElement(Metadata), Expected, Primary, Composed, Input, _captures, _primaryHash, _composedHash,
            trackedPaths ?? new HashSet<string>(["F.cs"], StringComparer.Ordinal));
    }

    private static JsonObject Change(string kind, string? oldPath, string? newPath, int? rangeStart = null) => new()
    {
        ["kind"] = kind,
        ["oldPath"] = oldPath,
        ["newPath"] = newPath,
        ["addedRanges"] = rangeStart is { } start
            ? new JsonArray(new JsonObject { ["start"] = start, ["count"] = 1 })
            : new JsonArray()
    };
}
