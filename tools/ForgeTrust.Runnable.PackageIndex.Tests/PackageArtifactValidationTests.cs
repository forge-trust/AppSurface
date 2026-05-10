using System.IO.Compression;
using System.Text;

namespace ForgeTrust.Runnable.PackageIndex.Tests;

public sealed class PackageArtifactValidationTests : IDisposable
{
    private const string PackageVersion = "0.0.0-ci.42";

    private readonly string _repositoryRoot;

    public PackageArtifactValidationTests()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "PackageArtifactValidationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenPublishDecisionIsMissing()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("publish_decision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenDoNotPublishReasonIsMissing()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Web/ForgeTrust.Runnable.Web.RazorDocs.Standalone/ForgeTrust.Runnable.Web.RazorDocs.Standalone.csproj
                classification: proof_host
                publish_decision: do_not_publish
                order: 20
                note: Host only.
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorDocs.Standalone/ForgeTrust.Runnable.Web.RazorDocs.Standalone.csproj", "<Project />");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs.Standalone/ForgeTrust.Runnable.Web.RazorDocs.Standalone.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs.Standalone/ForgeTrust.Runnable.Web.RazorDocs.Standalone.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs.Standalone")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("publish_reason", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenExpectedDependenciesDoNotMatchProjectReferences()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj
                classification: public
                publish_decision: publish
                order: 10
                use_when: Build modules.
                includes: Core.
                does_not_include: Web.
                start_here_path: ForgeTrust.Runnable.Core/README.md
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync("ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj", "<Project />");
        await WriteFileAsync("ForgeTrust.Runnable.Core/README.md", "# Core");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        var coreProjectPath = Path.Combine(_repositoryRoot, "ForgeTrust.Runnable.Core", "ForgeTrust.Runnable.Core.csproj");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj"] = CreateMetadata(
                "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                "ForgeTrust.Runnable.Core"),
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web",
                projectReferences: [coreProjectPath])
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("project references resolve to [ForgeTrust.Runnable.Core]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_AllowsToolPackagesWithoutExpectedPackageDependencies()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install as a tool.
                includes: CLI command.
                does_not_include: Runtime package.
                start_here_path: Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md", "# CLI");
        var webProjectPath = Path.Combine(_repositoryRoot, "Web", "ForgeTrust.Runnable.Web", "ForgeTrust.Runnable.Web.csproj");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                projectReferences: [webProjectPath],
                isTool: true)
        });

        var plan = await resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None);

        Assert.Contains(plan.Entries, entry => entry.PackageId == "ForgeTrust.Runnable.Web.RazorWire.Cli" && entry.IsTool);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsValidPackagesAndRendersReport()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var plan = CreatePlan();
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            dependencies: EmptyDependencies);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web",
            PackageVersion,
            dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.Runnable.Core"] = $"[{PackageVersion}, )"
            });

        var report = new PackageArtifactValidator().Validate(plan, artifactDirectory, PackageVersion);
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(report);

        Assert.Equal(2, report.Entries.Count);
        Assert.Contains("ForgeTrust.Runnable.Web", markdown, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.Runnable.Core", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPackageVersionDoesNotMatch()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            "0.0.0-ci.41",
            dependencies: EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                        "ForgeTrust.Runnable.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("expected '0.0.0-ci.42'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenRequiredMetadataIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            dependencies: EmptyDependencies,
            includeReadme: false);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                        "ForgeTrust.Runnable.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("readme", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenExpectedDependencyIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web",
            PackageVersion,
            dependencies: EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                        "ForgeTrust.Runnable.Web",
                        PackagePublishDecision.Publish,
                        ["ForgeTrust.Runnable.Core"],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("missing dependency 'ForgeTrust.Runnable.Core'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenFirstPartyAssemblyVersionDoesNotMatch()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            dependencies: EmptyDependencies,
            assemblyEntries: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lib/net10.0/ForgeTrust.Runnable.Core.dll"] = typeof(PackageArtifactValidationTests).Assembly.Location
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                        "ForgeTrust.Runnable.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("informational version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ForgeTrust.Runnable.Core.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenDuplicatePackageArtifactsExist()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            EmptyDependencies);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            "0.0.0-ci.43",
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                        "ForgeTrust.Runnable.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("multiple artifacts", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageVersionValidator_RejectsStableVersionsAndBuildMetadata()
    {
        var stable = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.RequirePrerelease("1.2.3"));
        var buildMetadata = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.RequirePrerelease("1.2.3-ci.4+sha"));

        Assert.Contains("prerelease", stable.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build metadata", buildMetadata.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageArtifactWorkflow_RunsRestoreBuildPackAndWritesReport()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        var reportPath = Path.Combine(artifactDirectory, "package-validation-report.md");
        var commandRunner = new RecordingCommandRunner();
        var workflow = new PackageArtifactWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                    "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                    "ForgeTrust.Runnable.Web")
            }),
            commandRunner,
            new PackageArtifactValidator());

        var report = await workflow.RunAsync(
            new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                artifactDirectory,
                reportPath,
                PackageVersion));

        Assert.Single(report.Entries);
        Assert.Equal(["dotnet restore", "dotnet build", "dotnet pack"], commandRunner.OperationNames);
        var packCommand = Assert.Single(commandRunner.Requests, request => request.OperationName == "dotnet pack");
        Assert.Contains("--no-restore", packCommand.Arguments);
        Assert.Contains("--no-build", packCommand.Arguments);
        Assert.Contains($"/p:Version={PackageVersion}", packCommand.Arguments);
        Assert.Contains($"/p:PackageVersion={PackageVersion}", packCommand.Arguments);
        Assert.Contains("/p:ContinuousIntegrationBuild=true", packCommand.Arguments);
        var buildCommand = Assert.Single(commandRunner.Requests, request => request.OperationName == "dotnet build");
        Assert.Contains($"/p:Version={PackageVersion}", buildCommand.Arguments);
        Assert.Contains($"/p:PackageVersion={PackageVersion}", buildCommand.Arguments);
        Assert.Contains("/p:ContinuousIntegrationBuild=true", buildCommand.Arguments);
        Assert.True(File.Exists(reportPath), $"Expected report at {reportPath}.");
    }

    [Fact]
    public async Task ProcessCommandRunner_UsesOperationSpecificFailureMessages()
    {
        var result = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ProcessCommandRunner().RunAsync(
                new CommandRunRequest(
                    "dotnet",
                    ["definitely-not-a-real-dotnet-command"],
                    _repositoryRoot,
                    "dotnet pack",
                    "src/App/App.csproj",
                    "pack",
                    "packing",
                    30_000),
                CancellationToken.None));

        Assert.Contains("Failed to pack 'src/App/App.csproj' with dotnet pack", result.Message, StringComparison.Ordinal);
    }

    private string ManifestPath => Path.Combine(_repositoryRoot, "packages", "package-index.yml");

    private static IReadOnlyDictionary<string, string> EmptyDependencies { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static PackagePublishPlan CreatePlan()
    {
        return new PackagePublishPlan([
            new PackagePublishPlanEntry(
                "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                "ForgeTrust.Runnable.Core",
                PackagePublishDecision.Publish,
                [],
                IsTool: false),
            new PackagePublishPlanEntry(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web",
                PackagePublishDecision.Publish,
                ["ForgeTrust.Runnable.Core"],
                IsTool: false)
        ]);
    }

    private static void WritePackage(
        string artifactDirectory,
        string packageId,
        string packageVersion,
        IReadOnlyDictionary<string, string> dependencies,
        bool includeReadme = true,
        IReadOnlyDictionary<string, string>? assemblyEntries = null)
    {
        var packagePath = Path.Combine(artifactDirectory, $"{packageId}.{packageVersion}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec");
        using (var stream = nuspecEntry.Open())
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.Write(CreateNuspec(packageId, packageVersion, dependencies, includeReadme));
        }

        if (includeReadme)
        {
            var readmeEntry = archive.CreateEntry("README.md");
            using var readmeStream = readmeEntry.Open();
            using var readmeWriter = new StreamWriter(readmeStream, Encoding.UTF8);
            readmeWriter.Write("# Package");
        }

        if (assemblyEntries is not null)
        {
            foreach (var (entryPath, sourcePath) in assemblyEntries)
            {
                var assemblyEntry = archive.CreateEntry(entryPath);
                using var sourceStream = File.OpenRead(sourcePath);
                using var assemblyStream = assemblyEntry.Open();
                sourceStream.CopyTo(assemblyStream);
            }
        }
    }

    private static string CreateNuspec(
        string packageId,
        string packageVersion,
        IReadOnlyDictionary<string, string> dependencies,
        bool includeReadme)
    {
        var dependencyXml = string.Join(
            Environment.NewLine,
            dependencies.Select(pair => $"""        <dependency id="{pair.Key}" version="{pair.Value}" />"""));
        var readmeXml = includeReadme ? "    <readme>README.md</readme>" : string.Empty;

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>{{packageVersion}}</version>
                <authors>Forge Trust</authors>
                <description>{{packageId}} package for Runnable application composition.</description>
                <license type="expression">MIT</license>
                <repository type="git" url="https://github.com/forge-trust/Runnable" />
                <tags>runnable dotnet</tags>
            {{readmeXml}}
                <dependencies>
                  <group targetFramework="net10.0">
            {{dependencyXml}}
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;
    }

    private PackagePublishPlanResolver CreateResolver(IReadOnlyDictionary<string, PackageProjectMetadata> metadataByProject)
    {
        return new PackagePublishPlanResolver(
            new PackageProjectScanner(),
            new FakeMetadataProvider(metadataByProject),
            new PackageManifestLoader());
    }

    private static PackageProjectMetadata CreateMetadata(
        string projectPath,
        string packageId,
        IReadOnlyList<string>? projectReferences = null,
        bool isTool = false)
    {
        return new PackageProjectMetadata(
            projectPath,
            packageId,
            "net10.0",
            true,
            isTool,
            "Library",
            projectReferences ?? []);
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private sealed class FakeMetadataProvider : IProjectMetadataProvider
    {
        private readonly IReadOnlyDictionary<string, PackageProjectMetadata> _metadataByProject;

        public FakeMetadataProvider(IReadOnlyDictionary<string, PackageProjectMetadata> metadataByProject)
        {
            _metadataByProject = metadataByProject;
        }

        public Task<PackageProjectMetadata> GetMetadataAsync(
            string repositoryRoot,
            string projectPath,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_metadataByProject[projectPath]);
        }
    }

    private sealed class RecordingCommandRunner : ICommandRunner
    {
        public List<CommandRunRequest> Requests { get; } = [];

        public IReadOnlyList<string> OperationNames => Requests.Select(request => request.OperationName).ToArray();

        public Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.OperationName == "dotnet pack")
            {
                var outputIndex = request.Arguments.ToList().IndexOf("--output");
                var artifactsDirectory = request.Arguments[outputIndex + 1];
                var packageVersion = request.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))
                    .Split('=', 2)[1];
                WritePackage(
                    artifactsDirectory,
                    "ForgeTrust.Runnable.Web",
                    packageVersion,
                    EmptyDependencies);
            }

            return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
        }
    }
}
