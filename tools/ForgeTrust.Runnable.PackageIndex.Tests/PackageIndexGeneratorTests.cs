using System.Text;

namespace ForgeTrust.Runnable.PackageIndex.Tests;

public sealed class PackageIndexGeneratorTests : IDisposable
{
    private readonly string _repositoryRoot;

    public PackageIndexGeneratorTests()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "PackageIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenCandidateProjectIsMissingFromManifest()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj
                classification: public
                order: 10
                use_when: Start here for CLI apps.
                includes: Command hosting.
                does_not_include: Web hosting.
                start_here_path: Console/ForgeTrust.Runnable.Console/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/README.md", "# Console");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj",
                "ForgeTrust.Runnable.Console"),
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var request = CreateRequest();
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(request));

        Assert.Contains("missing a classification", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenRepositoryRootDoesNotExist()
    {
        var missingRoot = Path.Combine(_repositoryRoot, "missing-root");
        var request = new PackageIndexRequest(
            missingRoot,
            Path.Combine(missingRoot, "packages", "package-index.yml"),
            Path.Combine(missingRoot, "packages", "README.md"));

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase));
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(request));

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase));
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("package-index.yml", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenPublicPackageGuidanceIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("UseWhen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestDeclaresProjectMoreThanOnce()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 20
                use_when: Duplicate entry.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("more than once", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestContainsUnknownProperty()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
                recipe_summmary: Typo should fail loudly.
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("could not be parsed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestReferencesProjectThatWasNotDiscovered()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj
                classification: support
                order: 20
                note: This project should not be discovered.
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("was not discovered", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenStartHereDocIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("missing documentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenDependencyReferenceIsUnknown()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
                depends_on:
                  - Missing.Package
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("unknown package id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenNonPublicEntryNoteIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj
                classification: proof_host
                order: 20
                start_here_path: Web/ForgeTrust.Runnable.Web.RazorDocs/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorDocs/README.md", "# RazorDocs");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("Note", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenStartHerePathEscapesRepositoryRoot()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: ../outside.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("outside the repository root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestOmitsRunnableWebPackage()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj
                classification: public
                order: 10
                use_when: Start here for CLI apps.
                includes: Command hosting.
                does_not_include: Web hosting.
                start_here_path: Console/ForgeTrust.Runnable.Console/README.md
            """);
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/README.md", "# Console");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj",
                "ForgeTrust.Runnable.Console")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("ForgeTrust.Runnable.Web", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenRunnableWebPackageIsNotPublic()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: support
                order: 10
                note: This row should stay out of direct-install guidance.
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("exactly one public", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ForgeTrust.Runnable.Web", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersRazorWireCliAsToolInstallSurface()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj
                classification: public
                order: 20
                use_when: Install this when you want to export RazorWire apps from a stable command-line tool.
                includes: The `razorwire` .NET tool command and static export workflow.
                does_not_include: The RazorWire runtime package or coordinated package publishing automation.
                start_here_path: Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md", "# RazorWire CLI");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("| `ForgeTrust.Runnable.Web.RazorWire.Cli` |", markdown, StringComparison.Ordinal);
        Assert.Contains("`dotnet tool install --global ForgeTrust.Runnable.Web.RazorWire.Cli`", markdown, StringComparison.Ordinal);
        Assert.Contains("Library package rows use `dotnet package add`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersChooserSectionsAndInstallCommands()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("# Runnable v0.1 package chooser", markdown, StringComparison.Ordinal);
        Assert.Contains("```bash", markdown, StringComparison.Ordinal);
        Assert.Contains("dotnet package add ForgeTrust.Runnable.Web", markdown, StringComparison.Ordinal);
        Assert.Contains("[examples/web-app/README.md](../examples/web-app/README.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.Runnable.Web` |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', markdown);
        Assert.EndsWith("\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### Support and runtime packages", markdown, StringComparison.Ordinal);
        Assert.Contains("### Docs and proof hosts", markdown, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global ForgeTrust.Runnable.Web.RazorWire.Cli", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenStaticChooserLinkTargetIsMissing()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        File.Delete(Path.Combine(_repositoryRoot, "releases", "upgrade-policy.md"));

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("Pre-1.0 upgrade policy", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing documentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_RendersAlsoBuildingListAndSupportStartHereLinks()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable v0.1 package chooser");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj
                classification: public
                order: 20
                use_when: Add this after the base web package when you want an OpenAPI document.
                includes: OpenAPI generation.
                does_not_include: A hosted API reference UI.
                start_here_path: Web/ForgeTrust.Runnable.Web.OpenApi/README.md
                recipe_summary: Add `ForgeTrust.Runnable.Web.OpenApi` when you want an OpenAPI document.
              - project: Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj
                classification: support
                order: 30
                note: Restored transitively on matching build hosts.
                start_here_path: Web/ForgeTrust.Runnable.Web.Tailwind/README.md
                start_here_label: Runtime README
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.OpenApi/README.md", "# OpenApi");
        await WriteFileAsync(
            "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
            "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.Tailwind/README.md", "# Tailwind");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("## Also building...", markdown, StringComparison.Ordinal);
        Assert.Contains("- Add `ForgeTrust.Runnable.Web.OpenApi` when you want an OpenAPI document.", markdown, StringComparison.Ordinal);
        Assert.Contains("Start here: [Runtime README](../Web/ForgeTrust.Runnable.Web.Tailwind/README.md)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ResolvesLinksRelativeToOutputDirectory()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("docs/guides/README.md.yml", "title: Runnable chooser mirror");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest("docs/guides/README.md"));

        Assert.Contains("[examples/web-app/README.md](../../examples/web-app/README.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("[Package README](../../Web/ForgeTrust.Runnable.Web/README.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("[Release hub](../../releases/README.md)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersDistinctTargetFrameworkSummary()
    {
        await WriteFileAsync("packages/README.md.yml", "title: Runnable");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
              - project: Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj
                classification: public
                order: 20
                use_when: Start here for CLI apps.
                includes: Command hosting.
                does_not_include: Web hosting.
                start_here_path: Console/ForgeTrust.Runnable.Console/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.Runnable.Console/README.md", "# Console");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj",
                "ForgeTrust.Runnable.Console",
                targetFramework: "net9.0")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("`net10.0`", markdown, StringComparison.Ordinal);
        Assert.Contains("`net9.0`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersMissingUnreleasedStateExplicitly()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: false);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("Not published yet", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateToFileAsync_CreatesOutputDirectoryAndWritesChooser()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("docs/guides/README.md.yml", "title: Runnable chooser mirror");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var request = CreateRequest("docs/guides/README.md");
        await generator.GenerateToFileAsync(request);

        Assert.True(File.Exists(request.OutputPath));
        var markdown = await File.ReadAllTextAsync(request.OutputPath);
        Assert.Contains("# Runnable v0.1 package chooser", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_ThrowsWhenGeneratedReadmeIsStale()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("packages/README.md", "# stale");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.VerifyAsync(CreateRequest()));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_ThrowsWhenGeneratedReadmeIsMissing()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                "ForgeTrust.Runnable.Web"),
            ["Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj",
                "ForgeTrust.Runnable.Web.OpenApi"),
            ["Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj",
                "ForgeTrust.Runnable.Web.RazorDocs"),
            ["Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj",
                "ForgeTrust.Runnable.Web.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.VerifyAsync(CreateRequest()));

        Assert.Contains("Missing generated file", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenChooserSidecarIsMissing()
    {
        await WriteFileAsync("packages/package-index.yml", "packages: []");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase));
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("paired sidecar", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandLineOptions_Parse_UsesDefaultsAndOverrides()
    {
        var defaults = CommandLineOptions.Parse([], _repositoryRoot);

        Assert.Equal(Path.Combine(_repositoryRoot, "packages", "package-index.yml"), defaults.Request.ManifestPath);
        Assert.Equal(Path.Combine(_repositoryRoot, "packages", "README.md"), defaults.Request.OutputPath);
        Assert.Equal(Path.Combine(_repositoryRoot, "artifacts", "packages"), defaults.ArtifactsOutputPath);
        Assert.Equal(Path.Combine(_repositoryRoot, "artifacts", "package-validation-report.md"), defaults.ReportPath);
        Assert.Null(defaults.PackageVersion);

        var parsed = CommandLineOptions.Parse(
            [
                "--repo-root", "src",
                "--manifest", "manifest.yml",
                "--output", "chooser.md",
                "--artifacts-output", "packages-out",
                "--package-version", "0.0.0-ci.99",
                "--report", "package-report.md"
            ],
            _repositoryRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(_repositoryRoot, "src")), parsed.Request.RepositoryRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(_repositoryRoot, "src", "manifest.yml")), parsed.Request.ManifestPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(_repositoryRoot, "src", "chooser.md")), parsed.Request.OutputPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(_repositoryRoot, "src", "packages-out")), parsed.ArtifactsOutputPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(_repositoryRoot, "src", "package-report.md")), parsed.ReportPath);
        Assert.Equal("0.0.0-ci.99", parsed.PackageVersion);

        var absoluteManifest = Path.Combine(_repositoryRoot, "abs", "manifest.yml");
        var absoluteOutput = Path.Combine(_repositoryRoot, "abs", "chooser.md");
        var absoluteArtifacts = Path.Combine(_repositoryRoot, "abs", "artifacts");
        var absoluteReport = Path.Combine(_repositoryRoot, "abs", "report.md");
        var absolute = CommandLineOptions.Parse(
            ["--manifest", absoluteManifest, "--output", absoluteOutput, "--artifacts-output", absoluteArtifacts, "--report", absoluteReport],
            _repositoryRoot);

        Assert.Equal(absoluteManifest, absolute.Request.ManifestPath);
        Assert.Equal(absoluteOutput, absolute.Request.OutputPath);
        Assert.Equal(absoluteArtifacts, absolute.ArtifactsOutputPath);
        Assert.Equal(absoluteReport, absolute.ReportPath);
    }

    [Fact]
    public void RepositoryPathComparison_MatchesHostFilesystemExpectation()
    {
        var expected = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        Assert.Equal(expected, PackageIndexGenerator.RepositoryPathComparison);
    }

    [Fact]
    public void CommandLineOptions_Parse_ThrowsWhenOptionValueIsMissing()
    {
        var error = Assert.Throws<PackageIndexException>(() => CommandLineOptions.Parse(["--manifest"], _repositoryRoot));

        Assert.Contains("requires a value", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WritesUsageWhenNoCommandIsProvided()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync([], stdout, stderr, _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesHelpToStandardOut()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["--help"], stdout, stderr, _repositoryRoot);

        Assert.Equal(0, exitCode);
        Assert.Contains("Commands:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("generate", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData("generate", "-h")]
    [InlineData("verify", "--help")]
    public async Task RunAsync_WritesCommandHelpToStandardOut(string command, string helpOption)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync([command, helpOption], stdout, stderr, _repositoryRoot);

        Assert.Equal(0, exitCode);
        Assert.Contains("Commands:", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("--repo-root", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WritesUsageWhenCommandIsUnknown()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["mystery"], stdout, stderr, _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown command 'mystery'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WritesUnknownCommandBeforeParsingOptions()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["mystery", "--bogus"], stdout, stderr, _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Unknown command 'mystery'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown option '--bogus'.", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_DelegatesToRunAsync()
    {
        var exitCode = await Program.Main([]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RunAsync_GenerateAndVerify_Succeed()
    {
        await WriteProgramRepoAsync();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var generateExitCode = await Program.RunAsync(["generate"], stdout, stderr, _repositoryRoot);
        var verifyExitCode = await Program.RunAsync(["verify"], stdout, stderr, _repositoryRoot);

        Assert.Equal(0, generateExitCode);
        Assert.Equal(0, verifyExitCode);
        Assert.Contains("Generated packages/README.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\\', stdout.ToString());
        Assert.Contains("Package chooser is up to date.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(File.Exists(Path.Combine(_repositoryRoot, "packages", "README.md")));
    }

    [Fact]
    public async Task RunAsync_VerifyPackages_UsesWorkflowAndWritesRelativeReportPath()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        PackageArtifactRequest? capturedRequest = null;

        var exitCode = await Program.RunAsync(
            [
                "verify-packages",
                "--package-version", "0.0.0-ci.99",
                "--artifacts-output", "packages-out",
                "--report", "reports/packages.md"
            ],
            stdout,
            stderr,
            _repositoryRoot,
            verifyPackagesAsync: (request, cancellationToken) =>
            {
                capturedRequest = request;
                Assert.False(cancellationToken.IsCancellationRequested);
                return Task.FromResult(new PackageArtifactValidationReport(
                    request.PackageVersion,
                    [
                        new PackageArtifactValidationReportEntry(
                            "ForgeTrust.Runnable.Web",
                            "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
                            PackagePublishDecision.Publish,
                            [])
                    ]));
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(Path.Combine(_repositoryRoot, "packages-out"), capturedRequest.ArtifactsOutputPath);
        Assert.Equal(Path.Combine(_repositoryRoot, "reports", "packages.md"), capturedRequest.ReportPath);
        Assert.Equal("0.0.0-ci.99", capturedRequest.PackageVersion);
        Assert.Contains("Validated 1 package artifacts for 0.0.0-ci.99. Report: reports/packages.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_VerifyPackages_WritesAbsoluteReportPathOutsideRepository()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var reportPath = Path.Combine(Path.GetTempPath(), $"package-report-{Guid.NewGuid():N}.md");

        var exitCode = await Program.RunAsync(
            ["verify-packages", "--package-version", "0.0.0-ci.99", "--report", reportPath],
            stdout,
            stderr,
            _repositoryRoot,
            verifyPackagesAsync: (request, _) => Task.FromResult(new PackageArtifactValidationReport(request.PackageVersion, [])));

        Assert.Equal(0, exitCode);
        Assert.Contains($"Report: {reportPath}.", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_VerifyPackages_RequiresPackageVersion()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["verify-packages"], stdout, stderr, _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Contains("--package-version", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_WritesGeneratorErrors()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(["generate", "--bogus"], stdout, stderr, _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown option", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public void IsCandidateProject_ExcludesGeneratedAndToolingPaths()
    {
        Assert.False(PackageProjectScanner.IsCandidateProject("tools/ForgeTrust.Runnable.PackageIndex/ForgeTrust.Runnable.PackageIndex.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("examples/web-app/WebAppExample.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("Web/ForgeTrust.Runnable.Web.RazorWire.IntegrationTests/ForgeTrust.Runnable.Web.RazorWire.IntegrationTests.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("Config/ForgeTrust.Runnable.Config.Tests/ForgeTrust.Runnable.Config.Tests.csproj"));
        Assert.True(PackageProjectScanner.IsCandidateProject("Web/ForgeTrust.Runnable.Web.RazorDocs.Standalone/ForgeTrust.Runnable.Web.RazorDocs.Standalone.csproj"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DotNetProjectMetadataProvider_ParsesRealMsbuildOutput()
    {
        await WriteFileAsync(
            "src/Dependency/Dependency.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await WriteFileAsync(
            "src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Dependency/Dependency.csproj" />
              </ItemGroup>
            </Project>
            """);

        var provider = new DotNetProjectMetadataProvider();
        var metadata = await provider.GetMetadataAsync(_repositoryRoot, "src/App/App.csproj", CancellationToken.None);

        Assert.Equal("App", metadata.PackageId);
        Assert.Equal("net10.0", metadata.TargetFramework);
        Assert.False(metadata.IsTool);
        Assert.Equal("Exe", metadata.OutputType);
        Assert.Single(metadata.ProjectReferences);
        Assert.EndsWith("src/Dependency/Dependency.csproj", metadata.ProjectReferences[0].Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMetadataJson_ThrowsWhenRequiredPropertyIsMissing()
    {
        const string standardOutput = """
            {
              "Properties": {
                "TargetFramework": "net10.0",
                "IsPackable": "true",
                "OutputType": "Library"
              }
            }
            """;

        var error = Assert.Throws<PackageIndexException>(
            () => DotNetProjectMetadataProvider.ParseMetadataJson(
                "src/App/App.csproj",
                standardOutput));

        Assert.Contains("malformed JSON", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PackageId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMetadataJson_ThrowsWhenPropertiesObjectIsMissing()
    {
        const string standardOutput = """
            {
              "Items": {
                "ProjectReference": []
              }
            }
            """;

        var error = Assert.Throws<PackageIndexException>(
            () => DotNetProjectMetadataProvider.ParseMetadataJson(
                "src/App/App.csproj",
                standardOutput));

        Assert.Contains("malformed JSON", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Properties", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMetadataJson_ThrowsWhenPropertiesNodeHasWrongShape()
    {
        const string standardOutput = """
            {
              "Properties": []
            }
            """;

        var error = Assert.Throws<PackageIndexException>(
            () => DotNetProjectMetadataProvider.ParseMetadataJson(
                "src/App/App.csproj",
                standardOutput));

        Assert.Contains("malformed JSON", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must be a JSON object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMetadataJson_UsesTargetFrameworkFallbackAndFiltersProjectReferences()
    {
        const string standardOutput = """
            {
              "Properties": {
                "PackageId": "ForgeTrust.Runnable.Web",
                "TargetFramework": "",
                "TargetFrameworks": "net10.0",
                "IsPackable": "true",
                "PackAsTool": "false",
                "OutputType": "Library"
              },
              "Items": {
                "ProjectReference": [
                  {
                    "Identity": "../Ignored/Ignored.csproj"
                  },
                  {
                    "FullPath": "/repo/src/Dependency/Dependency.csproj"
                  }
                ]
              }
            }
            """;

        var metadata = DotNetProjectMetadataProvider.ParseMetadataJson(
            "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
            standardOutput);

        Assert.Equal("ForgeTrust.Runnable.Web", metadata.PackageId);
        Assert.Equal("net10.0", metadata.TargetFramework);
        Assert.True(metadata.IsPackable);
        Assert.False(metadata.IsTool);
        Assert.Equal("Library", metadata.OutputType);
        Assert.Single(metadata.ProjectReferences);
        Assert.Equal("/repo/src/Dependency/Dependency.csproj", metadata.ProjectReferences[0]);
    }

    [Fact]
    public void ParseMetadataJson_ReturnsEmptyProjectReferencesWhenItemsSectionIsMissing()
    {
        const string standardOutput = """
            {
              "Properties": {
                "PackageId": "ForgeTrust.Runnable.Console",
                "TargetFramework": "net10.0",
                "IsPackable": "false",
                "PackAsTool": "true",
                "OutputType": "Exe"
              }
            }
            """;

        var metadata = DotNetProjectMetadataProvider.ParseMetadataJson(
            "Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj",
            standardOutput);

        Assert.Equal("ForgeTrust.Runnable.Console", metadata.PackageId);
        Assert.Equal("net10.0", metadata.TargetFramework);
        Assert.False(metadata.IsPackable);
        Assert.True(metadata.IsTool);
        Assert.Equal("Exe", metadata.OutputType);
        Assert.Empty(metadata.ProjectReferences);
    }

    [Fact]
    public void ParseMetadataJson_ThrowsWhenMetadataIsIncomplete()
    {
        const string standardOutput = """
            {
              "Properties": {
                "PackageId": "ForgeTrust.Runnable.Console",
                "TargetFramework": "",
                "TargetFrameworks": "",
                "IsPackable": "true",
                "PackAsTool": "false",
                "OutputType": ""
              }
            }
            """;

        var error = Assert.Throws<PackageIndexException>(
            () => DotNetProjectMetadataProvider.ParseMetadataJson(
                "Console/ForgeTrust.Runnable.Console/ForgeTrust.Runnable.Console.csproj",
                standardOutput));

        Assert.Contains("incomplete metadata", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DotNetProjectMetadataProvider_ThrowsWhenProjectCannotBeEvaluated()
    {
        var provider = new DotNetProjectMetadataProvider();

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => provider.GetMetadataAsync(_repositoryRoot, "missing/Nope.csproj", CancellationToken.None));

        Assert.Contains("Failed to evaluate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCommandRunner_ThrowsWhenProcessCannotStart()
    {
        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ProcessCommandRunner().RunAsync(
                new CommandRunRequest(
                    Path.Combine(_repositoryRoot, "missing-dotnet"),
                    [],
                    _repositoryRoot,
                    "dotnet msbuild",
                    "missing/Nope.csproj",
                    "evaluate",
                    "evaluating",
                    100),
                CancellationToken.None));

        Assert.Contains("Failed to start dotnet msbuild", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProcessCommandRunner_ThrowsWhenProcessTimesOut()
    {
        var command = CreateSleepCommand(durationSeconds: 5);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ProcessCommandRunner().RunAsync(
                new CommandRunRequest(
                    command.FileName,
                    command.Arguments,
                    _repositoryRoot,
                    "dotnet msbuild",
                    "slow/Project.csproj",
                    "evaluate",
                    "evaluating",
                    100),
                CancellationToken.None));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slow/Project.csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageManifestLoader_ThrowsWhenManifestHasNoPackages()
    {
        await WriteFileAsync("packages/package-index.yml", "packages: []");

        var loader = new PackageManifestLoader();
        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => loader.LoadAsync(Path.Combine(_repositoryRoot, "packages", "package-index.yml"), CancellationToken.None));

        Assert.Contains("does not define any packages", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private PackageIndexGenerator CreateGenerator(IReadOnlyDictionary<string, PackageProjectMetadata> metadataByProject)
    {
        return new PackageIndexGenerator(
            new PackageProjectScanner(),
            new FakeMetadataProvider(metadataByProject),
            new PackageManifestLoader());
    }

    private PackageIndexRequest CreateRequest(string outputRelativePath = "packages/README.md")
    {
        return new PackageIndexRequest(
            _repositoryRoot,
            Path.Combine(_repositoryRoot, "packages", "package-index.yml"),
            Path.Combine(_repositoryRoot, outputRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task WriteCommonChooserFilesAsync(bool includeUnreleased)
    {
        await WriteFileAsync(
            "packages/README.md.yml",
            """
            title: Runnable v0.1 package chooser
            """);
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup, middleware composition, and endpoint registration.
                does_not_include: OpenAPI, hosted API docs UI, and Tailwind asset compilation.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
                recipe_summary: Add `ForgeTrust.Runnable.Web.OpenApi` after `ForgeTrust.Runnable.Web` when you want an OpenAPI document.
              - project: Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj
                classification: public
                order: 20
                use_when: Add this after the base web package when you want an OpenAPI document.
                includes: OpenAPI generation and endpoint explorer wiring.
                does_not_include: A hosted API reference UI.
                start_here_path: Web/ForgeTrust.Runnable.Web.OpenApi/README.md
              - project: Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj
                classification: support
                order: 30
                note: Restored transitively by `ForgeTrust.Runnable.Web.Tailwind` on matching build hosts. Do not install it directly.
              - project: Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj
                classification: proof_host
                order: 40
                note: Reusable docs package for hosting harvested repository docs.
                start_here_path: Web/ForgeTrust.Runnable.Web.RazorDocs/README.md
              - project: Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj
                classification: public
                order: 50
                use_when: Install this when you want to export RazorWire apps from a stable command-line tool.
                includes: The `razorwire` .NET tool command and static export workflow.
                does_not_include: The RazorWire runtime package or coordinated package publishing automation.
                start_here_path: Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.OpenApi/ForgeTrust.Runnable.Web.OpenApi.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.OpenApi/README.md", "# OpenApi");
        await WriteFileAsync(
            "Web/ForgeTrust.Runnable.Web.Tailwind/runtimes/ForgeTrust.Runnable.Web.Tailwind.Runtime.osx-arm64.csproj",
            "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorDocs/ForgeTrust.Runnable.Web.RazorDocs.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorDocs/README.md", "# RazorDocs");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/ForgeTrust.Runnable.Web.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web.RazorWire.Cli/README.md", "# RazorWire CLI");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");
        if (includeUnreleased)
        {
            await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        }
    }

    private async Task WriteProgramRepoAsync()
    {
        await WriteFileAsync(
            "packages/README.md.yml",
            """
            title: Runnable v0.1 package chooser
            """);
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with Runnable modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.Runnable.Web/README.md
            """);
        await WriteFileAsync(
            "Web/ForgeTrust.Runnable.Web/ForgeTrust.Runnable.Web.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>ForgeTrust.Runnable.Web</PackageId>
              </PropertyGroup>
            </Project>
            """);
        await WriteFileAsync("Web/ForgeTrust.Runnable.Web/README.md", "# Web");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
    }

    private static PackageProjectMetadata CreateMetadata(
        string projectPath,
        string packageId,
        string outputType = "Library",
        string targetFramework = "net10.0",
        bool isTool = false)
    {
        return new PackageProjectMetadata(projectPath, packageId, targetFramework, true, isTool, outputType, []);
    }

    private static SleepCommand CreateSleepCommand(int durationSeconds)
    {
        return OperatingSystem.IsWindows()
            ? new SleepCommand("cmd.exe", ["/c", "timeout", "/t", durationSeconds.ToString(), "/nobreak"])
            : new SleepCommand("/bin/sh", ["-c", $"sleep {durationSeconds}"]);
    }

    private sealed record SleepCommand(string FileName, IReadOnlyList<string> Arguments);

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
}
