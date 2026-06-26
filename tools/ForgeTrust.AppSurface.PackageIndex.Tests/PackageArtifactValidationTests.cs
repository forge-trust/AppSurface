using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PackageArtifactValidationTests : IDisposable
{
    private const string PackageVersion = "0.0.0-ci.42";
    private const string RequiredPackageProjectUrl = "https://appsurface.dev";

    private readonly string _repositoryRoot;

    public PackageArtifactValidationTests()
    {
        _repositoryRoot = TestPathUtils.PathUnder(Path.GetTempPath(), "PackageArtifactValidationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenPublishDecisionIsMissing()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
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
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.AppSurface.Docs.Standalone/ForgeTrust.AppSurface.Docs.Standalone.csproj
                product_family: forge_trust
                classification: proof_host
                publish_decision: do_not_publish
                order: 20
                note: Host only.
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Docs.Standalone/ForgeTrust.AppSurface.Docs.Standalone.csproj", "<Project />");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Docs.Standalone/ForgeTrust.AppSurface.Docs.Standalone.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs.Standalone/ForgeTrust.AppSurface.Docs.Standalone.csproj",
                "ForgeTrust.AppSurface.Docs.Standalone")
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
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Build modules.
                includes: Core.
                does_not_include: Web.
                start_here_path: ForgeTrust.AppSurface.Core/README.md
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "<Project />");
        await WriteFileAsync("ForgeTrust.AppSurface.Core/README.md", "# Core");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var coreProjectPath = CombineSafeChildPath(_repositoryRoot, "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"] = CreateMetadata(
                "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                "ForgeTrust.AppSurface.Core"),
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web",
                projectReferences: [coreProjectPath])
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("project references resolve to [ForgeTrust.AppSurface.Core]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_AllowsToolPackagesWithoutExpectedPackageDependencies()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj
                product_family: razorwire
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install as a tool.
                includes: CLI command.
                does_not_include: Runtime package.
                start_here_path: Web/ForgeTrust.RazorWire.Cli/README.md
                tool_command_name: razorwire
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/README.md", "# CLI");
        var webProjectPath = CombineSafeChildPath(_repositoryRoot, "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                projectReferences: [webProjectPath],
                isTool: true)
        });

        var plan = await resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None);

        Assert.Contains(plan.Entries, entry => entry.PackageId == "ForgeTrust.RazorWire.Cli" && entry.IsTool && entry.ToolCommandName == "razorwire");
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenRepositoryRootOrManifestIsMissing()
    {
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase));
        var missingRepository = CombineSafeChildPath(_repositoryRoot, "missing");
        var missingRepositoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(missingRepository, CombineSafeChildPath(missingRepository, "packages/package-index.yml"), CancellationToken.None));

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
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj
                product_family: appsurface
                classification: {{classification}}
                publish_decision: {{publishDecision}}
                order: 20
                note: Internal package surface.
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj", "<Project />");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "ForgeTrust.AppSurface.Console")
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
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: support_publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
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
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                product_family: appsurface
                classification: support
                publish_decision: support_publish
                order: 10
                note: Shared dependency package.
              - project: Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj
                product_family: appsurface
                classification: support
                publish_decision: support_publish
                order: 20
                note: Optional OpenAPI dependency package.
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 30
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                expected_dependency_package_ids:
                  - ForgeTrust.AppSurface.Web.OpenApi
                  - ForgeTrust.AppSurface.Core
            """);
        await WriteFileAsync("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "<Project />");
        await WriteFileAsync("ForgeTrust.AppSurface.Core/README.md", "# Core");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.OpenApi/README.md", "# OpenAPI");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var coreProjectPath = CombineSafeChildPath(_repositoryRoot, "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj");
        var openApiProjectPath = CombineSafeChildPath(_repositoryRoot, "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"] = CreateMetadata(
                "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                "ForgeTrust.AppSurface.Core"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web",
                projectReferences: [openApiProjectPath, coreProjectPath])
        });

        var plan = await resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None);

        var webEntry = Assert.Single(plan.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Web");
        Assert.Equal(["ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Web.OpenApi"], webEntry.ExpectedDependencyPackageIds);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsToolPackageType()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.RazorWire.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"],
            toolCommandNames: ["razorwire"]);

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                    "ForgeTrust.RazorWire.Cli",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: true,
                    ToolCommandName: "razorwire")
            ]),
            artifactDirectory,
            PackageVersion);

        var entry = Assert.Single(report.Entries);
        Assert.Equal("razorwire", entry.ToolCommandName);
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(report);
        Assert.Contains("| Package | Project | Decision | ToolCommand | Expected package dependencies |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.RazorWire.Cli` | `Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj` | `publish` | `razorwire` | none |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsStablePackageVersion()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            "0.1.0",
            EmptyDependencies);

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    "ForgeTrust.AppSurface.Web",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            "0.1.0");

        var entry = Assert.Single(report.Entries);
        Assert.Equal("0.1.0", report.PackageVersion);
        Assert.Equal("ForgeTrust.AppSurface.Web", entry.PackageId);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolSettingsCommandDoesNotMatchPlan()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"],
            toolCommandNames: ["wrong-command"]);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("expected command 'appsurface'", error.Message, StringComparison.Ordinal);
        Assert.Contains("wrong-command", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenAnyToolSettingsFileDoesNotDeclareExpectedCommand()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"],
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net9.0/any/DotnetToolSettings.xml"] = Encoding.UTF8.GetBytes(CreateDotNetToolSettings(["wrong-command"])),
                ["tools/net10.0/any/DotnetToolSettings.xml"] = Encoding.UTF8.GetBytes(CreateDotNetToolSettings(["appsurface"]))
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("tools/net9.0/any/DotnetToolSettings.xml", error.Message, StringComparison.Ordinal);
        Assert.Contains("wrong-command", error.Message, StringComparison.Ordinal);
        Assert.Contains("expected command 'appsurface'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolSettingsFileDeclaresExtraCommand()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"],
            toolCommandNames: ["appsurface", "extra-command"]);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("extra-command", error.Message, StringComparison.Ordinal);
        Assert.Contains("only expected command 'appsurface'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolSettingsAreMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"]);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("DotnetToolSettings.xml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolSettingsXmlIsInvalid()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"],
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/DotnetToolSettings.xml"] = Encoding.UTF8.GetBytes("<DotNetCliTool>")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("invalid DotnetToolSettings.xml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolSettingsCommandNameIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"],
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/DotnetToolSettings.xml"] = Encoding.UTF8.GetBytes(
                    """
                    <DotNetCliTool Version="1">
                      <Commands>
                        <Command EntryPoint="Tool.dll" Runner="dotnet" />
                      </Commands>
                    </DotNetCliTool>
                    """)
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("missing the Name attribute", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenExpectedDependencyPackageIdIsUnknown()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                expected_dependency_package_ids:
                  - ForgeTrust.AppSurface.Missing
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => resolver.ResolveAsync(_repositoryRoot, ManifestPath, CancellationToken.None));

        Assert.Contains("unknown dependency package id 'ForgeTrust.AppSurface.Missing'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishPlanResolver_ThrowsWhenToolDefinesExpectedPackageDependencies()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj
                product_family: razorwire
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install as a tool.
                includes: CLI command.
                does_not_include: Runtime package.
                start_here_path: Web/ForgeTrust.RazorWire.Cli/README.md
                tool_command_name: razorwire
                expected_dependency_package_ids:
                  - ForgeTrust.AppSurface.Web
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/README.md", "# CLI");
        var resolver = CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                projectReferences: [CombineSafeChildPath(_repositoryRoot, "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj")],
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
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var plan = CreatePlan();
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            dependencies: EmptyDependencies);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            PackageVersion,
            dependencies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core"] = $"[{PackageVersion}, )"
            });

        var report = new PackageArtifactValidator().Validate(plan, artifactDirectory, PackageVersion);
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(report);

        Assert.Equal(2, report.Entries.Count);
        Assert.Contains("ForgeTrust.AppSurface.Web", markdown, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.AppSurface.Core", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenArtifactDirectoryIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "missing-artifacts");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(CreatePlan(), artifactDirectory, PackageVersion));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenExpectedPackageIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(CreatePlan(), artifactDirectory, PackageVersion));

        Assert.Contains("Missing package artifact for 'ForgeTrust.AppSurface.Web'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenUnexpectedPackageExists()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            PackageVersion,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core"] = PackageVersion
            });
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Extra",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(CreatePlan(), artifactDirectory, PackageVersion));

        Assert.Contains("Unexpected package artifact 'ForgeTrust.AppSurface.Extra'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPackageVersionDoesNotMatch()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            "0.0.0-ci.41",
            dependencies: EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
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
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            dependencies: EmptyDependencies,
            includeReadme: false);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("readme", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenDeclaredReadmeEntryIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            dependencies: EmptyDependencies,
            includeReadmeEntry: false);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("missing required README", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("README.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenDefaultDescriptionRemains()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            description: "Package Description");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("default NuGet package description", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenProjectUrlIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            projectUrl: string.Empty);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("project url", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenProjectUrlDoesNotPointToAppSurfaceWebsite()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            projectUrl: "https://github.com/forge-trust/AppSurface");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains(RequiredPackageProjectUrl, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsProjectUrlWithTrailingSlash()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            projectUrl: $"{RequiredPackageProjectUrl}/");

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                    "ForgeTrust.AppSurface.Core",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion);

        Assert.Single(report.Entries);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenToolPackageTypeIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.RazorWire.Cli",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                        "ForgeTrust.RazorWire.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "razorwire")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("DotnetTool", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNonToolPackageDeclaresToolPackageType()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            PackageVersion,
            EmptyDependencies,
            packageTypes: ["DotnetTool"]);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        "ForgeTrust.AppSurface.Web",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("DotnetTool", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenExpectedDependencyIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            PackageVersion,
            dependencies: EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        "ForgeTrust.AppSurface.Web",
                        PackagePublishDecision.Publish,
                        ["ForgeTrust.AppSurface.Core"],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("missing dependency 'ForgeTrust.AppSurface.Core'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenUnexpectedFirstPartyDependencyIsPresent()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.RazorWire.Cli",
            PackageVersion,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core"] = PackageVersion
            },
            packageTypes: ["DotnetTool"],
            toolCommandNames: ["razorwire"]);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false),
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                        "ForgeTrust.RazorWire.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: true,
                        ToolCommandName: "razorwire")
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("unexpected first-party dependencies", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ForgeTrust.AppSurface.Core", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenExpectedDependencyVersionDoesNotMatch()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            PackageVersion,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core"] = "[0.0.0-ci.41, )"
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        "ForgeTrust.AppSurface.Web",
                        PackagePublishDecision.Publish,
                        ["ForgeTrust.AppSurface.Core"],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("expected same-version dependency", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenAnyDependencyGroupVersionDoesNotMatch()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web",
            PackageVersion,
            dependencies: EmptyDependencies,
            dependencyXml: $$"""
                <dependencies>
                  <group targetFramework="net10.0">
                    <dependency id="ForgeTrust.AppSurface.Core" version="[{{PackageVersion}}, )" />
                  </group>
                  <group targetFramework="net9.0">
                    <dependency id="ForgeTrust.AppSurface.Core" version="0.0.0-ci.41" />
                  </group>
                </dependencies>
            """);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        "ForgeTrust.AppSurface.Web",
                        PackagePublishDecision.Publish,
                        ["ForgeTrust.AppSurface.Core"],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("expected same-version dependency", error.Message, StringComparison.Ordinal);
        Assert.Contains("0.0.0-ci.41", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenFirstPartyAssemblyVersionDoesNotMatch()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            dependencies: EmptyDependencies,
            assemblyEntries: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lib/net10.0/ForgeTrust.AppSurface.Core.dll"] = typeof(PackageArtifactValidationTests).Assembly.Location
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("informational version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ForgeTrust.AppSurface.Core.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_SkipsReferenceAssemblyPayloadVersionChecks()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            dependencies: EmptyDependencies,
            assemblyEntries: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ref/net10.0/ForgeTrust.AppSurface.Core.dll"] = typeof(PackageArtifactValidationTests).Assembly.Location
            });

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                    "ForgeTrust.AppSurface.Core",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion);

        Assert.Single(report.Entries);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenDuplicatePackageArtifactsExist()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            "0.0.0-ci.43",
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
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
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            includeNuspec: false);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains(".nuspec", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenMultipleNuspecFilesExist()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["extra.nuspec"] = Encoding.UTF8.GetBytes("<package />")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("multiple .nuspec", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsPackageIndexExceptionWhenNuspecXmlIsInvalid()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            includeNuspec: false,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core.nuspec"] = Encoding.UTF8.GetBytes("<package><metadata></package>")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("invalid nuspec XML", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData(false, true, "id")]
    [InlineData(true, false, "version")]
    public void PackageArtifactValidator_ThrowsWhenPackageIdentityMetadataIsMissing(
        bool includeId,
        bool includeVersion,
        string metadataName)
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            includeId: includeId,
            includeVersion: includeVersion);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
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
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Core",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["lib/net10.0/ForgeTrust.AppSurface.Core.dll"] = Encoding.UTF8.GetBytes("not a portable executable")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core",
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
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj",
                        "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64",
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
    public void PackageArtifactValidator_ThrowsWhenTailwindRuntimeIdIsUnsupported()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web.Tailwind.Runtime.solaris-x64",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.solaris-x64.csproj",
                        "ForgeTrust.AppSurface.Web.Tailwind.Runtime.solaris-x64",
                        PackagePublishDecision.SupportPublish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("unsupported runtime id", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("solaris-x64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsTailwindRuntimePayload()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["runtimes/linux-x64/native/tailwindcss-linux-x64"] = Encoding.UTF8.GetBytes("tailwind binary")
            });

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj",
                    "ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64",
                    PackagePublishDecision.SupportPublish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion);

        Assert.Single(report.Entries);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenSuspiciousPayloadIsUnclassified()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                new PackagePayloadInventory()));

        Assert.Contains("ASPKG123", error.Message, StringComparison.Ordinal);
        Assert.Contains("tools/net10.0/any/reportgenerator/ReportGenerator.dll", error.Message, StringComparison.Ordinal);
        Assert.Contains("Problem:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fix:", error.Message, StringComparison.Ordinal);
        Assert.Contains("packages/README.md#redistributed-payloads", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runtimes/linux-x64/native/copied-tool", "runtimes/*/native/**")]
    [InlineData("tools/net10.0/any/copied-tool.exe", "*.exe")]
    [InlineData("content/app/app.min.js", "*.min.js")]
    public void PackageArtifactValidator_ClassifiesSuspiciousPayloadRules(string entryPath, string expectedRule)
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [entryPath] = Encoding.UTF8.GetBytes("suspicious payload")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                new PackagePayloadInventory()));

        Assert.Contains("ASPKG123", error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedRule, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenThirdPartyLookingAssemblyIsUnclassified()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["lib/net10.0/Newtonsoft.Json.dll"] = Encoding.UTF8.GetBytes("third-party assembly")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                new PackagePayloadInventory()));

        Assert.Contains("ASPKG123", error.Message, StringComparison.Ordinal);
        Assert.Contains("lib/net10.0/Newtonsoft.Json.dll", error.Message, StringComparison.Ordinal);
        Assert.Contains("*.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsForgeTrustAssemblyAsFirstPartyPayload()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Flow",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["analyzers/dotnet/cs/ForgeTrust.AppSurface.Flow.Generators.dll"] = Encoding.UTF8.GetBytes("first-party analyzer")
            });

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Flow/ForgeTrust.AppSurface.Flow/ForgeTrust.AppSurface.Flow.csproj",
                    "ForgeTrust.AppSurface.Flow",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            new PackagePayloadInventory());

        Assert.Single(report.Entries);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsWildcardSegmentPayloadPattern()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["lib/net10.0/Newtonsoft.Json.dll"] = Encoding.UTF8.GetBytes("third-party assembly"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("Newtonsoft.Json 13.0.3 MIT")
            });
        var inventory = new PackagePayloadInventory
        {
            Notices =
            {
                new PackagePayloadNoticeRecord
                {
                    Id = "json-assembly",
                    PackageId = "ForgeTrust.AppSurface.Cli",
                    Component = "Newtonsoft.Json",
                    Version = "13.0.3",
                    License = "MIT",
                    SourceUrl = "https://www.newtonsoft.com/json",
                    PayloadPatterns = { "lib/net10.0/*.dll" },
                    NoticePaths = { "THIRD-PARTY-NOTICES.md" },
                    Markers = { "Newtonsoft.Json", "13.0.3", "MIT" }
                }
            }
        };

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                    "ForgeTrust.AppSurface.Cli",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            inventory);

        var payloadResult = Assert.Single(Assert.Single(report.Entries).PayloadResults!);
        Assert.Equal(["lib/net10.0/Newtonsoft.Json.dll"], payloadResult.PayloadEntries);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsTrailingWildcardSegmentPayloadPattern()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["lib/net10.0/Newtonsoft.Json.dll"] = Encoding.UTF8.GetBytes("third-party assembly"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("Newtonsoft.Json 13.0.3 MIT")
            });
        var inventory = new PackagePayloadInventory
        {
            Notices =
            {
                new PackagePayloadNoticeRecord
                {
                    Id = "json-assembly",
                    PackageId = "ForgeTrust.AppSurface.Cli",
                    Component = "Newtonsoft.Json",
                    Version = "13.0.3",
                    License = "MIT",
                    SourceUrl = "https://www.newtonsoft.com/json",
                    PayloadPatterns = { "lib/net10.0/Newtonsoft*" },
                    NoticePaths = { "THIRD-PARTY-NOTICES.md" },
                    Markers = { "Newtonsoft.Json", "13.0.3", "MIT" }
                }
            }
        };

        var report = new PackageArtifactValidator().Validate(
            CreateCliPublishPlan(),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            inventory);

        var payloadResult = Assert.Single(Assert.Single(report.Entries).PayloadResults!);
        Assert.Equal(["lib/net10.0/Newtonsoft.Json.dll"], payloadResult.PayloadEntries);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsNoticeClassifiedPayloadAndReportsCoverage()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["tools/net10.0/any/reportgenerator/ReportGenerator.resources.dll"] = Encoding.UTF8.GetBytes("copied satellite"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                    "ForgeTrust.AppSurface.Cli",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            CreateReportGeneratorInventory());

        var entry = Assert.Single(report.Entries);
        var payloadResult = Assert.Single(entry.PayloadResults!);
        Assert.Equal(2, entry.SuspiciousPayloadCount);
        Assert.Equal(2, entry.CoveredSuspiciousPayloadCount);
        Assert.Equal("cli-reportgenerator-tool-payload", payloadResult.RecordId);
        Assert.Equal("notice_enforced", payloadResult.Status);
        Assert.Equal("Directory.Packages.props", payloadResult.VersionSource);
        Assert.Equal(
            [
                "tools/net10.0/any/reportgenerator/ReportGenerator.dll",
                "tools/net10.0/any/reportgenerator/ReportGenerator.resources.dll"
            ],
            payloadResult.PayloadEntries);
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(report);
        Assert.Contains("Suspicious payloads", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.Cli` |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 2/2 |", markdown, StringComparison.Ordinal);
        Assert.Contains("## Redistributed payload coverage", markdown, StringComparison.Ordinal);
        Assert.Contains("`cli-reportgenerator-tool-payload`", markdown, StringComparison.Ordinal);
        Assert.Contains("`THIRD-PARTY-NOTICES.md`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AllowsRepositoryRootAsEvidencePath()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].SourcePaths.Clear();
        inventory.Notices[0].SourcePaths.Add(".");

        var report = new PackageArtifactValidator().Validate(
            CreateCliPublishPlan(),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            inventory);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(1, entry.CoveredSuspiciousPayloadCount);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenAuditOverlapsNoticeClassifiedPayload()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Audits.Add(
            new PackagePayloadAuditRecord
            {
                Id = "broad-tool-closure",
                PackageId = "ForgeTrust.AppSurface.Cli",
                AppliesTo = { "tools/net10.0/any/**/*.dll" },
                MatchedRule = "dotnet tool dependency closure",
                EvidenceKind = "dotnet_tool_dependency_closure",
                SourcePaths = { "Directory.Packages.props" },
                Reason = "Broad closure must not cover noticed payloads.",
                ReviewedOn = "2026-06-07",
                Source = "synthetic test",
                RevalidateWhen = "Package layout changes."
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG138", error.Message, StringComparison.Ordinal);
        Assert.Contains("broad-tool-closure", error.Message, StringComparison.Ordinal);
        Assert.Contains("tools/net10.0/any/reportgenerator/ReportGenerator.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticeMarkerIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG126", error.Message, StringComparison.Ordinal);
        Assert.Contains("Apache-2.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPayloadPatternUsesEmbeddedGlobstar()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].PayloadPatterns.Add("tools/**reportgenerator/**");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG128", error.Message, StringComparison.Ordinal);
        Assert.Contains("tools/**reportgenerator/**", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPayloadPatternIsEmpty()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].PayloadPatterns.Clear();
        inventory.Notices[0].PayloadPatterns.Add("");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("must not be empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenInventoryPathEscapesRepository()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].SourcePaths.Add("../outside.txt");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG135", error.Message, StringComparison.Ordinal);
        Assert.Contains("escapes the repository root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenInventoryPathIsAbsolute()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].SourcePaths.Add(CombineSafeChildPath(_repositoryRoot, "Directory.Packages.props"));

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG135", error.Message, StringComparison.Ordinal);
        Assert.Contains("repository-relative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPayloadInventoryReferencesUnknownPackage()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies);
        var inventory = new PackagePayloadInventory
        {
            Notices =
            {
                new PackagePayloadNoticeRecord
                {
                    Id = "stale-notice",
                    PackageId = "ForgeTrust.Missing.Package",
                    Component = "Missing component",
                    Version = "1.0.0",
                    License = "MIT",
                    SourceUrl = "https://example.invalid/missing",
                    PayloadPatterns = { "tools/**" },
                    NoticePaths = { "THIRD-PARTY-NOTICES.md" },
                    Markers = { "Missing component" }
                }
            },
            Audits =
            {
                new PackagePayloadAuditRecord
                {
                    Id = "stale-audit",
                    PackageId = "ForgeTrust.Missing.AuditPackage",
                    AppliesTo = { "tools/**" },
                    EvidenceKind = "manual_audit",
                    SourcePaths = { "Directory.Packages.props" },
                    Reason = "Missing audit package.",
                    ReviewedOn = "2026-06-12",
                    Source = "synthetic test",
                    RevalidateWhen = "Package layout changes."
                }
            }
        };

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG136", error.Message, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.Missing.Package", error.Message, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.Missing.AuditPackage", error.Message, StringComparison.Ordinal);
        Assert.Contains("packages/package-index.yml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AllowsGlobstarToMatchZeroSegmentsAndTrailingStar()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/reportgenerator/ReportGenerator"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].PayloadPatterns.Clear();
        inventory.Notices[0].PayloadPatterns.Add("tools/**/reportgenerator/ReportGenerator*");

        var report = new PackageArtifactValidator().Validate(
            CreateCliPublishPlan(),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            inventory);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(1, entry.SuspiciousPayloadCount);
        Assert.Equal(1, entry.CoveredSuspiciousPayloadCount);
        var payloadResult = Assert.Single(entry.PayloadResults!);
        Assert.Equal("tools/reportgenerator/ReportGenerator", Assert.Single(payloadResult.PayloadEntries));
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenInventoryPathUsesWindowsDriveRoot()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].SourcePaths.Add("C:/tmp/Directory.Packages.props");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG135", error.Message, StringComparison.Ordinal);
        Assert.Contains("repository-relative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenInventoryPathUsesBackslashRoot()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].SourcePaths.Add("\\tmp\\Directory.Packages.props");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG135", error.Message, StringComparison.Ordinal);
        Assert.Contains("repository-relative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticePathUsesCurrentDirectorySegment()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].NoticePaths.Clear();
        inventory.Notices[0].NoticePaths.Add("./THIRD-PARTY-NOTICES.md");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("current-directory segments", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticePathIsSlashOnly()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].NoticePaths.Clear();
        inventory.Notices[0].NoticePaths.Add("/");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("package payload paths must be relative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPackageEntriesCollideAfterNormalization()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("one"),
                ["third-party-notices.md"] = Encoding.UTF8.GetBytes("two")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        "ForgeTrust.AppSurface.Cli",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion));

        Assert.Contains("duplicate package entry path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_AcceptsGeneratedFirstPartyAuditAndReportsCoverage()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Web/ForgeTrust.RazorWire/assets/src/razorwire.ts", "source");
        WriteFile("Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js", "generated");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.RazorWire",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["content/razorwire/razorwire.js"] = Encoding.UTF8.GetBytes("generated")
            });

        var inventory = new PackagePayloadInventory
        {
            Audits =
            {
                new PackagePayloadAuditRecord
                {
                    Id = "razorwire-generated-browser-assets",
                    PackageId = "ForgeTrust.RazorWire",
                    AppliesTo = { "content/razorwire/razorwire.js" },
                    MatchedRule = "embedded generated browser assets",
                    EvidenceKind = "generated_first_party",
                    SourcePaths = { "Web/ForgeTrust.RazorWire/assets/src/razorwire.ts" },
                    GeneratedPaths = { "Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js" },
                    Reason = "Generated from first-party source.",
                    ReviewedOn = "2026-06-07",
                    Source = "RWPACK001",
                    RevalidateWhen = "RazorWire source changes."
                }
            }
        };

        var report = new PackageArtifactValidator().Validate(
            new PackagePublishPlan([
                new PackagePublishPlanEntry(
                    "Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj",
                    "ForgeTrust.RazorWire",
                    PackagePublishDecision.Publish,
                    [],
                    IsTool: false)
            ]),
            artifactDirectory,
            PackageVersion,
            _repositoryRoot,
            inventory);

        var payloadResult = Assert.Single(Assert.Single(report.Entries).PayloadResults!);
        Assert.Equal("generated_first_party", payloadResult.EvidenceKind);
        Assert.Equal("audit_enforced", payloadResult.Status);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenDeclaredNoticePathIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG125", error.Message, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticePathIsTooLarge()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = new byte[300 * 1024]
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG129", error.Message, StringComparison.Ordinal);
        Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticePathIsNotUtf8()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = [0x52, 0xFF, 0xFE, 0x00]
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG130", error.Message, StringComparison.Ordinal);
        Assert.Contains("UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticePathUsesUtf16Bom()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.Unicode.GetPreamble()
                    .Concat(Encoding.Unicode.GetBytes("ReportGenerator 5.5.10 Apache-2.0"))
                    .ToArray()
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG130", error.Message, StringComparison.Ordinal);
        Assert.Contains("UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenSourcePathIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG131", error.Message, StringComparison.Ordinal);
        Assert.Contains("source_paths", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenVersionSourceEvidenceIsIncomplete()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory(versionSourceContains: null)));

        Assert.Contains("ASPKG132", error.Message, StringComparison.Ordinal);
        Assert.Contains("version_source_path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenVersionSourcePathIsMissingButContainsIsSet()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory(versionSourcePath: null)));

        Assert.Contains("ASPKG132", error.Message, StringComparison.Ordinal);
        Assert.Contains("version_source_contains", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenVersionSourcePathIsMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory(versionSourcePath: "Missing.Packages.props")));

        Assert.Contains("ASPKG133", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenVersionSourceTextIsStale()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.9" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/net10.0/any/reportgenerator/ReportGenerator.dll"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG134", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not contain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenGeneratedFirstPartyAuditOmitsGeneratedPaths()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Web/ForgeTrust.RazorWire/assets/src/razorwire.ts", "source");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.RazorWire",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["content/razorwire/razorwire.js"] = Encoding.UTF8.GetBytes("generated")
            });
        var inventory = new PackagePayloadInventory
        {
            Audits =
            {
                new PackagePayloadAuditRecord
                {
                    Id = "razorwire-generated-browser-assets",
                    PackageId = "ForgeTrust.RazorWire",
                    AppliesTo = { "content/razorwire/razorwire.js" },
                    MatchedRule = "embedded generated browser assets",
                    EvidenceKind = "generated_first_party",
                    SourcePaths = { "Web/ForgeTrust.RazorWire/assets/src/razorwire.ts" },
                    Reason = "Generated from first-party source.",
                    ReviewedOn = "2026-06-07",
                    Source = "RWPACK001",
                    RevalidateWhen = "RazorWire source changes."
                }
            }
        };

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj",
                        "ForgeTrust.RazorWire",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG137", error.Message, StringComparison.Ordinal);
        Assert.Contains("generated_paths", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPayloadInventoryRequiresRepositoryRoot()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies);

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                payloadInventory: new PackagePayloadInventory()));

        Assert.Contains("requires a repository root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenNoticePatternMatchesNoPayloads()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                CreateReportGeneratorInventory()));

        Assert.Contains("ASPKG124", error.Message, StringComparison.Ordinal);
        Assert.Contains("matched no package payload entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenGlobstarNoticePatternCannotMatchPayload()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Directory.Packages.props", """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.AppSurface.Cli",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["tools/reportgenerator/ReportGenerator"] = Encoding.UTF8.GetBytes("copied tool"),
                ["THIRD-PARTY-NOTICES.md"] = Encoding.UTF8.GetBytes("ReportGenerator 5.5.10 Apache-2.0")
            });
        var inventory = CreateReportGeneratorInventory();
        inventory.Notices[0].PayloadPatterns.Clear();
        inventory.Notices[0].PayloadPatterns.Add("tools/**/missing/ReportGenerator");

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                CreateCliPublishPlan(),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG124", error.Message, StringComparison.Ordinal);
        Assert.Contains("tools/**/missing/ReportGenerator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenAuditPatternMatchesNoPayloads()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        WriteFile("Web/ForgeTrust.RazorWire/assets/src/razorwire.ts", "source");
        WriteFile("Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js", "generated");
        WritePackage(
            artifactDirectory,
            "ForgeTrust.RazorWire",
            PackageVersion,
            EmptyDependencies,
            rawEntries: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["content/razorwire/razorwire.js"] = Encoding.UTF8.GetBytes("generated")
            });
        var inventory = new PackagePayloadInventory
        {
            Audits =
            {
                new PackagePayloadAuditRecord
                {
                    Id = "razorwire-generated-browser-assets",
                    PackageId = "ForgeTrust.RazorWire",
                    AppliesTo = { "content/razorwire/missing.js" },
                    MatchedRule = "embedded generated browser assets",
                    EvidenceKind = "generated_first_party",
                    SourcePaths = { "Web/ForgeTrust.RazorWire/assets/src/razorwire.ts" },
                    GeneratedPaths = { "Web/ForgeTrust.RazorWire/wwwroot/razorwire/razorwire.js" },
                    Reason = "Generated from first-party source.",
                    ReviewedOn = "2026-06-07",
                    Source = "RWPACK001",
                    RevalidateWhen = "RazorWire source changes."
                }
            }
        };

        var error = Assert.Throws<PackageIndexException>(
            () => new PackageArtifactValidator().Validate(
                new PackagePublishPlan([
                    new PackagePublishPlanEntry(
                        "Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj",
                        "ForgeTrust.RazorWire",
                        PackagePublishDecision.Publish,
                        [],
                        IsTool: false)
                ]),
                artifactDirectory,
                PackageVersion,
                _repositoryRoot,
                inventory));

        Assert.Contains("ASPKG127", error.Message, StringComparison.Ordinal);
        Assert.Contains("matched no package payload entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForMalformedYaml()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                notices:
                  - id: broken
                    package_id: [
                """));

        Assert.Contains("could not be parsed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Problem:", error.Message, StringComparison.Ordinal);
        Assert.Contains("Fix:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForEmptyYaml()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(""));

        Assert.Contains("is empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForUnsupportedSchemaVersion()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 2
                notices:
                  - id: reportgenerator
                    package_id: ForgeTrust.AppSurface.Cli
                    component: ReportGenerator
                    version: 5.5.10
                    license: Apache-2.0
                    source_url: https://github.com/danielpalme/ReportGenerator
                    payload_patterns:
                      - tools/**/reportgenerator/**
                    notice_paths:
                      - THIRD-PARTY-NOTICES.md
                    markers:
                      - ReportGenerator
                """));

        Assert.Contains("schema_version: 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForNoRecords()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                """));

        Assert.Contains("at least one notice or audit record", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForIncompleteNoticeRecord()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                notices:
                  - id: incomplete
                    package_id: ForgeTrust.AppSurface.Cli
                    component: ReportGenerator
                    version: 5.5.10
                    license: Apache-2.0
                    source_url: https://github.com/danielpalme/ReportGenerator
                    payload_patterns:
                      - tools/**/reportgenerator/**
                    notice_paths:
                      - THIRD-PARTY-NOTICES.md
                """));

        Assert.Contains("markers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForIncompleteAuditRecord()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                audits:
                  - id: incomplete-audit
                    package_id: ForgeTrust.RazorWire
                    applies_to:
                      - content/razorwire/razorwire.js
                    evidence_kind: generated_first_party
                    reason: Generated assets.
                    reviewed_on: 2026-06-07
                    source: test
                    revalidate_when: source changes.
                """));

        Assert.Contains("source_paths", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForWhitespaceAuditField()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                audits:
                  - id: whitespace-audit
                    package_id: ForgeTrust.RazorWire
                    applies_to:
                      - content/razorwire/razorwire.js
                    evidence_kind: manual_audit
                    source_paths:
                      - Web/ForgeTrust.RazorWire/assets/src/razorwire.ts
                    reason: " "
                    reviewed_on: 2026-06-12
                    source: test
                    revalidate_when: source changes.
                """));

        Assert.Contains("reason", error.Message, StringComparison.Ordinal);
        Assert.Contains("required payload evidence is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsForDuplicateRecordIds()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                notices:
                  - id: duplicate
                    package_id: ForgeTrust.AppSurface.Cli
                    component: ReportGenerator
                    version: 5.5.10
                    license: Apache-2.0
                    source_url: https://github.com/danielpalme/ReportGenerator
                    payload_patterns:
                      - tools/**/reportgenerator/**
                    notice_paths:
                      - THIRD-PARTY-NOTICES.md
                    markers:
                      - ReportGenerator
                audits:
                  - id: duplicate
                    package_id: ForgeTrust.RazorWire
                    applies_to:
                      - lib/**/ForgeTrust.RazorWire.dll
                    evidence_kind: generated_first_party
                    source_paths:
                      - Web/ForgeTrust.RazorWire/assets/src/razorwire.ts
                    reason: Generated assets.
                    reviewed_on: 2026-06-07
                    source: test
                    revalidate_when: source changes.
                """));

        Assert.Contains("duplicate record id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ThrowsWhenGeneratedFirstPartyAuditOmitsGeneratedPaths()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().Parse(
                """
                schema_version: 1
                audits:
                  - id: generated-without-output
                    package_id: ForgeTrust.RazorWire
                    applies_to:
                      - lib/**/ForgeTrust.RazorWire.dll
                    evidence_kind: generated_first_party
                    source_paths:
                      - Web/ForgeTrust.RazorWire/assets/src/razorwire.ts
                    reason: Generated assets.
                    reviewed_on: 2026-06-07
                    source: test
                    revalidate_when: source changes.
                """));

        Assert.Contains("generated_paths", error.Message, StringComparison.Ordinal);
        Assert.Contains("generated-without-output", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagePayloadInventoryLoader_LoadAsyncReadsDefaultInventoryPath()
    {
        await WriteFileAsync("packages/third-party-payloads.yml",
            """
            schema_version: 1
            notices:
              - id: reportgenerator
                package_id: ForgeTrust.AppSurface.Cli
                component: ReportGenerator
                version: 5.5.10
                license: Apache-2.0
                source_url: https://github.com/danielpalme/ReportGenerator
                payload_patterns:
                  - tools/**/reportgenerator/**
                notice_paths:
                  - THIRD-PARTY-NOTICES.md
                markers:
                  - ReportGenerator
            """);

        var inventory = await new PackagePayloadInventoryLoader().LoadAsync(_repositoryRoot);

        var notice = Assert.Single(inventory.Notices);
        Assert.Equal("reportgenerator", notice.Id);
    }

    [Fact]
    public async Task PackagePayloadInventoryLoader_ThrowsWhenDefaultInventoryIsMissing()
    {
        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackagePayloadInventoryLoader().LoadAsync(_repositoryRoot));

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packages/third-party-payloads.yml", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/inventory.yml")]
    [InlineData("\\tmp\\inventory.yml")]
    [InlineData("C:\\tmp\\inventory.yml")]
    public void PackagePayloadInventoryLoader_RejectsRootedInventoryPath(string inventoryPath)
    {
        var error = Assert.Throws<PackageIndexException>(
            () => PackagePayloadInventoryLoader.ResolveInventoryPath(_repositoryRoot, inventoryPath));

        Assert.Contains("repository-relative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagePayloadInventoryLoader_ResolvesRelativeInventoryPath()
    {
        var inventoryPath = PackagePayloadInventoryLoader.ResolveInventoryPath(
            _repositoryRoot,
            "packages/third-party-payloads.yml");

        Assert.Equal(CombineSafeChildPath(_repositoryRoot, "packages/third-party-payloads.yml"), inventoryPath);
    }

    [Fact]
    public void PackageVersionValidator_AppliesReleaseClassificationPolicy()
    {
        PackageVersionValidator.Require("1.2.3", PackageVersionPolicy.StableOnly);
        PackageVersionValidator.Require("1.2.3", PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        PackageVersionValidator.Require("1.2.3-ci.4", PackageVersionPolicy.PrereleaseOnly);
        PackageVersionValidator.Require("1.2.3-ci.4", PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);

        var stableOnPrereleaseLane = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.Require("1.2.3", PackageVersionPolicy.PrereleaseOnly));
        var prereleaseOnStableLane = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.Require("1.2.3-ci.4", PackageVersionPolicy.StableOnly));
        var buildMetadata = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.Require("1.2.3-ci.4+sha", PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata));

        Assert.Contains("prerelease", stableOnPrereleaseLane.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stable", prereleaseOnStableLane.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build metadata", buildMetadata.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.two.3-ci.4")]
    [InlineData("1.2-ci.4")]
    public void PackageVersionValidator_RejectsMissingOrMalformedSemVerCore(string packageVersion)
    {
        var error = Assert.Throws<PackageIndexException>(
            () => PackageVersionValidator.Require(packageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata));

        Assert.Contains("Package version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactReportRenderer_RendersEveryPublishDecision()
    {
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(new PackageArtifactValidationReport(
            PackageVersion,
            [
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Web",
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    PackagePublishDecision.Publish,
                    []),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Web.OpenApi",
                    "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                    PackagePublishDecision.SupportPublish,
                    ["ForgeTrust.AppSurface.Web"]),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Example",
                    "examples/Example.csproj",
                    PackagePublishDecision.DoNotPublish,
                    []),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Experimental",
                    "experimental/Experimental.csproj",
                    (PackagePublishDecision)999,
                    [],
                    PayloadResults:
                    [
                        new PackagePayloadValidationResult(
                            "ForgeTrust.AppSurface.Experimental",
                            "generated-fixture",
                            "generated browser assets",
                            "generated_first_party",
                            "audit_enforced",
                            ["content/app/app.min.js"],
                            [],
                            "")
                    ]),
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.EmptyPayload",
                    "experimental/EmptyPayload.csproj",
                    PackagePublishDecision.Publish,
                    [])
            ]));

        Assert.Contains("`publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`support_publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`do_not_publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`999`", markdown, StringComparison.Ordinal);
        Assert.Contains("`ForgeTrust.AppSurface.Web`", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.Example` | `examples/Example.csproj` | `do_not_publish` | - | none |", markdown, StringComparison.Ordinal);
        Assert.Contains("`generated-fixture`", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.EmptyPayload` | `experimental/EmptyPayload.csproj` | `publish` | - | none | 0 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.Experimental` | `generated-fixture` | generated browser assets | `generated_first_party` | `audit_enforced` | `content/app/app.min.js` | - | - |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_RunsCoverageCommandsAndTreatsFailingGateAsSuccess()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(PackageVersion, createFailingGateReports: true);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.True(report.Succeeded, report.FirstFailure);
        Assert.Equal(
            [
                "dotnet new tool-manifest",
                "dotnet tool install",
                "appsurface --version",
                "appsurface coverage run",
                "appsurface coverage merge",
                "appsurface coverage gate",
                "appsurface coverage gate"
            ],
            commandRunner.Requests
                .Where(request => request.OperationName is "dotnet new tool-manifest" or "dotnet tool install" or "appsurface --version" or "appsurface coverage run" or "appsurface coverage merge" or "appsurface coverage gate")
                .Select(request => request.OperationName)
                .ToArray());
        var addPackageRequest = Assert.Single(commandRunner.Requests, request => request.OperationName == "dotnet add package");
        Assert.DoesNotContain("--configfile", addPackageRequest.Arguments);
        Assert.True(File.Exists(CombineSafeChildPath(report.WorkDirectory, "consumer/NuGet.config")));
        var failingGate = report.Commands.Last();
        Assert.True(failingGate.ExpectedNonZeroExitCode);
        Assert.True(failingGate.Succeeded);
        Assert.Equal(1, failingGate.ExitCode);
        Assert.All(report.Commands, command =>
        {
            Assert.True(File.Exists(command.StandardOutputPath));
            Assert.True(File.Exists(command.StandardErrorPath));
        });
        Assert.Contains(report.Artifacts, artifact => artifact.Description == "failing gate JSON report" && artifact.Exists);
        Assert.Contains(report.Artifacts, artifact => artifact.Description == "failing gate Markdown report" && artifact.Exists);
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_FailsWhenIntentionalGateDoesNotWriteReports()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(PackageVersion, createFailingGateReports: false);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("did not write both gate reports", report.FirstFailure, StringComparison.Ordinal);
        Assert.Contains(report.Artifacts, artifact => artifact.Description == "failing gate JSON report" && !artifact.Exists);
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_ReturnsFailureReportWhenCliPackageCannotBeSelected()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var commandRunner = new CoverageProofRecordingCommandRunner(PackageVersion, createFailingGateReports: true);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            new PackageArtifactValidationReport(PackageVersion, []),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("requires validated package", report.FirstFailure, StringComparison.Ordinal);
        Assert.Null(report.SelectedArtifact);
        Assert.Empty(commandRunner.Requests);
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_ReturnsFailureReportWhenWorkDirectoryIsUnsafe()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(PackageVersion, createFailingGateReports: true);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                artifactDirectory,
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("not a safe deletion target", report.FirstFailure, StringComparison.Ordinal);
        Assert.NotNull(report.SelectedArtifact);
        Assert.Empty(commandRunner.Requests);
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_StopsAfterFirstRequiredCommandFailure()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(2, "template stdout", "template stderr")
        ]);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        var command = Assert.Single(report.Commands);
        Assert.False(report.Succeeded);
        Assert.Equal("dotnet new sln", command.OperationName);
        Assert.Equal(2, command.ExitCode);
        Assert.Contains("Expected exit code 0", report.FirstFailure, StringComparison.Ordinal);
        Assert.True(File.Exists(command.StandardOutputPath));
        Assert.True(File.Exists(command.StandardErrorPath));
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_FailsWhenInstalledCliVersionDoesNotMatchPackageVersion()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner("0.0.0-wrong", createFailingGateReports: true);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("Expected 'appsurface --version'", report.FirstFailure, StringComparison.Ordinal);
        Assert.Equal("appsurface --version", report.Commands.Last().OperationName);
        Assert.Contains(commandRunner.Requests, request => request.OperationName == "dotnet tool install");
        Assert.DoesNotContain(commandRunner.Requests, request => request.OperationName == "appsurface coverage run");
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_FailsWhenCoverageRunArtifactsAreMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(
            PackageVersion,
            createFailingGateReports: true,
            createCoverageRunArtifacts: false);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("coverage run merged Cobertura", report.FirstFailure, StringComparison.Ordinal);
        Assert.Contains(report.Artifacts, artifact => artifact.Description == "coverage run merged Cobertura" && !artifact.Exists);
        Assert.DoesNotContain(commandRunner.Requests, request => request.OperationName == "appsurface coverage merge");
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_FailsWhenCoverageMergeArtifactsAreMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(
            PackageVersion,
            createFailingGateReports: true,
            createCoverageMergeArtifacts: false);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("coverage merge Cobertura", report.FirstFailure, StringComparison.Ordinal);
        Assert.Contains(report.Artifacts, artifact => artifact.Description == "coverage merge Cobertura" && !artifact.Exists);
        Assert.DoesNotContain(
            commandRunner.Requests,
            request => request.OperationName == "appsurface coverage gate"
                && request.TimeoutDescription.Contains("passing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_FailsWhenPassingGateArtifactsAreMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(
            PackageVersion,
            createFailingGateReports: true,
            createPassingGateReports: false);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("passing gate JSON report", report.FirstFailure, StringComparison.Ordinal);
        Assert.Contains(report.Artifacts, artifact => artifact.Description == "passing gate JSON report" && !artifact.Exists);
        Assert.DoesNotContain(
            commandRunner.Requests,
            request => request.OperationName == "appsurface coverage gate"
                && request.TimeoutDescription.Contains("intentionally failing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dotnet new classlib", null)]
    [InlineData("dotnet new xunit", null)]
    [InlineData("dotnet sln add", null)]
    [InlineData("dotnet add reference", null)]
    [InlineData("dotnet add package", null)]
    [InlineData("dotnet new tool-manifest", null)]
    [InlineData("dotnet tool install", null)]
    [InlineData("appsurface --version", null)]
    [InlineData("appsurface coverage run", null)]
    [InlineData("appsurface coverage merge", null)]
    [InlineData("appsurface coverage gate", "passing")]
    public async Task CoverageCliConsumerProofWorkflow_StopsWhenRequiredCommandFails(
        string failedOperationName,
        string? failedTimeoutDescription)
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(
            PackageVersion,
            createFailingGateReports: true,
            failOperationName: failedOperationName,
            failTimeoutDescriptionContains: failedTimeoutDescription);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, $"coverage-proof-{Guid.NewGuid():N}"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Equal(failedOperationName, report.Commands.Last().OperationName);
        Assert.Contains("Expected exit code 0", report.FirstFailure, StringComparison.Ordinal);
        Assert.Equal(commandRunner.Requests.Count, report.Commands.Count);
        if (failedTimeoutDescription is not null)
        {
            Assert.Contains(failedTimeoutDescription, commandRunner.Requests.Last().TimeoutDescription, StringComparison.Ordinal);
            Assert.DoesNotContain(
                commandRunner.Requests,
                request => request.OperationName == "appsurface coverage gate"
                    && request.TimeoutDescription.Contains("intentionally failing", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_FailsWhenIntentionalGateExitsZero()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(cliArtifactPath, "cli package", Encoding.UTF8);
        var commandRunner = new CoverageProofRecordingCommandRunner(
            PackageVersion,
            createFailingGateReports: true,
            intentionallyFailingGateExitsNonZero: false);
        var workflow = new CoverageCliConsumerProofWorkflow(commandRunner);

        var report = await workflow.RunAsync(
            new CoverageCliConsumerProofRequest(
                _repositoryRoot,
                artifactDirectory,
                PackageVersion,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                "https://api.nuget.org/v3/index.json"),
            CreateCliProofValidationReport(cliArtifactPath),
            CancellationToken.None);

        Assert.False(report.Succeeded);
        Assert.Contains("Expected a non-zero exit code", report.FirstFailure, StringComparison.Ordinal);
        Assert.False(report.Commands.Last().Succeeded);
        Assert.True(report.Commands.Last().ExpectedNonZeroExitCode);
    }

    [Fact]
    public async Task CoverageCliConsumerProofWorkflow_ThrowsWhenRequestRootPathsAreMissing()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var workflow = new CoverageCliConsumerProofWorkflow(new RecordingExternalCommandRunner([]));
        var missingRepository = CombineSafeChildPath(_repositoryRoot, "missing-repository");
        var missingArtifacts = CombineSafeChildPath(_repositoryRoot, "missing-artifacts");

        var missingRepositoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new CoverageCliConsumerProofRequest(
                    missingRepository,
                    artifactDirectory,
                    PackageVersion,
                    CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                    "https://api.nuget.org/v3/index.json"),
                new PackageArtifactValidationReport(PackageVersion, []),
                CancellationToken.None));
        var missingArtifactsError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new CoverageCliConsumerProofRequest(
                    _repositoryRoot,
                    missingArtifacts,
                    PackageVersion,
                    CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                    "https://api.nuget.org/v3/index.json"),
                new PackageArtifactValidationReport(PackageVersion, []),
                CancellationToken.None));

        Assert.Contains("Repository root", missingRepositoryError.Message, StringComparison.Ordinal);
        Assert.Contains("Package artifact directory", missingArtifactsError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageCliConsumerProofWorkflow_SelectsOnlyValidatedCliToolPackage()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var cliArtifactPath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        File.WriteAllText(cliArtifactPath, "cli package", Encoding.UTF8);

        var selected = CoverageCliConsumerProofWorkflow.SelectCliToolPackage(
            CreateCliProofValidationReport(cliArtifactPath),
            PackageVersion);

        Assert.Equal("ForgeTrust.AppSurface.Cli", selected.PackageId);
        Assert.Equal("appsurface", selected.ToolCommandName);
        Assert.Equal(cliArtifactPath, selected.ArtifactPath);
        Assert.False(string.IsNullOrWhiteSpace(selected.Sha512));
    }

    [Fact]
    public void CoverageCliConsumerProofWorkflow_RejectsInvalidCliPackageSelection()
    {
        var missingPackage = Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.SelectCliToolPackage(
                new PackageArtifactValidationReport(PackageVersion, []),
                PackageVersion));
        var nonTool = Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.SelectCliToolPackage(
                new PackageArtifactValidationReport(
                    PackageVersion,
                    [
                        new PackageArtifactValidationReportEntry(
                            "ForgeTrust.AppSurface.Cli",
                            "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                            PackagePublishDecision.Publish,
                            [])
                    ]),
                PackageVersion));
        var wrongCommand = Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.SelectCliToolPackage(
                new PackageArtifactValidationReport(
                    PackageVersion,
                    [
                        new PackageArtifactValidationReportEntry(
                            "ForgeTrust.AppSurface.Cli",
                            "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                            PackagePublishDecision.Publish,
                            [],
                            "missing.nupkg",
                            IsTool: true,
                            ToolCommandName: "wrong")
                    ]),
                PackageVersion));
        var missingCommand = Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.SelectCliToolPackage(
                new PackageArtifactValidationReport(
                    PackageVersion,
                    [
                        new PackageArtifactValidationReportEntry(
                            "ForgeTrust.AppSurface.Cli",
                            "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                            PackagePublishDecision.Publish,
                            [],
                            "missing.nupkg",
                            IsTool: true,
                            ToolCommandName: "")
                    ]),
                PackageVersion));
        var wrongVersionArtifactDirectory = CombineSafeChildPath(_repositoryRoot, "wrong-version");
        Directory.CreateDirectory(wrongVersionArtifactDirectory);
        var wrongVersionArtifactPath = CombineSafeChildPath(
            wrongVersionArtifactDirectory,
            "ForgeTrust.AppSurface.Cli.0.0.0-wrong.nupkg");
        File.WriteAllText(wrongVersionArtifactPath, "cli package", Encoding.UTF8);
        var wrongVersion = Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.SelectCliToolPackage(
                CreateCliProofValidationReport(wrongVersionArtifactPath),
                PackageVersion));

        Assert.Contains("requires validated package", missingPackage.Message, StringComparison.Ordinal);
        Assert.Contains(".NET tool", nonTool.Message, StringComparison.Ordinal);
        Assert.Contains("tool command", wrongCommand.Message, StringComparison.Ordinal);
        Assert.Contains("tool command", missingCommand.Message, StringComparison.Ordinal);
        Assert.Contains("does not match package version", wrongVersion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageCliConsumerProofWorkflow_PrepareWorkDirectoryDeletesOnlySafeChild()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        var workDirectory = CombineSafeChildPath(artifactDirectory, "coverage-proof");
        var staleFile = CombineSafeChildPath(workDirectory, "stale.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staleFile)!);
        File.WriteAllText(staleFile, "stale", Encoding.UTF8);

        CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(workDirectory, _repositoryRoot, artifactDirectory);

        Assert.True(Directory.Exists(workDirectory));
        Assert.False(File.Exists(staleFile));
    }

    [Fact]
    public void CoverageCliConsumerProofWorkflow_PrepareWorkDirectoryRejectsUnsafeDeletionTargets()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);

        Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(_repositoryRoot, _repositoryRoot, artifactDirectory));
        Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(
                _repositoryRoot + Path.DirectorySeparatorChar,
                _repositoryRoot,
                artifactDirectory));
        Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(artifactDirectory, _repositoryRoot, artifactDirectory));
        Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(
                artifactDirectory + Path.DirectorySeparatorChar,
                _repositoryRoot,
                artifactDirectory));
        Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(Directory.GetParent(_repositoryRoot)!.FullName, _repositoryRoot, artifactDirectory));
        Assert.Throws<PackageIndexException>(
            () => CoverageCliConsumerProofWorkflow.PrepareWorkDirectory(
                Directory.GetParent(_repositoryRoot)!.FullName + Path.DirectorySeparatorChar,
                _repositoryRoot,
                artifactDirectory));
    }

    [Fact]
    public void CoverageCliConsumerProofWorkflow_RendersNuGetSourceIsolationConfig()
    {
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        var config = CoverageCliConsumerProofWorkflow.RenderMappedNuGetConfig(
            artifactDirectory,
            "https://api.nuget.org/v3/index.json");
        var document = XDocument.Parse(config);
        var localSource = document.Descendants("packageSource")
            .Single(source => string.Equals(source.Attribute("key")?.Value, "local-appsurface", StringComparison.Ordinal));
        var nugetOrgSource = document.Descendants("packageSource")
            .Single(source => string.Equals(source.Attribute("key")?.Value, "nuget-org", StringComparison.Ordinal));

        Assert.Contains(document.Descendants("add"), source => source.Attribute("key")?.Value == "local-appsurface" && source.Attribute("value")?.Value == Path.GetFullPath(artifactDirectory));
        Assert.Contains(localSource.Descendants("package"), package => package.Attribute("pattern")?.Value == "ForgeTrust.AppSurface.*");
        Assert.Contains(localSource.Descendants("package"), package => package.Attribute("pattern")?.Value == "ForgeTrust.RazorWire.*");
        Assert.Contains(nugetOrgSource.Descendants("package"), package => package.Attribute("pattern")?.Value == "*");
    }

    [Fact]
    public void CoverageCliConsumerProofReportRenderer_RendersSuccessAndFailureSections()
    {
        var selected = new CoverageCliConsumerProofSelectedArtifact(
            "ForgeTrust.AppSurface.Cli",
            "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
            "/tmp/ForgeTrust.AppSurface.Cli.nupkg",
            "appsurface",
            "abc123");
        var command = new CoverageCliConsumerProofCommandResult(
            "appsurface coverage gate",
            "appsurface",
            ["coverage", "gate"],
            "/tmp/work",
            1,
            ExpectedNonZeroExitCode: true,
            Succeeded: true,
            FailureReason: string.Empty,
            TimeSpan.FromMilliseconds(12),
            "/tmp/stdout.log",
            "/tmp/stderr.log",
            "ASCOV020 Coverage gate failed.",
            string.Empty);
        var success = new CoverageCliConsumerProofReport(
            PackageVersion,
            "/tmp/work",
            "https://api.nuget.org/v3/index.json",
            selected,
            "/tmp/tool.config",
            "/tmp/fixture.config",
            "/tmp/logs",
            [command],
            [new CoverageCliConsumerProofArtifactCheck("failing gate JSON report", "/tmp/coverage-gate.json", Exists: true)],
            string.Empty,
            "dotnet run -- verify-packages");
        var failure = success with
        {
            FirstFailure = "missing artifact",
            Artifacts = [new CoverageCliConsumerProofArtifactCheck("coverage merge Cobertura", "/tmp/coverage.cobertura.xml", Exists: false)]
        };

        var successMarkdown = CoverageCliConsumerProofReportRenderer.RenderMarkdown(success);
        var failureMarkdown = CoverageCliConsumerProofReportRenderer.RenderMarkdown(failure);

        Assert.Contains("Status: `passed`", successMarkdown, StringComparison.Ordinal);
        Assert.Contains("Selected artifact SHA-512: `abc123`", successMarkdown, StringComparison.Ordinal);
        Assert.Contains("ASCOV020 Coverage gate failed.", successMarkdown, StringComparison.Ordinal);
        Assert.Contains("Status: `failed`", failureMarkdown, StringComparison.Ordinal);
        Assert.Contains("## Missing artifacts", failureMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageCliConsumerProofReportRenderer_RendersEmptyOptionalSections()
    {
        var report = new CoverageCliConsumerProofReport(
            PackageVersion,
            "/tmp/work",
            "https://api.nuget.org/v3/index.json",
            SelectedArtifact: null,
            ToolNuGetConfigPath: string.Empty,
            FixtureNuGetConfigPath: string.Empty,
            LogsDirectory: string.Empty,
            Commands:
            [
                new CoverageCliConsumerProofCommandResult(
                    "dotnet new sln",
                    "dotnet",
                    ["new", "sln"],
                    "/tmp/work",
                    1,
                    ExpectedNonZeroExitCode: false,
                    Succeeded: false,
                    "Expected exit code 0, got 1.",
                    TimeSpan.Zero,
                    "/tmp/stdout.log",
                    "/tmp/stderr.log",
                    string.Empty,
                    "template failed")
            ],
            Artifacts: [],
            FirstFailure: string.Empty,
            ReproduceCommand: string.Empty);

        var markdown = CoverageCliConsumerProofReportRenderer.RenderMarkdown(report);

        Assert.Contains("Status: `failed`", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Selected artifact:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Reproduce:", markdown, StringComparison.Ordinal);
        Assert.Contains("No artifact checks ran.", markdown, StringComparison.Ordinal);
        Assert.Contains("stderr:", markdown, StringComparison.Ordinal);
        Assert.Contains("template failed", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageArtifactManifestReader_RejectsArtifactFileNamesWithDirectorySegments()
    {
        var manifestPath = CombineSafeChildPath(_repositoryRoot, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "schema_version": 1,
              "package_version": "{{PackageVersion}}",
              "generated_at_utc": "2026-05-12T00:00:00Z",
              "entries": [
                {
                  "package_id": "ForgeTrust.AppSurface.Web",
                  "project_path": "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                  "decision": "publish",
                  "artifact_file_name": "../ForgeTrust.AppSurface.Web.{{PackageVersion}}.nupkg",
                  "sha512": "abc",
                  "is_tool": false
                }
              ]
            }
            """,
            Encoding.UTF8);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader().ReadAsync(manifestPath, CancellationToken.None));

        Assert.Contains("without directory segments", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageArtifactManifestReader_RejectsToolEntryWithoutToolCommandName()
    {
        var manifestPath = CombineSafeChildPath(_repositoryRoot, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "schema_version": 1,
              "package_version": "{{PackageVersion}}",
              "generated_at_utc": "2026-05-12T00:00:00Z",
              "entries": [
                {
                  "package_id": "ForgeTrust.AppSurface.Cli",
                  "project_path": "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                  "decision": "publish",
                  "artifact_file_name": "ForgeTrust.AppSurface.Cli.{{PackageVersion}}.nupkg",
                  "sha512": "abc",
                  "is_tool": true
                }
              ]
            }
            """,
            Encoding.UTF8);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader().ReadAsync(manifestPath, CancellationToken.None));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageArtifactManifestReader_RejectsToolCommandNameOnNonToolEntry()
    {
        var manifestPath = CombineSafeChildPath(_repositoryRoot, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "schema_version": 1,
              "package_version": "{{PackageVersion}}",
              "generated_at_utc": "2026-05-12T00:00:00Z",
              "entries": [
                {
                  "package_id": "ForgeTrust.AppSurface.Web",
                  "project_path": "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                  "decision": "publish",
                  "artifact_file_name": "ForgeTrust.AppSurface.Web.{{PackageVersion}}.nupkg",
                  "sha512": "abc",
                  "is_tool": false,
                  "tool_command_name": "appsurface"
                }
              ]
            }
            """,
            Encoding.UTF8);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader().ReadAsync(manifestPath, CancellationToken.None));

        Assert.Contains("not marked as a tool", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../appsurface")]
    [InlineData("con")]
    [InlineData("con.txt")]
    public async Task PackageArtifactManifestReader_RejectsInvalidToolCommandName(string toolCommandName)
    {
        var manifestPath = CombineSafeChildPath(_repositoryRoot, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "schema_version": 1,
              "package_version": "{{PackageVersion}}",
              "generated_at_utc": "2026-05-12T00:00:00Z",
              "entries": [
                {
                  "package_id": "ForgeTrust.AppSurface.Cli",
                  "project_path": "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                  "decision": "publish",
                  "artifact_file_name": "ForgeTrust.AppSurface.Cli.{{PackageVersion}}.nupkg",
                  "sha512": "abc",
                  "is_tool": true,
                  "tool_command_name": "{{toolCommandName}}"
                }
              ]
            }
            """,
            Encoding.UTF8);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader().ReadAsync(manifestPath, CancellationToken.None));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackageArtifactManifestReader_AppliesVersionPolicy()
    {
        var stableManifestPath = CombineSafeChildPath(_repositoryRoot, "stable-manifest.json");
        await WriteManifestAsync(stableManifestPath, "0.1.0");
        var prereleaseManifestPath = CombineSafeChildPath(_repositoryRoot, "prerelease-manifest.json");
        await WriteManifestAsync(prereleaseManifestPath, PackageVersion);
        var buildMetadataManifestPath = CombineSafeChildPath(_repositoryRoot, "build-manifest.json");
        await WriteManifestAsync(buildMetadataManifestPath, "0.1.0+sha");

        var stableManifest = await new PackageArtifactManifestReader().ReadAsync(stableManifestPath, CancellationToken.None);
        var prereleaseManifest = await new PackageArtifactManifestReader().ReadAsync(prereleaseManifestPath, CancellationToken.None);
        var stablePolicyError = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader(PackageVersionPolicy.StableOnly).ReadAsync(prereleaseManifestPath, CancellationToken.None));
        var prereleasePolicyError = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader(PackageVersionPolicy.PrereleaseOnly).ReadAsync(stableManifestPath, CancellationToken.None));
        var buildMetadataError = await Assert.ThrowsAsync<PackageIndexException>(
            () => new PackageArtifactManifestReader().ReadAsync(buildMetadataManifestPath, CancellationToken.None));

        Assert.Equal("0.1.0", stableManifest.PackageVersion);
        Assert.Equal(PackageVersion, prereleaseManifest.PackageVersion);
        Assert.Contains("stable", stablePolicyError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prerelease", prereleasePolicyError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build metadata", buildMetadataError.Message, StringComparison.OrdinalIgnoreCase);

        static async Task WriteManifestAsync(string manifestPath, string packageVersion)
        {
            await File.WriteAllTextAsync(
                manifestPath,
                $$"""
                {
                  "schema_version": 1,
                  "package_version": "{{packageVersion}}",
                  "generated_at_utc": "2026-05-12T00:00:00Z",
                  "entries": [
                    {
                      "package_id": "ForgeTrust.AppSurface.Web",
                      "project_path": "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                      "decision": "publish",
                      "artifact_file_name": "ForgeTrust.AppSurface.Web.{{packageVersion}}.nupkg",
                      "sha512": "abc",
                      "is_tool": false
                    }
                  ]
                }
                """,
                Encoding.UTF8);
        }
    }

    [Fact]
    public async Task PackageArtifactWorkflow_RunsRestoreBuildPackAndWritesReport()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("packages/third-party-payloads.yml",
            """
            schema_version: 1
            audits:
              - id: workflow-fixture-proof
                package_id: ForgeTrust.AppSurface.Web
                applies_to:
                  - README.md
                evidence_kind: fixture_audit
                source_paths:
                  - packages/package-index.yml
                reason: Keeps the workflow test focused on command orchestration.
                reviewed_on: 2026-06-07
                source: test fixture
                revalidate_when: fixture changes.
            """);
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        var reportPath = CombineSafeChildPath(artifactDirectory, "package-validation-report.md");
        var artifactManifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        Directory.CreateDirectory(artifactDirectory);
        var stalePackage = CombineSafeChildPath(artifactDirectory, "stale.nupkg");
        var staleSymbolPackage = CombineSafeChildPath(artifactDirectory, "stale.snupkg");
        await File.WriteAllTextAsync(stalePackage, "old package", Encoding.UTF8);
        await File.WriteAllTextAsync(staleSymbolPackage, "old symbol package", Encoding.UTF8);
        var commandRunner = new RecordingCommandRunner();
        var coverageProofWorkflow = new RecordingCoverageCliConsumerProofWorkflow(succeeded: true);
        var workflow = new PackageArtifactWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    "ForgeTrust.AppSurface.Web")
            }),
            commandRunner,
            new PackageArtifactValidator(),
            coverageProofWorkflow);

        var report = await workflow.RunAsync(
            new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                artifactDirectory,
                reportPath,
                PackageVersion,
                artifactManifestPath,
                CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                CombineSafeChildPath(artifactDirectory, "coverage-proof.md"),
                "https://api.nuget.org/v3/index.json"));

        Assert.Single(report.Entries);
        Assert.Single(coverageProofWorkflow.Requests);
        Assert.Equal(["dotnet restore", "dotnet build", "dotnet pack"], commandRunner.OperationNames);
        var restoreCommand = Assert.Single(commandRunner.Requests, request => request.OperationName == "dotnet restore");
        Assert.Contains("/p:ContinuousIntegrationBuild=true", restoreCommand.Arguments);
        Assert.Contains("/p:TailwindRuntimeBinaryResolutionEnabled=true", restoreCommand.Arguments);
        var packCommand = Assert.Single(commandRunner.Requests, request => request.OperationName == "dotnet pack");
        Assert.Contains("--no-restore", packCommand.Arguments);
        Assert.Contains("--no-build", packCommand.Arguments);
        Assert.Contains($"/p:Version={PackageVersion}", packCommand.Arguments);
        Assert.Contains($"/p:PackageVersion={PackageVersion}", packCommand.Arguments);
        Assert.Contains("/p:ContinuousIntegrationBuild=true", packCommand.Arguments);
        Assert.Contains("/p:TailwindRuntimeBinaryResolutionEnabled=true", packCommand.Arguments);
        var buildCommand = Assert.Single(commandRunner.Requests, request => request.OperationName == "dotnet build");
        Assert.Contains($"/p:Version={PackageVersion}", buildCommand.Arguments);
        Assert.Contains($"/p:PackageVersion={PackageVersion}", buildCommand.Arguments);
        Assert.Contains("/p:ContinuousIntegrationBuild=true", buildCommand.Arguments);
        Assert.Contains("/p:TailwindRuntimeBinaryResolutionEnabled=true", buildCommand.Arguments);
        Assert.False(File.Exists(stalePackage));
        Assert.False(File.Exists(staleSymbolPackage));
        Assert.True(File.Exists(reportPath), $"Expected report at {reportPath}.");
        Assert.Contains("Coverage CLI consumer proof", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
        Assert.True(File.Exists(artifactManifestPath), $"Expected artifact manifest at {artifactManifestPath}.");
    }

    [Fact]
    public async Task PackageArtifactWorkflow_DoesNotWriteArtifactManifestWhenCoverageProofFails()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first.
                includes: Base web hosting.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("packages/third-party-payloads.yml",
            """
            schema_version: 1
            audits:
              - id: workflow-fixture-proof
                package_id: ForgeTrust.AppSurface.Web
                applies_to:
                  - README.md
                evidence_kind: fixture_audit
                source_paths:
                  - packages/package-index.yml
                reason: Keeps the workflow test focused on command orchestration.
                reviewed_on: 2026-06-07
                source: test fixture
                revalidate_when: fixture changes.
            """);
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        var reportPath = CombineSafeChildPath(artifactDirectory, "package-validation-report.md");
        var artifactManifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllTextAsync(artifactManifestPath, "stale manifest", Encoding.UTF8);
        var workflow = new PackageArtifactWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    "ForgeTrust.AppSurface.Web")
            }),
            new RecordingCommandRunner(),
            new PackageArtifactValidator(),
            new RecordingCoverageCliConsumerProofWorkflow(succeeded: false));

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PackageArtifactRequest(
                    _repositoryRoot,
                    ManifestPath,
                    artifactDirectory,
                    reportPath,
                    PackageVersion,
                    artifactManifestPath,
                    CombineSafeChildPath(artifactDirectory, "coverage-proof"),
                    CombineSafeChildPath(artifactDirectory, "coverage-proof.md"),
                    "https://api.nuget.org/v3/index.json")));

        Assert.Contains("Coverage CLI consumer proof failed", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(reportPath), $"Expected failure report at {reportPath}.");
        Assert.Contains("First failure", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
        Assert.False(File.Exists(artifactManifestPath), "A failed proof must not leave a publish-ready manifest.");
    }

    [Fact]
    public async Task TailwindRuntimeBinaryResolutionWorkflowPolicy_KeepsSolutionBuildsAndPackageValidationEnabled()
    {
        var repositoryRoot = GetRepositoryRoot();
        var buildWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/build.yml"));
        var codeQualityWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/code-quality.yml"));
        var vcsIgnoreParityWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/vcs-ignore-parity.yml"));
        var packageGateWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/package-gate.yml"));
        var packageArtifactsWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/package-artifacts.yml"));
        var prereleasePublishWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/nuget-prerelease-publish.yml"));
        var stablePublishWorkflow = await File.ReadAllTextAsync(CombineSafeChildPath(repositoryRoot, ".github/workflows/nuget-stable-publish.yml"));
        const string disabledRuntimeResolutionSetting =
            """(?im)(?:^\s*TailwindRuntimeBinaryResolutionEnabled:\s*(?:"false"|'false'|false)\s*$|(?:^|\s)(?:env\s+)?TailwindRuntimeBinaryResolutionEnabled=false\b|(?:/p:|-p:|/property:|-property:)(?:[^\s'"]*;)*TailwindRuntimeBinaryResolutionEnabled=false\b)""";

        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, buildWorkflow);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, codeQualityWorkflow);
        Assert.Matches(disabledRuntimeResolutionSetting, vcsIgnoreParityWorkflow);
        Assert.Contains("ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj", vcsIgnoreParityWorkflow, StringComparison.Ordinal);
        Assert.Contains("/p:TailwindRuntimeBinaryResolutionEnabled=true", packageGateWorkflow, StringComparison.Ordinal);
        Assert.Contains("TailwindRuntimeBinaryResolutionEnabled: \"true\"", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload package validation diagnostics", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ always() }}", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof.md", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/NuGet.tool.config", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/consumer/NuGet.config", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/consumer/TestResults/**", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/logs/**", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage-cli-consumer-proof/**", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload package validation diagnostics", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload package validation diagnostics", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("appsurface-prerelease-validation-diagnostics", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("appsurface-stable-validation-diagnostics", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/NuGet.tool.config", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/NuGet.tool.config", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/consumer/NuGet.config", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/consumer/NuGet.config", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/consumer/TestResults/**", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/consumer/TestResults/**", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/logs/**", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("coverage-cli-consumer-proof/logs/**", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage-cli-consumer-proof/**", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage-cli-consumer-proof/**", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload package artifacts", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload validated package artifacts", prereleasePublishWorkflow, StringComparison.Ordinal);
        Assert.Contains("Upload validated package artifacts", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, packageGateWorkflow);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, packageArtifactsWorkflow);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, stablePublishWorkflow);
        Assert.DoesNotContain("cache: true", stablePublishWorkflow, StringComparison.Ordinal);
        Assert.Matches(disabledRuntimeResolutionSetting, "-p:TailwindRuntimeBinaryResolutionEnabled=false");
        Assert.Matches(disabledRuntimeResolutionSetting, "/property:TailwindRuntimeBinaryResolutionEnabled=false");
        Assert.Matches(disabledRuntimeResolutionSetting, "-property:TailwindRuntimeBinaryResolutionEnabled=false");
        Assert.Matches(disabledRuntimeResolutionSetting, "/p:Configuration=Debug;TailwindRuntimeBinaryResolutionEnabled=false");
        Assert.Matches(disabledRuntimeResolutionSetting, "-property:Configuration=Debug;TailwindRuntimeBinaryResolutionEnabled=false");
        Assert.Matches(disabledRuntimeResolutionSetting, "TailwindRuntimeBinaryResolutionEnabled=false dotnet build");
        Assert.Matches(disabledRuntimeResolutionSetting, "env TailwindRuntimeBinaryResolutionEnabled=false dotnet build");
    }

    [Fact]
    public async Task PackageArtifactWorkflow_ThrowsWhenRequestPathsAreInvalid()
    {
        var workflow = new PackageArtifactWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)),
            new RecordingCommandRunner(),
            new PackageArtifactValidator(),
            new RecordingCoverageCliConsumerProofWorkflow(succeeded: true));
        var missingRepository = CombineSafeChildPath(_repositoryRoot, "missing");
        var missingRepositoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                missingRepository,
                CombineSafeChildPath(missingRepository, "packages/package-index.yml"),
                CombineSafeChildPath(missingRepository, "artifacts"),
                CombineSafeChildPath(missingRepository, "report.md"),
                PackageVersion,
                CombineSafeChildPath(missingRepository, "manifest.json"),
                CombineSafeChildPath(missingRepository, "proof"),
                CombineSafeChildPath(missingRepository, "proof.md"),
                "https://api.nuget.org/v3/index.json")));

        var missingManifestError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                CombineSafeChildPath(_repositoryRoot, "artifacts"),
                CombineSafeChildPath(_repositoryRoot, "report.md"),
                PackageVersion,
                CombineSafeChildPath(_repositoryRoot, "manifest.json"),
                CombineSafeChildPath(_repositoryRoot, "proof"),
                CombineSafeChildPath(_repositoryRoot, "proof.md"),
                "https://api.nuget.org/v3/index.json")));

        await WriteFileAsync("packages/package-index.yml", "packages: []");
        var missingArtifactPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                " ",
                CombineSafeChildPath(_repositoryRoot, "report.md"),
                PackageVersion,
                CombineSafeChildPath(_repositoryRoot, "manifest.json"),
                CombineSafeChildPath(_repositoryRoot, "proof"),
                CombineSafeChildPath(_repositoryRoot, "proof.md"),
                "https://api.nuget.org/v3/index.json")));
        var missingReportPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                CombineSafeChildPath(_repositoryRoot, "artifacts"),
                "",
                PackageVersion,
                CombineSafeChildPath(_repositoryRoot, "manifest.json"),
                CombineSafeChildPath(_repositoryRoot, "proof"),
                CombineSafeChildPath(_repositoryRoot, "proof.md"),
                "https://api.nuget.org/v3/index.json")));
        var missingArtifactManifestPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                CombineSafeChildPath(_repositoryRoot, "artifacts"),
                CombineSafeChildPath(_repositoryRoot, "report.md"),
                PackageVersion,
                "",
                CombineSafeChildPath(_repositoryRoot, "proof"),
                CombineSafeChildPath(_repositoryRoot, "proof.md"),
                "https://api.nuget.org/v3/index.json")));
        var missingCoverageProofWorkDirectoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                CombineSafeChildPath(_repositoryRoot, "artifacts"),
                CombineSafeChildPath(_repositoryRoot, "report.md"),
                PackageVersion,
                CombineSafeChildPath(_repositoryRoot, "manifest.json"),
                "",
                CombineSafeChildPath(_repositoryRoot, "proof.md"),
                "https://api.nuget.org/v3/index.json")));
        var missingCoverageProofReportPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                CombineSafeChildPath(_repositoryRoot, "artifacts"),
                CombineSafeChildPath(_repositoryRoot, "report.md"),
                PackageVersion,
                CombineSafeChildPath(_repositoryRoot, "manifest.json"),
                CombineSafeChildPath(_repositoryRoot, "proof"),
                "",
                "https://api.nuget.org/v3/index.json")));
        var missingSourceError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                CombineSafeChildPath(_repositoryRoot, "artifacts"),
                CombineSafeChildPath(_repositoryRoot, "report.md"),
                PackageVersion,
                CombineSafeChildPath(_repositoryRoot, "manifest.json"),
                CombineSafeChildPath(_repositoryRoot, "proof"),
                CombineSafeChildPath(_repositoryRoot, "proof.md"),
                "")));

        Assert.Contains("Repository root", missingRepositoryError.Message, StringComparison.Ordinal);
        Assert.Contains("Manifest", missingManifestError.Message, StringComparison.Ordinal);
        Assert.Contains("output path", missingArtifactPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report path", missingReportPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest path", missingArtifactManifestPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("work directory", missingCoverageProofWorkDirectoryError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proof report path", missingCoverageProofReportPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source", missingSourceError.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task CliWrapCommandRunner_ReturnsResultWhenProcessCannotStart()
    {
        var result = await new CliWrapCommandRunner().RunAsync(
            new ExternalCommandRequest(
                "definitely-not-a-real-package-index-command",
                [],
                _repositoryRoot,
                "missing command",
                "starting missing command",
                30_000),
            CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("missing command failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("definitely-not-a-real-package-index-command", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagePrereleasePublishWorkflow_PushesArtifactsInManifestOrderAndWritesLedger()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                product_family: appsurface
                classification: support
                publish_decision: support_publish
                order: 10
                note: Core dependency.
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install this first.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                expected_dependency_package_ids:
                  - ForgeTrust.AppSurface.Core
            """);
        await WriteFileAsync("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var corePackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        var webPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(corePackagePath, "core", Encoding.UTF8);
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry(
                        "ForgeTrust.AppSurface.Core",
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        PackagePublishDecision.SupportPublish,
                        [],
                        corePackagePath),
                    new PackageArtifactValidationReportEntry(
                        "ForgeTrust.AppSurface.Web",
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        PackagePublishDecision.Publish,
                        ["ForgeTrust.AppSurface.Core"],
                        webPackagePath)
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(0, "pushed", string.Empty),
            new ExternalCommandResult(0, "Package already exists.", string.Empty)
        ]);
        var workflow = new PackagePrereleasePublishWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"] = CreateMetadata(
                    "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                    "ForgeTrust.AppSurface.Core"),
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    "ForgeTrust.AppSurface.Web",
                    projectReferences: [CombineSafeChildPath(_repositoryRoot, "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj")])
            }),
            new PackageArtifactManifestReader(),
            commandRunner,
            new PackagePublishLedgerRenderer());
        var publishLogPath = CombineSafeChildPath(artifactDirectory, "publish.md");
        Environment.SetEnvironmentVariable("PACKAGE_INDEX_TEST_NUGET_API_KEY", "secret");

        try
        {
            var ledger = await workflow.RunAsync(
                new PackagePrereleasePublishRequest(
                    _repositoryRoot,
                    ManifestPath,
                    artifactDirectory,
                    manifestPath,
                    publishLogPath,
                    "https://api.nuget.org/v3/index.json",
                    "PACKAGE_INDEX_TEST_NUGET_API_KEY"),
                CancellationToken.None);

            Assert.Equal([PackagePublishStatus.Pushed, PackagePublishStatus.DuplicateReported], ledger.Entries.Select(entry => entry.Status).ToArray());
            Assert.EndsWith($"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg", commandRunner.Requests[0].Arguments[2], StringComparison.Ordinal);
            Assert.EndsWith($"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg", commandRunner.Requests[1].Arguments[2], StringComparison.Ordinal);
            Assert.All(commandRunner.Requests, request => Assert.Contains("--skip-duplicate", request.Arguments));
            Assert.Contains("duplicate-reported", await File.ReadAllTextAsync(publishLogPath), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PACKAGE_INDEX_TEST_NUGET_API_KEY", null);
        }
    }

    [Fact]
    public async Task PackagePrereleasePublishWorkflow_StopsAfterFirstPublishFailure()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Core.
                includes: Core.
                does_not_include: Web.
                start_here_path: ForgeTrust.AppSurface.Core/README.md
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "<Project />");
        await WriteFileAsync("ForgeTrust.AppSurface.Core/README.md", "# Core");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var corePackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        var webPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(corePackagePath, "core", Encoding.UTF8);
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", PackagePublishDecision.Publish, [], corePackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], webPackagePath)
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(1, string.Empty, "nuget outage")
        ]);
        var workflow = new PackagePrereleasePublishWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"] = CreateMetadata("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "ForgeTrust.AppSurface.Core"),
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web")
            }),
            new PackageArtifactManifestReader(),
            commandRunner,
            new PackagePublishLedgerRenderer());
        Environment.SetEnvironmentVariable("PACKAGE_INDEX_TEST_NUGET_API_KEY", "secret");

        try
        {
            var ledger = await workflow.RunAsync(
                new PackagePrereleasePublishRequest(
                    _repositoryRoot,
                    ManifestPath,
                    artifactDirectory,
                    manifestPath,
                    CombineSafeChildPath(artifactDirectory, "publish.md"),
                    "https://api.nuget.org/v3/index.json",
                    "PACKAGE_INDEX_TEST_NUGET_API_KEY"),
                CancellationToken.None);

            Assert.Equal([PackagePublishStatus.Failed, PackagePublishStatus.SkippedAfterFailure], ledger.Entries.Select(entry => entry.Status).ToArray());
            Assert.Single(commandRunner.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PACKAGE_INDEX_TEST_NUGET_API_KEY", null);
        }
    }

    [Fact]
    public async Task PackagePrereleasePublishWorkflow_RedactsSecretsAndPersistsLedgerAfterEachAttempt()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Core.
                includes: Core.
                does_not_include: Web.
                start_here_path: ForgeTrust.AppSurface.Core/README.md
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "<Project />");
        await WriteFileAsync("ForgeTrust.AppSurface.Core/README.md", "# Core");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var corePackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        var webPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(corePackagePath, "core", Encoding.UTF8);
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", PackagePublishDecision.Publish, [], corePackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], webPackagePath)
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var publishLogPath = CombineSafeChildPath(artifactDirectory, "publish.md");
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(0, "api-key: super-secret-token", "pushed super-secret-token"),
            new InvalidOperationException("runner crashed")
        ]);
        var workflow = new PackagePrereleasePublishWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"] = CreateMetadata("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "ForgeTrust.AppSurface.Core"),
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web")
            }),
            new PackageArtifactManifestReader(),
            commandRunner,
            new PackagePublishLedgerRenderer());
        Environment.SetEnvironmentVariable("PACKAGE_INDEX_TEST_NUGET_API_KEY", "super-secret-token");

        try
        {
            var ledger = await workflow.RunAsync(
                new PackagePrereleasePublishRequest(
                    _repositoryRoot,
                    ManifestPath,
                    artifactDirectory,
                    manifestPath,
                    publishLogPath,
                    "https://api.nuget.org/v3/index.json",
                    "PACKAGE_INDEX_TEST_NUGET_API_KEY"),
                CancellationToken.None);

            Assert.Equal([PackagePublishStatus.Pushed, PackagePublishStatus.Failed], ledger.Entries.Select(entry => entry.Status).ToArray());
            var ledgerMarkdown = await File.ReadAllTextAsync(publishLogPath);
            Assert.Contains("ForgeTrust.AppSurface.Core", ledgerMarkdown, StringComparison.Ordinal);
            Assert.Contains("runner crashed", ledgerMarkdown, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-token", ledgerMarkdown, StringComparison.Ordinal);
            Assert.Contains("[redacted]", ledgerMarkdown, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PACKAGE_INDEX_TEST_NUGET_API_KEY", null);
        }
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_RestoresPublicPackagesWithRetryAndIsolatedConfig()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj
                product_family: appsurface
                classification: support
                publish_decision: support_publish
                order: 10
                note: Core dependency.
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 30
                use_when: Config.
                includes: Config.
                does_not_include: Extras.
                start_here_path: Config/ForgeTrust.AppSurface.Config/README.md
            """);
        await WriteFileAsync("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj", "<Project />");
        await WriteFileAsync("Config/ForgeTrust.AppSurface.Config/README.md", "# Config");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var packagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(packagePath, "web", Encoding.UTF8);
        var configPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Config.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(configPackagePath, "config", Encoding.UTF8);
        var supportPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(supportPackagePath, "core", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", PackagePublishDecision.SupportPublish, [], supportPackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], packagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Config", "Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj", PackagePublishDecision.Publish, [], configPackagePath)
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(1, string.Empty, "not indexed yet"),
            new ExternalCommandResult(0, "restored", string.Empty)
        ]);
        var delays = new List<TimeSpan>();
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"] = CreateMetadata("ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj", "ForgeTrust.AppSurface.Core"),
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web"),
                ["Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj"] = CreateMetadata("Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj", "ForgeTrust.AppSurface.Config")
            }),
            commandRunner,
            new PackageSmokeInstallReportRenderer(),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var workDirectory = CombineSafeChildPath(_repositoryRoot, "smoke");

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                CombineSafeChildPath(workDirectory, "smoke.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        Assert.Equal(["ForgeTrust.AppSurface.Web", "ForgeTrust.AppSurface.Config"], report.Entries.Select(entry => entry.PackageId).ToArray());
        Assert.All(report.Entries, entry => Assert.Equal(PackageSmokeInstallStatus.Restored, entry.Status));
        Assert.Equal(2, commandRunner.Requests.Count);
        Assert.Single(delays);
        Assert.True(File.Exists(CombineSafeChildPath(workDirectory, "NuGet.config")));
        Assert.Contains("<clear />", await File.ReadAllTextAsync(CombineSafeChildPath(workDirectory, "NuGet.config")), StringComparison.Ordinal);
        var smokeProject = await File.ReadAllTextAsync(CombineSafeChildPath(workDirectory, "package-restore/Smoke.csproj"));
        Assert.Contains("Include=\"ForgeTrust.AppSurface.Web\"", smokeProject, StringComparison.Ordinal);
        Assert.Contains("Include=\"ForgeTrust.AppSurface.Config\"", smokeProject, StringComparison.Ordinal);
        Assert.Contains("NUGET_PACKAGES", commandRunner.Requests[0].Environment!.Keys);
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_MarksAllPackagesFailedWhenAggregateRestoreFails()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Config.
                includes: Config.
                does_not_include: Extras.
                start_here_path: Config/ForgeTrust.AppSurface.Config/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj", "<Project />");
        await WriteFileAsync("Config/ForgeTrust.AppSurface.Config/README.md", "# Config");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var webPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var configPackagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Config.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(configPackagePath, "config", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], webPackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Config", "Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj", PackagePublishDecision.Publish, [], configPackagePath)
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var restoreFailure = new ExternalCommandResult(1, string.Empty, "restore failed");
        var commandRunner = new RecordingExternalCommandRunner([
            restoreFailure,
            restoreFailure,
            restoreFailure,
            restoreFailure,
            restoreFailure
        ]);
        var delays = new List<TimeSpan>();
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web"),
                ["Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj"] = CreateMetadata("Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj", "ForgeTrust.AppSurface.Config")
            }),
            commandRunner,
            new PackageSmokeInstallReportRenderer(),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var workDirectory = CombineSafeChildPath(_repositoryRoot, "smoke");

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                CombineSafeChildPath(workDirectory, "report.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        Assert.Equal(5, commandRunner.Requests.Count);
        Assert.Equal(4, delays.Count);
        Assert.All(report.Entries, entry =>
        {
            Assert.Equal(PackageSmokeInstallStatus.Failed, entry.Status);
            Assert.Contains("restore failed", entry.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_RunsToolSmokeWhenAggregateRestoreFails()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Tool.
                includes: CLI command.
                does_not_include: Runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# CLI");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var webPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Web"));
        var cliPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        await File.WriteAllTextAsync(cliPackagePath, "cli", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], webPackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Cli", "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", PackagePublishDecision.Publish, [], cliPackagePath, IsTool: true, ToolCommandName: "appsurface")
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var restoreFailure = new ExternalCommandResult(1, string.Empty, "restore failed");
        var commandRunner = new RecordingExternalCommandRunner([
            restoreFailure,
            restoreFailure,
            restoreFailure,
            restoreFailure,
            restoreFailure,
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, "USAGE\nappsurface [command]", string.Empty),
            new ExternalCommandResult(0, PackageVersion, string.Empty)
        ]);
        var delays = new List<TimeSpan>();
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web"),
                ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "ForgeTrust.AppSurface.Cli", isTool: true)
            }),
            commandRunner,
            new PackageSmokeInstallReportRenderer(),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        var workDirectory = CombineSafeChildPath(_repositoryRoot, "smoke");

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                CombineSafeChildPath(workDirectory, "smoke.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        var packageEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Web");
        Assert.Equal(PackageSmokeInstallStatus.Failed, packageEntry.Status);
        Assert.Contains("restore failed", packageEntry.Output, StringComparison.Ordinal);
        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Restored, toolEntry.Status);
        Assert.Contains("installed", toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(4, delays.Count);
        Assert.Equal(
            ["dotnet restore", "dotnet restore", "dotnet restore", "dotnet restore", "dotnet restore", "dotnet tool install", "dotnet tool run", "dotnet tool run"],
            commandRunner.Requests.Select(request => request.OperationName).ToArray());
    }

    [Theory]
    [InlineData("USAGE\nappsurface [command]")]
    [InlineData("USAGE\nappsurface.exe [command]")]
    public async Task PackageSmokeInstallWorkflow_InstallsToolAndVerifiesVersionCommand(string helpOutput)
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Tool.
                includes: CLI command.
                does_not_include: Runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# CLI");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var webPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Web"));
        var cliPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        await File.WriteAllTextAsync(cliPackagePath, "cli", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], webPackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Cli", "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", PackagePublishDecision.Publish, [], cliPackagePath, IsTool: true, ToolCommandName: "appsurface")
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(0, "restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, helpOutput, string.Empty),
            new ExternalCommandResult(0, PackageVersion, string.Empty)
        ]);
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web"),
                ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "ForgeTrust.AppSurface.Cli", isTool: true)
            }),
            commandRunner,
            new PackageSmokeInstallReportRenderer(),
            (_, _) => Task.CompletedTask);
        var workDirectory = CombineSafeChildPath(_repositoryRoot, "smoke");
        var stalePackageRestoreFile = CombineSafeChildPath(
            CombineSafeChildPath(workDirectory, "package-restore"),
            "stale.txt");
        var staleToolWorkFile = CombineSafeChildPath(
            CombineSafeChildPath(workDirectory, "ForgeTrust.AppSurface.Cli"),
            "stale.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(stalePackageRestoreFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staleToolWorkFile)!);
        await File.WriteAllTextAsync(stalePackageRestoreFile, "stale", Encoding.UTF8);
        await File.WriteAllTextAsync(staleToolWorkFile, "stale", Encoding.UTF8);

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                CombineSafeChildPath(workDirectory, "smoke.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Restored, toolEntry.Status);
        Assert.False(File.Exists(stalePackageRestoreFile));
        Assert.False(File.Exists(staleToolWorkFile));
        var toolRunRequests = commandRunner.Requests.Where(request => request.OperationName == "dotnet tool run").ToArray();
        Assert.Collection(
            toolRunRequests,
            helpRequest =>
            {
                Assert.Contains("appsurface", Path.GetFileName(helpRequest.FileName), StringComparison.OrdinalIgnoreCase);
                Assert.Equal(["--help"], helpRequest.Arguments);
            },
            versionRequest =>
            {
                Assert.Contains("appsurface", Path.GetFileName(versionRequest.FileName), StringComparison.OrdinalIgnoreCase);
                Assert.Equal(["--version"], versionRequest.Arguments);
            });
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenHelpOutputDoesNotNameCommand()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Tool.
                includes: CLI command.
                does_not_include: Runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# CLI");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var webPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Web"));
        var cliPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        await File.WriteAllTextAsync(cliPackagePath, "cli", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", PackagePublishDecision.Publish, [], webPackagePath),
                    new PackageArtifactValidationReportEntry("ForgeTrust.AppSurface.Cli", "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", PackagePublishDecision.Publish, [], cliPackagePath, IsTool: true, ToolCommandName: "appsurface")
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var commandRunner = new RecordingExternalCommandRunner([
            new ExternalCommandResult(0, "restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, "USAGE\ndotnet ForgeTrust.AppSurface.Cli.dll [command]", string.Empty)
        ]);
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web"),
                ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "ForgeTrust.AppSurface.Cli", isTool: true)
            }),
            commandRunner,
            new PackageSmokeInstallReportRenderer(),
            (_, _) => Task.CompletedTask);

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                CombineSafeChildPath(_repositoryRoot, "smoke"),
                CombineSafeChildPath(CombineSafeChildPath(_repositoryRoot, "smoke"), "smoke.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Contains("did not include the command name", toolEntry.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenInstallFails()
    {
        var installFailure = new ExternalCommandResult(1, string.Empty, "install failed");
        var (report, commandRunner) = await RunToolOnlySmokeWorkflowAsync(
            new ExternalCommandResult(0, "web restored", string.Empty),
            installFailure,
            installFailure,
            installFailure,
            installFailure,
            installFailure);

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Equal(1, toolEntry.ExitCode);
        Assert.Contains("install failed", toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(6, commandRunner.Requests.Count);
        Assert.Equal("dotnet restore", commandRunner.Requests[0].OperationName);
        Assert.All(commandRunner.Requests.Skip(1), request => Assert.Equal("dotnet tool install", request.OperationName));
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenHelpCommandFails()
    {
        var (report, commandRunner) = await RunToolOnlySmokeWorkflowAsync(
            new ExternalCommandResult(0, "web restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(2, string.Empty, "help failed"));

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Equal(2, toolEntry.ExitCode);
        Assert.Contains("installed", toolEntry.Output, StringComparison.Ordinal);
        Assert.Contains("help failed", toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(["dotnet restore", "dotnet tool install", "dotnet tool run"], commandRunner.Requests.Select(request => request.OperationName).ToArray());
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenHelpCommandWritesStderr()
    {
        var (report, commandRunner) = await RunToolOnlySmokeWorkflowAsync(
            new ExternalCommandResult(0, "web restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, "USAGE\nappsurface [command]", "lifecycle noise"));

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Equal(1, toolEntry.ExitCode);
        Assert.Contains("completed but also wrote to stderr", toolEntry.Output, StringComparison.Ordinal);
        Assert.Contains("lifecycle noise", toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(["dotnet restore", "dotnet tool install", "dotnet tool run"], commandRunner.Requests.Select(request => request.OperationName).ToArray());
    }

    [Theory]
    [InlineData("v0.0.0-ci.42")]
    [InlineData("0.0.0")]
    [InlineData("0.0.0-ci.42+abc123")]
    [InlineData("USAGE\nappsurface [command]")]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenVersionOutputDoesNotMatchPackageVersion(string versionOutput)
    {
        var (report, commandRunner) = await RunToolOnlySmokeWorkflowAsync(
            new ExternalCommandResult(0, "web restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, "USAGE\nappsurface [command]", string.Empty),
            new ExternalCommandResult(0, versionOutput, string.Empty));

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Equal(1, toolEntry.ExitCode);
        Assert.Contains($"expected installed tool version '{PackageVersion}'", toolEntry.Output, StringComparison.Ordinal);
        Assert.Contains(versionOutput.Trim(), toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(["dotnet restore", "dotnet tool install", "dotnet tool run", "dotnet tool run"], commandRunner.Requests.Select(request => request.OperationName).ToArray());
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenVersionCommandFails()
    {
        var (report, commandRunner) = await RunToolOnlySmokeWorkflowAsync(
            new ExternalCommandResult(0, "web restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, "USAGE\nappsurface [command]", string.Empty),
            new ExternalCommandResult(2, string.Empty, "version failed"));

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Equal(2, toolEntry.ExitCode);
        Assert.Contains("version failed", toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(["dotnet restore", "dotnet tool install", "dotnet tool run", "dotnet tool run"], commandRunner.Requests.Select(request => request.OperationName).ToArray());
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_FailsToolSmokeWhenVersionCommandWritesStderr()
    {
        var (report, commandRunner) = await RunToolOnlySmokeWorkflowAsync(
            new ExternalCommandResult(0, "web restored", string.Empty),
            new ExternalCommandResult(0, "installed", string.Empty),
            new ExternalCommandResult(0, "USAGE\nappsurface [command]", string.Empty),
            new ExternalCommandResult(0, PackageVersion, "lifecycle noise"));

        var toolEntry = Assert.Single(report.Entries, entry => entry.PackageId == "ForgeTrust.AppSurface.Cli");
        Assert.Equal(PackageSmokeInstallStatus.Failed, toolEntry.Status);
        Assert.Equal(1, toolEntry.ExitCode);
        Assert.Contains("also wrote to stderr", toolEntry.Output, StringComparison.Ordinal);
        Assert.Contains("lifecycle noise", toolEntry.Output, StringComparison.Ordinal);
        Assert.Equal(["dotnet restore", "dotnet tool install", "dotnet tool run", "dotnet tool run"], commandRunner.Requests.Select(request => request.OperationName).ToArray());
    }

    [Fact]
    public void PackageSmokeInstallWorkflow_ResolveToolShimPathUsesToolDirectory()
    {
        var toolPath = CombineSafeChildPath(_repositoryRoot, "tools");
        var expectedShimName = OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface";

        var shimPath = PackageSmokeInstallWorkflow.ResolveToolShimPath(toolPath, "appsurface");

        Assert.Equal(CombineSafeChildPath(toolPath, expectedShimName), shimPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../appsurface")]
    [InlineData(".")]
    [InlineData("app surface")]
    public void PackageSmokeInstallWorkflow_ResolveToolShimPathRejectsInvalidCommandName(string commandName)
    {
        var error = Assert.Throws<ArgumentException>(
            () => PackageSmokeInstallWorkflow.ResolveToolShimPath(CombineSafeChildPath(_repositoryRoot, "tools"), commandName));

        Assert.Equal("commandName", error.ParamName);
    }

    [Fact]
    public void CombineSafeChildPath_RejectsTraversalOutsideDirectory()
    {
        var error = Assert.Throws<ArgumentException>(
            () => CombineSafeChildPath(_repositoryRoot, "../outside.txt"));

        Assert.Equal("childPath", error.ParamName);
        Assert.Contains("escapes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageSmokeInstallWorkflow_RejectsManifestThatDoesNotMatchPackagePlan()
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var packagePath = CombineSafeChildPath(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(packagePath, "web", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry(
                        "ForgeTrust.AppSurface.Web\"><PackageReference Include=\"Bad",
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        PackagePublishDecision.Publish,
                        [],
                        packagePath)
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "ForgeTrust.AppSurface.Web")
            }),
            new RecordingExternalCommandRunner([]),
            new PackageSmokeInstallReportRenderer(),
            (_, _) => Task.CompletedTask);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(
                new PackageSmokeInstallRequest(
                    _repositoryRoot,
                    ManifestPath,
                    manifestPath,
                    CombineSafeChildPath(_repositoryRoot, "smoke"),
                    CombineSafeChildPath(_repositoryRoot, "smoke/report.md"),
                    "https://api.nuget.org/v3/index.json"),
                CancellationToken.None));

        Assert.Contains("does not match package plan", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageArtifactManifestPlanValidator_RejectsToolCommandMismatch()
    {
        var plan = new PackagePublishPlan([
            new PackagePublishPlanEntry(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                PackagePublishDecision.Publish,
                [],
                IsTool: true,
                ToolCommandName: "appsurface")
        ]);
        var manifest = new PackageArtifactManifest(
            1,
            PackageVersion,
            DateTimeOffset.UnixEpoch,
            [
                new PackageArtifactManifestEntry(
                    "ForgeTrust.AppSurface.Cli",
                    "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                    "publish",
                    $"ForgeTrust.AppSurface.Cli.{PackageVersion}.nupkg",
                    "abc",
                    IsTool: true,
                    ToolCommandName: "wrong")
            ]);

        var error = Assert.Throws<PackageIndexException>(
            () => PackageArtifactManifestPlanValidator.Validate(plan, manifest, _repositoryRoot));

        Assert.Contains("does not match package plan", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(PackageSmokeInstallReport Report, RecordingExternalCommandRunner CommandRunner)> RunToolOnlySmokeWorkflowAsync(
        params object[] commandResults)
    {
        await WriteFileAsync("packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Web.
                includes: Web.
                does_not_include: Extras.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Tool.
                includes: CLI command.
                does_not_include: Runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# CLI");
        var artifactDirectory = CombineSafeChildPath(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var webPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Web"));
        var cliPackagePath = CombineSafeChildPath(artifactDirectory, CreatePackageFileName("ForgeTrust.AppSurface.Cli"));
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        await File.WriteAllTextAsync(cliPackagePath, "cli", Encoding.UTF8);
        var manifestPath = CombineSafeChildPath(artifactDirectory, "package-artifact-manifest.json");
        await new PackageArtifactManifestWriter().WriteAsync(
            new PackageArtifactValidationReport(
                PackageVersion,
                [
                    new PackageArtifactValidationReportEntry(
                        "ForgeTrust.AppSurface.Web",
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        PackagePublishDecision.Publish,
                        [],
                        webPackagePath),
                    new PackageArtifactValidationReportEntry(
                        "ForgeTrust.AppSurface.Cli",
                        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                        PackagePublishDecision.Publish,
                        [],
                        cliPackagePath,
                        IsTool: true,
                        ToolCommandName: "appsurface")
                ]),
            artifactDirectory,
            manifestPath,
            CancellationToken.None);
        var commandRunner = new RecordingExternalCommandRunner(commandResults);
        var workflow = new PackageSmokeInstallWorkflow(
            new PackageArtifactManifestReader(),
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    "ForgeTrust.AppSurface.Web"),
                ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                    "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                    "ForgeTrust.AppSurface.Cli",
                    isTool: true)
            }),
            commandRunner,
            new PackageSmokeInstallReportRenderer(),
            (_, _) => Task.CompletedTask);
        var workDirectory = CombineSafeChildPath(_repositoryRoot, "smoke");
        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                CombineSafeChildPath(workDirectory, "smoke.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        return (report, commandRunner);
    }

    private string ManifestPath => CombineSafeChildPath(_repositoryRoot, "packages/package-index.yml");

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
                "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                "ForgeTrust.AppSurface.Core",
                PackagePublishDecision.Publish,
                [],
                IsTool: false),
            new PackagePublishPlanEntry(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web",
                PackagePublishDecision.Publish,
                ["ForgeTrust.AppSurface.Core"],
                IsTool: false)
        ]);
    }

    private static void WritePackage(
        string artifactDirectory,
        string packageId,
        string packageVersion,
        IReadOnlyDictionary<string, string> dependencies,
        bool includeReadme = true,
        bool includeReadmeEntry = true,
        IReadOnlyDictionary<string, string>? assemblyEntries = null,
        string? description = null,
        bool includeNuspec = true,
        bool includeId = true,
        bool includeVersion = true,
        IReadOnlyList<string>? packageTypes = null,
        IReadOnlyDictionary<string, byte[]>? rawEntries = null,
        string? dependencyXml = null,
        IReadOnlyList<string>? toolCommandNames = null,
        string? projectUrl = RequiredPackageProjectUrl)
    {
        var packagePath = CombineSafeChildPath(artifactDirectory, $"{packageId}.{packageVersion}.nupkg");
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
                packageTypes,
                dependencyXml,
                projectUrl));
        }

        if (includeReadme && includeReadmeEntry)
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

        if (toolCommandNames is not null)
        {
            var settingsEntry = archive.CreateEntry("tools/net10.0/any/DotnetToolSettings.xml");
            using var settingsStream = settingsEntry.Open();
            using var settingsWriter = new StreamWriter(settingsStream, Encoding.UTF8);
            settingsWriter.Write(CreateDotNetToolSettings(toolCommandNames));
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
        IReadOnlyList<string>? packageTypes,
        string? dependencyXmlOverride,
        string? projectUrl)
    {
        var dependencyXml = string.Join(
            Environment.NewLine,
            dependencies.Select(pair => $"""        <dependency id="{pair.Key}" version="{pair.Value}" />"""));
        var dependenciesXml = dependencyXmlOverride ?? $$"""
                <dependencies>
                  <group targetFramework="net10.0">
            {{dependencyXml}}
                  </group>
                </dependencies>
            """;
        var readmeXml = includeReadme ? "    <readme>README.md</readme>" : string.Empty;
        var projectUrlXml = string.IsNullOrEmpty(projectUrl) ? string.Empty : $"    <projectUrl>{projectUrl}</projectUrl>";
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
                <description>{{description ?? $"{packageId} package for AppSurface application composition."}}</description>
                <license type="expression">MIT</license>
            {{projectUrlXml}}
                <repository type="git" url="https://github.com/forge-trust/AppSurface" />
                <tags>appsurface dotnet</tags>
            {{readmeXml}}
            {{packageTypesXml}}
            {{dependenciesXml}}
              </metadata>
            </package>
            """;
    }

    private static string CreateDotNetToolSettings(IReadOnlyList<string> toolCommandNames)
    {
        var commands = string.Join(
            Environment.NewLine,
            toolCommandNames.Select(commandName =>
                $"""      <Command Name="{System.Security.SecurityElement.Escape(commandName)}" EntryPoint="Tool.dll" Runner="dotnet" />"""));

        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <DotNetCliTool Version="1">
              <Commands>
            {{commands}}
              </Commands>
            </DotNetCliTool>
            """;
    }

    private static string CreatePackageFileName(string packageId)
    {
        var fileName = $"{packageId}.{PackageVersion}.nupkg";
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Package file name '{fileName}' must not contain path segments.");
        }

        return fileName;
    }

    private static PackageArtifactValidationReport CreateCliProofValidationReport(string cliArtifactPath)
    {
        return new PackageArtifactValidationReport(
            PackageVersion,
            [
                new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Cli",
                    "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                    PackagePublishDecision.Publish,
                    [],
                    cliArtifactPath,
                    IsTool: true,
                    ToolCommandName: "appsurface")
            ]);
    }

    private static PackagePublishPlan CreateCliPublishPlan()
    {
        return new PackagePublishPlan([
            new PackagePublishPlanEntry(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                PackagePublishDecision.Publish,
                [],
                IsTool: false)
        ]);
    }

    private static string CombineSafeChildPath(string directory, string childPath)
    {
        try
        {
            return TestPathUtils.PathUnder(directory, childPath);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"Child path '{childPath}' escapes '{directory}' or is not relative.", nameof(childPath), exception);
        }
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
        var fullPath = TestPathUtils.PathUnder(_repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = TestPathUtils.PathUnder(_repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
    }

    private static PackagePayloadInventory CreateReportGeneratorInventory(
        string? versionSourcePath = "Directory.Packages.props",
        string? versionSourceContains = """<PackageVersion Include="ReportGenerator" Version="5.5.10" />""")
    {
        return new PackagePayloadInventory
        {
            Notices =
            {
                new PackagePayloadNoticeRecord
                {
                    Id = "cli-reportgenerator-tool-payload",
                    PackageId = "ForgeTrust.AppSurface.Cli",
                    Component = "ReportGenerator",
                    Version = "5.5.10",
                    License = "Apache-2.0",
                    SourceUrl = "https://github.com/danielpalme/ReportGenerator",
                    PayloadPatterns = { "tools/**/reportgenerator/**" },
                    NoticePaths = { "THIRD-PARTY-NOTICES.md" },
                    Markers = { "ReportGenerator", "5.5.10", "Apache-2.0" },
                    SourcePaths = { "Directory.Packages.props" },
                    VersionSourcePath = versionSourcePath,
                    VersionSourceContains = versionSourceContains
                }
            }
        };
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(CombineSafeChildPath(current.FullName, "ForgeTrust.AppSurface.slnx")) &&
                Directory.Exists(CombineSafeChildPath(current.FullName, ".github/workflows")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
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
                    "ForgeTrust.AppSurface.Web",
                    packageVersion,
                    EmptyDependencies);
            }

            return Task.FromResult(new CommandRunResult(string.Empty, string.Empty));
        }
    }

    private sealed class RecordingExternalCommandRunner : IExternalCommandRunner
    {
        private readonly Queue<object> _results;

        public RecordingExternalCommandRunner(IEnumerable<object> results)
        {
            _results = new Queue<object>(results);
        }

        public List<ExternalCommandRequest> Requests { get; } = [];

        public Task<ExternalCommandResult> RunAsync(
            ExternalCommandRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_results.Count == 0)
            {
                return Task.FromResult(new ExternalCommandResult(0, string.Empty, string.Empty));
            }

            var result = _results.Dequeue();
            if (result is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult((ExternalCommandResult)result);
        }
    }

    private sealed class RecordingCoverageCliConsumerProofWorkflow : ICoverageCliConsumerProofWorkflow
    {
        private readonly bool _succeeded;

        public RecordingCoverageCliConsumerProofWorkflow(bool succeeded)
        {
            _succeeded = succeeded;
        }

        public List<CoverageCliConsumerProofRequest> Requests { get; } = [];

        public Task<CoverageCliConsumerProofReport> RunAsync(
            CoverageCliConsumerProofRequest request,
            PackageArtifactValidationReport validationReport,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var selectedArtifact = validationReport.Entries
                .Select(entry => new CoverageCliConsumerProofSelectedArtifact(
                    entry.PackageId,
                    entry.ProjectPath,
                    entry.ArtifactPath,
                    entry.ToolCommandName,
                    "test-sha512"))
                .FirstOrDefault();
            return Task.FromResult(new CoverageCliConsumerProofReport(
                request.PackageVersion,
                request.WorkDirectory,
                request.Source,
                selectedArtifact,
                CombineSafeChildPath(request.WorkDirectory, "NuGet.tool.config"),
                CombineSafeChildPath(request.WorkDirectory, "NuGet.fixture.config"),
                CombineSafeChildPath(request.WorkDirectory, "logs"),
                [],
                [],
                _succeeded ? string.Empty : "proof failed",
                "dotnet run -- verify-packages"));
        }
    }

    private sealed class CoverageProofRecordingCommandRunner : IExternalCommandRunner
    {
        private readonly string _packageVersion;
        private readonly bool _createFailingGateReports;
        private readonly bool _createCoverageRunArtifacts;
        private readonly bool _createCoverageMergeArtifacts;
        private readonly bool _createPassingGateReports;
        private readonly string? _failOperationName;
        private readonly string? _failTimeoutDescriptionContains;
        private readonly bool _intentionallyFailingGateExitsNonZero;

        public CoverageProofRecordingCommandRunner(
            string packageVersion,
            bool createFailingGateReports,
            bool createCoverageRunArtifacts = true,
            bool createCoverageMergeArtifacts = true,
            bool createPassingGateReports = true,
            string? failOperationName = null,
            string? failTimeoutDescriptionContains = null,
            bool intentionallyFailingGateExitsNonZero = true)
        {
            _packageVersion = packageVersion;
            _createFailingGateReports = createFailingGateReports;
            _createCoverageRunArtifacts = createCoverageRunArtifacts;
            _createCoverageMergeArtifacts = createCoverageMergeArtifacts;
            _createPassingGateReports = createPassingGateReports;
            _failOperationName = failOperationName;
            _failTimeoutDescriptionContains = failTimeoutDescriptionContains;
            _intentionallyFailingGateExitsNonZero = intentionallyFailingGateExitsNonZero;
        }

        public List<ExternalCommandRequest> Requests { get; } = [];

        public Task<ExternalCommandResult> RunAsync(
            ExternalCommandRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (string.Equals(request.OperationName, _failOperationName, StringComparison.Ordinal)
                && (_failTimeoutDescriptionContains is null
                    || request.TimeoutDescription.Contains(_failTimeoutDescriptionContains, StringComparison.Ordinal)))
            {
                return Task.FromResult(new ExternalCommandResult(2, string.Empty, $"{request.OperationName} failed"));
            }

            if (request.OperationName == "appsurface --version")
            {
                return Task.FromResult(new ExternalCommandResult(0, _packageVersion, string.Empty));
            }

            if (request.OperationName == "appsurface coverage run")
            {
                var outputDirectory = ReadOption(request.Arguments, "--output");
                if (_createCoverageRunArtifacts)
                {
                    CreateCoverageRunArtifacts(outputDirectory);
                }

                return Task.FromResult(new ExternalCommandResult(0, "coverage run passed", string.Empty));
            }

            if (request.OperationName == "appsurface coverage merge")
            {
                var outputDirectory = ReadOption(request.Arguments, "--output");
                if (_createCoverageMergeArtifacts)
                {
                    CreateCoverageMergeArtifacts(outputDirectory);
                }

                return Task.FromResult(new ExternalCommandResult(0, "coverage merge passed", string.Empty));
            }

            if (request.OperationName == "appsurface coverage gate")
            {
                var outputDirectory = ReadOption(request.Arguments, "--output");
                var isFailingGate = request.TimeoutDescription.Contains("intentionally failing", StringComparison.Ordinal);
                if ((isFailingGate && _createFailingGateReports)
                    || (!isFailingGate && _createPassingGateReports))
                {
                    CreateCoverageGateArtifacts(outputDirectory);
                }

                return Task.FromResult(isFailingGate
                    ? new ExternalCommandResult(
                        _intentionallyFailingGateExitsNonZero ? 1 : 0,
                        "ASCOV020 Coverage gate failed.",
                        string.Empty)
                    : new ExternalCommandResult(0, "Coverage gate passed.", string.Empty));
            }

            return Task.FromResult(new ExternalCommandResult(0, $"{request.OperationName} passed", string.Empty));
        }

        private static string ReadOption(IReadOnlyList<string> arguments, string option)
        {
            var index = arguments.ToList().IndexOf(option);
            if (index < 0 || index + 1 >= arguments.Count)
            {
                throw new InvalidOperationException($"Expected option '{option}'.");
            }

            return arguments[index + 1];
        }

        private static void CreateCoverageRunArtifacts(string outputDirectory)
        {
            var projectDirectory = CombineSafeChildPath(outputDirectory, "projects/Smoke.Tests-123");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "coverage.cobertura.xml"), "<coverage />", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "summary.txt"), "summary", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "timings.json"), "{}", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, ".appsurface-coverage-output"), "owned", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(projectDirectory, "dotnet-test.log"), "tests", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(projectDirectory, "coverage.cobertura.xml"), "<coverage />", Encoding.UTF8);
        }

        private static void CreateCoverageMergeArtifacts(string outputDirectory)
        {
            var inputDirectory = CombineSafeChildPath(outputDirectory, "reportgenerator-input/000001-Smoke.Tests");
            Directory.CreateDirectory(inputDirectory);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "coverage.cobertura.xml"), "<coverage />", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "summary.txt"), "summary", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "timings.json"), "{}", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(inputDirectory, "coverage.cobertura.xml"), "<coverage />", Encoding.UTF8);
        }

        private static void CreateCoverageGateArtifacts(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "coverage-gate.json"), "{}", Encoding.UTF8);
            File.WriteAllText(CombineSafeChildPath(outputDirectory, "coverage-gate.md"), "# Gate", Encoding.UTF8);
        }
    }
}
