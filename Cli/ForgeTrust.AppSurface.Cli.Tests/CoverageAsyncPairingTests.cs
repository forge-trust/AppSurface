using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInAsyncPairingTests
{
    private const string Root = "/repo";
    private const string SourceType = "Fixture.Owner";
    private const string StateMachine = "Fixture.Owner/<WorkAsync>d__5";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Verified_full_observation_is_shared_without_changing_either_denominator(bool primaryCovered)
    {
        var confirmations = Method();
        var primary = Report(SourceType, "WorkAsync", primaryCovered ? 7 : 0, primaryCovered ? 2 : 0, 2);
        var native = Report(StateMachine, "MoveNext", primaryCovered ? 0 : 5, primaryCovered ? 0 : 2, 2);
        (XDocument Document, string SourceRoot)[] inputs = [(primary, Root), (native, Root)];
        IReadOnlyList<CoverageAsyncMethod>[] symbols = [[confirmations], [confirmations]];

        var projected = CoverageAsyncPairing.Project(inputs, symbols);
        var composed = CoverageFanInComposer.Compose(projected);
        Assert.All(composed.Descendants("line"), line =>
        {
            Assert.NotEqual("0", line.Attribute("hits")!.Value);
            Assert.Equal("100% (2/2)", Branches(line));
        });
        var primaryPaths = CoverageFanInComposer.NormalizePaths(new XDocument(primary), Root);
        CoverageFanInIndependentVerifier.VerifyComposition(inputs, composed, primaryPaths, symbols);
        Assert.Throws<InvalidDataException>(() =>
            CoverageFanInIndependentVerifier.VerifyComposition(inputs, composed, primaryPaths));

        var previouslyUncovered = primaryCovered ? native : primary;
        Assert.All(previouslyUncovered.Descendants("line"), line => Assert.Equal("0% (0/2)", Branches(line)));
        Assert.Equal("4", composed.Root!.Attribute("branches-valid")!.Value);
        Assert.Equal((primaryCovered ? 2 : 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            primaryPaths.Root!.Attribute("branches-covered")!.Value);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("duplicate-symbol")]
    [InlineData("different-source")]
    [InlineData("no-sequence-point")]
    [InlineData("different-denominator")]
    [InlineData("duplicate-method")]
    [InlineData("duplicate-primary-class")]
    [InlineData("missing-state-machine-class")]
    [InlineData("renamed-source-method")]
    [InlineData("repeated-method-line")]
    [InlineData("missing-method-lines")]
    [InlineData("bad-hit-count")]
    [InlineData("inconsistent-branch-flag")]
    [InlineData("different-branch-kind")]
    [InlineData("different-package")]
    [InlineData("missing-symbol")]
    public void Ambiguous_or_different_records_keep_their_original_measurement(string defect)
    {
        var confirmed = Method();
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 1, 2, defect == "different-denominator" ? 3 : 2);
        var nativeMethod = native.Descendants("method").Single();
        var primaryClass = primary.Descendants("class").Single();
        switch (defect)
        {
            case "duplicate-method":
                nativeMethod.Parent!.Add(new XElement(nativeMethod));
                break;
            case "duplicate-primary-class":
                primaryClass.Parent!.Add(new XElement(primaryClass));
                break;
            case "missing-state-machine-class":
                nativeMethod.Ancestors("class").Single().SetAttributeValue("name", "Unrelated");
                break;
            case "renamed-source-method":
                primaryClass.Descendants("method").Single().SetAttributeValue("name", "Unrelated");
                break;
            case "repeated-method-line":
                var line = nativeMethod.Descendants("line").Single();
                line.Parent!.Add(new XElement(line));
                break;
            case "missing-method-lines":
                nativeMethod.Element("lines")!.Remove();
                break;
            case "bad-hit-count":
                nativeMethod.Descendants("line").Single().SetAttributeValue("hits", "-1");
                break;
            case "inconsistent-branch-flag":
                nativeMethod.Descendants("line").Single().SetAttributeValue("branch", "false");
                break;
            case "different-branch-kind":
                var nonBranch = nativeMethod.Descendants("line").Single();
                nonBranch.SetAttributeValue("branch", "false");
                nonBranch.SetAttributeValue("condition-coverage", null);
                break;
            case "different-package":
                native.Descendants("package").Single().SetAttributeValue("name", "ForeignAssembly");
                break;
        }
        IReadOnlyList<CoverageAsyncMethod> nativeSymbols = defect switch
        {
            "fingerprint" => [confirmed with { Fingerprint = "compiled-other-shape" }],
            "duplicate-symbol" => [confirmed, confirmed],
            "missing-symbol" => [],
            "different-source" => [confirmed with { File = "elsewhere.cs" }],
            _ => [confirmed]
        };
        IReadOnlyList<CoverageAsyncMethod> primarySymbols =
            defect == "no-sequence-point" ? [confirmed with { Lines = [99] }] : [confirmed];

        var projected = CoverageAsyncPairing.Project([(primary, Root), (native, Root)], [primarySymbols, nativeSymbols]);

        Assert.All(projected[0].Document.Descendants("line"), line => Assert.Equal("0% (0/2)", Branches(line)));
    }

    [Theory]
    [InlineData("unique")]
    [InlineData("ambiguous-symbol")]
    [InlineData("ambiguous-class")]
    [InlineData("missing-class")]
    public void Flattened_nested_class_requires_a_unique_source_record(string shape)
    {
        var nested = Method() with { SourceType = SourceType + "/Inner" };
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 1, 2, 2);
        var primaryClass = primary.Descendants("class").Single();
        if (shape == "ambiguous-class") primaryClass.Parent!.Add(new XElement(primaryClass));
        if (shape == "missing-class") primaryClass.SetAttributeValue("name", "Unrelated");
        IReadOnlyList<CoverageAsyncMethod> primarySymbols = shape == "ambiguous-symbol"
            ? [nested, nested with { SourceType = SourceType + "/Other", StateMachineType = StateMachine + "Other" }]
            : [nested];

        var projected = CoverageAsyncPairing.Project([(primary, Root), (native, Root)], [primarySymbols, [nested]]);

        Assert.All(projected[0].Document.Descendants("line"),
            line => Assert.Equal(shape == "unique" ? "100% (2/2)" : "0% (0/2)", Branches(line)));
    }

    [Fact]
    public void Branchless_records_share_hits_and_unqualified_native_documents_are_unchanged()
    {
        var method = Method();
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 2, 2, 2);
        foreach (var line in primary.Descendants("line").Concat(native.Descendants("line")))
        {
            line.SetAttributeValue("condition-coverage", null);
            line.SetAttributeValue("branch", "false");
        }
        var baseline = Report("Other", "Unrelated", 0, 0, 2);
        var secondNative = new XDocument(native);
        foreach (var line in secondNative.Descendants("line")) line.SetAttributeValue("hits", "3");

        var projected = CoverageAsyncPairing.Project([(primary, Root), (baseline, Root), (native, Root), (secondNative, Root)],
            [[method], [], [method], [method]]);

        Assert.All(projected[0].Document.Descendants("line"), line =>
        {
            Assert.Equal("3", line.Attribute("hits")!.Value);
            Assert.Null(line.Attribute("condition-coverage"));
        });
        Assert.Same(baseline, projected[1].Document);
        Assert.All(primary.Descendants("line"), line => Assert.Equal("0", line.Attribute("hits")!.Value));
    }

    [Fact]
    public void Class_line_with_a_distinct_condition_observation_is_untouched()
    {
        var method = Method();
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 7, 2, 2);
        var nativeClass = native.Descendants("class").Single();
        nativeClass.Element("lines")!.Element("line")!.SetAttributeValue("condition-coverage", "50% (1/2)");

        var projected = CoverageAsyncPairing.Project([(primary, Root), (native, Root)], [[method], [method]]);
        var primaryClass = projected[0].Document.Descendants("class").Single();
        Assert.Equal("0% (0/2)", Branches(primaryClass.Element("lines")!.Element("line")!));
        Assert.Equal("100% (2/2)", Branches(primaryClass.Descendants("method").Single().Descendants("line").Single()));
    }

    [Fact]
    public void Partial_observations_are_kept_separate_and_shared_class_lines_are_untouched()
    {
        var method = Method();
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 7, 1, 2);
        var sharedMethods = primary.Descendants("methods").Single();
        sharedMethods.Add(new XElement(sharedMethods.Element("method")!));
        sharedMethods.Elements("method").Last().SetAttributeValue("name", "Other");

        var projected = CoverageAsyncPairing.Project([(primary, Root), (native, Root)], [[method], [method]]);
        var primaryClass = projected[0].Document.Descendants("class").Single();
        var primaryMethod = primaryClass.Descendants("method").First();
        Assert.Equal("0", primaryClass.Element("lines")!.Element("line")!.Attribute("hits")!.Value);
        Assert.Equal("7", primaryMethod.Descendants("line").Single().Attribute("hits")!.Value);
        Assert.Equal("0% (0/2)", Branches(primaryMethod.Descendants("line").Single()));
        Assert.All(projected[1].Document.Descendants("line"), line => Assert.Equal("50% (1/2)", Branches(line)));
    }

    private static CoverageAsyncMethod Method() => new("FixtureAssembly", SourceType, "WorkAsync",
        StateMachine, "src/Fixture.cs", [10], "compiled-same-shape");

    private static string Branches(XElement line) => line.Attribute("condition-coverage")!.Value;

    private static XDocument Report(string type, string method, int hits, int covered, int valid)
    {
        XElement Line() => new("line", new XAttribute("number", 10), new XAttribute("hits", hits),
            new XAttribute("branch", "true"), new XAttribute("condition-coverage", $"{100 * covered / valid}% ({covered}/{valid})"));
        var cls = new XElement("class", new XAttribute("name", type), new XAttribute("filename", "src/Fixture.cs"),
            new XElement("methods", new XElement("method", new XAttribute("name", method), new XAttribute("signature", "()"),
                new XElement("lines", Line()))),
            new XElement("lines", Line()));
        return new XDocument(new XElement("coverage", new XAttribute("lines-valid", 1),
            new XAttribute("lines-covered", hits > 0 ? 1 : 0), new XAttribute("branches-valid", valid),
            new XAttribute("branches-covered", covered),
            new XElement("packages", new XElement("package", new XAttribute("name", "FixtureAssembly"), new XElement("classes", cls)))));
    }
}
