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
    public void Ambiguous_or_different_records_keep_their_original_measurement(string defect)
    {
        var confirmed = Method();
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 1, 2, defect == "different-denominator" ? 3 : 2);
        if (defect == "duplicate-method")
        {
            var method = native.Descendants("method").Single();
            method.Parent!.Add(new XElement(method));
        }
        IReadOnlyList<CoverageAsyncMethod> nativeSymbols = defect switch
        {
            "fingerprint" => [confirmed with { Fingerprint = "compiled-other-shape" }],
            "duplicate-symbol" => [confirmed, confirmed],
            "different-source" => [confirmed with { File = "elsewhere.cs" }],
            _ => [confirmed]
        };
        IReadOnlyList<CoverageAsyncMethod> primarySymbols =
            defect == "no-sequence-point" ? [confirmed with { Lines = [99] }] : [confirmed];

        var projected = CoverageAsyncPairing.Project([(primary, Root), (native, Root)], [primarySymbols, nativeSymbols]);

        Assert.All(projected[0].Document.Descendants("line"), line => Assert.Equal("0% (0/2)", Branches(line)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flattened_nested_class_requires_a_unique_source_sequence_point(bool ambiguous)
    {
        var nested = Method() with { SourceType = SourceType + "/Inner" };
        var primary = Report(SourceType, "WorkAsync", 0, 0, 2);
        var native = Report(StateMachine, "MoveNext", 1, 2, 2);
        IReadOnlyList<CoverageAsyncMethod> primarySymbols = ambiguous
            ? [nested, nested with { SourceType = SourceType + "/Other", StateMachineType = StateMachine + "Other" }]
            : [nested];

        var projected = CoverageAsyncPairing.Project([(primary, Root), (native, Root)], [primarySymbols, [nested]]);

        Assert.All(projected[0].Document.Descendants("line"),
            line => Assert.Equal(ambiguous ? "0% (0/2)" : "100% (2/2)", Branches(line)));
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
