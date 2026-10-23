using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageCompilerMapInputsTests
{
    private const string FirstGeneratedName = "Demo.Worker/<Run>d__7";
    private const string SecondGeneratedName = "Demo.Worker/<Run>d__12`1";

    [Fact]
    public async Task PrepareAsync_ShouldPreserveASingleAbsoluteSourceAnchorWithoutRewritingRelativeFilenames()
    {
        using var temp = new TempDirectory();
        var sourceDocument = CreateCollisionReport("src/Worker.cs", reverseGeneratedMaps: false,
            includeNonCollidingClass: false);
        sourceDocument.Root!.AddFirst(new XElement("sources", new XElement("source", temp.Path)));
        var input = temp.WriteDocument("anchored.xml", sourceDocument);
        var prepared = await CoverageCompilerMapInputs.PrepareAsync([input], Path.Combine(temp.Path, "anchored"), default);

        Assert.Equal(2, prepared.Count);
        Assert.Equal(ComputeClassProjectionHash(sourceDocument.Descendants("class")),
            ComputeClassProjectionHash(prepared.SelectMany(path => XDocument.Load(path).Descendants("class"))));
        Assert.All(prepared, path =>
        {
            var document = XDocument.Load(path);
            Assert.True(XNode.DeepEquals(sourceDocument.Root.Element("sources"), document.Root!.Element("sources")));
            Assert.Equal("src/Worker.cs", (string?)Assert.Single(document.Descendants("class")).Attribute("filename"));
        });

        foreach (var filename in new[] { "../Worker.cs", "src//Worker.cs", "src/./Worker.cs", "src\\Worker.cs" })
        {
            var unsafeDocument = new XDocument(sourceDocument);
            foreach (var map in unsafeDocument.Descendants("class")) map.SetAttributeValue("filename", filename);
            var path = temp.WriteDocument(Guid.NewGuid().ToString("N") + ".xml", unsafeDocument);
            Assert.Equal([path], await CoverageCompilerMapInputs.PrepareAsync([path], Path.Combine(temp.Path, "unused"), default));
        }
        sourceDocument.Root.Element("sources")!.Add(new XElement("source", Path.Combine(temp.Path, "other")));
        var ambiguous = temp.WriteDocument("ambiguous.xml", sourceDocument);
        Assert.Equal([ambiguous], await CoverageCompilerMapInputs.PrepareAsync([ambiguous], Path.Combine(temp.Path, "unused"), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_ShouldPreserveEveryGeneratedMapExactlyOnce_InEitherInputOrder(bool reverseGeneratedMaps)
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var sourceDocument = CreateCollisionReport(sourceFile, reverseGeneratedMaps, includeNonCollidingClass: true);
        var originalClasses = sourceDocument.Descendants("class").Select(element => new XElement(element)).ToArray();
        var originalProjectionHash = ComputeClassProjectionHash(originalClasses);
        var inputPath = temp.WriteDocument("coverage.cobertura.xml", sourceDocument);
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal(3, preparedPaths.Count);
        Assert.DoesNotContain(inputPath, preparedPaths);
        Assert.All(preparedPaths, path => Assert.True(File.Exists(path)));
        var stagingDirectory = Path.GetDirectoryName(preparedPaths[0])!;
        Assert.All(preparedPaths, path => Assert.Equal(stagingDirectory, Path.GetDirectoryName(path)));
        Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(stagingDirectory));

        var preparedDocuments = preparedPaths.Select(path => XDocument.Load(path)).ToArray();
        var preparedClasses = preparedDocuments.SelectMany(document => document.Descendants("class")).ToArray();
        Assert.Equal(originalProjectionHash, ComputeClassProjectionHash(preparedClasses));
        Assert.Equal(3, preparedClasses.Length);

        foreach (var originalClass in originalClasses)
        {
            var name = (string)originalClass.Attribute("name")!;
            var matchingClasses = preparedClasses
                .Where(candidate => (string?)candidate.Attribute("name") == name)
                .ToArray();
            var preparedClass = Assert.Single(matchingClasses);
            Assert.True(XNode.DeepEquals(originalClass, preparedClass));

            var containingDocument = Assert.Single(preparedDocuments,
                document => document.Descendants("class").Any(candidate => (string?)candidate.Attribute("name") == name));
            Assert.Single(containingDocument.Descendants("class"));
        }

        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));

        var firstMap = FindClass(preparedClasses, FirstGeneratedName);
        var secondMap = FindClass(preparedClasses, SecondGeneratedName);
        Assert.Equal(sourceFile, (string)firstMap.Attribute("filename")!);
        Assert.Equal(sourceFile, (string)secondMap.Attribute("filename")!);
        Assert.Equal("11", (string)Assert.Single(firstMap.Element("lines")!.Elements("line"),
            line => (string?)line.Attribute("number") == "64").Attribute("hits")!);
        Assert.Equal("0", (string)Assert.Single(secondMap.Element("lines")!.Elements("line"),
            line => (string?)line.Attribute("number") == "64").Attribute("hits")!);
        foreach (var generatedMap in new[] { firstMap, secondMap })
        {
            var branchLine = Assert.Single(generatedMap.Element("lines")!.Elements("line"),
                line => (string?)line.Attribute("branch") == "true");
            Assert.Equal(["0", "1"], branchLine.Descendants("condition")
                .Select(condition => (string)condition.Attribute("number")!).ToArray());
            Assert.Equal("0%", (string)branchLine.Descendants("condition").Last().Attribute("coverage")!);
        }

        var nonCollidingMap = FindClass(preparedClasses, "Demo.Worker");
        Assert.True(XNode.DeepEquals(originalClasses.Single(element => (string?)element.Attribute("name") == "Demo.Worker"), nonCollidingMap));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_ShouldPassThroughDisjointGeneratedMapsWithoutChangingBytes(bool reverseGeneratedMaps)
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var document = CreateCollisionReport(sourceFile, reverseGeneratedMaps, includeNonCollidingClass: false);
        var secondMap = FindClass(document.Descendants("class"), SecondGeneratedName);
        foreach (var line in secondMap.Descendants("line"))
        {
            line.SetAttributeValue("number", (int)line.Attribute("number")! + 64);
        }
        var inputPath = temp.WriteDocument("disjoint.cobertura.xml", document);
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal([inputPath], preparedPaths);
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_ShouldPassThroughRepeatedGeneratedNamesWithoutCombiningCounters(bool reverseGeneratedMaps)
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var document = CreateCollisionReport(sourceFile, reverseGeneratedMaps, includeNonCollidingClass: false);
        foreach (var map in document.Descendants("class"))
        {
            map.SetAttributeValue("name", FirstGeneratedName);
        }
        var inputPath = temp.WriteDocument("repeated-names.cobertura.xml", document);
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal([inputPath], preparedPaths);
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
        Assert.False(Directory.Exists(outputDirectory));
        var retainedMaps = XDocument.Load(Assert.Single(preparedPaths)).Descendants("class").ToArray();
        Assert.Equal(2, retainedMaps.Length);
        Assert.All(retainedMaps, map => Assert.Equal(FirstGeneratedName, (string?)map.Attribute("name")));
        Assert.Equal(ComputeClassProjectionHash(document.Descendants("class")), ComputeClassProjectionHash(retainedMaps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_ShouldPartitionOnlyTheOverlappingFamily(bool reverseInputOrder)
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var document = CreateCollisionReport(sourceFile, reverseGeneratedMaps: false, includeNonCollidingClass: false);
        const string firstDisjointName = "Demo.OtherWorker/<Run>d__3";
        const string secondDisjointName = "Demo.OtherWorker/<Run>d__9";
        var firstDisjointMap = CreateGeneratedClass(firstDisjointName, sourceFile, 7);
        var secondDisjointMap = CreateGeneratedClass(secondDisjointName, sourceFile, 0);
        foreach (var line in secondDisjointMap.Descendants("line"))
        {
            line.SetAttributeValue("number", (int)line.Attribute("number")! + 64);
        }
        var classContainer = Assert.Single(document.Descendants("classes"));
        classContainer.Add(firstDisjointMap, secondDisjointMap);
        if (reverseInputOrder)
        {
            classContainer.ReplaceNodes(classContainer.Elements("class").Reverse().Select(map => new XElement(map)).ToArray());
        }
        var originalClasses = document.Descendants("class").Select(map => new XElement(map)).ToArray();
        var inputPath = temp.WriteDocument("independent-families.cobertura.xml", document);
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal(3, preparedPaths.Count);
        Assert.DoesNotContain(inputPath, preparedPaths);
        Assert.All(preparedPaths, path => Assert.True(File.Exists(path)));
        var stagingDirectory = Path.GetDirectoryName(preparedPaths[0])!;
        Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(stagingDirectory));
        Assert.All(preparedPaths, path => Assert.Equal(stagingDirectory, Path.GetDirectoryName(path)));
        var preparedDocuments = preparedPaths.Select(path => XDocument.Load(path)).ToArray();
        var preparedClasses = preparedDocuments.SelectMany(prepared => prepared.Descendants("class")).ToArray();
        Assert.Equal(4, preparedClasses.Length);
        Assert.Equal(ComputeClassProjectionHash(originalClasses), ComputeClassProjectionHash(preparedClasses));
        foreach (var original in originalClasses)
        {
            var retained = FindClass(preparedClasses, (string)original.Attribute("name")!);
            Assert.True(XNode.DeepEquals(original, retained));
        }

        var baseDocument = Assert.Single(preparedDocuments,
            prepared => prepared.Descendants("class").Any(map => (string?)map.Attribute("name") == firstDisjointName));
        var expectedBaseNames = originalClasses
            .Select(map => (string)map.Attribute("name")!)
            .Where(name => name is firstDisjointName or secondDisjointName);
        Assert.Equal(expectedBaseNames, baseDocument.Descendants("class").Select(map => (string)map.Attribute("name")!));
        foreach (var name in new[] { FirstGeneratedName, SecondGeneratedName })
        {
            var partition = Assert.Single(preparedDocuments,
                prepared => prepared.Descendants("class").Any(map => (string?)map.Attribute("name") == name));
            Assert.Equal(name, (string?)Assert.Single(partition.Descendants("class")).Attribute("name"));
        }
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
    }

    [Fact]
    public async Task PrepareAsync_ShouldPassThroughEmptyCoberturaWithoutStaging()
    {
        using var temp = new TempDirectory();
        var inputPath = temp.WriteText("empty.cobertura.xml", "<coverage />");
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal([inputPath], preparedPaths);
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task PrepareAsync_ShouldPassThroughWholeReportWhenAnyClassHasRelativeFilename()
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var document = CreateCollisionReport(sourceFile, reverseGeneratedMaps: false, includeNonCollidingClass: false);
        document.Root!.Element("packages")!.Element("package")!.Element("classes")!
            .Add(CreatePlainClass("Demo.Relative", "relative/Other.cs", 3));
        var inputPath = temp.WriteDocument("mixed-filenames.cobertura.xml", document);
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal([inputPath], preparedPaths);
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task PrepareAsync_ShouldPassThroughMissingAndGlobInputsUnchanged()
    {
        using var temp = new TempDirectory();
        string[] inputs =
        [
            Path.Combine(temp.Path, "missing.cobertura.xml"),
            Path.Combine(temp.Path, "reports", "**", "coverage*.xml"),
        ];
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync(inputs, outputDirectory, CancellationToken.None);

        Assert.Equal(inputs, preparedPaths);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task PrepareAsync_ShouldStopBeforeCreatingOutputWhenAlreadyCancelled()
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var inputPath = temp.WriteDocument(
            "coverage.cobertura.xml",
            CreateCollisionReport(sourceFile, reverseGeneratedMaps: false, includeNonCollidingClass: false));
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, cancellation.Token));

        Assert.False(Directory.Exists(outputDirectory));
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
    }

    [Fact]
    public async Task PrepareAsync_ShouldIgnoreStandardCoberturaDoctypeWithoutExternalResolution()
    {
        using var temp = new TempDirectory();
        var inputPath = temp.WriteText(
            "doctype.cobertura.xml",
            "<!DOCTYPE coverage SYSTEM \"http://cobertura.sourceforge.net/xml/coverage-04.dtd\"><coverage />");
        var originalInputHash = ComputeFileHash(inputPath);
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var preparedPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal([inputPath], preparedPaths);
        Assert.Equal(originalInputHash, ComputeFileHash(inputPath));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task PrepareAsync_ShouldRejectMalformedXmlAndEntityReferencesWithTheFixedError()
    {
        using var temp = new TempDirectory();
        var outputDirectory = Path.Combine(temp.Path, "merge-output");
        var invalidInputs = new[]
        {
            temp.WriteText("malformed.cobertura.xml", "<coverage><packages>"),
            temp.WriteText(
                "external-entity.cobertura.xml",
                "<!DOCTYPE coverage [<!ENTITY external SYSTEM \"file:///must-not-be-resolved\">]><coverage>&external;</coverage>"),
        };

        foreach (var inputPath in invalidInputs)
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None));

            Assert.Equal("The compiler-generated coverage map input was rejected.", exception.Message);
            Assert.False(Directory.Exists(outputDirectory));
        }
    }

    [Fact]
    public async Task PrepareAsync_ShouldUseAFreshOwnedStagingDirectoryForEachCall()
    {
        using var temp = new TempDirectory();
        var sourceFile = Path.GetFullPath(Path.Combine(temp.Path, "src", "Worker.cs"));
        var inputPath = temp.WriteDocument(
            "coverage.cobertura.xml",
            CreateCollisionReport(sourceFile, reverseGeneratedMaps: false, includeNonCollidingClass: false));
        var outputDirectory = Path.Combine(temp.Path, "merge-output");

        var firstCallPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);
        var secondCallPaths = await CoverageCompilerMapInputs.PrepareAsync([inputPath], outputDirectory, CancellationToken.None);

        Assert.Equal(2, firstCallPaths.Count);
        Assert.Equal(2, secondCallPaths.Count);
        Assert.All(firstCallPaths, path => Assert.True(File.Exists(path)));
        Assert.All(secondCallPaths, path => Assert.True(File.Exists(path)));
        Assert.Empty(firstCallPaths.Intersect(secondCallPaths, StringComparer.Ordinal));

        var firstStagingDirectory = Path.GetDirectoryName(firstCallPaths[0])!;
        var secondStagingDirectory = Path.GetDirectoryName(secondCallPaths[0])!;
        Assert.NotEqual(firstStagingDirectory, secondStagingDirectory);
        Assert.All(firstCallPaths, path => Assert.Equal(firstStagingDirectory, Path.GetDirectoryName(path)));
        Assert.All(secondCallPaths, path => Assert.Equal(secondStagingDirectory, Path.GetDirectoryName(path)));
        Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(firstStagingDirectory));
        Assert.Equal(Path.GetFullPath(outputDirectory), Path.GetDirectoryName(secondStagingDirectory));
    }

    private static XDocument CreateCollisionReport(string sourceFile, bool reverseGeneratedMaps, bool includeNonCollidingClass)
    {
        XElement[] generatedMaps =
        [
            CreateGeneratedClass(FirstGeneratedName, sourceFile, 11),
            CreateGeneratedClass(SecondGeneratedName, sourceFile, 0),
        ];
        if (reverseGeneratedMaps)
        {
            Array.Reverse(generatedMaps);
        }

        var classes = new List<XElement>(generatedMaps);
        if (includeNonCollidingClass)
        {
            classes.Add(CreatePlainClass("Demo.Worker", sourceFile, 2));
        }

        return new XDocument(
            new XElement("coverage",
                new XAttribute("line-rate", "0.75"),
                new XAttribute("branch-rate", "0.5"),
                new XElement("packages",
                    new XElement("package",
                        new XAttribute("name", "Demo"),
                        new XElement("classes", classes)))));
    }

    private static XElement CreateGeneratedClass(string name, string sourceFile, int hits)
    {
        var hitText = hits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var firstConditionCoverage = hits == 0 ? "0%" : "100%";
        var aggregateConditionCoverage = hits == 0 ? "0% (0/2)" : "50% (1/2)";
        XElement CreateBranchLine() =>
            new("line",
                new XAttribute("number", "64"),
                new XAttribute("hits", hitText),
                new XAttribute("branch", "true"),
                new XAttribute("condition-coverage", aggregateConditionCoverage),
                new XElement("conditions",
                    new XElement("condition",
                        new XAttribute("number", "0"),
                        new XAttribute("type", "jump"),
                        new XAttribute("coverage", firstConditionCoverage)),
                    new XElement("condition",
                        new XAttribute("number", "1"),
                        new XAttribute("type", "jump"),
                        new XAttribute("coverage", "0%"))));

        XElement CreateOtherLine() => new(
            "line",
            new XAttribute("number", "65"),
            new XAttribute("hits", hits == 0 ? "0" : "4"));

        return new XElement("class",
            new XAttribute("name", name),
            new XAttribute("filename", sourceFile),
            new XAttribute("line-rate", hits == 0 ? "0" : "0.5"),
            new XAttribute("branch-rate", hits == 0 ? "0" : "0.5"),
            new XAttribute("complexity", "1"),
            new XElement("methods",
                new XElement("method",
                    new XAttribute("name", "Run"),
                    new XAttribute("signature", "()"),
                    new XAttribute("line-rate", hits == 0 ? "0" : "0.5"),
                    new XAttribute("branch-rate", hits == 0 ? "0" : "0.5"),
                    new XElement("lines", CreateBranchLine(), CreateOtherLine()))),
            new XElement("lines", CreateBranchLine(), CreateOtherLine()));
    }

    private static XElement CreatePlainClass(string name, string sourceFile, int hits) =>
        new("class",
            new XAttribute("name", name),
            new XAttribute("filename", sourceFile),
            new XAttribute("line-rate", "1"),
            new XAttribute("branch-rate", "1"),
            new XAttribute("complexity", "1"),
            new XElement("methods"),
            new XElement("lines",
                new XElement("line",
                    new XAttribute("number", "64"),
                    new XAttribute("hits", hits.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

    private static XElement FindClass(IEnumerable<XElement> classes, string name) =>
        Assert.Single(classes, element => (string?)element.Attribute("name") == name);

    private static string ComputeClassProjectionHash(IEnumerable<XElement> classes)
    {
        var projection = string.Join(
            "\n",
            classes.Select(element => element.ToString(SaveOptions.DisableFormatting)).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));
    }

    private static string ComputeFileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "appsurface-coverage-compiler-map-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteDocument(string relativePath, XDocument document)
        {
            var path = GetPath(relativePath);
            document.Save(path, SaveOptions.DisableFormatting);
            return path;
        }

        public string WriteText(string relativePath, string contents)
        {
            var path = GetPath(relativePath);
            File.WriteAllText(path, contents, Encoding.UTF8);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private string GetPath(string relativePath)
        {
            var path = TestPathUtils.PathUnder(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            return path;
        }
    }
}
