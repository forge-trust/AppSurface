using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInComposerTests
{
    private const string WindowsRoot = @"C:\agent\work";

    [Theory]
    [InlineData("/agent/work", "/", "agent/work/src/Widget.cs", "/agent/work/src/Widget.cs")]
    [InlineData("C:/agent/work", "C:/", "agent/work/src/Widget.cs", "C:/agent/work/src/Widget.cs")]
    [InlineData("//server/share/work", "//server/share/", "work/src/Widget.cs", "//server/share/work/src/Widget.cs")]
    public void Collector_source_bases_and_absolute_normalized_paths_identify_the_same_file(
        string repository, string source, string rawFilename, string normalizedFilename)
    {
        static XDocument Report(string filename, string source) => new(new XElement("coverage",
            new XAttribute("branches-valid", 0), new XElement("sources", new XElement("source", source)),
            new XElement("packages", new XElement("package", new XAttribute("name", "Core"),
                new XElement("classes", new XElement("class", new XAttribute("name", "Widget"),
                    new XAttribute("filename", filename), new XElement("lines", new XElement("line",
                        new XAttribute("number", 1), new XAttribute("hits", 1), new XAttribute("branch", "false")))))))));
        var raw = Report(rawFilename, source);
        var normalized = Report(normalizedFilename, source);
        CoverageFanInComposer.ValidateNormalization(raw, normalized, repository);
        var composed = CoverageFanInComposer.Compose([(raw, repository), (normalized, repository)]);
        Assert.Equal("src/Widget.cs", Assert.Single(composed.Descendants("class")).Attribute("filename")?.Value);
        raw.Root!.Element("sources")!.Add(new XElement("source", repository + "/different"));
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.Compose([(raw, repository)]));
    }

    [Fact]
    public void Compose_ShouldPreserveRecordOccurrencesOverloadsAndNativeOnlyPoints()
    {
        const string primary = """
            <coverage lines-covered="1" lines-valid="6" branches-covered="0" branches-valid="8">
              <packages><package name="Core"><classes>
                <class name="Widget" filename="C:\agent\work\src\Widget.cs">
                  <methods>
                    <method name="Run" signature="()"><lines><line number="20" hits="0" branch="false" /><line number="20" hits="1" branch="false" /></lines></method>
                    <method name="Run" signature="(int)"><lines><line number="21" hits="1" branch="false" /></lines></method>
                    <method name="Run" signature="()"><lines><line number="22" hits="0" branch="false" /></lines></method>
                  </methods>
                  <lines>
                    <line number="10" hits="0" branch="true" condition-coverage="0% (0/2)" />
                    <line number="10" hits="1" branch="true" condition-coverage="0% (0/2)" />
                    <line number="11" hits="0" branch="true" condition-coverage="0% (0/2)" />
                    <line number="12" hits="0" branch="true" condition-coverage="0% (0/2)" />
                  </lines>
                </class>
                <class name="Widget" filename="src/Widget.cs">
                  <lines><line number="30" hits="0" branch="false" /></lines>
                </class>
                <class name="PrimaryOnly" filename="src/PrimaryOnly.cs">
                  <lines><line number="40" hits="0" branch="false" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        const string supplementOne = """
            <coverage lines-covered="5" lines-valid="6" branches-covered="5" branches-valid="6">
              <packages><package name="Core"><classes>
                <class name="Widget" filename="C:/agent/work/src/Widget.cs">
                  <methods>
                    <method name="Run" signature="()"><lines><line number="20" hits="4" branch="false" /><line number="20" hits="5" branch="false" /></lines></method>
                    <method name="Run" signature="(int)"><lines><line number="21" hits="0" branch="false" /></lines></method>
                    <method name="Run" signature="()"><lines><line number="22" hits="5" branch="false" /></lines></method>
                  </methods>
                  <lines>
                    <line number="10" hits="3" branch="true" condition-coverage="100% (2/2)" />
                    <line number="10" hits="0" branch="true" condition-coverage="100% (1/1)" />
                    <line number="11" hits="8" branch="true" condition-coverage="50% (1/2)" />
                    <line number="12" hits="2" branch="true" condition-coverage="100% (1/1)" />
                  </lines>
                </class>
                <class name="Widget" filename="src/Widget.cs">
                  <lines><line number="30" hits="7" branch="false" /></lines>
                </class>
                <class name="NativeOnly" filename="src/NativeOnly.cs">
                  <methods><method name="Native" signature="()"><lines><line number="91" hits="2" branch="false" /></lines></method></methods>
                  <lines><line number="90" hits="1" branch="false" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
        const string supplementTwo = """
            <coverage lines-covered="1" lines-valid="1" branches-covered="1" branches-valid="2">
              <packages><package name="Core"><classes>
                <class name="Widget" filename="src/Widget.cs">
                  <lines><line number="11" hits="9" branch="true" condition-coverage="50% (1/2)" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;

        var composed = CoverageFanInComposer.Compose(
        [
            (XDocument.Parse(primary), WindowsRoot),
            (XDocument.Parse(supplementOne), @"C:/agent/work"),
            (XDocument.Parse(supplementTwo), "C:\\agent\\work"),
        ]);

        var classes = composed.Descendants("class").ToArray();
        Assert.Equal(4, classes.Length);
        var widgets = classes.Where(item => item.Attribute("name")?.Value == "Widget").ToArray();
        Assert.Equal(2, widgets.Length);
        Assert.Contains(classes, item => item.Attribute("name")?.Value == "NativeOnly");
        Assert.Contains(classes, item => item.Attribute("name")?.Value == "PrimaryOnly");

        var firstWidget = widgets[0];
        Assert.Equal("src/Widget.cs", firstWidget.Attribute("filename")?.Value);
        Assert.Equal("Core", firstWidget.Parent!.Parent!.Attribute("name")?.Value);
        var firstLines = ReadLines(firstWidget.Element("lines")!);
        Assert.Equal((3L, 2L, 2L), firstLines[(10, 0)]);
        Assert.Equal((1L, 0L, 2L), firstLines[(10, 1)]);
        Assert.Equal((9L, 0L, 2L), firstLines[(11, 0)]);
        Assert.Equal((2L, 0L, 2L), firstLines[(12, 0)]);

        var methods = firstWidget.Element("methods")!.Elements("method").ToArray();
        Assert.Equal(3, methods.Length);
        Assert.Equal("()", methods[0].Attribute("signature")?.Value);
        Assert.Equal("()", methods[1].Attribute("signature")?.Value);
        Assert.Equal("(int)", methods[2].Attribute("signature")?.Value);
        var firstMethodLines = ReadLines(methods[0].Element("lines")!);
        Assert.Equal(2, firstMethodLines.Count);
        Assert.Equal(20L, Assert.Single(firstMethodLines.Keys, key => key.Occurrence == 0).Number);
        Assert.Equal(4L, firstMethodLines[(20, 0)].Hits);
        Assert.Equal(5L, firstMethodLines[(20, 1)].Hits);
        Assert.Equal(22L, Assert.Single(ReadLines(methods[1].Element("lines")!).Keys).Number);
        Assert.Equal(21L, Assert.Single(ReadLines(methods[2].Element("lines")!).Keys).Number);
        Assert.Equal(1L, ReadLines(methods[2].Element("lines")!)[(21, 0)].Hits);

        var secondWidget = widgets[1];
        Assert.Equal((7L, 0L, 0L), ReadLines(secondWidget.Element("lines")!)[(30, 0)]);
        var nativeOnly = Assert.Single(classes, item => item.Attribute("name")?.Value == "NativeOnly");
        Assert.Equal("src/NativeOnly.cs", nativeOnly.Attribute("filename")?.Value);
        Assert.Equal(
            2L,
            ReadLines(nativeOnly.Element("methods")!.Element("method")!.Element("lines")!)[(91, 0)].Hits);
        Assert.Equal("7", composed.Root!.Attribute("lines-valid")?.Value);
        Assert.Equal("6", composed.Root.Attribute("lines-covered")?.Value);
        Assert.Equal("8", composed.Root.Attribute("branches-valid")?.Value);
        Assert.Equal("2", composed.Root.Attribute("branches-covered")?.Value);
    }

    [Theory]
    [InlineData("0% (0/2)", 0L)]
    [InlineData("50% (1/2)", 0L)]
    [InlineData("100% (2/2)", 2L)]
    [InlineData("100% (1/1)", 0L)]
    public void Compose_PromotesPatchLineBranchesOnlyWhenSupplementCompletesRetainedDenominator(
        string supplementCoverage, long expectedCovered)
    {
        var primary = XDocument.Parse("""
            <coverage><packages><package name="P"><classes><class name="A" filename="src/A.cs"><lines>
              <line number="7" hits="1" branch="true" condition-coverage="0% (0/2)" />
            </lines></class></classes></package></packages></coverage>
            """);
        var supplement = XDocument.Parse($"""
            <coverage><packages><package name="P"><classes><class name="A" filename="src/A.cs"><lines>
              <line number="7" hits="3" branch="true" condition-coverage="{supplementCoverage}" />
            </lines></class></classes></package></packages></coverage>
            """);

        var composed = CoverageFanInComposer.Compose([(primary, WindowsRoot), (supplement, WindowsRoot)]);
        var line = Assert.Single(composed.Descendants("class")).Element("lines")!.Element("line")!;

        Assert.Equal("3", line.Attribute("hits")?.Value);
        Assert.Equal(expectedCovered, ParseBranchCounts(line.Attribute("condition-coverage")!.Value).Covered);
        Assert.Equal(2L, ParseBranchCounts(line.Attribute("condition-coverage")!.Value).Valid);
    }

    [Fact]
    public void Compose_ShouldRejectNullAndEmptyInputCollections()
    {
        Assert.Throws<ArgumentNullException>(() => CoverageFanInComposer.Compose(null!));
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.Compose([]));
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.Compose([(null!, WindowsRoot)]));
    }

    [Theory]
    [InlineData("wrong-root", "<report />")]
    [InlineData("empty-report", "<coverage><packages /></coverage>")]
    [InlineData("empty-classes", "<coverage><packages><package name='P'><classes /></package></packages></coverage>")]
    [InlineData("missing-package-name", "<coverage><packages><package><classes><class name='A' filename='A.cs'><lines><line number='1' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("missing-class-name", "<coverage><packages><package name='P'><classes><class filename='A.cs'><lines><line number='1' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("missing-filename", "<coverage><packages><package name='P'><classes><class name='A'><lines><line number='1' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("missing-lines", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs' /></classes></package></packages></coverage>")]
    [InlineData("missing-line-number", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("malformed-line-number", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='one' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("zero-line-number", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='0' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("negative-line-number", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='-1' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("missing-hits", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("malformed-hits", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='many' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("negative-hits", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='-1' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("branch-flag-without-counter", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='0' branch='true' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("malformed-branch-syntax", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='0' condition-coverage='1/2' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("invalid-branch-covered-count", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='0' condition-coverage='100% (2/1)' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("zero-branch-denominator", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='0' condition-coverage='0% (0/0)' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("branch-counter-overflow-text", "<coverage><packages><package name='P'><classes><class name='A' filename='A.cs'><lines><line number='1' hits='0' condition-coverage='100% (9223372036854775808/9223372036854775808)' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("empty-filename", "<coverage><packages><package name='P'><classes><class name='A' filename=''><lines><line number='1' hits='0' /></lines></class></classes></package></packages></coverage>")]
    [InlineData("filename-escapes-root", "<coverage><packages><package name='P'><classes><class name='A' filename='../A.cs'><lines><line number='1' hits='0' /></lines></class></classes></package></packages></coverage>")]
    public void Compose_ShouldRejectMalformedReportsWithoutMutatingSource(string scenario, string xml)
    {
        var report = XDocument.Parse(xml);
        var original = new XDocument(report);

        var error = Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.Compose([(report, WindowsRoot)]));

        Assert.True(XNode.DeepEquals(original, report), $"The {scenario} report was changed before rejection.");
        Assert.Contains(error.Message, new[] { "Coverage report data is invalid.", "Coverage path is invalid." });
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/root")]
    [InlineData("C:relative/root")]
    [InlineData("//server")]
    [InlineData("/agent//work")]
    [InlineData("/agent/../work")]
    [InlineData("C:/agent/../work")]
    [InlineData("/agent/\0work")]
    public void Compose_ShouldRejectInvalidSourceRoots(string sourceRoot)
    {
        var report = ValidReport();

        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.Compose([(report, sourceRoot)]));
        Assert.Equal("A.cs", report.Descendants("class").Single().Attribute("filename")?.Value);
    }

    [Fact]
    public void Compose_ShouldRejectBranchCounterOverflowWithoutChangingInput()
    {
        var report = XDocument.Parse("""
            <coverage><packages><package name="P"><classes><class name="A" filename="A.cs"><lines>
              <line number="1" hits="0" condition-coverage="0% (0/9223372036854775807)" />
              <line number="2" hits="0" condition-coverage="0% (0/1)" />
            </lines></class></classes></package></packages></coverage>
            """);
        var original = new XDocument(report);

        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.Compose([(report, WindowsRoot)]));

        Assert.True(XNode.DeepEquals(original, report));
    }

    [Fact]
    public void Compose_ShouldPromoteFullBranchCoverageWithTheSameDenominator()
    {
        var primary = XDocument.Parse("""
            <coverage><packages><package name="P"><classes><class name="A" filename="A.cs"><lines>
              <line number="5" hits="0" condition-coverage="25% (1/4)" />
            </lines></class></classes></package></packages></coverage>
            """);
        var supplement = XDocument.Parse("""
            <coverage><packages><package name="P"><classes><class name="A" filename="A.cs"><lines>
              <line number="5" hits="0" condition-coverage="100% (4/4)" />
            </lines></class></classes></package></packages></coverage>
            """);

        var composed = CoverageFanInComposer.Compose([(primary, WindowsRoot), (supplement, WindowsRoot)]);

        Assert.Equal((0L, 4L, 4L), ReadLines(Assert.Single(composed.Descendants("class")).Element("lines")!)[(5, 0)]);
        Assert.Equal("4", composed.Root!.Attribute("branches-covered")?.Value);
        Assert.Equal("4", composed.Root.Attribute("branches-valid")?.Value);
    }

    [Fact]
    public void Compose_PromotesOnlyMatchingMethodOverloadFromACompleteObservation()
    {
        var primary = XDocument.Parse("""
            <coverage><packages><package name="P"><classes><class name="A" filename="A.cs"><lines>
              <line number="5" hits="1" condition-coverage="25% (1/4)" />
            </lines><methods>
              <method name="Decide" signature="(int)"><lines><line number="5" hits="1" condition-coverage="25% (1/4)" /></lines></method>
              <method name="Decide" signature="(string)"><lines><line number="5" hits="1" condition-coverage="25% (1/4)" /></lines></method>
            </methods></class></classes></package></packages></coverage>
            """);
        var native = new XDocument(primary);
        native.Descendants("method").First().Descendants("line").Single()
            .SetAttributeValue("condition-coverage", "100% (4/4)");
        native.Descendants("method").Last().Descendants("line").Single()
            .SetAttributeValue("condition-coverage", "50% (1/2)");

        var composed = CoverageFanInComposer.Compose([(primary, WindowsRoot), (native, WindowsRoot)]);

        var methods = composed.Descendants("method").ToDictionary(x => x.Attribute("signature")!.Value);
        Assert.Equal((1L, 4L, 4L), ReadLines(methods["(int)"].Element("lines")!)[(5, 0)]);
        Assert.Equal((1L, 1L, 4L), ReadLines(methods["(string)"].Element("lines")!)[(5, 0)]);
        Assert.Equal((1L, 1L, 4L), ReadLines(composed.Descendants("class").Single().Element("lines")!)[(5, 0)]);
        primary.Root!.SetAttributeValue("lines-covered", "1");
        primary.Root.SetAttributeValue("lines-valid", "1");
        primary.Root.SetAttributeValue("branches-covered", "1");
        primary.Root.SetAttributeValue("branches-valid", "4");
        var primaryPaths = new XDocument(primary);
        CoverageFanInIndependentVerifier.VerifyComposition([(primary, WindowsRoot), (native, WindowsRoot)],
            composed, primaryPaths);
    }

    [Fact]
    public void NormalizePaths_ShouldLeaveEarlierFilenamesUntouchedWhenLaterPathIsInvalid()
    {
        var report = XDocument.Parse("""
            <coverage><packages><package name="P"><classes>
              <class name="A" filename="C:\agent\work\src\A.cs" />
              <class name="B" filename="C:\outside\B.cs" />
            </classes></package></packages></coverage>
            """);
        var original = new XDocument(report);

        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.NormalizePaths(report, WindowsRoot));

        Assert.True(XNode.DeepEquals(original, report));
    }

    [Fact]
    public void NormalizePaths_ShouldRejectClassWithoutFilenameBeforeChangingAnyClassPath()
    {
        var report = XDocument.Parse("""
            <coverage><packages><package name="P"><classes>
              <class name="A" filename="C:\agent\work\src\A.cs" />
              <class name="B" />
            </classes></package></packages></coverage>
            """);
        var original = new XDocument(report);

        var error = Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.NormalizePaths(report, WindowsRoot));

        Assert.Equal("Coverage path is invalid.", error.Message);
        Assert.True(XNode.DeepEquals(original, report));
    }

    [Fact]
    public void ValidateNormalization_ShouldAcceptGoldenClassFoldWithEveryMethodObservationPreserved()
    {
        var raw = MethodNormalizationRaw();
        var normalized = MethodNormalizationGolden();
        var rawBefore = new XDocument(raw);
        var normalizedBefore = new XDocument(normalized);

        CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot);

        Assert.Equal(2, raw.Descendants("class").Count());
        Assert.Single(normalized.Descendants("class"));
        Assert.True(XNode.DeepEquals(rawBefore, raw));
        Assert.True(XNode.DeepEquals(normalizedBefore, normalized));
    }

    [Fact]
    public void CollectorMethodProjectionDiscardsReconstructedRangesAndRetainsExactOriginalRecords()
    {
        var raw = MethodNormalizationRaw();
        var normalized = MethodNormalizationGolden();
        foreach (var method in normalized.Descendants("method")) method.SetAttributeValue("name", "reconstructed");
        var projected = CoverageFanInComposer.RetainCollectorMethods(raw, normalized, WindowsRoot);

        CoverageFanInComposer.ValidateNormalization(raw, projected, WindowsRoot);
        CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, projected, WindowsRoot);
        Assert.Equal(raw.Descendants("method").Select(x => x.ToString(SaveOptions.DisableFormatting)),
            projected.Descendants("method").Select(x => x.ToString(SaveOptions.DisableFormatting)));
        Assert.All(normalized.Descendants("method"), method => Assert.Equal("reconstructed", method.Attribute("name")!.Value));
        var changedHits = new XDocument(projected);
        changedHits.Descendants("method").First().Descendants("line").First().SetAttributeValue("hits", "9");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, changedHits, WindowsRoot));
        var changedOwner = new XDocument(projected);
        changedOwner.Descendants("class").First(cls => cls.Element("methods") is not null).SetAttributeValue("name", "WrongOwner");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyCollectorMethods(raw, changedOwner, WindowsRoot));
        normalized.Descendants("class").Single().SetAttributeValue("filename", "src/Missing.cs");
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.RetainCollectorMethods(raw, normalized, WindowsRoot));
    }

    [Theory]
    [InlineData("drop-method")]
    [InlineData("drop-method-point")]
    [InlineData("change-method-hit-state")]
    [InlineData("promote-uncovered-method-point")]
    [InlineData("change-method-branches")]
    [InlineData("drop-empty-method")]
    public void ValidateNormalization_ShouldRejectLostOrChangedMethodObservations(string mutation)
    {
        var raw = MethodNormalizationRaw();
        var normalized = MethodNormalizationGolden();
        var methods = normalized.Descendants("method").ToArray();
        switch (mutation)
        {
            case "drop-method":
                methods[0].Remove();
                break;
            case "drop-method-point":
                methods[0].Element("lines")!.Elements("line").Last().Remove();
                break;
            case "change-method-hit-state":
                methods[1].Element("lines")!.Element("line")!.SetAttributeValue("hits", "0");
                break;
            case "promote-uncovered-method-point":
                methods[2].Element("lines")!.Element("line")!.SetAttributeValue("hits", "4");
                break;
            case "change-method-branches":
                methods[1].Element("lines")!.Element("line")!.SetAttributeValue("condition-coverage", "100% (2/2)");
                break;
            case "drop-empty-method":
                normalized.Descendants("method").Single(method => method.Attribute("name")?.Value == "Empty").Remove();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot));
    }

    [Theory]
    [InlineData("missing-number")]
    [InlineData("malformed-number")]
    [InlineData("nonpositive-number")]
    [InlineData("missing-hits")]
    [InlineData("negative-hits")]
    [InlineData("branch-without-counter")]
    [InlineData("malformed-branch-counter")]
    [InlineData("zero-branch-denominator")]
    [InlineData("covered-exceeds-valid")]
    [InlineData("branch-counter-overflow")]
    public void ValidateNormalization_ShouldRejectMalformedMethodPoints(string mutation)
    {
        var raw = MethodNormalizationRaw();
        var normalized = MethodNormalizationGolden();
        var line = mutation is "branch-without-counter" or "malformed-branch-counter"
            or "zero-branch-denominator" or "covered-exceeds-valid" or "branch-counter-overflow"
            ? normalized.Descendants("method").Single(method => method.Attribute("signature")?.Value == "(int)")
                .Element("lines")!.Element("line")!
            : normalized.Descendants("method").First().Element("lines")!.Elements("line").First();
        switch (mutation)
        {
            case "missing-number":
                line.Attribute("number")!.Remove();
                break;
            case "malformed-number":
                line.SetAttributeValue("number", "ten");
                break;
            case "nonpositive-number":
                line.SetAttributeValue("number", "0");
                break;
            case "missing-hits":
                line.Attribute("hits")!.Remove();
                break;
            case "negative-hits":
                line.SetAttributeValue("hits", "-1");
                break;
            case "branch-without-counter":
                line.SetAttributeValue("condition-coverage", null);
                break;
            case "malformed-branch-counter":
                line.SetAttributeValue("condition-coverage", "50% (one/2)");
                break;
            case "zero-branch-denominator":
                line.SetAttributeValue("condition-coverage", "0% (0/0)");
                break;
            case "covered-exceeds-valid":
                line.SetAttributeValue("condition-coverage", "100% (2/1)");
                break;
            case "branch-counter-overflow":
                line.SetAttributeValue("condition-coverage", "100% (9223372036854775808/9223372036854775808)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot));
    }

    [Theory]
    [InlineData("null-filename")]
    [InlineData("missing-root-counter")]
    [InlineData("negative-root-counter")]
    public void ValidateNormalization_ShouldRejectInvalidMethodOrBranchRootEvidence(string mutation)
    {
        var raw = MethodNormalizationRaw();
        var normalized = MethodNormalizationGolden();
        if (mutation == "null-filename")
        {
            raw.Descendants("class").First().Attribute("filename")!.Remove();
        }
        else if (mutation == "missing-root-counter")
        {
            raw.Root!.Attribute("branches-valid")!.Remove();
        }
        else
        {
            raw.Root!.SetAttributeValue("branches-valid", "-1");
        }

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot));
    }

    [Fact]
    public void NormalizePaths_ShouldConvertWindowsAndPosixRootsToPortableRelativePaths()
    {
        var windows = XDocument.Parse("""
            <coverage><packages><package name="P"><classes>
              <class name="A" filename="C:\agent\work\src\A.cs" />
            </classes></package></packages></coverage>
            """);
        var posix = XDocument.Parse("""
            <coverage><packages><package name="P"><classes>
              <class name="B" filename="/agent/work/src/B.cs" />
            </classes></package></packages></coverage>
            """);

        Assert.Same(windows, CoverageFanInComposer.NormalizePaths(windows, @"C:\agent\work"));
        CoverageFanInComposer.NormalizePaths(posix, "/agent/work");

        Assert.Equal("src/A.cs", windows.Descendants("class").Single().Attribute("filename")?.Value);
        Assert.Equal("src/B.cs", posix.Descendants("class").Single().Attribute("filename")?.Value);
    }

    [Fact]
    public void NormalizePaths_IgnoresEscapingSourceBasesAndAcceptsFilesystemRootAndTrailingRootSeparator()
    {
        var report = XDocument.Parse("""
            <coverage><sources>
              <source>C:\outside</source>
              <source>C:\agent\work\</source>
            </sources><packages><package name="P"><classes>
              <class name="A" filename="src/A.cs" />
            </classes></package></packages></coverage>
            """);
        CoverageFanInComposer.NormalizePaths(report, @"C:\agent\work\");

        Assert.Equal("src/A.cs", report.Descendants("class").Single().Attribute("filename")?.Value);
        Assert.Equal("C:/agent/work/", report.Root!.Element("sources")!.Element("source")!.Value);

        var filesystemRoot = DocumentWithClassPath("src/B.cs");
        CoverageFanInComposer.NormalizePaths(filesystemRoot, "/");
        Assert.Equal("src/B.cs", filesystemRoot.Descendants("class").Single().Attribute("filename")?.Value);
    }

    [Theory]
    [InlineData(@"C:\outside\A.cs", @"C:\agent\work")]
    [InlineData(@"C:\agent\work\..\outside\A.cs", @"C:\agent\work")]
    [InlineData("src/../A.cs", @"C:\agent\work")]
    [InlineData("src//A.cs", @"C:\agent\work")]
    [InlineData("C:src/A.cs", @"C:\agent\work")]
    [InlineData("/agent/work/src/A.cs", @"C:\agent\work")]
    [InlineData("//server", "//server/share/work")]
    public void NormalizePaths_ShouldRejectAliasesAndEscapes(string filename, string sourceRoot)
    {
        var document = DocumentWithClassPath(filename);

        var error = Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.NormalizePaths(document, sourceRoot));

        Assert.Equal("Coverage path is invalid.", error.Message);
    }

    [Fact]
    public void ValidateNormalization_ShouldRetainEveryRawSourcePointAndObservedHit()
    {
        var raw = XDocument.Parse("""
            <coverage branches-valid="1"><packages><package name="P"><classes>
              <class name="Raw" filename="C:\agent\work\src\A.cs"><lines>
                <line number="7" hits="2" branch="true" condition-coverage="0% (0/1)" />
                <line number="8" hits="0" branch="false" />
              </lines></class>
            </classes></package></packages></coverage>
            """);
        var normalized = XDocument.Parse("""
            <coverage branches-valid="1"><packages><package name="P"><classes>
              <class name="Folded" filename="src/A.cs"><lines>
                <line number="7" hits="3" branch="true" condition-coverage="0% (0/1)" />
                <line number="8" hits="0" branch="false" />
              </lines></class>
            </classes></package></packages></coverage>
            """);

        CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot);

        var missingPoint = new XDocument(normalized);
        missingPoint.Descendants("line").Last().Remove();
        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, missingPoint, WindowsRoot));

        normalized.Descendants("line").First().SetAttributeValue("hits", "0");
        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot));
        normalized.Descendants("line").First().SetAttributeValue("hits", "3");
        normalized.Root!.SetAttributeValue("branches-valid", "0");
        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void ValidateNormalization_ShouldRejectNonPositiveRawLineNumbersAndNegativeHits(long number, long hits)
    {
        var raw = XDocument.Parse($"""
            <coverage branches-valid="0"><packages><package name="P"><classes>
              <class name="Raw" filename="C:\agent\work\src\A.cs"><lines>
                <line number="{number}" hits="{hits}" branch="false" />
              </lines></class>
            </classes></package></packages></coverage>
            """);
        var normalized = XDocument.Parse($"""
            <coverage branches-valid="0"><packages><package name="P"><classes>
              <class name="Folded" filename="src/A.cs"><lines>
                <line number="{number}" hits="{hits}" branch="false" />
              </lines></class>
            </classes></package></packages></coverage>
            """);

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidateNormalization(raw, normalized, WindowsRoot));
    }

    [Fact]
    public void ValidatePrimaryTimings_ShouldRequireExactCompleteSuccessfulInventory()
    {
        var expectedProjects = new[] { "tests/A.Tests.csproj", "test-projects/CustomSuite.csproj" };
        using var document = JsonDocument.Parse(CreateTimings(
        [
            (expectedProjects[0], 0, "completed", "produced"),
            (expectedProjects[1], 0, "completed", "produced"),
        ]));

        CoverageFanInComposer.ValidatePrimaryTimings(document.RootElement, expectedProjects, "Release", "collector");
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("extra-project")]
    [InlineData("duplicate-project")]
    [InlineData("failed-exit")]
    [InlineData("incomplete")]
    [InlineData("missing-coverage")]
    [InlineData("wrong-configuration")]
    [InlineData("wrong-driver")]
    [InlineData("failed-merge")]
    [InlineData("wrong-artifact-count")]
    public void ValidatePrimaryTimings_ShouldRejectMutatedEvidence(string mutation)
    {
        var expectedProjects = new[] { "tests/A.Tests.csproj", "test-projects/CustomSuite.csproj" };
        var projects = new List<(string Project, int ExitCode, string Status, string Artifact)>
        {
            (expectedProjects[0], 0, "completed", "produced"),
            (expectedProjects[1], 0, "completed", "produced"),
        };
        var configuration = "Release";
        var driver = "collector";
        var mergeExitCode = 0;
        var artifactCount = 2;

        switch (mutation)
        {
            case "missing-project":
                projects.RemoveAt(1);
                break;
            case "extra-project":
                projects[1] = ("tests/C.Tests.csproj", 0, "completed", "produced");
                break;
            case "duplicate-project":
                projects[1] = (expectedProjects[0], 0, "completed", "produced");
                break;
            case "failed-exit":
                projects[0] = (expectedProjects[0], 1, "completed", "produced");
                break;
            case "incomplete":
                projects[0] = (expectedProjects[0], 0, "timed-out", "produced");
                break;
            case "missing-coverage":
                projects[0] = (expectedProjects[0], 0, "completed", "missing");
                break;
            case "wrong-configuration":
                configuration = "Debug";
                break;
            case "wrong-driver":
                driver = "msbuild";
                break;
            case "failed-merge":
                mergeExitCode = 1;
                break;
            case "wrong-artifact-count":
                artifactCount = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        using var document = JsonDocument.Parse(CreateTimings(
            projects,
            mergeExitCode,
            artifactCount,
            configuration,
            driver));

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidatePrimaryTimings(document.RootElement, expectedProjects, "Release", "collector"));
    }

    [Fact]
    public void ValidatePrimaryTimings_ShouldRejectDuplicateJsonFields()
    {
        using var document = JsonDocument.Parse("""
            {
              "configuration": "Release",
              "configuration": "Release",
              "coverageDriver": "collector",
              "merge": { "exitCode": 0 },
              "artifacts": { "coverageFiles": 1 },
              "projects": [
                { "project": "tests/A.Tests.csproj", "exitCode": 0, "executionStatus": "completed", "coverageArtifactStatus": "produced" }
              ]
            }
            """);

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidatePrimaryTimings(
                document.RootElement,
                ["tests/A.Tests.csproj"],
                "Release",
                "collector"));
    }

    [Theory]
    [InlineData("non-object-timings")]
    [InlineData("null-inventory")]
    [InlineData("empty-inventory")]
    [InlineData("empty-project")]
    [InlineData("duplicate-expected-project")]
    [InlineData("empty-configuration")]
    [InlineData("empty-driver")]
    public void ValidatePrimaryTimings_ShouldRejectInvalidCallerInventoryAndIdentity(string scenario)
    {
        using var document = JsonDocument.Parse(CreateTimings(
            [("tests/A.Tests.csproj", 0, "completed", "produced")]));
        JsonElement timings = document.RootElement;
        IReadOnlyList<string> expectedProjects = ["tests/A.Tests.csproj"];
        var configuration = "Release";
        var driver = "collector";

        switch (scenario)
        {
            case "non-object-timings":
                using (var scalar = JsonDocument.Parse("[]"))
                    timings = scalar.RootElement.Clone();
                break;
            case "null-inventory":
                expectedProjects = null!;
                break;
            case "empty-inventory":
                expectedProjects = [];
                break;
            case "empty-project":
                expectedProjects = [""];
                break;
            case "duplicate-expected-project":
                expectedProjects = ["tests/A.Tests.csproj", "tests/A.Tests.csproj"];
                break;
            case "empty-configuration":
                configuration = "";
                break;
            case "empty-driver":
                driver = "";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInComposer.ValidatePrimaryTimings(timings, expectedProjects, configuration, driver));
    }

    [Theory]
    [InlineData("configuration", "null")]
    [InlineData("coverageDriver", "0")]
    [InlineData("merge", "[]")]
    [InlineData("merge.exitCode", "\"0\"")]
    [InlineData("merge.exitCode", "2147483648")]
    [InlineData("artifacts", "false")]
    [InlineData("artifacts.coverageFiles", "\"1\"")]
    [InlineData("artifacts.coverageFiles", "2147483648")]
    [InlineData("projects", "{}")]
    [InlineData("projects.0", "42")]
    [InlineData("projects.0.project", "null")]
    [InlineData("projects.0.project", "\"\"")]
    [InlineData("projects.0.exitCode", "null")]
    [InlineData("projects.0.exitCode", "2147483648")]
    [InlineData("projects.0.executionStatus", "true")]
    [InlineData("projects.0.coverageArtifactStatus", "0")]
    public void ValidatePrimaryTimingsRejectsTypedFieldCorruptionWithoutChangingInventory(string field, string jsonValue)
    {
        var expected = new[] { "tests/A.Tests.csproj" };
        var node = System.Text.Json.Nodes.JsonNode.Parse(CreateTimings([(expected[0], 0, "completed", "produced")]))!;
        var parts = field.Split('.');
        var current = node;
        foreach (var part in parts[..^1])
            current = current is System.Text.Json.Nodes.JsonArray ? current[int.Parse(part)]! : current[part]!;
        if (current is System.Text.Json.Nodes.JsonArray) current[int.Parse(parts[^1])] = System.Text.Json.Nodes.JsonNode.Parse(jsonValue);
        else current[parts[^1]] = System.Text.Json.Nodes.JsonNode.Parse(jsonValue);
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.ValidatePrimaryTimings(
            document.RootElement, expected, "Release", "collector"));
        Assert.Equal(["tests/A.Tests.csproj"], expected);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("coverageDriver")]
    [InlineData("merge")]
    [InlineData("artifacts")]
    [InlineData("projects")]
    public void ValidatePrimaryTimingsRejectsEachOmittedRequiredObservation(string field)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(CreateTimings([("tests/A.Tests.csproj", 0, "completed", "produced")]))!.AsObject();
        node.Remove(field);
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CoverageFanInComposer.ValidatePrimaryTimings(
            document.RootElement, ["tests/A.Tests.csproj"], "Release", "collector"));
    }

    [Fact]
    public void CompositionAndNormalizationRetainMethodsWithoutOptionalNamesOrSignatures()
    {
        var raw = XDocument.Parse("""
            <coverage branches-valid="0"><packages><package name="P"><classes><class name="A" filename="src/A.cs">
              <methods><method><lines><line number="7" hits="2" /></lines></method></methods>
              <lines><line number="1" hits="1" /></lines>
            </class></classes></package></packages></coverage>
            """);
        var retained = CoverageFanInComposer.RetainCollectorMethods(raw, raw, WindowsRoot);
        CoverageFanInComposer.ValidateNormalization(raw, retained, WindowsRoot);
        var composed = CoverageFanInComposer.Compose([(raw, WindowsRoot), (retained, WindowsRoot)]);
        var method = Assert.Single(composed.Descendants("method"));
        Assert.Equal(string.Empty, method.Attribute("name")!.Value);
        Assert.Equal(string.Empty, method.Attribute("signature")!.Value);
        Assert.Equal("2", Assert.Single(method.Descendants("line")).Attribute("hits")!.Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("C:/")]
    [InlineData("//server/share/")]
    public void RootMappedSourceBasesRemainPortableAtFilesystemBoundaries(string sourceRoot)
    {
        var report = XDocument.Parse($"""
            <coverage><sources><source>{sourceRoot}</source></sources><packages><package name="P"><classes>
              <class name="A" filename="src/A.cs"><lines><line number="1" hits="1" /></lines></class>
            </classes></package></packages></coverage>
            """);
        CoverageFanInComposer.NormalizePaths(report, sourceRoot);
        Assert.Equal("src/A.cs", Assert.Single(report.Descendants("class")).Attribute("filename")!.Value);
        var composed = CoverageFanInComposer.Compose([(report, sourceRoot)]);
        Assert.Equal("src/A.cs", Assert.Single(composed.Descendants("class")).Attribute("filename")!.Value);
    }

    private static (long Covered, long Valid) ParseBranchCounts(string value)
    {
        var open = value.IndexOf('(');
        var slash = value.IndexOf('/');
        var close = value.IndexOf(')');
        return (long.Parse(value.AsSpan(open + 1, slash - open - 1), System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(value.AsSpan(slash + 1, close - slash - 1), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static XDocument MethodNormalizationRaw() => XDocument.Parse("""
        <coverage branches-valid="1"><packages><package name="P"><classes>
          <class name="First" filename="C:\agent\work\src\A.cs"><lines><line number="1" hits="0" branch="false" /></lines><methods>
            <method name="Run" signature="()"><lines><line number="10" hits="0" branch="false" /><line number="10" hits="2" branch="false" /></lines></method>
            <method name="Run" signature="(int)"><lines><line number="11" hits="1" branch="true" condition-coverage="50% (1/2)" /></lines></method>
            <method name="Run" signature="()"><lines><line number="12" hits="0" branch="false" /></lines></method>
            <method name="Empty" signature="()"><lines /></method>
          </methods></class>
          <class name="Second" filename="src/A.cs"><lines><line number="2" hits="1" branch="false" /></lines><methods>
            <method name="Run" signature="()"><lines><line number="13" hits="3" branch="false" /></lines></method>
          </methods></class>
        </classes></package></packages></coverage>
        """);

    private static XDocument MethodNormalizationGolden() => XDocument.Parse("""
        <coverage branches-valid="1"><packages><package name="P"><classes>
          <class name="Folded" filename="src/A.cs"><lines><line number="1" hits="0" branch="false" /><line number="2" hits="1" branch="false" /></lines><methods>
            <method name="Run" signature="()"><lines><line number="10" hits="0" branch="false" /><line number="10" hits="7" branch="false" /></lines></method>
            <method name="Run" signature="(int)"><lines><line number="11" hits="9" branch="true" condition-coverage="50% (1/2)" /></lines></method>
            <method name="Run" signature="()"><lines><line number="12" hits="0" branch="false" /></lines></method>
            <method name="Empty" signature="()"><lines /></method>
            <method name="Run" signature="()"><lines><line number="13" hits="3" branch="false" /></lines></method>
          </methods></class>
        </classes></package></packages></coverage>
        """);

    private static XDocument ValidReport() => XDocument.Parse("""
        <coverage><packages><package name="P"><classes>
          <class name="A" filename="A.cs"><lines><line number="1" hits="0" /></lines></class>
        </classes></package></packages></coverage>
        """);

    private static XDocument DocumentWithClassPath(string filename)
    {
        return new XDocument(new XElement("coverage",
            new XElement("packages",
                new XElement("package", new XAttribute("name", "P"),
                    new XElement("classes",
                        new XElement("class", new XAttribute("name", "A"), new XAttribute("filename", filename)))))));
    }

    private static Dictionary<(long Number, int Occurrence), (long Hits, long BranchesCovered, long BranchesValid)> ReadLines(XElement lines)
    {
        var result = new Dictionary<(long Number, int Occurrence), (long Hits, long BranchesCovered, long BranchesValid)>();
        var occurrences = new Dictionary<long, int>();
        foreach (var line in lines.Elements("line"))
        {
            var number = long.Parse(line.Attribute("number")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            var hits = long.Parse(line.Attribute("hits")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            var conditionCoverage = line.Attribute("condition-coverage")?.Value;
            var branchesCovered = 0L;
            var branchesValid = 0L;
            if (conditionCoverage is not null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(conditionCoverage, @"\((\d+)/(\d+)\)\z");
                branchesCovered = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                branchesValid = long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            var occurrence = occurrences.TryGetValue(number, out var previous) ? previous : 0;
            occurrences[number] = occurrence + 1;
            result.Add((number, occurrence), (hits, branchesCovered, branchesValid));
        }

        return result;
    }

    private static string CreateTimings(
        IReadOnlyList<(string Project, int ExitCode, string Status, string Artifact)> projects,
        int mergeExitCode = 0,
        int? artifactCount = null,
        string configuration = "Release",
        string driver = "collector")
    {
        var projectJson = string.Join(",", projects.Select(project =>
            $$"""{"project":"{{project.Project}}","exitCode":{{project.ExitCode}},"executionStatus":"{{project.Status}}","coverageArtifactStatus":"{{project.Artifact}}"}"""));
        return $$"""
            {
              "configuration": "{{configuration}}",
              "coverageDriver": "{{driver}}",
              "merge": { "exitCode": {{mergeExitCode}} },
              "artifacts": { "coverageFiles": {{artifactCount ?? projects.Count}} },
              "projects": [{{projectJson}}]
            }
            """;
    }
}
