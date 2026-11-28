using System.IO.Compression;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Checks safe user-facing option diagnostics from the Dogfood executable.</summary>
public sealed class CoverageDogfoodDiagnosticsTests
{
    [Theory]
    [InlineData("grouped-duplicate")]
    [InlineData("ungrouped-duplicate")]
    [InlineData("case-varied-second-group")]
    [InlineData("outside-framework-group")]
    public async Task PackageProofRejectsInvalidPrivatePinsBeforeCreatingOutput(string defect)
    {
        using var temp = TestDirectory.Create();
        const string packageId = "ForgeTrust.AppSurface.Coverage";
        const string privateId = "ForgeTrust.AppSurface.Evidence.Coverage";
        const string version = "0.0.0-private-pin-test";
        var feed = TestPathUtils.PathUnder(temp.Path, "feed");
        var output = TestPathUtils.PathUnder(temp.Path, "proof");
        Directory.CreateDirectory(feed);
        var pin = new XElement("dependency", new XAttribute("id", privateId), new XAttribute("version", $"[{version}]"));
        var group = new XElement("group", new XAttribute("targetFramework", "net10.0"), pin);
        var dependencies = new XElement("dependencies", group);
        switch (defect)
        {
            case "grouped-duplicate":
                group.Add(new XElement(pin));
                break;
            case "ungrouped-duplicate":
                dependencies.ReplaceNodes(new XElement(pin), new XElement(pin));
                break;
            case "case-varied-second-group":
                dependencies.Add(new XElement("group", new XAttribute("targetFramework", "net9.0"),
                    new XElement("dependency", new XAttribute("id", privateId.ToLowerInvariant()), new XAttribute("version", $"[{version}]"))));
                break;
            case "outside-framework-group":
                dependencies.Add(new XElement(pin));
                break;
        }
        var metadata = new XElement("metadata", new XElement("id", packageId), new XElement("version", version),
            new XElement("repository", new XAttribute("commit", "0123456789abcdef0123456789abcdef01234567")), dependencies);
        using (var archive = ZipFile.Open(TestPathUtils.PathUnder(feed, $"{packageId}.{version}.nupkg"), ZipArchiveMode.Create))
        using (var specification = archive.CreateEntry($"{packageId}.nuspec").Open())
            new XDocument(new XElement("package", metadata)).Save(specification);

        var repository = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var result = await RunPackageProofAsync(repository, temp.Path, feed, version, output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("coverage.private-dependency-invalid", result.StandardError, StringComparison.Ordinal);
        Assert.False(Path.Exists(output));
    }

    [Fact]
    public async Task PackageProofRejectsOversizedCandidateBeforeReadingItsZipDirectory()
    {
        using var temp = TestDirectory.Create();
        const string version = "0.0.0-private-pin-test";
        var feed = TestPathUtils.PathUnder(temp.Path, "feed");
        var output = TestPathUtils.PathUnder(temp.Path, "proof");
        Directory.CreateDirectory(feed);
        var candidate = TestPathUtils.PathUnder(feed, $"ForgeTrust.AppSurface.Coverage.{version}.nupkg");
        using (var stream = File.Create(candidate))
            stream.SetLength(512L * 1024 * 1024 + 1);

        var repository = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var result = await RunPackageProofAsync(repository, temp.Path, feed, version, output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("coverage.candidate-archive-limit", result.StandardError, StringComparison.Ordinal);
        Assert.False(Path.Exists(output));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("dtd")]
    public async Task PackageProofRejectsInvalidNuspecBeforeCreatingOutput(string defect)
    {
        using var temp = TestDirectory.Create();
        const string packageId = "ForgeTrust.AppSurface.Coverage";
        const string version = "0.0.0-private-pin-test";
        var feed = TestPathUtils.PathUnder(temp.Path, "feed");
        var output = TestPathUtils.PathUnder(temp.Path, "proof");
        Directory.CreateDirectory(feed);
        var contents = defect switch
        {
            "oversized" => "<package><metadata/></package>" + new string(' ', 1024 * 1024),
            "malformed" => "<package><metadata/>",
            "dtd" => "<!DOCTYPE package><package><metadata/></package>",
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        using (var archive = ZipFile.Open(TestPathUtils.PathUnder(feed, $"{packageId}.{version}.nupkg"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry($"{packageId}.nuspec").Open()))
            writer.Write(contents);

        var repository = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var result = await RunPackageProofAsync(repository, temp.Path, feed, version, output);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("coverage.candidate-nuspec-invalid", result.StandardError, StringComparison.Ordinal);
        Assert.False(Path.Exists(output));
    }

    private static async Task<CoverageRunProcessResult> RunPackageProofAsync(
        string repository, string workingDirectory, string feed, string version, string output)
    {
        var (assembly, _) = FindDogfoodAssembly(repository);
        var dotnet = TestDotnetHost.ResolveExisting();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest(dotnet,
            [assembly, "--package-proof", "--feed", feed, "--version", version, "--output", output, "--dotnet", dotnet],
            workingDirectory, null, null, CoverageRunProcessLease.Detached());
        return await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token);
    }

    [Fact]
    public void CollectionInputsSelectExactProjectsAndPreserveOriginalReportBytes()
    {
        using var temp = TestDirectory.Create();
        var collection = TestPathUtils.PathUnder(temp.Path, "collection");
        Directory.CreateDirectory(collection);
        const string alpha = "projects/alpha/coverage.cobertura.xml";
        const string beta = "projects/beta/coverage.cobertura.xml";
        var alphaPath = TestPathUtils.PathUnder(collection, alpha);
        var betaPath = TestPathUtils.PathUnder(collection, beta);
        Directory.CreateDirectory(Path.GetDirectoryName(alphaPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(betaPath)!);
        byte[] alphaBytes = [0, 1, 2, 255];
        byte[] betaBytes = [7, 8, 9];
        File.WriteAllBytes(alphaPath, alphaBytes);
        File.WriteAllBytes(betaPath, betaBytes);
        var timings = TestPathUtils.PathUnder(collection, "timings.json");
        File.WriteAllText(timings, $$"""{"projects":[{"project":"A.Tests","coverageFile":"{{alpha}}"},{"project":"B.Tests","coverageFile":"{{beta}}"}]}""");

        var inputs = NativeCoverageCaptureInputs.FromCollection(collection, ["A.Tests", "B.Tests"], 4096);

        Assert.Equal(timings, inputs.TimingsPath);
        Assert.Equal(2, inputs.RawCoveragePaths.Count);
        Assert.Equal(alphaPath, inputs.RawCoveragePaths["A.Tests"]);
        Assert.Equal(betaPath, inputs.RawCoveragePaths["B.Tests"]);
        Assert.Equal(alphaBytes, File.ReadAllBytes(inputs.RawCoveragePaths["A.Tests"]));
        Assert.Equal(betaBytes, File.ReadAllBytes(inputs.RawCoveragePaths["B.Tests"]));
    }

    [Theory]
    [InlineData("{\"projects\":[{\"project\":\"A.Tests\",\"coverageFile\":\"a.xml\"},{\"project\":\"A.Tests\",\"coverageFile\":\"b.xml\"}]}")]
    [InlineData("{\"projects\":[{\"project\":\"A.Tests\",\"coverageFile\":\"a.xml\"}]}")]
    [InlineData("{\"projects\":[{\"project\":\"A.Tests\",\"coverageFile\":\"a.xml\"},{\"project\":\"B.Tests\",\"coverageFile\":\"b.xml\"},{\"project\":\"Extra.Tests\",\"coverageFile\":\"extra.xml\"}]}")]
    [InlineData("{\"projects\":[{\"project\":\"A.Tests\",\"coverageFile\":\"a.xml\"},{\"project\":\"b.tests\",\"coverageFile\":\"b.xml\"}]}")]
    [InlineData("""{"projects":[{"project":"A.Tests","coverageFile":"a.xml"},{"project":"B.Tests"}]}""")]
    public void CollectionInputsRejectDuplicateMissingExtraAndCaseChangedProjects(string json)
    {
        using var temp = TestDirectory.Create();
        var collection = TestPathUtils.PathUnder(temp.Path, "collection");
        Directory.CreateDirectory(collection);
        File.WriteAllText(TestPathUtils.PathUnder(collection, "timings.json"), json);

        Assert.Throws<InvalidDataException>(() => NativeCoverageCaptureInputs.FromCollection(
            collection, ["A.Tests", "B.Tests"], 4096));
    }

    [Fact]
    public void CollectionInputsRejectTimingsExceedingTheConfiguredLimit()
    {
        using var temp = TestDirectory.Create();
        var collection = TestPathUtils.PathUnder(temp.Path, "collection");
        Directory.CreateDirectory(collection);
        File.WriteAllText(TestPathUtils.PathUnder(collection, "timings.json"), "{\"projects\":[]}");

        Assert.Throws<InvalidDataException>(() => NativeCoverageCaptureInputs.FromCollection(collection, ["A.Tests"], 2));
    }

    [Fact]
    public void CollectionInputsRejectEscapingCoveragePaths()
    {
        using var temp = TestDirectory.Create();
        var collection = TestPathUtils.PathUnder(temp.Path, "collection");
        Directory.CreateDirectory(collection);
        File.WriteAllText(TestPathUtils.PathUnder(collection, "timings.json"),
            "{\"projects\":[{\"project\":\"A.Tests\",\"coverageFile\":\"../outside.xml\"}]}");

        Assert.ThrowsAny<ArgumentException>(() => NativeCoverageCaptureInputs.FromCollection(
            collection, ["A.Tests"], 4096));
    }

    [Fact]
    public void ReplayInputsRetainExplicitIndexAndTimingsPaths()
    {
        using var temp = TestDirectory.Create();
        var index = TestPathUtils.PathUnder(temp.Path, "raw-index.json");
        var timings = TestPathUtils.PathUnder(temp.Path, "timings.json");
        File.WriteAllText(index, "{\"A.Tests\":\"report.xml\"}");
        File.WriteAllText(timings, "{}");

        var inputs = NativeCoverageCaptureInputs.FromReplay(index, timings);

        Assert.Equal(timings, inputs.TimingsPath);
        Assert.Equal("report.xml", inputs.RawCoveragePaths["A.Tests"]);
    }

    [Fact]
    public void NativeProjectsMatchTheOrderedEmbeddedProjectInventory()
    {
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var projectPath = TestPathUtils.PathUnder(repositoryRoot,
            ".github", "workflows", "native-coverage-test-projects.txt");
        string[] expected =
        [
            "Aspire/ForgeTrust.AppSurface.Aspire.Tests/ForgeTrust.AppSurface.Aspire.Tests.csproj",
            "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj",
        ];

        Assert.Equal(expected, File.ReadAllLines(projectPath));
        Assert.Equal(expected, NativeCoverageTestInventory.ReadNativeProjects());
    }

    [Theory]
    [InlineData("native")]
    [InlineData("shared-cli")]
    [InlineData("lifecycle-cli")]
    public async Task PrintTestFilterWritesTheOrderedSelectionToStandardOutput(string lane)
    {
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var selectorPath = TestPathUtils.PathUnder(repositoryRoot,
            ".github", "workflows", "native-coverage-test-selectors.txt");
        var selectors = File.ReadAllLines(selectorPath)
            .Select(line => line.Split('\t', 2))
            .Where(entry => entry[0] == lane || entry[0] == "shared-cli")
            .Select(entry => entry[1]);

        var (assembly, _) = FindDogfoodAssembly(repositoryRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest("dotnet", [assembly, "--print-test-filter", lane],
            repositoryRoot, null, null, CoverageRunProcessLease.Detached());
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(string.Join("|", selectors), result.StandardOutput?.TrimEnd('\r', '\n'));
    }

    [Theory]
    [InlineData("2", "appsurface-visual-baseline-png-v1", "diff-policy-version",
        "Pass --contract-version 3 when using --diff-policy-id.")]
    [InlineData("3", "private-selector-value", "unsupported-diff-policy-id",
        "Use appsurface-visual-baseline-png-v1 for --diff-policy-id.")]
    [InlineData("private-version-value", null, "ArgumentException",
        "Inspect retained native command result files and correct the explicit repository policy/configuration.")]
    public async Task RunAsyncReportsSafeOptionDiagnostics(string contractVersion, string? diffPolicyId,
        string expectedReason, string expectedRecovery)
    {
        using var temp = TestDirectory.Create();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var (dogfoodAssembly, configuration) = FindDogfoodAssembly(repositoryRoot);

        var arguments = new List<string>
        {
            dogfoodAssembly, "--operation", "fanin", "--repository", repositoryRoot,
            "--cli", Path.Join(repositoryRoot, "appsurface"), "--output", Path.Join(temp.Path, "result"),
            "--configuration", configuration, "--contract-version", contractVersion
        };
        if (diffPolicyId is not null)
            arguments.AddRange(["--diff-policy-id", diffPolicyId]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest("dotnet", arguments, repositoryRoot, null, null, CoverageRunProcessLease.Detached());
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token);
        var error = result.StandardError;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"ASCOV311 Dogfood failed at options ({expectedReason}).", error);
        Assert.Contains(expectedRecovery, error);
        Assert.DoesNotContain("private-", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fanin", null)]
    [InlineData("native", "--raw-index")]
    [InlineData("native", "--timings")]
    public async Task CollectionRootRejectsFanInAndConflictingReplayInputs(string operation, string? conflictingOption)
    {
        using var temp = TestDirectory.Create();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var (dogfoodAssembly, configuration) = FindDogfoodAssembly(repositoryRoot);
        var output = TestPathUtils.PathUnder(temp.Path, "result");
        var arguments = new List<string>
        {
            dogfoodAssembly, "--operation", operation, "--repository", repositoryRoot,
            "--cli", TestPathUtils.PathUnder(repositoryRoot, "appsurface"), "--output", output,
            "--configuration", configuration, "--contract-version", "2",
            "--collection-root", TestPathUtils.PathUnder(temp.Path, "collection")
        };
        if (conflictingOption is not null)
            arguments.AddRange([conflictingOption, TestPathUtils.PathUnder(temp.Path, "replay.json")]);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest("dotnet", arguments, repositoryRoot, null, null, CoverageRunProcessLease.Detached());
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.False(Directory.Exists(output));
        Assert.Contains("ASCOV311 Dogfood failed at options (ArgumentException).", result.StandardError);
    }

    [Theory]
    [InlineData("native", true, "--native-platform")]
    [InlineData("native", false, "--native-platform")]
    [InlineData("native", false, "--raw-index")]
    [InlineData("native", false, "--timings")]
    [InlineData("fanin", false, "--primary")]
    [InlineData("fanin", false, "--timings")]
    [InlineData("fanin", false, "--native-inputs")]
    public async Task MissingOperationArgumentsFailBeforeReadingSourceOrCreatingOutput(string operation,
        bool useCollection, string omittedOption)
    {
        using var temp = TestDirectory.Create();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        var (assembly, configuration) = FindDogfoodAssembly(repositoryRoot);
        var output = TestPathUtils.PathUnder(temp.Path, "result");
        // A missing checkout distinguishes option rejection from a source-inventory failure.
        var arguments = new List<string>
        {
            assembly, "--operation", operation, "--repository", TestPathUtils.PathUnder(temp.Path, "missing-source"),
            "--cli", TestPathUtils.PathUnder(temp.Path, "appsurface"), "--output", output,
            "--configuration", configuration, "--run-scope", "tests", "--run-id", "missing-option", "--attempt", "1"
        };
        string[] modeOptions = operation switch
        {
            "fanin" => ["--primary", "--timings", "--native-inputs"],
            _ when useCollection => ["--native-platform", "--collection-root"],
            _ => ["--native-platform", "--raw-index", "--timings"]
        };
        foreach (var option in modeOptions.Where(option => option != omittedOption))
        {
            arguments.Add(option);
            arguments.Add(option == "--native-platform" ? "Linux" : TestPathUtils.PathUnder(temp.Path, option[2..]));
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest("dotnet", arguments, repositoryRoot, null, null, CoverageRunProcessLease.Detached());
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ASCOV311 Dogfood failed at options (ArgumentException).", result.StandardError);
        Assert.False(Directory.Exists(output));
    }

    private static (string Assembly, string Configuration) FindDogfoodAssembly(string repositoryRoot)
    {
        var testOutputDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = testOutputDirectory.Parent?.Name ?? throw new DirectoryNotFoundException("Could not determine the test configuration.");
        var dogfoodAssembly = TestPathUtils.PathUnder(repositoryRoot,
            $"tools/ForgeTrust.AppSurface.Coverage.Dogfood/bin/{configuration}/{testOutputDirectory.Name}/ForgeTrust.AppSurface.Coverage.Dogfood.dll");
        Assert.True(File.Exists(dogfoodAssembly), $"The Dogfood executable was not built: {dogfoodAssembly}");
        return (dogfoodAssembly, configuration);
    }
}
