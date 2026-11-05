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

    [Theory]
    [InlineData("@@ -1, +1 @@\n-old\n+new")]
    [InlineData("@@ -1 +1,2 @@\n-old\n+new")]
    [InlineData("@@ -1 +0,1 @@\n-old\n+new")]
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

    [Fact]
    public void VerifyGates_RejectsInvalidUtf8OctalPath()
    {
        var coverage = AggregateCoverage();
        var diff = """
            diff --git "a/src/Widget\377.cs" "b/src/Widget\377.cs"
            --- "a/src/Widget\377.cs"
            +++ "b/src/Widget\377.cs"
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
        var branchPercent = (decimal)patchBranchCovered * 100m / patchBranchMeasurable;
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
