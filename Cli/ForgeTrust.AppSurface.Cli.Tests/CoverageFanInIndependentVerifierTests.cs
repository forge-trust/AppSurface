using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInIndependentVerifierTests
{
    private const string SourceRoot = @"C:\agent\work";
    private const string Diff = """
        diff --git a/src/Widget.cs b/src/Widget.cs
        index 1111111..2222222 100644
        --- a/src/Widget.cs
        +++ b/src/Widget.cs
        @@ -1 +1 @@
        -old
        \ No newline at end of file
        +new
        \ No newline at end of file
        """;

    private const string Primary = """
        <coverage lines-covered="0" lines-valid="2" branches-covered="0" branches-valid="4">
          <packages><package name="Core"><classes>
            <class name="Widget" filename="C:\agent\work\src\Widget.cs">
              <methods>
                <method name="Run" signature="()"><lines><line number="1" hits="0" branch="false" /></lines></method>
                <method name="Run" signature="()"><lines><line number="2" hits="0" branch="false" /></lines></method>
                <method name="Run" signature="(int)"><lines><line number="3" hits="0" branch="false" /></lines></method>
              </methods>
              <lines>
                <line number="10" hits="0" branch="true" condition-coverage="0% (0/2)" />
                <line number="12" hits="0" branch="true" condition-coverage="0% (0/2)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private const string Native = """
        <coverage lines-covered="4" lines-valid="5" branches-covered="2" branches-valid="4">
          <packages><package name="Core"><classes>
            <class name="Widget" filename="src/Widget.cs">
              <methods>
                <method name="Run" signature="()"><lines><line number="1" hits="4" branch="false" /></lines></method>
                <method name="Run" signature="()"><lines><line number="2" hits="2" branch="false" /></lines></method>
                <method name="Run" signature="(int)"><lines><line number="3" hits="0" branch="false" /></lines></method>
              </methods>
              <lines>
                <line number="10" hits="1" branch="true" condition-coverage="100% (2/2)" />
                <line number="11" hits="1" branch="false" />
                <line number="12" hits="5" branch="true" condition-coverage="50% (1/2)" />
              </lines>
            </class>
            <class name="NativeOnly" filename="src/NativeOnly.cs">
              <methods><method name="Native" signature="()"><lines><line number="30" hits="1" branch="false" /></lines></method></methods>
              <lines><line number="20" hits="1" branch="false" /></lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private const string ComposedGolden = """
        <coverage lines-covered="4" lines-valid="4" branches-covered="2" branches-valid="4">
          <packages><package name="Core"><classes>
            <class name="NativeOnly" filename="src/NativeOnly.cs">
              <methods><method name="Native" signature="()"><lines><line number="30" hits="1" branch="false" /></lines></method></methods>
              <lines><line number="20" hits="1" branch="false" /></lines>
            </class>
            <class name="Widget" filename="src/Widget.cs">
              <methods>
                <method name="Run" signature="()"><lines><line number="1" hits="4" branch="false" /></lines></method>
                <method name="Run" signature="()"><lines><line number="2" hits="2" branch="false" /></lines></method>
                <method name="Run" signature="(int)"><lines><line number="3" hits="0" branch="false" /></lines></method>
              </methods>
              <lines>
                <line number="10" hits="1" branch="true" condition-coverage="100% (2/2)" />
                <line number="11" hits="1" branch="false" />
                <line number="12" hits="5" branch="true" condition-coverage="0% (0/2)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    private const string PrimaryPathsGolden = """
        <coverage lines-covered="0" lines-valid="2" branches-covered="0" branches-valid="4">
          <packages><package name="Core"><classes>
            <class name="Widget" filename="src/Widget.cs">
              <methods>
                <method name="Run" signature="()"><lines><line number="1" hits="0" branch="false" /></lines></method>
                <method name="Run" signature="()"><lines><line number="2" hits="0" branch="false" /></lines></method>
                <method name="Run" signature="(int)"><lines><line number="3" hits="0" branch="false" /></lines></method>
              </methods>
              <lines>
                <line number="10" hits="0" branch="true" condition-coverage="0% (0/2)" />
                <line number="12" hits="0" branch="true" condition-coverage="0% (0/2)" />
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    [Fact]
    public void VerifyComposition_AcceptsHandAuthoredGoldenWithOccurrencesOverloadsAndNativePoints()
    {
        CoverageFanInIndependentVerifier.VerifyComposition(
            [(XDocument.Parse(Primary), SourceRoot), (XDocument.Parse(Native), @"C:/agent/work")],
            XDocument.Parse(ComposedGolden), XDocument.Parse(PrimaryPathsGolden));
    }

    [Fact]
    public void VerifyComposition_RejectsDroppedNativeClassAndMethodRecords()
    {
        var noNativeClass = XDocument.Parse(ComposedGolden);
        noNativeClass.Descendants("class").Single(x => (string?)x.Attribute("name") == "NativeOnly").Remove();
        Assert.Throws<InvalidDataException>(() => VerifyComposition(noNativeClass));

        var missingOverload = XDocument.Parse(ComposedGolden);
        missingOverload.Descendants("class").Single(x => (string?)x.Attribute("name") == "Widget")
            .Descendants("method").Single(x => (string?)x.Attribute("signature") == "(int)").Remove();
        Assert.Throws<InvalidDataException>(() => VerifyComposition(missingOverload));
    }

    [Fact]
    public void VerifyComposition_RejectsInventedFullBranchFromTwoPartialObservations()
    {
        var invented = XDocument.Parse(ComposedGolden);
        var line = invented.Descendants("class").Single(x => (string?)x.Attribute("name") == "Widget")
            .Elements("lines").Elements("line").Single(x => (string?)x.Attribute("number") == "12");
        line.SetAttributeValue("condition-coverage", "100% (2/2)");
        Assert.Throws<InvalidDataException>(() => VerifyComposition(invented));
    }

    [Fact]
    public void VerifyComposition_RejectsMalformedClassOrMethodPointsEvenWhenSnapshotsAgree()
    {
        var valid = XDocument.Parse(PrimaryPathsGolden);
        CoverageFanInIndependentVerifier.VerifyComposition([(valid, SourceRoot)], valid, valid);

        var invalidClassPoint = new XDocument(valid);
        invalidClassPoint.Descendants("class").Single().Element("lines")!.Elements("line").First()
            .SetAttributeValue("number", "0");
        var overflowingMethodPoint = new XDocument(valid);
        overflowingMethodPoint.Descendants("method").First().Element("lines")!.Element("line")!
            .SetAttributeValue("hits", "2147483648");

        // Matching copies cannot turn malformed producer observations into valid evidence.
        foreach (var malformed in new[] { invalidClassPoint, overflowingMethodPoint })
            Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition(
                [(malformed, SourceRoot)], new XDocument(malformed), new XDocument(malformed)));
    }

    [Fact]
    public void VerifyNormalization_AllowsPathRewriteButRejectsInventedHitsBranchesAndPoints()
    {
        const string rawText = """
            <coverage lines-covered="0" lines-valid="2" branches-covered="1" branches-valid="2">
              <packages><package name="Generated"><classes>
                <class name="CompilerGenerated" filename="C:\agent\work\obj\Generated.cs">
                  <lines><line number="7" hits="0" branch="true" condition-coverage="50% (1/2)" /><line number="8" hits="0" branch="false" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        const string normalizedText = """
            <coverage lines-covered="0" lines-valid="2" branches-covered="1" branches-valid="2">
              <packages><package name="Generated"><classes>
                <class name="RewrittenCompilerType" filename="obj/Generated.cs">
                  <lines><line number="7" hits="0" branch="true" condition-coverage="50% (1/2)" /><line number="8" hits="0" branch="false" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        var raw = XDocument.Parse(rawText);
        CoverageFanInIndependentVerifier.VerifyNormalization(raw, XDocument.Parse(normalizedText), SourceRoot);

        var inventedHit = XDocument.Parse(normalizedText);
        var firstLine = inventedHit.Descendants("line").First();
        firstLine.SetAttributeValue("hits", "1");
        inventedHit.Root!.SetAttributeValue("lines-covered", "1");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(raw, inventedHit, SourceRoot));

        var promotedPartialBranch = XDocument.Parse(normalizedText);
        promotedPartialBranch.Descendants("line").First().SetAttributeValue("condition-coverage", "100% (2/2)");
        promotedPartialBranch.Root!.SetAttributeValue("branches-covered", "2");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(raw, promotedPartialBranch, SourceRoot));

        var inventedPoint = XDocument.Parse(normalizedText);
        inventedPoint.Descendants("lines").Single().Add(new XElement("line", new XAttribute("number", 9),
            new XAttribute("hits", 0), new XAttribute("branch", "false")));
        inventedPoint.Root!.SetAttributeValue("lines-valid", "3");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(raw, inventedPoint, SourceRoot));
    }

    [Fact]
    public void VerifyNormalization_ResolvesPosixSourcesAndAbsoluteNormalizerPaths()
    {
        const string root = "/private/var/tmp/repo";
        var raw = CoverageWithSource("/", "private/var/tmp/repo/Consumer.Alpha/AlphaDecision.cs");
        var normalized = CoverageWithSource("/", "/private/var/tmp/repo/Consumer.Alpha/AlphaDecision.cs");
        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, root);

        var composed = CoverageWithFilename("Consumer.Alpha/AlphaDecision.cs");
        CoverageFanInIndependentVerifier.VerifyComposition([(raw, root)], composed, composed);
    }

    [Fact]
    public void VerifyNormalization_ResolvesWindowsSourcesAndAbsoluteNormalizerPathsCrossPlatform()
    {
        const string root = @"C:\agent\work";
        var raw = CoverageWithSource(@"C:\agent", @"work\Consumer.Alpha\AlphaDecision.cs");
        var normalized = CoverageWithSource(root, @"C:\agent\work\Consumer.Alpha\AlphaDecision.cs");
        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, root);

        var composed = CoverageWithFilename("Consumer.Alpha/AlphaDecision.cs");
        CoverageFanInIndependentVerifier.VerifyComposition([(raw, root)], composed, composed);
    }

    [Fact]
    public void VerifyNormalization_AllowsClassFoldingWhileRetainingMethodOccurrencesAndPoints()
    {
        var (raw, normalized) = MethodNormalizationGoldens();
        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, "/private/var/tmp/repo");
    }

    [Theory]
    [InlineData("empty-method")]
    [InlineData("method-name")]
    [InlineData("overload-signature")]
    [InlineData("repeated-method")]
    [InlineData("repeated-line")]
    [InlineData("hit")]
    [InlineData("branch-counts")]
    public void VerifyNormalization_RejectsChangedMethodEvidenceWithoutChangingClassTotals(string mutation)
    {
        var (raw, normalized) = MethodNormalizationGoldens();
        var folded = normalized.Descendants("class").Single();
        var runMethods = folded.Descendants("method").Where(x => (string?)x.Attribute("name") == "Run"
            && (string?)x.Attribute("signature") == "()").ToArray();
        switch (mutation)
        {
            case "empty-method":
                folded.Descendants("method").Single(x => (string?)x.Attribute("name") == "Empty").Remove();
                break;
            case "method-name":
                runMethods[0].SetAttributeValue("name", "Renamed");
                break;
            case "overload-signature":
                folded.Descendants("method").Single(x => (string?)x.Attribute("signature") == "(int)")
                    .SetAttributeValue("signature", "(long)");
                break;
            case "repeated-method":
                runMethods[1].Remove();
                break;
            case "repeated-line":
                runMethods[0].Element("lines")!.Elements("line").Last().Remove();
                break;
            case "hit":
                runMethods[0].Element("lines")!.Elements("line").First().SetAttributeValue("hits", "0");
                break;
            case "branch-counts":
                runMethods[0].Element("lines")!.Elements("line").First()
                    .SetAttributeValue("condition-coverage", "0% (0/2)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Equal("1", normalized.Root!.Attribute("lines-covered")?.Value);
        Assert.Equal("2", normalized.Root.Attribute("lines-valid")?.Value);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, "/private/var/tmp/repo"));
    }

    [Fact]
    public void VerifyNormalization_RejectsPathMismatchAndOutsideOrAmbiguousSources()
    {
        const string posixRoot = "/private/var/tmp/repo";
        var posixRaw = CoverageWithSource("/", "private/var/tmp/repo/Consumer.Alpha/AlphaDecision.cs");
        var posixMismatch = CoverageWithSource("/", "/private/var/tmp/repo/Consumer.Beta/AlphaDecision.cs");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            posixRaw, posixMismatch, posixRoot));

        AssertInvalidSourceResolution("/private/var/tmp/repo", "", "src/AlphaDecision.cs");
        AssertInvalidSourceResolution("/private/var/tmp/repo", "/outside/repo", "src/AlphaDecision.cs");
        AssertInvalidSourceResolution("/private/var/tmp/repo", "/private/var/tmp/repo", "../outside.cs");
        AssertInvalidSourceResolution("/private/var/tmp/repo",
            ["/private/var/tmp/repo/Consumer.Alpha", "/private/var/tmp/repo/Consumer.Beta"], "AlphaDecision.cs");

        const string windowsRoot = @"C:\agent\work";
        var windowsRaw = CoverageWithSource(@"C:\agent", @"work\Consumer.Alpha\AlphaDecision.cs");
        var windowsMismatch = CoverageWithSource(windowsRoot, @"C:\agent\work\Consumer.Beta\AlphaDecision.cs");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            windowsRaw, windowsMismatch, windowsRoot));

        AssertInvalidSourceResolution(windowsRoot, "", @"src\AlphaDecision.cs");
        AssertInvalidSourceResolution(windowsRoot, @"D:\outside\repo", @"src\AlphaDecision.cs");
        AssertInvalidSourceResolution(windowsRoot, windowsRoot, @"..\outside.cs");
        AssertInvalidSourceResolution(windowsRoot,
            [@"C:\agent\work\Consumer.Alpha", @"C:\agent\work\Consumer.Beta"], "AlphaDecision.cs");
    }

    private static void AssertInvalidSourceResolution(string root, string source, string filename)
    {
        var report = CoverageWithSource(source, filename);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(report, report, root));
    }

    private static void AssertInvalidSourceResolution(string root, string[] sources, string filename)
    {
        var report = CoverageWithSources(sources, filename);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(report, report, root));
    }

    private static XDocument CoverageWithSource(string source, string filename) => CoverageWithSources([source], filename);

    private static XDocument CoverageWithSources(string[] sources, string filename) => new(
        new XElement("coverage", new XAttribute("lines-covered", 1), new XAttribute("lines-valid", 1),
            new XAttribute("branches-covered", 0), new XAttribute("branches-valid", 0),
            new XElement("sources", sources.Select(x => new XElement("source", x))),
            new XElement("packages", new XElement("package", new XAttribute("name", "Consumer"),
                new XElement("classes", new XElement("class", new XAttribute("name", "AlphaDecision"),
                    new XAttribute("filename", filename), new XElement("lines",
                        new XElement("line", new XAttribute("number", 1), new XAttribute("hits", 1),
                            new XAttribute("branch", "false")))))))));

    private static XDocument CoverageWithFilename(string filename) => new(
        new XElement("coverage", new XAttribute("lines-covered", 1), new XAttribute("lines-valid", 1),
            new XAttribute("branches-covered", 0), new XAttribute("branches-valid", 0),
            new XElement("packages", new XElement("package", new XAttribute("name", "Consumer"),
                new XElement("classes", new XElement("class", new XAttribute("name", "AlphaDecision"),
                    new XAttribute("filename", filename), new XElement("lines",
                        new XElement("line", new XAttribute("number", 1), new XAttribute("hits", 1),
                            new XAttribute("branch", "false")))))))));

    private static (XDocument Raw, XDocument Normalized) MethodNormalizationGoldens() =>
        (XDocument.Parse("""
            <coverage lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="0">
              <sources><source>/</source></sources>
              <packages><package name="Generated"><classes>
                <class name="CompilerGeneratedA" filename="private/var/tmp/repo/Consumer.Alpha/AlphaDecision.cs">
                  <methods>
                    <method name="Run" signature="()"><lines>
                      <line number="7" hits="2" branch="true" condition-coverage="50% (1/2)" />
                      <line number="7" hits="0" branch="false" />
                    </lines></method>
                    <method name="Run" signature="(int)"><lines>
                      <line number="8" hits="0" branch="false" />
                    </lines></method>
                    <method name="Empty" signature="()" />
                  </methods>
                  <lines><line number="100" hits="1" branch="false" /><line number="101" hits="0" branch="false" /></lines>
                </class>
                <class name="CompilerGeneratedB" filename="private/var/tmp/repo/Consumer.Alpha/AlphaDecision.cs">
                  <methods><method name="Run" signature="()"><lines>
                    <line number="7" hits="0" branch="true" condition-coverage="0% (0/1)" />
                  </lines></method></methods>
                  <lines />
                </class>
              </classes></package></packages>
            </coverage>
            """), XDocument.Parse("""
            <coverage lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="0">
              <sources><source>/</source></sources>
              <packages><package name="Generated"><classes>
                <class name="RewrittenCompilerType" filename="/private/var/tmp/repo/Consumer.Alpha/AlphaDecision.cs">
                  <methods>
                    <method name="Run" signature="()"><lines>
                      <line number="7" hits="2" branch="true" condition-coverage="50% (1/2)" />
                      <line number="7" hits="0" branch="false" />
                    </lines></method>
                    <method name="Run" signature="(int)"><lines>
                      <line number="8" hits="0" branch="false" />
                    </lines></method>
                    <method name="Empty" signature="()" />
                    <method name="Run" signature="()"><lines>
                      <line number="7" hits="0" branch="true" condition-coverage="0% (0/1)" />
                    </lines></method>
                  </methods>
                  <lines><line number="100" hits="1" branch="false" /><line number="101" hits="0" branch="false" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """));

    [Fact]
    public void VerifyGates_RecomputesDiffSnapshotAndPatchArithmeticForBothReports()
    {
        var coverage = AggregateCoverage();
        using var primaryGate = Gate(coverage, isPrimary: true);
        using var composedGate = Gate(coverage, isPrimary: false);
        using var policy = PatchPolicy();

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, Diff);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage,
            Diff.Replace("+new", "+changed", StringComparison.Ordinal)));
    }

    [Fact]
    public void VerifyGates_RejectsComposedSummaryThatDisagreesWithClassSnapshot()
    {
        var primary = AggregateCoverage();
        var composed = new XDocument(primary);
        using var primaryGate = Gate(primary, isPrimary: true);
        using var composedGate = Gate(composed, isPrimary: false);
        using var policy = PatchPolicy();
        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, primary, composed, Diff);

        var changedComposed = new XDocument(composed);
        changedComposed.Descendants("line").Single(x => (string?)x.Attribute("number") == "20")
            .SetAttributeValue("hits", "0");
        // The unchanged composed root and report claim 19/20, but only 18 class points remain covered.
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, primary, changedComposed, Diff));
    }

    [Fact]
    public void VerifyGates_RejectsWeakerReportThresholdsAndForgedEffectiveFloor()
    {
        var coverage = AggregateCoverage();
        using var primaryGate = Gate(coverage, isPrimary: true);
        using var composedGate = Gate(coverage, isPrimary: false);
        using var policy = PatchPolicy();

        var weaker = JsonNode.Parse(primaryGate.RootElement.GetRawText())!;
        weaker["thresholds"]!["line"] = 90m;
        weaker["effectiveThresholds"]!["line"] = 89.5m;
        using var weakerReport = JsonDocument.Parse(weaker.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            weakerReport.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, Diff));

        var forgedFloor = JsonNode.Parse(primaryGate.RootElement.GetRawText())!;
        forgedFloor["effectiveThresholds"]!["line"] = 90m;
        using var forgedReport = JsonDocument.Parse(forgedFloor.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            forgedReport.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, Diff));
    }

    [Theory]
    [InlineData("mode", "\"unknown\"")]
    [InlineData("minLine", "-1")]
    [InlineData("minBranch", "101")]
    [InlineData("minPatchLine", "null")]
    [InlineData("minPatchBranch", "\"85\"")]
    [InlineData("tolerance", "101")]
    [InlineData("tolerance", "1e100")]
    [InlineData("patchLineMode", "\"unknown\"")]
    [InlineData("diffBase", "null")]
    [InlineData("diffBase", "\" \"")]
    public void VerifyGates_RejectsMalformedTrustedGatePolicy(string field, string jsonValue)
    {
        var coverage = AggregateCoverage();
        using var primaryGate = Gate(coverage, isPrimary: true);
        using var composedGate = Gate(coverage, isPrimary: false);
        using var policy = PatchPolicy();
        using var malformed = WithJsonValue(policy, "gate." + field, jsonValue);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, malformed.RootElement, coverage, coverage, Diff));
    }

    [Theory]
    [InlineData("passed", "false")]
    [InlineData("coverage", "\"primary-paths.cobertura.xml\"")]
    [InlineData("thresholds.branch", "1")]
    [InlineData("thresholds.patchLine", "null")]
    [InlineData("thresholds.patchBranch", "\"85\"")]
    [InlineData("effectiveThresholds.patchBranch", "80")]
    [InlineData("line.covered", "18")]
    [InlineData("line.valid", "21")]
    [InlineData("line.percent", "94")]
    [InlineData("branch.percent", "84")]
    [InlineData("patchDiffSource.kind", "\"file\"")]
    [InlineData("patchDiffSource.strictness", "\"external\"")]
    [InlineData("patchDiffSource.label", "\"other\"")]
    [InlineData("patchDiffSource.diffBase", "\"other\"")]
    [InlineData("patchDiffSource.path", "\"diff.patch\"")]
    [InlineData("patchDiffSource.bytes", "0")]
    [InlineData("patchDiffSource.empty", "true")]
    [InlineData("patchLine.diffBase", "\"other\"")]
    [InlineData("patchLine.covered", "0")]
    [InlineData("patchBranch.measurable", "2147483648")]
    [InlineData("patchBranch.percent", "99")]
    public void VerifyGates_RejectsForgedReportFactsDespitePassingFlag(string field, string jsonValue)
    {
        var coverage = AggregateCoverage();
        using var primaryGate = Gate(coverage, isPrimary: true);
        using var composedGate = Gate(coverage, isPrimary: false);
        using var policy = PatchPolicy();
        using var forged = WithJsonValue(composedGate, field, jsonValue);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, forged.RootElement, policy.RootElement, coverage, coverage, Diff));
    }

    [Theory]
    [InlineData(false, "TrUe", null)]
    [InlineData(true, "true", "100% (2/1)")]
    [InlineData(false, "true", "0% (0/0)")]
    [InlineData(true, "true", "1/2")]
    [InlineData(false, "true", "100% (2147483648/2147483648)")]
    [InlineData(true, "true", "0% (-1/2)")]
    public void VerifyComposition_RejectsMalformedBranchEvidenceEvenWhenSnapshotsMatch(
        bool methodPoint, string branch, string? condition)
    {
        var malformed = XDocument.Parse(PrimaryPathsGolden);
        var point = methodPoint
            ? malformed.Descendants("method").First().Element("lines")!.Element("line")!
            : malformed.Descendants("class").Single().Element("lines")!.Element("line")!;
        point.SetAttributeValue("branch", branch);
        point.SetAttributeValue("condition-coverage", condition);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition(
            [(malformed, SourceRoot)], new XDocument(malformed), new XDocument(malformed)));
    }

    [Theory]
    [InlineData("/work/repo", "src", "Widget.cs")]
    [InlineData(@"C:\agent\work", "src", "Widget.cs")]
    [InlineData(@"\\server\share\repo", @"\\server\share\repo\src", "Widget.cs")]
    [InlineData(@"\\server\share\repo", "src", @"\\server\share\repo\src\Widget.cs")]
    public void VerifyNormalizationAndComposition_ResolveRelativeAndUncSourceBases(
        string sourceRoot, string source, string filename)
    {
        var raw = CoverageWithSource(source, filename);
        var normalized = CoverageWithFilename("src/Widget.cs");

        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, sourceRoot);
        CoverageFanInIndependentVerifier.VerifyComposition([(raw, sourceRoot)], normalized, normalized);
    }

    [Theory]
    [InlineData("relative/repo", "src", "Widget.cs")]
    [InlineData(@"\\server", "src", "Widget.cs")]
    [InlineData(@"\\server\share\repo", @"\\other\share\repo", "src/Widget.cs")]
    [InlineData(@"\\server\share\repo", @"\\server\other\repo", "src/Widget.cs")]
    [InlineData(@"\\server\share\repo", @"\\server\share\repository", "src/Widget.cs")]
    [InlineData("/work/repo", "/work", "repo")]
    [InlineData("/work/repo", "/work/repo", "/outside/Widget.cs")]
    [InlineData("/work/repo", "/work/repo", "src//Widget.cs")]
    [InlineData("/work/repo", "/work/repo", "src/./Widget.cs")]
    [InlineData("/work/repo", "/work/repo", "src/Widget:stream")]
    [InlineData("/work/repo", "/work/repo", "src/\0Widget.cs")]
    public void VerifyNormalization_RejectsMalformedOrForeignPortableSourcePaths(
        string sourceRoot, string source, string filename)
    {
        var raw = CoverageWithSource(source, filename);
        var normalized = CoverageWithFilename("src/Widget.cs");

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, sourceRoot));
    }

    [Theory]
    [InlineData(false, "number", "not-a-number")]
    [InlineData(false, "hits", "2147483648")]
    [InlineData(true, "number", "0")]
    [InlineData(true, "hits", "-1")]
    [InlineData(true, "hits", "2147483648")]
    public void VerifyNormalization_RejectsMalformedPointNumbersBeforeComparingFidelity(
        bool methodPoint, string attribute, string value)
    {
        var raw = CoverageWithFilename("src/Widget.cs");
        var normalized = new XDocument(raw);
        var cls = normalized.Descendants("class").Single();
        var point = cls.Element("lines")!.Element("line")!;
        if (methodPoint)
        {
            point = new XElement(point);
            cls.Add(new XElement("methods", new XElement("method", new XAttribute("name", "Run"),
                new XAttribute("signature", "()"), new XElement("lines", point))));
        }
        point.SetAttributeValue(attribute, value);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, SourceRoot));
    }

    [Theory]
    [InlineData("src/Tab\tWidget.cs", "\"b/src/Tab\\tWidget.cs\"")]
    [InlineData("src/New\nWidget.cs", "\"b/src/New\\nWidget.cs\"")]
    [InlineData("src/Return\rWidget.cs", "\"b/src/Return\\rWidget.cs\"")]
    [InlineData("src/Quote\"Widget.cs", "\"b/src/Quote\\\"Widget.cs\"")]
    [InlineData("src/😀Widget.cs", "\"b/src/😀Widget.cs\"")]
    public void VerifyGates_AcceptsEscapedGitPathsAndContextWithoutCountingContextAsChanged(
        string filename, string quotedPath)
    {
        var coverage = AggregateCoverage();
        coverage.Descendants("class").Single().SetAttributeValue("filename", filename);
        // Raw string headers can already use CRLF on a Windows checkout, while the hunk uses literal LF.
        var diff = FileDiff(quotedPath, "@@ -1,2 +1,2 @@\n-old\n+new\n context")
            .ReplaceLineEndings("\r\n");
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        // Only line 1 changed; the covered context line must not inflate either patch denominator.
        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);
    }

    [Fact]
    public void VerifyCompositionAndGates_PreserveOriginalPrimaryCompilerPointSummary()
    {
        // The primary summary includes two covered compiler points absent from serialized class records.
        var primary = XDocument.Parse("""
            <coverage lines-covered="3" lines-valid="3" branches-covered="4" branches-valid="4">
              <packages><package name="Core"><classes><class name="Widget" filename="src/Widget.cs"><lines>
                <line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" />
              </lines></class></classes></package></packages>
            </coverage>
            """);
        var primaryPaths = new XDocument(primary);
        var composed = XDocument.Parse("""
            <coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2">
              <packages><package name="Core"><classes><class name="Widget" filename="src/Widget.cs"><lines>
                <line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" />
              </lines></class></classes></package></packages>
            </coverage>
            """);
        // Gate() supplies hand-authored report metadata; no production composer or gate is the oracle.
        using var primaryGate = Gate(primaryPaths, isPrimary: true);
        using var composedGate = Gate(composed, isPrimary: false);
        using var policy = PatchPolicy();
        CoverageFanInIndependentVerifier.VerifyComposition([(primary, SourceRoot)], composed, primaryPaths);
        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, primaryPaths, composed, Diff);

        var changedPrimary = new XDocument(primaryPaths);
        changedPrimary.Root!.SetAttributeValue("lines-covered", "1");
        changedPrimary.Root.SetAttributeValue("lines-valid", "1");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition(
            [(primary, SourceRoot)], composed, changedPrimary));
    }

    [Theory]
    [InlineData("@@ -1, +1 @@\n-old\n+new")]
    [InlineData("@@ -1 +1,2 @@\n-old\n+new")]
    [InlineData("@@ -1 +0,1 @@\n-old\n+new")]
    [InlineData("@@ -1,0 +1 @@\n context")]
    [InlineData("@@ -1 +1,0 @@\n+new")]
    [InlineData("@@ -1 +1,2 @@\n-old\n+new\n?unexpected")]
    [InlineData("@@ -1,2147483648 +1 @@\n-old\n+new")]
    [InlineData("@@ -1 +2147483648 @@\n-old\n+new")]
    public void VerifyGates_RejectsMalformedOrTruncatedHunkRanges(string hunk)
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff("b/src/Widget.cs", hunk);
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyGates_AcceptsQuotedUtf8OctalPathForNativeUnicodeFilename()
    {
        var coverage = AggregateCoverage();
        coverage.Descendants("class").Single().SetAttributeValue("filename", "src/café.cs");
        var diff = """
            diff --git "a/src/caf\303\251.cs" "b/src/caf\303\251.cs"
            --- "a/src/caf\303\251.cs"
            +++ "b/src/caf\303\251.cs"
            @@ -1 +1 @@
            -old
            +new
            """;
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);
    }

    [Fact]
    public void VerifyGates_AcceptsGitTabDelimitedUnquotedPathWithSpaces()
    {
        const string filename = "src/Widget File.cs";
        var coverage = AggregateCoverage();
        coverage.Descendants("class").Single().SetAttributeValue("filename", filename);
        var diff = """
            diff --git a/src/Widget File.cs b/src/Widget File.cs
            --- a/src/Widget File.cs{TAB}
            +++ b/src/Widget File.cs{TAB}
            @@ -1 +1 @@
            -old
            +new
            """.Replace("{TAB}", "\t", StringComparison.Ordinal);
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);
    }

    [Fact]
    public void VerifyGates_RejectsNonTabSuffixAfterQuotedPathHeader()
    {
        var coverage = AggregateCoverage();
        var diff = """
            diff --git "a/src/Widget.cs" "b/src/Widget.cs"
            --- "a/src/Widget.cs"
            +++ "b/src/Widget.cs" suffix
            @@ -1 +1 @@
            -old
            +new
            """;
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyGates_RejectsLiteralBackslashThatAliasesCoveredFile()
    {
        var coverage = AggregateCoverage();
        const string diff = """
            diff --git "a/src\\Widget.cs" "b/src\\Widget.cs"
            --- "a/src\\Widget.cs"
            +++ "b/src\\Widget.cs"
            @@ -1 +1 @@
            -old
            +new
            """;
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyGates_AcceptsAddedSourceLineBeginningWithFileHeaderPrefix()
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff("b/src/Widget.cs", "@@ -1 +1 @@\n-old\n+++ literal source text");
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);
    }

    [Fact]
    public void VerifyGates_AcceptsRenameWithDeletedFileAndMeasurableDestination()
    {
        var coverage = AggregateCoverage();
        var diff = """
            diff --git a/src/Removed.cs b/src/Removed.cs
            deleted file mode 100644
            --- a/src/Removed.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -old
            diff --git "a/src/Old Widget.cs" "b/src/Widget.cs"
            similarity index 80%
            rename from src/Old Widget.cs
            rename to src/Widget.cs
            --- "a/src/Old Widget.cs"
            +++ "b/src/Widget.cs"
            @@ -1 +1 @@
            -old
            +new
            """;
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();
        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);
    }

    [Theory]
    [InlineData("\"b/src/Widget\\377.cs\"")]
    [InlineData("\"b/src/Widget\\777.cs\"")]
    [InlineData("\"b/src/Widget\\q.cs\"")]
    [InlineData("\"b/src/Widget.cs")]
    [InlineData("\"b/src/Widget\\\"")]
    [InlineData("b/../Widget.cs")]
    [InlineData("b/src//Widget.cs")]
    [InlineData("b//absolute/Widget.cs")]
    public void VerifyGates_RejectsMalformedOrEscapingGitPaths(string newPath)
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff(newPath, "@@ -1 +1 @@\n-old\n+new");
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyGates_RejectsChangedFileWithoutMeasurableCoveragePoints()
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff("b/src/Missing.cs", "@@ -1 +1 @@\n-old\n+new");
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var primaryWithoutMeasurements = EmptyPatchMeasurements(primaryGate);
        using var composedWithoutMeasurements = EmptyPatchMeasurements(composedGate);
        using var policy = PatchPolicy();
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryWithoutMeasurements.RootElement, composedWithoutMeasurements.RootElement,
            policy.RootElement, coverage, coverage, diff));
    }

    [Theory]
    [InlineData("changed-count")]
    [InlineData("tolerance")]
    [InlineData("patch-mode")]
    [InlineData("diff-sha")]
    public void VerifyGates_RejectsForgedPatchMetadataAtIndependentSeam(string mutation)
    {
        var coverage = AggregateCoverage();
        using var primaryGate = Gate(coverage, isPrimary: true);
        using var composedGate = Gate(coverage, isPrimary: false);
        using var policy = PatchPolicy();
        var report = JsonNode.Parse(composedGate.RootElement.GetRawText())!;
        switch (mutation)
        {
            case "changed-count": report["patchLine"]!["changed"] = 2; break;
            case "tolerance": report["tolerancePercent"] = 0.4m; break;
            case "patch-mode": report["patchLineMode"] = "measurable"; break;
            case "diff-sha": report["patchDiffSource"]!["sha256"] = new string('0', 64); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        using var forged = JsonDocument.Parse(report.ToJsonString());

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, forged.RootElement, policy.RootElement, coverage, coverage, Diff));
    }

    [Fact]
    public void VerifyGates_RecomputesPatchMetricsWhenForgedReportHashMatchesDiff()
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff("b/src/Widget.cs", "@@ -1 +17 @@\n-old\n+new");
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyNormalization_RejectsRawRootCounterOutsideObservedPointBoundary()
    {
        var (raw, normalized) = MethodNormalizationGoldens();
        raw.Root!.SetAttributeValue("lines-valid", int.MaxValue);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, "/private/var/tmp/repo"));
    }

    [Fact]
    public void VerifyNormalization_RejectsReducedPointDenominatorDespiteUnchangedAggregateTotals()
    {
        var raw = XDocument.Parse("""
            <coverage lines-covered="2" lines-valid="2" branches-covered="3" branches-valid="4">
              <packages><package name="Core"><classes><class name="Widget" filename="src/Widget.cs"><lines>
                <line number="1" hits="1" branch="true" condition-coverage="50% (1/2)" />
                <line number="2" hits="1" branch="true" condition-coverage="100% (2/2)" />
              </lines></class></classes></package></packages>
            </coverage>
            """);
        CoverageFanInIndependentVerifier.VerifyNormalization(raw, new XDocument(raw), SourceRoot);

        var normalized = new XDocument(raw);
        var points = normalized.Descendants("line").ToArray();
        points[0].SetAttributeValue("condition-coverage", "0% (0/1)");
        points[1].SetAttributeValue("condition-coverage", "100% (3/3)");

        // Both reports still total 3/4, and neither point's covered ratio improves.
        // Moving a denominator to another point must not conceal a dropped branch.
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, SourceRoot));
    }

    [Fact]
    public void VerifyNormalization_AllowsConservativeFoldedDuplicatesWithoutInventingMethodFacts()
    {
        var raw = XDocument.Parse("""
            <coverage lines-valid="1" lines-covered="1" branches-valid="3" branches-covered="1">
              <packages><package name="P"><classes><class name="Generated" filename="src/A.cs">
                <methods><method name="Run" signature="()"><lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"/></lines></method></methods>
                <lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"/></lines>
              </class></classes></package></packages>
            </coverage>
            """);
        var normalized = XDocument.Parse("""
            <coverage lines-valid="2" lines-covered="2" branches-valid="6" branches-covered="2">
              <packages><package name="P"><classes>
                <class name="A" filename="src/A.cs"><methods><method name="Run" signature="()"><lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"/></lines></method></methods><lines><line number="1" hits="1" branch="true" condition-coverage="33.3% (1/3)"/></lines></class>
                <class name="A.Nested" filename="src/A.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="33.3% (1/3)"/></lines></class>
              </classes></package></packages>
            </coverage>
            """);

        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, SourceRoot);
        var promoted = new XDocument(normalized);
        foreach (var cls in promoted.Descendants("class"))
            cls.Element("lines")!.Element("line")!.SetAttributeValue("condition-coverage", "100% (3/3)");
        promoted.Root!.SetAttributeValue("branches-covered", "6");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(raw, promoted, SourceRoot));
        normalized.Descendants("method").Single().Remove();
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, SourceRoot));
    }

    [Fact]
    public void VerifyGates_RejectsForgedPassAndReducedPatchBranchDenominator()
    {
        var primaryCoverage = AggregateCoverage();
        using var primaryGate = Gate(primaryCoverage, isPrimary: true);
        using var policy = PatchPolicy();
        using var forgedPass = Gate(primaryCoverage, isPrimary: false, patchLineCovered: 0);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, forgedPass.RootElement, policy.RootElement, primaryCoverage, primaryCoverage, Diff));

        var lowerDenominator = AggregateCoverage();
        var patchLine = lowerDenominator.Descendants("line").Single(x => (string?)x.Attribute("number") == "1");
        patchLine.SetAttributeValue("condition-coverage", "100% (1/1)");
        lowerDenominator.Descendants("line").Single(x => (string?)x.Attribute("number") == "17")
            .SetAttributeValue("condition-coverage", "100% (1/1)");
        lowerDenominator.Root!.SetAttributeValue("branches-valid", "19");
        lowerDenominator.Root.SetAttributeValue("branches-covered", "17");
        using var lowerGate = Gate(lowerDenominator, isPrimary: false, patchBranchCovered: 1, patchBranchMeasurable: 1);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, lowerGate.RootElement, policy.RootElement, primaryCoverage, lowerDenominator, Diff));
    }

    [Fact]
    public void VerifyGates_AcceptsNonemptyPatchWithNoMeasurableBranchesAtOneHundredPercent()
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff("b/src/Widget.cs", "@@ -20 +20 @@\n-old\n+new");
        using var primaryGate = Gate(coverage, isPrimary: true, patchBranchCovered: 0,
            patchBranchMeasurable: 0, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, patchBranchCovered: 0,
            patchBranchMeasurable: 0, diffText: diff);
        using var policy = PatchPolicy();

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);

        using var forged = WithJsonValue(composedGate, "patchBranch.percent", "0");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, forged.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyGates_AggregateOnlyRequiresTheSamePrimaryReportAndNoPatchEvidence()
    {
        var coverage = AggregateCoverage();
        using var report = AggregateOnlyGate(coverage);
        using var policy = JsonDocument.Parse("""
            {"gate":{"mode":"aggregateOnly","minLine":95,"minBranch":85,"minPatchLine":95,"minPatchBranch":85,
              "tolerance":0.5,"patchLineMode":"codecov","diffBase":null}}
            """);

        CoverageFanInIndependentVerifier.VerifyGates(report.RootElement, report.RootElement, policy.RootElement,
            coverage, coverage);

        using var forgedPatch = Gate(coverage, isPrimary: false);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            report.RootElement, forgedPatch.RootElement, policy.RootElement, coverage, coverage));
    }

    [Fact]
    public void VerifyPrimaryInventory_RejectsMissingDuplicateOrFailedProjectObservations()
    {
        using var good = JsonDocument.Parse("""
            {"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":2},"projects":[
              {"project":"tests/A.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"},
              {"project":"tests/B.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}
            """);
        CoverageFanInIndependentVerifier.VerifyPrimaryInventory(good.RootElement,
            ["tests/A.csproj", "tests/B.csproj"], "Release", "collector");

        using var missing = JsonDocument.Parse("""
            {"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[
              {"project":"tests/A.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}
            """);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyPrimaryInventory(missing.RootElement,
            ["tests/A.csproj", "tests/B.csproj"], "Release", "collector"));
    }

    private static void VerifyComposition(XDocument composed) => CoverageFanInIndependentVerifier.VerifyComposition(
        [(XDocument.Parse(Primary), SourceRoot), (XDocument.Parse(Native), @"C:/agent/work")],
        composed, XDocument.Parse(PrimaryPathsGolden));

    [Theory]
    [InlineData("configuration", "\"Debug\"")]
    [InlineData("coverageDriver", "\"msbuild\"")]
    [InlineData("merge.exitCode", "1")]
    [InlineData("artifacts.coverageFiles", "0")]
    [InlineData("projects", "{}")]
    [InlineData("projects", "[]")]
    public void VerifyPrimaryInventoryRejectsInconsistentExecutionMetadata(string field, string value)
    {
        using var good = JsonDocument.Parse("""
            {"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[
            {"project":"checks/A.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}
            """);
        using var bad = WithJsonValue(good, field, value);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyPrimaryInventory(
            bad.RootElement, ["checks/A.csproj"], "Release", "collector"));
    }

    [Theory]
    [InlineData("exitCode", "1")]
    [InlineData("executionStatus", "\"skipped\"")]
    [InlineData("coverageArtifactStatus", "\"missing\"")]
    [InlineData("project", "\"checks/Other.csproj\"")]
    public void VerifyPrimaryInventoryRejectsFailedOrSubstitutedProject(string field, string value)
    {
        var entry = JsonNode.Parse("""{"project":"checks/A.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}""")!;
        entry[field] = JsonNode.Parse(value);
        var timings = new JsonObject
        {
            ["configuration"] = "Release",
            ["coverageDriver"] = "collector",
            ["merge"] = new JsonObject { ["exitCode"] = 0 },
            ["artifacts"] = new JsonObject { ["coverageFiles"] = 1 },
            ["projects"] = new JsonArray(entry)
        };
        using var document = JsonDocument.Parse(timings.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyPrimaryInventory(
            document.RootElement, ["checks/A.csproj"], "Release", "collector"));
    }

    [Fact]
    public void VerifierRejectsNullEmptyAndMalformedInputsAtItsBoundary()
    {
        var report = XDocument.Parse(PrimaryPathsGolden);
        foreach (var inputs in new IReadOnlyList<(XDocument, string)>[]
        {
            null!, [], [(null!, SourceRoot)], [(report, " ")]
        })
            Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition(inputs, report, report));
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(null!, report, SourceRoot));
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(report, null!, SourceRoot));
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(report, report, " "));
        using var timings = JsonDocument.Parse("{}");
        foreach (var inventory in new IReadOnlyList<string>[] { null!, [], [" "], ["same", "same"] })
            Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyPrimaryInventory(timings.RootElement, inventory, "Release", "collector"));
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition([(report, SourceRoot)], new XDocument(), report));
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyComposition([(report, SourceRoot)], XDocument.Parse("<other/>"), report));
    }

    [Theory]
    [InlineData("thresholds.line", "1")]
    [InlineData("effectiveThresholds.branch", "1")]
    [InlineData("line.valid", "-1")]
    [InlineData("line.covered", "-1")]
    [InlineData("branch.covered", "21")]
    [InlineData("patchDiffSource.sha256", "null")]
    [InlineData("patchDiffSource.bytes", "\"1\"")]
    [InlineData("patchDiffSource.bytes", "1e30")]
    [InlineData("patchDiffSource.empty", "0")]
    [InlineData("patchLine.changed", "2")]
    [InlineData("patchLine.percent", "null")]
    [InlineData("patchLine.covered", "2147483648")]
    [InlineData("patchLineMode", "true")]
    [InlineData("patchLineMode", "\"measurable\"")]
    [InlineData("patchBranch.percent", "1e100")]
    public void VerifyGatesRejectsIndependentCounterAndTypeFaults(string field, string value)
    {
        var coverage = AggregateCoverage();
        using var primary = Gate(coverage, true);
        using var composed = Gate(coverage, false);
        using var policy = PatchPolicy();
        using var bad = WithJsonValue(composed, field, value);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(primary.RootElement,
            bad.RootElement, policy.RootElement, coverage, coverage, Diff));
    }

    [Theory]
    [InlineData("\"b/src/Alarm\\aWidget.cs\"")]
    [InlineData("\"b/src/Back\\bWidget.cs\"")]
    [InlineData("\"b/src/Vertical\\vWidget.cs\"")]
    [InlineData("\"b/src/Form\\fWidget.cs\"")]
    [InlineData("\"b/src/Bad\\qWidget.cs\"")]
    public void VerifyGatesRejectsEscapedPathsWhoseDeclaredMeasurementsDoNotMatchCoveredSource(string path)
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff(path, "@@ -1 +1 @@\n-old\n+new");
        using var primary = Gate(coverage, true, diffText: diff);
        using var composed = Gate(coverage, false, diffText: diff);
        using var policy = PatchPolicy();
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(primary.RootElement,
            composed.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Theory]
    [InlineData("[]", "[]")]
    [InlineData("[1,true,false,null,\"x\",{},[]]", "[1.0,true,false,null,\"x\",{},[]]")]
    [InlineData("[1]", "[1,2]")]
    [InlineData("[1]", "[\"1\"]")]
    [InlineData("[1]", "[2]")]
    [InlineData("[\"one\"]", "[\"two\"]")]
    [InlineData("{\"passed\":true}", "{\"passed\":true,\"line\":[]}")]
    [InlineData("{\"passed\":true}", "{\"other\":true}")]
    public void VerifyGates_RejectsMalformedBaselineReportShapesEvenWhenJsonValuesAreEquivalent(
        string primaryJson, string composedJson)
    {
        var coverage = AggregateCoverage();
        using var primary = JsonDocument.Parse(primaryJson);
        using var composed = JsonDocument.Parse(composedJson);
        using var policy = JsonDocument.Parse("""
            {"gate":{"mode":"aggregateOnly","minLine":95,"minBranch":85,"minPatchLine":95,"minPatchBranch":85,
              "tolerance":0.5,"patchLineMode":"codecov","diffBase":null}}
            """);

        // Array equality, numeric lexical equivalence and matching partial objects cannot supply a gate report.
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primary.RootElement, composed.RootElement, policy.RootElement, coverage, coverage));
    }

    [Theory]
    [InlineData("passed", "0")]
    [InlineData("line", "[]")]
    [InlineData("line.covered", "\"19\"")]
    [InlineData("line.valid", "null")]
    [InlineData("thresholds", "[]")]
    [InlineData("effectiveThresholds.patchLine", "\"94.5\"")]
    [InlineData("patchDiffSource", "[]")]
    [InlineData("patchDiffSource.bytes", "null")]
    [InlineData("patchDiffSource.empty", "\"false\"")]
    [InlineData("patchLine.diffBase", "42")]
    public void VerifyGates_RejectsJsonKindsThatCannotRepresentRequiredCounterFacts(string field, string jsonValue)
    {
        var coverage = AggregateCoverage();
        using var primary = Gate(coverage, isPrimary: true);
        using var composed = Gate(coverage, isPrimary: false);
        using var policy = PatchPolicy();
        using var malformed = WithJsonValue(composed, field, jsonValue);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primary.RootElement, malformed.RootElement, policy.RootElement, coverage, coverage, Diff));
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(0, 1, 3)]
    [InlineData(1, 0, 3)]
    [InlineData(1, 1, 4)]
    public void VerifyGates_DuplicateMethodAndClassPointsRetainOneChangedLineAndAllBranchFacts(
        int firstHits, int secondHits, int expectedBranches)
    {
        var coverage = AggregateCoverage();
        var cls = coverage.Descendants("class").Single();
        var methods = new XElement("methods");
        foreach (var hits in new[] { firstHits, secondHits })
        {
            var point = new XElement("line", new XAttribute("number", 1), new XAttribute("hits", hits),
                new XAttribute("branch", hits == 1 ? "true" : "false"));
            if (hits == 1) point.SetAttributeValue("condition-coverage", "100% (1/1)");
            methods.Add(new XElement("method", new XAttribute("name", "Run"), new XAttribute("signature", "()"),
                new XElement("lines", point)));
        }
        cls.AddFirst(methods);
        // The class contributes two branches; each covered method occurrence contributes one.
        // The hand-authored vectors cover false/false, false/true and true/false hit merging.
        CoverageFanInIndependentVerifier.VerifyComposition([(coverage, SourceRoot)], coverage, coverage);
        using var primary = Gate(coverage, isPrimary: true,
            patchBranchCovered: expectedBranches, patchBranchMeasurable: expectedBranches);
        using var composed = Gate(coverage, isPrimary: false,
            patchBranchCovered: expectedBranches, patchBranchMeasurable: expectedBranches);
        using var policy = PatchPolicy();
        CoverageFanInIndependentVerifier.VerifyGates(primary.RootElement, composed.RootElement,
            policy.RootElement, coverage, coverage, Diff);

        using var invented = Gate(coverage, isPrimary: false,
            patchBranchCovered: expectedBranches + 1, patchBranchMeasurable: expectedBranches + 1);
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primary.RootElement, invented.RootElement, policy.RootElement, coverage, coverage, Diff));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VerifyNormalizationAndCollectorMethods_PreserveOptionalMethodIdentityFields(
        bool hasName, bool hasSignature)
    {
        var raw = CoverageWithFilename("src/Widget.cs");
        var method = new XElement("method", new XElement("lines",
            new XElement("line", new XAttribute("number", 1), new XAttribute("hits", 2),
                new XAttribute("branch", "false"))));
        if (hasName) method.SetAttributeValue("name", "Run");
        if (hasSignature) method.SetAttributeValue("signature", "()");
        raw.Descendants("class").Single().Add(new XElement("methods", method));
        var normalized = new XDocument(raw);
        CoverageFanInIndependentVerifier.VerifyComposition([(raw, SourceRoot)], normalized, normalized);
        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, SourceRoot);
        CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, normalized, SourceRoot);

        var changed = normalized.Descendants("method").Single();
        if (!hasName) changed.SetAttributeValue("name", "InventedName");
        else if (!hasSignature) changed.SetAttributeValue("signature", "(int)");
        else changed.SetAttributeValue("name", "Renamed");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, SourceRoot));
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyCollectorMethods(
            raw, normalized, SourceRoot));
    }

    [Theory]
    [InlineData("missing-method")]
    [InlineData("renamed-method")]
    [InlineData("changed-numeric-hits")]
    [InlineData("extra-method-only-owner")]
    public void VerifyCollectorMethods_RejectsCountOrExactFactChangesUnderOriginalClassOwners(string mutation)
    {
        var raw = XDocument.Parse(PrimaryPathsGolden);
        raw.Descendants("method").First().Element("lines")!.Element("line")!.SetAttributeValue("hits", "2");
        var normalized = new XDocument(raw);
        CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, normalized, SourceRoot);
        var first = normalized.Descendants("method").First();
        switch (mutation)
        {
            case "missing-method": first.Remove(); break;
            case "renamed-method": first.SetAttributeValue("name", "Other"); break;
            case "changed-numeric-hits": first.Element("lines")!.Element("line")!.SetAttributeValue("hits", "3"); break;
            case "extra-method-only-owner":
                normalized.Descendants("classes").Single().Add(new XElement("class",
                    new XAttribute("name", "Extra"), new XAttribute("filename", "src/Extra.cs"),
                    new XElement("methods", new XElement(first))));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyCollectorMethods(
            raw, normalized, SourceRoot));
    }

    [Theory]
    [InlineData("/", "/Widget.cs")]
    [InlineData(@"C:\", @"C:\Widget.cs")]
    [InlineData(@"\\server\share\", @"\\server\share\Widget.cs")]
    public void VerifyNormalizationAndComposition_ResolveFilesDirectlyUnderFilesystemRoots(
        string sourceRoot, string filename)
    {
        var raw = CoverageWithFilename(filename);
        var normalized = CoverageWithFilename("Widget.cs");

        CoverageFanInIndependentVerifier.VerifyNormalization(raw, normalized, sourceRoot);
        CoverageFanInIndependentVerifier.VerifyComposition([(raw, sourceRoot)], normalized, normalized);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/repo", "/repo")]
    [InlineData(@"C:\repo", @"C:\repo")]
    [InlineData(@"C:\repo", @"c:\repo\Widget.cs")]
    [InlineData(@"\\server\share\repo", @"\\server\share")]
    [InlineData(@"\\server\share\repo", @"\\server\share\repository\Widget.cs")]
    public void VerifyNormalization_RejectsRootObjectsAndNonmatchingAbsoluteRootIdentities(
        string sourceRoot, string filename)
    {
        var raw = CoverageWithFilename(filename);
        var normalized = CoverageWithFilename("Widget.cs");

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(
            raw, normalized, sourceRoot));
    }

    [Theory]
    [InlineData("patchDiffSource", "{}")]
    [InlineData("patchLine", "{}")]
    [InlineData("patchBranch", "{}")]
    public void VerifyGatesAggregateOnlyRejectsPatchFactsEvenWhenBothReceiptsAgree(string field, string value)
    {
        var coverage = AggregateCoverage();
        using var report = AggregateOnlyGate(coverage);
        using var policy = JsonDocument.Parse("""
            {"gate":{"mode":"aggregateOnly","minLine":95,"minBranch":85,"minPatchLine":95,"minPatchBranch":85,
              "tolerance":0.5,"patchLineMode":"codecov","diffBase":null}}
            """);
        CoverageFanInIndependentVerifier.VerifyGates(report.RootElement, report.RootElement, policy.RootElement,
            coverage, coverage);
        using var forged = WithJsonValue(report, field, value);

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            forged.RootElement, forged.RootElement, policy.RootElement, coverage, coverage));
    }

    [Fact]
    public void VerifyGatesMeasurableModeCountsAHitWithoutClaimingCompleteBranchCoverage()
    {
        var primary = AggregateCoverage();
        var composed = AggregateCoverage();
        composed.Descendants("line").First().SetAttributeValue("condition-coverage", "50% (1/2)");
        composed.Root!.SetAttributeValue("branches-covered", "16");
        using var primaryTemplate = Gate(primary, isPrimary: true);
        using var composedTemplate = Gate(composed, isPrimary: false, patchBranchCovered: 1);
        using var primaryGate = WithJsonValue(primaryTemplate, "patchLineMode", "\"measurable\"");
        var composedNode = JsonNode.Parse(composedTemplate.RootElement.GetRawText())!;
        composedNode["patchLineMode"] = "measurable";
        composedNode["thresholds"]!["patchBranch"] = 50;
        composedNode["effectiveThresholds"]!["patchBranch"] = 49.5m;
        using var composedGate = JsonDocument.Parse(composedNode.ToJsonString());
        using var policy = JsonDocument.Parse("""
            {"gate":{"mode":"aggregateAndPatch","minLine":95,"minBranch":85,"minPatchLine":95,"minPatchBranch":50,
              "tolerance":0.5,"patchLineMode":"measurable","diffBase":"base"}}
            """);

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, primary, composed, Diff);

        using var codecovPrimary = WithJsonValue(primaryGate, "patchLineMode", "\"codecov\"");
        using var codecovComposed = WithJsonValue(composedGate, "patchLineMode", "\"codecov\"");
        using var codecovPolicy = WithJsonValue(policy, "gate.patchLineMode", "\"codecov\"");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            codecovPrimary.RootElement, codecovComposed.RootElement, codecovPolicy.RootElement, primary, composed, Diff));
    }

    [Theory]
    [InlineData("hits", "3")]
    [InlineData("number", "4")]
    [InlineData("name", "Other")]
    [InlineData("signature", "(string)")]
    public void VerifyCompositionRejectsChangedMethodFactsWithUnchangedMethodCountAndClassTotals(string field, string value)
    {
        var composed = XDocument.Parse(ComposedGolden);
        var method = composed.Descendants("class").Single(x => (string?)x.Attribute("name") == "Widget")
            .Descendants("method").First();
        var target = field is "hits" or "number" ? method.Descendants("line").Single() : method;
        target.SetAttributeValue(field, value);

        Assert.Throws<InvalidDataException>(() => VerifyComposition(composed));
    }

    [Theory]
    [InlineData("diff --git a/src/Second.cs b/src/Second.cs", "-old\n+new")]
    [InlineData("diff --git a/src/Second.cs b/src/Second.cs", "-old\n-old\n+new")]
    [InlineData("diff --git a/src/Second.cs b/src/Second.cs", "-old\n+new\n+new")]
    [InlineData("@@ -2 +2 @@", "-old\n+new")]
    [InlineData("@@ -2 +2 @@", "-old\n-old\n+new")]
    [InlineData("@@ -2 +2 @@", "-old\n+new\n+new")]
    public void VerifyGatesRejectsASecondSectionBeforeBothHunkRangesAreConsumed(string nextSection, string incompleteBody)
    {
        var coverage = AggregateCoverage();
        var diff = FileDiff("b/src/Widget.cs", "@@ -1,2 +1,2 @@\n" + incompleteBody + "\n" + nextSection);
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyGatesCountsARepeatedChangedLineOnceAcrossCompleteDiffSections()
    {
        var coverage = AggregateCoverage();
        var section = FileDiff("src/Widget.cs", "@@ -1 +1 @@\n-old\n+new");
        var diff = section + "\n" + section;
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        CoverageFanInIndependentVerifier.VerifyGates(primaryGate.RootElement, composedGate.RootElement,
            policy.RootElement, coverage, coverage, diff);

        using var doubleCounted = WithJsonValue(composedGate, "patchLine.changed", "2");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, doubleCounted.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    [Fact]
    public void VerifyNormalizationRejectsMethodOnlyEvidenceEvenWhenBothDocumentsAgree()
    {
        var raw = XDocument.Parse(PrimaryPathsGolden);
        raw.Descendants("class").Single().Element("lines")!.RemoveNodes();
        raw.Root!.SetAttributeValue("lines-valid", "0");
        raw.Root.SetAttributeValue("lines-covered", "0");
        raw.Root.SetAttributeValue("branches-valid", "0");
        raw.Root.SetAttributeValue("branches-covered", "0");

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNormalization(raw, new XDocument(raw), SourceRoot));
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.ValidateNormalization(raw, new XDocument(raw), SourceRoot));
    }

    private static string FileDiff(string newPath, string hunk) => $"""
        diff --git a/src/Widget.cs {newPath}
        --- a/src/Widget.cs
        +++ {newPath}
        {hunk}
        """;

    private static JsonDocument EmptyPatchMeasurements(JsonDocument gate)
    {
        var report = JsonNode.Parse(gate.RootElement.GetRawText())!.AsObject();
        foreach (var metricName in new[] { "patchLine", "patchBranch" })
        {
            report[metricName]!["measurable"] = 0;
            report[metricName]!["covered"] = 0;
            report[metricName]!["percent"] = 0;
        }
        return JsonDocument.Parse(report.ToJsonString());
    }

    private static JsonDocument WithJsonValue(JsonDocument original, string field, string jsonValue)
    {
        var node = JsonNode.Parse(original.RootElement.GetRawText())!;
        var path = field.Split('.');
        var parent = node;
        foreach (var segment in path[..^1])
            parent = parent[segment]!;
        parent[path[^1]] = JsonNode.Parse(jsonValue);
        return JsonDocument.Parse(node.ToJsonString());
    }

    private static XDocument AggregateCoverage()
    {
        var lines = new XElement("lines");
        lines.Add(new XElement("line", new XAttribute("number", 1), new XAttribute("hits", 1),
            new XAttribute("branch", "true"), new XAttribute("condition-coverage", "100% (2/2)")));
        for (var number = 2; number <= 16; number++)
            lines.Add(new XElement("line", new XAttribute("number", number), new XAttribute("hits", 1),
                new XAttribute("branch", "true"), new XAttribute("condition-coverage", "100% (1/1)")));
        lines.Add(new XElement("line", new XAttribute("number", 17), new XAttribute("hits", 1),
            new XAttribute("branch", "true"), new XAttribute("condition-coverage", "0% (0/1)")));
        lines.Add(new XElement("line", new XAttribute("number", 18), new XAttribute("hits", 1),
            new XAttribute("branch", "true"), new XAttribute("condition-coverage", "0% (0/1)")));
        lines.Add(new XElement("line", new XAttribute("number", 19), new XAttribute("hits", 0),
            new XAttribute("branch", "true"), new XAttribute("condition-coverage", "0% (0/1)")));
        lines.Add(new XElement("line", new XAttribute("number", 20), new XAttribute("hits", 1), new XAttribute("branch", "false")));
        return new XDocument(new XElement("coverage", new XAttribute("lines-covered", 19), new XAttribute("lines-valid", 20),
            new XAttribute("branches-covered", 17), new XAttribute("branches-valid", 20),
            new XElement("packages", new XElement("package", new XAttribute("name", "Core"), new XElement("classes",
                new XElement("class", new XAttribute("name", "Widget"), new XAttribute("filename", "src/Widget.cs"), lines))))));
    }

    private static JsonDocument Gate(XDocument coverage, bool isPrimary, int patchLineCovered = 1,
        int patchBranchCovered = 2, int patchBranchMeasurable = 2, string? diffText = null)
    {
        var bytes = Encoding.UTF8.GetBytes(diffText ?? Diff);
        var branchPercent = patchBranchMeasurable == 0 ? 100m : (decimal)patchBranchCovered * 100m / patchBranchMeasurable;
        var lineThreshold = isPrimary ? 95m : 0m;
        var branchThreshold = isPrimary ? 85m : 0m;
        var patchLineThreshold = isPrimary ? (decimal?)null : 95m;
        var patchBranchThreshold = isPrimary ? (decimal?)null : 85m;
        var root = coverage.Root!;
        var lineCovered = (int)root.Attribute("lines-covered")!;
        var lineValid = (int)root.Attribute("lines-valid")!;
        var branchCovered = (int)root.Attribute("branches-covered")!;
        var branchValid = (int)root.Attribute("branches-valid")!;
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            passed = true,
            coverage = isPrimary ? "primary-paths.cobertura.xml" : "coverage.cobertura.xml",
            tolerancePercent = 0.5m,
            thresholds = new { line = lineThreshold, branch = branchThreshold, patchLine = patchLineThreshold, patchBranch = patchBranchThreshold },
            effectiveThresholds = new
            {
                line = Math.Max(0m, lineThreshold - 0.5m),
                branch = Math.Max(0m, branchThreshold - 0.5m),
                patchLine = patchLineThreshold is { } pl ? (decimal?)Math.Max(0m, pl - 0.5m) : null,
                patchBranch = patchBranchThreshold is { } pb ? (decimal?)Math.Max(0m, pb - 0.5m) : null
            },
            patchLineMode = "codecov",
            patchDiffSource = new
            {
                kind = "git-base",
                label = "base",
                strictness = "local-git",
                diffBase = "base",
                path = (string?)null,
                bytes = bytes.LongLength,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                empty = false
            },
            line = new { covered = lineCovered, valid = lineValid, percent = Percent(lineCovered, lineValid) },
            branch = new { covered = branchCovered, valid = branchValid, percent = Percent(branchCovered, branchValid) },
            patchLine = new { diffBase = "base", changed = 1, measurable = 1, covered = patchLineCovered, percent = patchLineCovered * 100m },
            patchBranch = new { diffBase = "base", changed = 1, measurable = patchBranchMeasurable, covered = patchBranchCovered, percent = branchPercent }
        }));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("t")]
    [InlineData("n")]
    [InlineData("v")]
    [InlineData("f")]
    [InlineData("r")]
    [InlineData("000")]
    public void VerifyGates_RejectsEscapedControlSuffixInsteadOfAliasingCoveredSource(string escape)
    {
        var coverage = AggregateCoverage();
        var diff = "diff --git a/src/Widget.cs b/src/Widget.cs\n--- a/src/Widget.cs\n+++ \"b/src/Widget.cs\\"
            + escape + "\"\n@@ -0,0 +1 @@\n+changed\n";
        using var primaryGate = Gate(coverage, isPrimary: true, diffText: diff);
        using var composedGate = Gate(coverage, isPrimary: false, diffText: diff);
        using var policy = PatchPolicy();

        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyGates(
            primaryGate.RootElement, composedGate.RootElement, policy.RootElement, coverage, coverage, diff));
    }

    private static JsonDocument PatchPolicy() => JsonDocument.Parse("""
        {"gate":{"mode":"aggregateAndPatch","minLine":95,"minBranch":85,"minPatchLine":95,"minPatchBranch":85,
          "tolerance":0.5,"patchLineMode":"codecov","diffBase":"base"}}
        """);

    private static JsonDocument AggregateOnlyGate(XDocument coverage)
    {
        var root = coverage.Root!;
        var lineCovered = (int)root.Attribute("lines-covered")!;
        var lineValid = (int)root.Attribute("lines-valid")!;
        var branchCovered = (int)root.Attribute("branches-covered")!;
        var branchValid = (int)root.Attribute("branches-valid")!;
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            passed = true,
            coverage = "primary-paths.cobertura.xml",
            tolerancePercent = 0.5m,
            thresholds = new { line = 95m, branch = 85m, patchLine = (decimal?)null, patchBranch = (decimal?)null },
            effectiveThresholds = new { line = 94.5m, branch = 84.5m, patchLine = (decimal?)null, patchBranch = (decimal?)null },
            patchLineMode = (string?)null,
            patchDiffSource = (object?)null,
            line = new { covered = lineCovered, valid = lineValid, percent = Percent(lineCovered, lineValid) },
            branch = new { covered = branchCovered, valid = branchValid, percent = Percent(branchCovered, branchValid) },
            patchLine = (object?)null,
            patchBranch = (object?)null
        }));
    }

    private static decimal Percent(int covered, int valid) => Math.Round((decimal)covered * 100m / valid, 4,
        MidpointRounding.AwayFromZero);
}
