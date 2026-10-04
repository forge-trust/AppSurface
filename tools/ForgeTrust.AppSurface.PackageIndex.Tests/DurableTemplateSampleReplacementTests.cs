using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateSampleReplacementTests : IDisposable
{
    private const string ProjectName = "FirstDurableWorker";
    private const string SourceName = "AppSurfaceDurableWorker";
    private readonly string _root = CreateTestRoot();
    private readonly string _repositoryRoot;
    private readonly string _generatedRoot;

    public DurableTemplateSampleReplacementTests()
    {
        Directory.CreateDirectory(_root);
        _repositoryRoot = FindRepositoryRoot();
        _generatedRoot = TestPathUtils.PathUnder(_root, ProjectName);
        MaterializeGeneratedTemplate(_generatedRoot);
    }

    [Fact]
    public void ReplacesOnlyTheThreeDocumentedWorkLocationsWithStableContentEvidence()
    {
        var before = Snapshot(_generatedRoot);

        var changedPaths = DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName);

        var after = Snapshot(_generatedRoot);
        var changed = before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var original)
                || !after.TryGetValue(path, out var replacement)
                || !original.Equals(replacement, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedChangedPaths();
        Assert.Equal(expected, changedPaths);
        Assert.Equal(expected, changed);
        var contentSha256 = DurableTemplateSampleReplacement.ComputeContentSha256(_generatedRoot, changedPaths);
        Assert.Equal(ComputeContentSha256(_generatedRoot, expected), contentSha256);
        Assert.Matches("^[0-9a-f]{64}$", contentSha256);

        var definition = ReadWorkFile("SampleWork.cs");
        Assert.Contains("public sealed record SampleWork(string Value, int Multiplier = 2);", definition, StringComparison.Ordinal);
        Assert.Contains("public sealed record SampleWorkResult(string Value, int AppliedMultiplier = 2);", definition, StringComparison.Ordinal);
        Assert.Contains("public const string WorkName = \"appsurface.replacement.multiplied-sample-work\";", definition, StringComparison.Ordinal);
        Assert.Contains("public const string WorkVersion = \"v2\";", definition, StringComparison.Ordinal);
        Assert.Contains("public const string Scope = \"appsurface-replacement-sample\";", definition, StringComparison.Ordinal);
        Assert.Contains("appsurface.replacement.multiplied-sample-work-input", definition, StringComparison.Ordinal);
        Assert.Contains("appsurface.replacement.multiplied-sample-work-result", definition, StringComparison.Ordinal);

        var executor = ReadWorkFile("SampleWorkExecutor.cs");
        Assert.Contains("new SampleWorkResult($\"processed:{payload.Value}\", payload.Multiplier)", executor, StringComparison.Ordinal);

        var registration = ReadWorkFile("WorkRegistration.cs");
        Assert.Contains("var binding = SampleWorkDefinition.Definition.ExecutedBy<SampleWorkExecutor>();", registration, StringComparison.Ordinal);
        Assert.Contains("DurableWorkBinding<SampleWork, SampleWorkResult, SampleWorkExecutor>", registration, StringComparison.Ordinal);
        Assert.Contains("services.AddDurableWork(binding);", registration, StringComparison.Ordinal);

        var producer = ReadWorkFile("SampleWorkProducer.cs");
        Assert.Contains("if (work.Multiplier is < 1 or > 12)", producer, StringComparison.Ordinal);
        Assert.Contains("new DurableScopeId(SampleWorkDefinition.Scope)", producer, StringComparison.Ordinal);

        var secondRoot = TestPathUtils.PathUnder(_root, "second", ProjectName);
        MaterializeGeneratedTemplate(secondRoot);
        var secondPaths = DurableTemplateSampleReplacement.Apply(secondRoot, ProjectName);
        Assert.Equal(changedPaths, secondPaths);
        Assert.Equal(contentSha256, DurableTemplateSampleReplacement.ComputeContentSha256(secondRoot, secondPaths));
    }

    [Fact]
    public void InvalidCompilationInputFailsBeforeAnyWorkFileIsChanged()
    {
        var registrationPath = WorkPath("WorkRegistration.cs");
        var invalidRegistration = File.ReadAllText(registrationPath, Encoding.UTF8)
            .Replace("services.AddDurableWork(SampleWorkDefinition.Definition.ExecutedBy<SampleWorkExecutor>());",
                "services.AddDurableWork(;", StringComparison.Ordinal);
        Assert.NotEqual(File.ReadAllText(registrationPath, Encoding.UTF8), invalidRegistration);
        File.WriteAllText(registrationPath, invalidRegistration, new UTF8Encoding(false));
        var before = Snapshot(_generatedRoot);

        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName));

        Assert.Equal(before, Snapshot(_generatedRoot));
        Assert.Contains("services.AddDurableWork(;", File.ReadAllText(registrationPath, Encoding.UTF8), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-final-file")]
    [InlineData("invalid-utf8")]
    [InlineData("wrong-namespace")]
    public void InvalidFinalWorkInputFailsBeforeAnyWorkFileIsChanged(string fault)
    {
        var producerPath = WorkPath("SampleWorkProducer.cs");
        var producer = File.ReadAllText(producerPath, Encoding.UTF8);
        switch (fault)
        {
            case "missing-final-file":
                File.Delete(producerPath);
                break;
            case "invalid-utf8":
                File.WriteAllBytes(producerPath, [0xC3, 0x28]);
                break;
            case "wrong-namespace":
                File.WriteAllText(producerPath,
                    producer.Replace($"namespace {ProjectName}.Work;", "namespace OtherProject.Work;", StringComparison.Ordinal),
                    new UTF8Encoding(false));
                break;
            default:
                throw new InvalidOperationException($"Unexpected fixture fault: {fault}");
        }
        var before = Snapshot(_generatedRoot);

        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName));

        Assert.Equal(before, Snapshot(_generatedRoot));
    }

    [Fact]
    public void ApplyRejectsMissingMisnamedAndIncompleteGeneratedRoots()
    {
        var missing = TestPathUtils.PathUnder(_root, "missing", ProjectName);
        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(missing, ProjectName));

        var misnamed = TestPathUtils.PathUnder(_root, "DifferentGeneratedName");
        Directory.CreateDirectory(misnamed);
        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(misnamed, ProjectName));

        var workDirectory = TestPathUtils.PathUnder(_generatedRoot, "src", ProjectName, "Work");
        Directory.Delete(workDirectory, recursive: true);
        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName));
    }

    [Fact]
    public void ReplacementPreservesTheExistingCrLfLineEndingStyle()
    {
        foreach (var relativePath in ExpectedChangedPaths())
        {
            var path = TestPathUtils.PathUnder(_generatedRoot, relativePath.Split('/'));
            var content = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(path, content.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(false));
        }

        var changedPaths = DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName);

        Assert.Equal(ExpectedChangedPaths(), changedPaths);
        foreach (var relativePath in changedPaths)
        {
            var content = File.ReadAllText(TestPathUtils.PathUnder(_generatedRoot, relativePath.Split('/')), Encoding.UTF8);
            Assert.Contains("\r\n", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", content.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ContentHashRejectsUnapprovedOrIncompleteWorkInventories()
    {
        var paths = DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName);
        Assert.Throws<PackageIndexException>(() =>
            DurableTemplateSampleReplacement.ComputeContentSha256(_generatedRoot, paths.Take(3).ToArray()));
        Assert.Throws<PackageIndexException>(() =>
            DurableTemplateSampleReplacement.ComputeContentSha256(_generatedRoot, paths.Append("src/FirstDurableWorker/Work/extra.cs").ToArray()));

        File.Delete(TestPathUtils.PathUnder(_generatedRoot, paths[0].Split('/')));
        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.ComputeContentSha256(_generatedRoot, paths));
    }

    [Fact]
    public void ArbitraryProjectNamesAndRepeatedReplacementFailWithoutFurtherMutation()
    {
        var before = Snapshot(_generatedRoot);
        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(_generatedRoot, "FirstDurableWorker; arbitrary tokens"));
        Assert.Equal(before, Snapshot(_generatedRoot));

        DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName);
        var replaced = Snapshot(_generatedRoot);
        Assert.Throws<PackageIndexException>(() => DurableTemplateSampleReplacement.Apply(_generatedRoot, ProjectName));
        Assert.Equal(replaced, Snapshot(_generatedRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string ReadWorkFile(string fileName) => File.ReadAllText(WorkPath(fileName), Encoding.UTF8);

    private string WorkPath(string fileName) => TestPathUtils.PathUnder(_generatedRoot, "src", ProjectName, "Work", fileName);

    private static string[] ExpectedChangedPaths()
    =>
    [
        $"src/{ProjectName}/Work/SampleWork.cs",
        $"src/{ProjectName}/Work/SampleWorkExecutor.cs",
        $"src/{ProjectName}/Work/SampleWorkProducer.cs",
        $"src/{ProjectName}/Work/WorkRegistration.cs"
    ];

    private static string ComputeContentSha256(string root, IEnumerable<string> relativePaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relativePath in relativePaths.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath + "\0"));
            hash.AppendData(File.ReadAllBytes(TestPathUtils.PathUnder(root, relativePath.Split('/'))));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private void MaterializeGeneratedTemplate(string generatedRoot)
    {
        var sourceRoot = TestPathUtils.PathUnder(_repositoryRoot, DurableTemplateStaging.ContentPath);
        foreach (var source in DurableTemplateStaging.EnumerateContent(sourceRoot))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(".template.config/", StringComparison.Ordinal)) continue;
            var generatedRelative = relative.Replace(SourceName, ProjectName, StringComparison.Ordinal);
            var destination = TestPathUtils.PathUnder(generatedRoot, generatedRelative.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var content = File.ReadAllText(source, Encoding.UTF8).Replace(SourceName, ProjectName, StringComparison.Ordinal);
            File.WriteAllText(destination, content, new UTF8Encoding(false));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx"))
                && Directory.Exists(TestPathUtils.PathUnder(directory.FullName, DurableTemplateStaging.ContentPath)))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the AppSurface repository root from the test assembly.");
    }

    private static string CreateTestRoot()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/tmp/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        return TestPathUtils.PathUnder(temporary, "template-sample-replacement-tests", Guid.NewGuid().ToString("N"));
    }
}
