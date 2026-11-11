using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageFanInCollectorProjectionTests
{
    private const string Root = "/agent/work";
    private const string Original = """
        <coverage lines-covered="1" lines-valid="2" branches-covered="2" branches-valid="4">
          <packages><package name="P"><classes>
            <class name="A" filename="src/A.cs"><lines><line number="1" hits="0" branch="true" condition-coverage="0% (0/2)"/></lines>
              <methods><method name="Run" signature="()"><lines><line number="1" hits="0"/></lines></method></methods>
            </class>
            <class name="B" filename="src/A.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class>
          </classes></package></packages>
        </coverage>
        """;

    [Theory]
    [InlineData("exact")]
    [InlineData("class-owner")]
    [InlineData("package-owner")]
    [InlineData("method-owner")]
    [InlineData("method-signature")]
    [InlineData("drop-method")]
    [InlineData("invent-method-hit")]
    [InlineData("duplicate-class-point")]
    [InlineData("root-counter")]
    public void NativeVerifierRequiresExactOriginalOwnershipAndFacts(string mutation)
    {
        var raw = XDocument.Parse(Original);
        // Hand-authored golden: the verifier is checked without the composer as oracle.
        var projected = XDocument.Parse(Original);
        var before = new XDocument(raw);
        switch (mutation)
        {
            case "class-owner":
                var points = projected.Descendants("class").Select(c => c.Element("lines")!.Element("line")!).ToArray();
                points[0].SetAttributeValue("hits", 1);
                points[0].SetAttributeValue("condition-coverage", "100% (2/2)");
                points[1].SetAttributeValue("hits", 0);
                points[1].SetAttributeValue("condition-coverage", "0% (0/2)");
                break;
            case "package-owner": projected.Descendants("package").Single().SetAttributeValue("name", "Other"); break;
            case "method-owner":
                var methods = projected.Descendants("methods").Single();
                methods.Remove(); projected.Descendants("class").Last().AddFirst(methods);
                break;
            case "method-signature": projected.Descendants("method").Single().SetAttributeValue("signature", "(int)"); break;
            case "drop-method": projected.Descendants("method").Single().Remove(); break;
            case "invent-method-hit": projected.Descendants("method").Single().Descendants("line").Single().SetAttributeValue("hits", 1); break;
            case "duplicate-class-point":
                projected.Descendants("class").First().Element("lines")!.Add(new XElement(projected.Descendants("class").Last().Element("lines")!.Element("line")!));
                projected.Root!.SetAttributeValue("lines-valid", 3);
                projected.Root.SetAttributeValue("lines-covered", 2);
                projected.Root.SetAttributeValue("branches-valid", 6);
                projected.Root.SetAttributeValue("branches-covered", 4);
                break;
            case "root-counter": projected.Root!.SetAttributeValue("branches-covered", 4); break;
        }
        var projectedBefore = new XDocument(projected);
        if (mutation == "exact") CoverageFanInIndependentVerifier.VerifyNativeCollection([(raw, Root)], projected);
        else Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNativeCollection([(raw, Root)], projected));
        Assert.True(XNode.DeepEquals(before, raw));
        Assert.True(XNode.DeepEquals(projectedBefore, projected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateClassAndLineOccurrencesCannotExchangeObservations(bool duplicateClass)
    {
        var raw = XDocument.Parse(Original);
        if (duplicateClass)
            raw.Descendants("class").Last().SetAttributeValue("name", "A");
        else
        {
            var last = raw.Descendants("class").Last();
            raw.Descendants("class").First().Element("lines")!.Add(new XElement(last.Element("lines")!.Element("line")!));
            last.Remove();
        }
        var projection = new XDocument(raw);
        var lines = projection.Descendants("class").SelectMany(c => c.Element("lines")!.Elements("line")).ToArray();
        lines[0].SetAttributeValue("hits", 1);
        lines[0].SetAttributeValue("condition-coverage", "100% (2/2)");
        lines[1].SetAttributeValue("hits", 0);
        lines[1].SetAttributeValue("condition-coverage", "0% (0/2)");
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNativeCollection([(raw, Root)], projection));
    }

    [Theory]
    [InlineData("/agent/work", "/", "agent/work/src/A.cs")]
    [InlineData("C:/agent/work", "C:/", "agent/work/src/A.cs")]
    [InlineData("//server/share/work", "//server/share/", "work/src/A.cs")]
    public void RawProjectionPreservesSourceBasisAndMethodsWithoutSynthesizingOwners(string root, string source, string filename)
    {
        var raw = XDocument.Parse(Original);
        raw.Root!.AddFirst(new XElement("sources", new XElement("source", source)));
        foreach (var owner in raw.Descendants("class")) owner.SetAttributeValue("filename", filename);
        var before = new XDocument(raw);
        var projection = CoverageFanInComposer.Compose([(raw, root)]);
        CoverageFanInIndependentVerifier.VerifyNativeCollection([(raw, root)], projection);
        Assert.All(projection.Descendants("class"), owner => Assert.Equal("src/A.cs", owner.Attribute("filename")!.Value));
        Assert.Equal(2, projection.Descendants("class").Count());
        Assert.Single(projection.Descendants("method"));
        Assert.Equal("2", projection.Root!.Attribute("branches-covered")!.Value);
        Assert.Equal("4", projection.Root.Attribute("branches-valid")!.Value);
        Assert.True(XNode.DeepEquals(before, raw));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("2147483648")]
    [InlineData("-1")]
    [InlineData("class")]
    [InlineData("root")]
    [InlineData("branch")]
    public void CanonicalVerifierClassifiesMalformedOriginalsAndProjectionsWithoutMutation(string mutation)
    {
        foreach (var mutateRaw in new[] { true, false })
        {
            var raw = XDocument.Parse(Original);
            var projection = XDocument.Parse(Original);
            var changed = mutateRaw ? raw : projection;
            if (mutation == "class") changed.Descendants("class").First().Attribute("filename")!.Remove();
            else if (mutation == "root") changed.Root!.Name = "other";
            else if (mutation == "branch") changed.Descendants("class").First().Element("lines")!.Element("line")!.SetAttributeValue("condition-coverage", "100% (3/2)");
            else changed.Descendants("method").Single().Descendants("line").Single().SetAttributeValue("hits", mutation);
            var rawBefore = new XDocument(raw);
            var projectionBefore = new XDocument(projection);
            var error = Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNativeCollection([(raw, Root)], projection));
            Assert.Equal("Coverage composition failed independent verification.", error.Message);
            Assert.True(XNode.DeepEquals(rawBefore, raw));
            Assert.True(XNode.DeepEquals(projectionBefore, projection));
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("null-report")]
    [InlineData("blank-root")]
    [InlineData("no-class-points")]
    public void NativeVerifierRejectsMissingOriginalsAndMethodOnlyEvidence(string mutation)
    {
        var raw = XDocument.Parse(Original);
        IReadOnlyList<(XDocument Document, string SourceRoot)> originals = [(raw, Root)];
        switch (mutation)
        {
            case "empty": originals = []; break;
            case "null": originals = null!; break;
            case "null-report": originals = [(null!, Root)]; break;
            case "blank-root": originals = [(raw, " ")]; break;
            default: foreach (var owner in raw.Descendants("class")) owner.Element("lines")!.RemoveNodes(); break;
        }
        Assert.Throws<InvalidDataException>(() => CoverageFanInIndependentVerifier.VerifyNativeCollection(originals, XDocument.Parse(Original)));
    }
}
