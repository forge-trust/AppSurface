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
    public async Task PublishPlanResolver_ThrowsWhenRepositoryRootOrManifestIsMissing()
    {
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase));
        var missingRepository = Path.Combine(_repositoryRoot, "missing");
        var missingRepositoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(missingRepository, Path.Combine(missingRepository, "packages", "package-index.yml"), CancellationToken.None));

        var missingManifestError = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("Repository root", missingRepositoryError.Message, StringComparison.Ordinal);
        Assert.Contains("Manifest", missingManifestError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("support", "publish", "Support manifest entry")]
    [InlineData("proof_host", "publish", "Proof-host manifest entry")]
    [InlineData("excluded", "support_publish", "Excluded manifest entry")]
    public async Task PublishPlanResolver_ThrowsWhenPublishDecisionDoesNotMatchClassification(
        string classification,
        string publishDecision,
        string expectedMessage)
    {
        await WriteFileAsync("packages/package-index.yml",
            $$"""
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj
                classification: {{classification}}
                publish_decision: {{publishDecision}}
                order: 20
                note: Internal package surface.
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj", "<Project />");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj",
                "ForgeTrust.Runnable.Console")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenPublicEntryIsNotMarkedForPublish()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: support_publish
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

        Assert.Contains("Public manifest entry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_OrdersPackageDependenciesDeterministically()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj
                classification: support
                publish_decision: support_publish
                order: 10
                note: Shared dependency package.
              - project: Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj
                classification: support
                publish_decision: support_publish
                order: 20
                note: Optional OpenAPI dependency package.
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                publish_decision: publish
                order: 30
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
                expected_dependency_package_ids:
                  - ForgeTrust.Runnable.Web.OpenApi
                  - ForgeTrust.Runnable.Core
            """);
        await WriteFileAsync("ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj", "<Project />");
        await WriteFileAsync("ForgeTrust.Runnable.Core/README.md", "# Core");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.OpenApi/README.md", "# OpenAPI");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        var coreProjectPath = Path.Combine(_repositoryRoot, "ForgeTrust.Runnable.Core", "ForgeTrust.Runnable.Core.csproj");
        var openApiProjectPath = Path.Combine(_repositoryRoot, "Web", "ForgeTrust.Runnable.Web.OpenApi", "ForgeTrust.Runnable.Web.OpenApi.csproj");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj"] = CreateMetadata(
                "ForgeTrust.Runnable.Core/ForgeTrust.Runnable.Core.csproj",
                "ForgeTrust.Runnable.Core"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web",
                projectReferences: [openApiProjectPath, coreProjectPath])
        });

        var plan = await resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None);

        var webEntry = Assert.Single(plan.Entries, entry => entry.PackageId == "ForgeTrust.Runnable.Web");
        Assert.Equal(["ForgeTrust.Runnable.Core", "ForgeTrust.Runnable.Web.OpenApi"], webEntry.ExpectedDependencyPackageIds);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsToolPackageType()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web.RazorWire.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"]);

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                    "ForgeTrust.Runnable.Web.RazorWire.Cli",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: true)
            ]),
            artifactDirectory,
            PackageVersion);

        Assert.Single(report.Entries);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenExpectedDependencyPackageIdIsUnknown()
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
                expected_dependency_package_ids:
                  - ForgeTrust.Runnable.Missing
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

        Assert.Contains("unknown dependency package id 'ForgeTrust.Runnable.Missing'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenToolDefinesExpectedPackageDependencies()
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
                expected_dependency_package_ids:
                  - ForgeTrust.Runnable.Web
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md", "# CLI");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                projectReferences: [Path.Combine(_repositoryRoot, "Web", "ForgeTrust.Runnable.Web", "ForgeTrust.Runnable.Web.csproj")],
                isTool: true)
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("Tool manifest entry", error.Message, StringComparison.Ordinal);
        Assert.Contains("must not define expected package dependencies", error.Message, StringComparison.Ordinal);
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
    public void PackageArtifactValidator_ThrowsWhenArtifactDirectoryIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "missing-artifacts");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(CreatePlan(), artifactDirectory, PackageVersion));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenExpectedPackageIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(CreatePlan(), artifactDirectory, PackageVersion));

        Assert.Contains("Missing package artifact for 'ForgeTrust.Runnable.Web'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenUnexpectedPackageExists()
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
            "ForgeTrust.Runnable.Web",
            PackageVersion,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.Runnable.Core"] = PackageVersion
            });
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Extra",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(CreatePlan(), artifactDirectory, PackageVersion));

        Assert.Contains("Unexpected package artifact 'ForgeTrust.Runnable.Extra'", error.Message, StringComparison.Ordinal);
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
    public void PackageArtifactValidator_ThrowsWhenDefaultDescriptionRemains()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            EmptyDependencies,
            description: "Package Description");

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

        Assert.Contains("default NuGet package description", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolPackageTypeIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web.RazorWire.Cli",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                        "ForgeTrust.Runnable.Web.RazorWire.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("DotnetTool", error.Message, StringComparison.Ordinal);
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
    public void PackageArtifactValidator_ThrowsWhenExpectedDependencyVersionDoesNotMatch()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web",
            PackageVersion,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.Runnable.Core"] = "[0.0.0-ci.41, )"
            });

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

        Assert.Contains("expected same-version dependency", error.Message, StringComparison.Ordinal);
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
    public void PackageArtifactValidator_ThrowsWhenNuspecIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            EmptyDependencies,
            includeNuspec: false);

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

        Assert.Contains(".nuspec", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, true, "id")]
    [InlineData(true, false, "version")]
    public void PackageArtifactValidator_ThrowsWhenPackageIdentityMetadataIsMissing(
        bool includeId,
        bool includeVersion,
        string metadataName)
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            EmptyDependencies,
            includeId: includeId,
            includeVersion: includeVersion);

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

        Assert.Contains($"metadata '{metadataName}'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenFirstPartyAssemblyIsNotValidPortableExecutable()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Core",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["lib/net10.0/ForgeTrust.Runnable.Core.dll"] = Encoding.UTF8.GetBytes("not a portable executable")
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

        Assert.Contains("could not be inspected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenTailwindRuntimePayloadIsMissing()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web.Tailwind.Runtime.linux-x64",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.linux-x64.csproj",
                        "ForgeTrust.Runnable.Web.Tailwind.Runtime.linux-x64",
                        PackagePublishDecision.SupportPublish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("missing required payload", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtimes/linux-x64/native/tailwindcss-linux-x64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsTailwindRuntimePayload()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.Runnable.Web.Tailwind.Runtime.linux-x64",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["runtimes/linux-x64/native/tailwindcss-linux-x64"] = Encoding.UTF8.GetBytes("tailwind binary")
            });

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.linux-x64.csproj",
                    "ForgeTrust.Runnable.Web.Tailwind.Runtime.linux-x64",
                    PackagePublishDecision.SupportPublish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion);

        Assert.Single(report.Entries);
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

    [Theory]
    [InlineData("")]
    [InlineData("1.two.3-ci.4")]
    [InlineData("1.2-ci.4")]
    public void PackageVersionValidator_RejectsMissingOrMalformedSemVerCore(string packageVersion)
    {
        var error = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.RequirePrerelease(packageVersion));

        Assert.Contains("Package version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactReportRenderer_RendersEveryPublishDecision()
    {
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(new PackageArtifactValidationReport(
            PackageVersion,
            [
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.Runnable.Web",
                    "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                    PackagePublishDecision.Publish,
                    []),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.Runnable.Web.OpenApi",
                    "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                    PackagePublishDecision.SupportPublish,
                    ["ForgeTrust.Runnable.Web"]),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.Runnable.Example",
                    "examples/Example.csproj",
                    PackagePublishDecision.DoNotPublish,
                    []),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.Runnable.Experimental",
                    "experimental/Experimental.csproj",
                    (PackagePublishDecision)999,
                    [])
            ]));

        Assert.Contains("`publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`support-publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`do-not-publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`999`", markdown, StringComparison.Ordinal);
        Assert.Contains("`ForgeTrust.Runnable.Web`", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.Runnable.Example` | `examples/Example.csproj` | `do-not-publish` | none |", markdown, StringComparison.Ordinal);
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
        Directory.CreateDirectory(artifactDirectory);
        var stalePackage = Path.Combine(artifactDirectory, "stale.nupkg");
        var staleSymbolPackage = Path.Combine(artifactDirectory, "stale.snupkg");
        await File.WriteAllTextAsync(stalePackage, "old package", Encoding.UTF8);
        await File.WriteAllTextAsync(staleSymbolPackage, "old symbol package", Encoding.UTF8);
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
        Assert.False(File.Exists(stalePackage));
        Assert.False(File.Exists(staleSymbolPackage));
        Assert.True(File.Exists(reportPath), $"Expected report at {reportPath}.");
    }

    [Fact]
    public async Task PackageArtifactWorkflow_ThrowsWhenRequestPathsAreInvalid()
    {
        var workflow = new PackageArtifactWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)),
            new RecordingCommandRunner(),
            new PackageArtifactValidator());
        var missingRepository = Path.Combine(_repositoryRoot, "missing");
        var missingRepositoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                missingRepository,
                Path.Combine(missingRepository, "packages", "package-index.yml"),
                Path.Combine(missingRepository, "artifacts"),
                Path.Combine(missingRepository, "report.md"),
                PackageVersion)));

        var missingManifestError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                Path.Combine(_repositoryRoot, "artifacts"),
                Path.Combine(_repositoryRoot, "report.md"),
                PackageVersion)));

        await WriteFileAsync("packages/package-index.yml", "packages: []");
        var missingArtifactPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                " ",
                Path.Combine(_repositoryRoot, "report.md"),
                PackageVersion)));
        var missingReportPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                Path.Combine(_repositoryRoot, "artifacts"),
                "",
                PackageVersion)));

        Assert.Contains("Repository root", missingRepositoryError.Message, StringComparison.Ordinal);
        Assert.Contains("Manifest", missingManifestError.Message, StringComparison.Ordinal);
        Assert.Contains("output path", missingArtifactPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report path", missingReportPathError.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCommandRunner_PassesEnvironmentOverrides()
    {
        var command = CreateShellCommand(OperatingSystem.IsWindows()
            ? "echo %PACKAGE_INDEX_TEST_VALUE%"
            : "printf %s \"$PACKAGE_INDEX_TEST_VALUE\"");

        var result = await new ProcessCommandRunner().RunAsync(
            new CommandRunRequest(
                command.FileName,
                command.Arguments,
                _repositoryRoot,
                "environment probe",
                "env",
                "probe",
                "probing",
                30_000,
                new Dictionary<string, string?>
                {
                    ["PACKAGE_INDEX_TEST_VALUE"] = "from-env"
                }),
            CancellationToken.None);

        Assert.Contains("from-env", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCommandRunner_IncludesStderrWhenProcessTimesOut()
    {
        var command = CreateShellCommand(OperatingSystem.IsWindows()
            ? "echo still working 1>&2 & ping -n 6 127.0.0.1 > nul"
            : "printf 'still working' >&2; sleep 5");

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ProcessCommandRunner().RunAsync(
                new CommandRunRequest(
                    command.FileName,
                    command.Arguments,
                    _repositoryRoot,
                    "timeout probe",
                    "slow",
                    "probe",
                    "probing",
                    100),
                CancellationToken.None));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("still working", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCommandRunner_TerminatesProcessWhenCallerCancels()
    {
        var command = CreateShellCommand(OperatingSystem.IsWindows()
            ? "ping -n 6 127.0.0.1 > nul"
            : "sleep 5");
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ProcessCommandRunner().RunAsync(
                new CommandRunRequest(
                    command.FileName,
                    command.Arguments,
                    _repositoryRoot,
                    "cancel probe",
                    "slow",
                    "probe",
                    "probing",
                    30_000),
                cts.Token));
    }

    private string ManifestPath => Path.Combine(_repositoryRoot, "packages", "package-index.yml");

    private static IReadOnlyDictionary<string, string> EmptyDependencies { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static (string FileName, IReadOnlyList<string> Arguments) CreateShellCommand(string script)
    {
        return OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", script])
            : ("/bin/sh", ["-c", script]);
    }

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
        IReadOnlyDictionary<string, string>? assemblyEntries = null,
        string? description = null,
        bool includeNuspec = true,
        bool includeId = true,
        bool includeVersion = true,
        IReadOnlyList<string>? packageTypes = null,
        IReadOnlyDictionary<string, byte[]>? rawEntries = null)
    {
        var packagePath = Path.Combine(artifactDirectory, $"{packageId}.{packageVersion}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        if (includeNuspec)
        {
            var nuspecEntry = archive.CreateEntry($"{packageId}.nuspec");
            using var stream = nuspecEntry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(CreateNuspec(
                packageId,
                packageVersion,
                dependencies,
                includeReadme,
                description,
                includeId,
                includeVersion,
                packageTypes));
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

        if (rawEntries is not null)
        {
            foreach (var (entryPath, contents) in rawEntries)
            {
                var rawEntry = archive.CreateEntry(entryPath);
                using var rawStream = rawEntry.Open();
                rawStream.Write(contents, 0, contents.Length);
            }
        }
    }

    private static string CreateNuspec(
        string packageId,
        string packageVersion,
        IReadOnlyDictionary<string, string> dependencies,
        bool includeReadme,
        string? description,
        bool includeId,
        bool includeVersion,
        IReadOnlyList<string>? packageTypes)
    {
        var dependencyXml = string.Join(
            Environment.NewLine,
            dependencies.Select(pair => $"""        <dependency id="{pair.Key}" version="{pair.Value}" />"""));
        var readmeXml = includeReadme ? "    <readme>README.md</readme>" : string.Empty;
        var idXml = includeId ? $"    <id>{packageId}</id>" : string.Empty;
        var versionXml = includeVersion ? $"    <version>{packageVersion}</version>" : string.Empty;
        var packageTypesXml = packageTypes is null
            ? string.Empty
            : $$"""
                    <packageTypes>
            {{string.Join(Environment.NewLine, packageTypes.Select(packageType => $"""      <packageType name="{packageType}" />"""))}}
                    </packageTypes>
            """;

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
            {{idXml}}
            {{versionXml}}
                <authors>Forge Trust</authors>
                <description>{{description ?? $"{packageId} package for Runnable application composition."}}</description>
                <license type="expression">MIT</license>
                <repository type="git" url="https://github.com/forge-trust/Runnable" />
                <tags>runnable dotnet</tags>
            {{readmeXml}}
            {{packageTypesXml}}
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
