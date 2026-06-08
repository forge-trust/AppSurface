using System.IO.Compression;
using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PackageArtifactValidationTests : IDisposable
{
    private const string PackageVersion = "0.0.0-ci.42";
    private const string RequiredPackageProjectUrl = "https://appsurface.dev";

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
        var coreProjectPath = Path.Combine(_repositoryRoot, "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core.csproj");
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
        var webProjectPath = Path.Combine(_repositoryRoot, "Web", "ForgeTrust.AppSurface.Web", "ForgeTrust.AppSurface.Web.csproj");
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
        var coreProjectPath = Path.Combine(_repositoryRoot, "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core.csproj");
        var openApiProjectPath = Path.Combine(_repositoryRoot, "Web", "ForgeTrust.AppSurface.Web.OpenApi", "ForgeTrust.AppSurface.Web.OpenApi.csproj");
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
                projectReferences: [Path.Combine(_repositoryRoot, "Web", "ForgeTrust.AppSurface.Web", "ForgeTrust.AppSurface.Web.csproj")],
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenThirdPartyLookingAssemblyIsUnclassified()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
    public void PackageArtifactValidator_AcceptsNoticeClassifiedPayloadAndReportsCoverage()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        Assert.Equal(1, entry.SuspiciousPayloadCount);
        Assert.Equal(1, entry.CoveredSuspiciousPayloadCount);
        Assert.Equal("cli-reportgenerator-tool-payload", payloadResult.RecordId);
        Assert.Equal("notice_enforced", payloadResult.Status);
        Assert.Equal("Directory.Packages.props", payloadResult.VersionSource);
        var markdown = PackageArtifactReportRenderer.RenderMarkdown(report);
        Assert.Contains("Suspicious payloads", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.Cli` |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 1/1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("## Redistributed payload coverage", markdown, StringComparison.Ordinal);
        Assert.Contains("`cli-reportgenerator-tool-payload`", markdown, StringComparison.Ordinal);
        Assert.Contains("`THIRD-PARTY-NOTICES.md`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenAuditOverlapsNoticeClassifiedPayload()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
    public void PackageArtifactValidator_ThrowsWhenInventoryPathEscapesRepository()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        inventory.Notices[0].SourcePaths.Add(Path.Combine(_repositoryRoot, "Directory.Packages.props"));

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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        Assert.Contains("packages/package-index.yml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageArtifactValidator_ThrowsWhenPackageEntriesCollideAfterNormalization()
    {
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
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
                    [])
            ]));

        Assert.Contains("`publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`support_publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`do_not_publish`", markdown, StringComparison.Ordinal);
        Assert.Contains("`999`", markdown, StringComparison.Ordinal);
        Assert.Contains("`ForgeTrust.AppSurface.Web`", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.Example` | `examples/Example.csproj` | `do_not_publish` | - | none |", markdown, StringComparison.Ordinal);
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        var reportPath = Path.Combine(artifactDirectory, "package-validation-report.md");
        var artifactManifestPath = Path.Combine(artifactDirectory, "package-artifact-manifest.json");
        Directory.CreateDirectory(artifactDirectory);
        var stalePackage = Path.Combine(artifactDirectory, "stale.nupkg");
        var staleSymbolPackage = Path.Combine(artifactDirectory, "stale.snupkg");
        await File.WriteAllTextAsync(stalePackage, "old package", Encoding.UTF8);
        await File.WriteAllTextAsync(staleSymbolPackage, "old symbol package", Encoding.UTF8);
        var commandRunner = new RecordingCommandRunner();
        var workflow = new PackageArtifactWorkflow(
            CreateResolver(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                    "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    "ForgeTrust.AppSurface.Web")
            }),
            commandRunner,
            new PackageArtifactValidator());

        var report = await workflow.RunAsync(
            new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                artifactDirectory,
                reportPath,
                PackageVersion,
                artifactManifestPath));

        Assert.Single(report.Entries);
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
        Assert.True(File.Exists(artifactManifestPath), $"Expected artifact manifest at {artifactManifestPath}.");
    }

    [Fact]
    public async Task TailwindRuntimeBinaryResolutionWorkflowPolicy_KeepsSolutionBuildsAndPackageValidationEnabled()
    {
        var repositoryRoot = GetRepositoryRoot();
        var buildWorkflow = await File.ReadAllTextAsync(Path.Join(repositoryRoot, ".github", "workflows", "build.yml"));
        var codeQualityWorkflow = await File.ReadAllTextAsync(Path.Join(repositoryRoot, ".github", "workflows", "code-quality.yml"));
        var vcsIgnoreParityWorkflow = await File.ReadAllTextAsync(Path.Join(repositoryRoot, ".github", "workflows", "vcs-ignore-parity.yml"));
        var packageGateWorkflow = await File.ReadAllTextAsync(Path.Join(repositoryRoot, ".github", "workflows", "package-gate.yml"));
        var packageArtifactsWorkflow = await File.ReadAllTextAsync(Path.Join(repositoryRoot, ".github", "workflows", "package-artifacts.yml"));
        const string disabledRuntimeResolutionSetting =
            """(?im)(?:^\s*TailwindRuntimeBinaryResolutionEnabled:\s*(?:"false"|'false'|false)\s*$|(?:^|\s)(?:env\s+)?TailwindRuntimeBinaryResolutionEnabled=false\b|(?:/p:|-p:|/property:|-property:)(?:[^\s'"]*;)*TailwindRuntimeBinaryResolutionEnabled=false\b)""";

        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, buildWorkflow);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, codeQualityWorkflow);
        Assert.Matches(disabledRuntimeResolutionSetting, vcsIgnoreParityWorkflow);
        Assert.Contains("ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj", vcsIgnoreParityWorkflow, StringComparison.Ordinal);
        Assert.Contains("/p:TailwindRuntimeBinaryResolutionEnabled=true", packageGateWorkflow, StringComparison.Ordinal);
        Assert.Contains("TailwindRuntimeBinaryResolutionEnabled: \"true\"", packageArtifactsWorkflow, StringComparison.Ordinal);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, packageGateWorkflow);
        Assert.DoesNotMatch(disabledRuntimeResolutionSetting, packageArtifactsWorkflow);
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
            new PackageArtifactValidator());
        var missingRepository = Path.Combine(_repositoryRoot, "missing");
        var missingRepositoryError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                missingRepository,
                Path.Combine(missingRepository, "packages", "package-index.yml"),
                Path.Combine(missingRepository, "artifacts"),
                Path.Combine(missingRepository, "report.md"),
                PackageVersion,
                Path.Combine(missingRepository, "manifest.json"))));

        var missingManifestError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                Path.Combine(_repositoryRoot, "artifacts"),
                Path.Combine(_repositoryRoot, "report.md"),
                PackageVersion,
                Path.Combine(_repositoryRoot, "manifest.json"))));

        await WriteFileAsync("packages/package-index.yml", "packages: []");
        var missingArtifactPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                " ",
                Path.Combine(_repositoryRoot, "report.md"),
                PackageVersion,
                Path.Combine(_repositoryRoot, "manifest.json"))));
        var missingReportPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                Path.Combine(_repositoryRoot, "artifacts"),
                "",
                PackageVersion,
                Path.Combine(_repositoryRoot, "manifest.json"))));
        var missingArtifactManifestPathError = await Assert.ThrowsAsync<PackageIndexException>(
            () => workflow.RunAsync(new PackageArtifactRequest(
                _repositoryRoot,
                ManifestPath,
                Path.Combine(_repositoryRoot, "artifacts"),
                Path.Combine(_repositoryRoot, "report.md"),
                PackageVersion,
                "")));

        Assert.Contains("Repository root", missingRepositoryError.Message, StringComparison.Ordinal);
        Assert.Contains("Manifest", missingManifestError.Message, StringComparison.Ordinal);
        Assert.Contains("output path", missingArtifactPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("report path", missingReportPathError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest path", missingArtifactManifestPathError.Message, StringComparison.OrdinalIgnoreCase);
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var corePackagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        var webPackagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(corePackagePath, "core", Encoding.UTF8);
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var manifestPath = Path.Combine(artifactDirectory, "package-artifact-manifest.json");
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
                    projectReferences: [Path.Combine(_repositoryRoot, "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Core.csproj")])
            }),
            new PackageArtifactManifestReader(),
            commandRunner,
            new PackagePublishLedgerRenderer());
        var publishLogPath = Path.Combine(artifactDirectory, "publish.md");
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var corePackagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        var webPackagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(corePackagePath, "core", Encoding.UTF8);
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var manifestPath = Path.Combine(artifactDirectory, "package-artifact-manifest.json");
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
                    Path.Combine(artifactDirectory, "publish.md"),
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var corePackagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        var webPackagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(corePackagePath, "core", Encoding.UTF8);
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var manifestPath = Path.Combine(artifactDirectory, "package-artifact-manifest.json");
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
        var publishLogPath = Path.Combine(artifactDirectory, "publish.md");
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
        var artifactDirectory = Path.Join(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var packagePath = Path.Join(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(packagePath, "web", Encoding.UTF8);
        var configPackagePath = Path.Join(artifactDirectory, $"ForgeTrust.AppSurface.Config.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(configPackagePath, "config", Encoding.UTF8);
        var supportPackagePath = Path.Join(artifactDirectory, $"ForgeTrust.AppSurface.Core.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(supportPackagePath, "core", Encoding.UTF8);
        var manifestPath = Path.Join(artifactDirectory, "package-artifact-manifest.json");
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
        var workDirectory = Path.Join(_repositoryRoot, "smoke");

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                Path.Join(workDirectory, "smoke.md"),
                "https://api.nuget.org/v3/index.json"),
            CancellationToken.None);

        Assert.Equal(["ForgeTrust.AppSurface.Web", "ForgeTrust.AppSurface.Config"], report.Entries.Select(entry => entry.PackageId).ToArray());
        Assert.All(report.Entries, entry => Assert.Equal(PackageSmokeInstallStatus.Restored, entry.Status));
        Assert.Equal(2, commandRunner.Requests.Count);
        Assert.Single(delays);
        Assert.True(File.Exists(Path.Join(workDirectory, "NuGet.config")));
        Assert.Contains("<clear />", await File.ReadAllTextAsync(Path.Join(workDirectory, "NuGet.config")), StringComparison.Ordinal);
        var smokeProject = await File.ReadAllTextAsync(Path.Join(workDirectory, "package-restore", "Smoke.csproj"));
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
        var artifactDirectory = Path.Join(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var webPackagePath = Path.Join(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(webPackagePath, "web", Encoding.UTF8);
        var configPackagePath = Path.Join(artifactDirectory, $"ForgeTrust.AppSurface.Config.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(configPackagePath, "config", Encoding.UTF8);
        var manifestPath = Path.Join(artifactDirectory, "package-artifact-manifest.json");
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

        var workDirectory = Path.Join(_repositoryRoot, "smoke");

        var report = await workflow.RunAsync(
            new PackageSmokeInstallRequest(
                _repositoryRoot,
                ManifestPath,
                manifestPath,
                workDirectory,
                Path.Join(workDirectory, "report.md"),
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

        Assert.Equal(Path.Join(toolPath, expectedShimName), shimPath);
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
        var artifactDirectory = Path.Combine(_repositoryRoot, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var packagePath = Path.Combine(artifactDirectory, $"ForgeTrust.AppSurface.Web.{PackageVersion}.nupkg");
        await File.WriteAllTextAsync(packagePath, "web", Encoding.UTF8);
        var manifestPath = Path.Combine(artifactDirectory, "package-artifact-manifest.json");
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
                    Path.Combine(_repositoryRoot, "smoke"),
                    Path.Combine(_repositoryRoot, "smoke", "report.md"),
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

    private static PackagePayloadInventory CreateReportGeneratorInventory()
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
                    VersionSourcePath = "Directory.Packages.props",
                    VersionSourceContains = """<PackageVersion Include="ReportGenerator" Version="5.5.10" />"""
                }
            }
        };
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "ForgeTrust.AppSurface.slnx")) &&
                Directory.Exists(Path.Join(current.FullName, ".github", "workflows")))
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
}
