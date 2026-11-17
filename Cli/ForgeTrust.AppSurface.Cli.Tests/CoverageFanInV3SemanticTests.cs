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
    [Fact]
    public void HandAuthoredMeasuredClaimsPassIndependentReplay() => new Oracle().Verify();

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
        }
        // Rebind all portable hashes/copies except the explicit copy-disagreement case. These refusals
        // must follow from semantic replay, never a stale transport hash or missing private registration.
        if (mutation != "copy") oracle.ComposedGate["patchLine"] = metric.DeepClone();
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
            "unsupported-binary" => oracle.Input with { Patch = Encoding.UTF8.GetBytes(patch[..patch.IndexOf("@@", StringComparison.Ordinal)] + "Binary files a/F.cs and b/F.cs differ\n") },
            _ => oracle.Input with { MaximumRecords = 0 }
        };
        oracle.Claim["diffSha256"] = CoverageFanInIO.Hash(oracle.Input.Patch);
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
        private readonly string _primaryHash = new('1', 64);
        private readonly string _composedHash = new('2', 64);
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

        internal void RehashMetadata() => Claim["diffMetadataSha256"] = CoverageFanInIO.Hash(CoverageFanInV3Encoding.Canonical(JsonSerializer.SerializeToElement(Metadata)));
        internal void Verify() => CoverageFanInV3Verifier.Verify(JsonSerializer.SerializeToElement(PrimaryGate), JsonSerializer.SerializeToElement(ComposedGate),
            JsonSerializer.SerializeToElement(Claim), JsonSerializer.SerializeToElement(Metadata), Expected, Primary, Composed, Input, _captures, _primaryHash, _composedHash,
            new HashSet<string>(["F.cs"], StringComparer.Ordinal));
    }
}
