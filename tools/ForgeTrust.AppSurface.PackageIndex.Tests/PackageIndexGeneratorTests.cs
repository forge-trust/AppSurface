using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class PackageIndexGeneratorTests : IDisposable
{
    private const string FirstSuccessPathMarkdown = """
        # First Success Path

        ## Package-First Path
        """;

    private readonly string _repositoryRoot;

    public PackageIndexGeneratorTests()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "PackageIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenCandidateProjectIsMissingFromManifest()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Start here for CLI apps.
                includes: Command hosting.
                does_not_include: Web hosting.
                start_here_path: Console/ForgeTrust.AppSurface.Console/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/README.md", "# Console");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "ForgeTrust.AppSurface.Console"),
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
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
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase));
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("package-index.yml", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenPublicPackageGuidanceIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            $$"""
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 10
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("UseWhen", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestDeclaresProjectMoreThanOnce()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            $$"""
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                depends_on:
                  - ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 20
                use_when: Duplicate entry.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("more than once", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestContainsUnknownProperty()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                recipe_summmary: Typo should fail loudly.
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("could not be parsed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "must define 'product_family'")]
    [InlineData("    product_family: unknown_family\n", "Choose the family")]
    public async Task GenerateAsync_ValidatesProductFamily(string productFamilyYaml, string expectedMessage)
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            "packages:\n"
            + "  - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj\n"
            + productFamilyYaml
            + "    classification: public\n"
            + "    publish_decision: publish\n"
            + "    order: 10\n"
            + "    use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.\n"
            + "    includes: Base startup\n"
            + "    does_not_include: OpenAPI\n"
            + "    start_here_path: Web/ForgeTrust.AppSurface.Web/README.md\n");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("264")]
    [InlineData("https://github.com/elsewhere/AppSurface/issues/264")]
    [InlineData("https://github.com/forge-trust/AppSurface/issues/not-a-number")]
    public async Task GenerateAsync_ValidatesReadinessBlockerSyntax(string readinessBlocker)
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            $$"""
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                readiness_blocker: "{{readinessBlocker}}"
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("readiness_blocker", error.Message, StringComparison.Ordinal);
        Assert.Contains("#123", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_SurfacesPublicPackagePublicationBlockerInChooser()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                readiness_blocker: "#642"
                readiness_note: Pinned dependency cleanup is incomplete.
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                readiness_blocker: "#642"
                readiness_note: Pinned dependency cleanup is incomplete.
                order: 20
                use_when: Add this to tests for an AppSurface profile-based AppHost.
                includes: Typed profile testing
                does_not_include: Native AppHost replacement
                start_here_path: Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md
                recipe_summary: Add `ForgeTrust.AppSurface.Aspire.Testing` for profile-based AppHosts.
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync(
            "Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj",
            "<Project />");
        await WriteFileAsync("Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md", "# Aspire Testing");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj"] = CreateMetadata(
                "Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj",
                "ForgeTrust.AppSurface.Aspire.Testing")
        });
        var request = CreateRequest();

        var chooser = await generator.GenerateAsync(request);

        Assert.Contains(
            "Publication of [`ForgeTrust.AppSurface.Web`](../Web/ForgeTrust.AppSurface.Web/README.md) is blocked by #642; it is not currently installable. Pinned dependency cleanup is incomplete.",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "- Publication of [`ForgeTrust.AppSurface.Aspire.Testing`](../Aspire/ForgeTrust.AppSurface.Aspire.Testing/README.md) is blocked by #642; it is not currently installable. Pinned dependency cleanup is incomplete.",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains("Publication blocked by #642; not currently installable.", chooser, StringComparison.Ordinal);
        Assert.Contains("publication blocked by #642", chooser, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet package add ForgeTrust.AppSurface.Web", chooser, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet package add ForgeTrust.AppSurface.Aspire.Testing", chooser, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ValidatesReadinessNoteAsPlainText()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                readiness_note: "<strong>unsafe</strong>"
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base startup
                does_not_include: OpenAPI
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("readiness_note", error.Message, StringComparison.Ordinal);
        Assert.Contains("plain text", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestReferencesProjectThatWasNotDiscovered()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj
                product_family: appsurface
                classification: support
                order: 20
                note: This project should not be discovered.
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("was not discovered", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenStartHereDocIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("missing documentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenDependencyReferenceIsUnknown()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                depends_on:
                  - Missing.Package
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("unknown package id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenPublishDecisionIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("publish_decision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenDoNotPublishReasonIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: do_not_publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("publish_reason", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenExpectedDependencyPackageIdIsUnknown()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                expected_dependency_package_ids:
                  - Missing.Package
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("expects unknown dependency package id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenExpectedDependencyPackageIdIsEmpty()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                expected_dependency_package_ids:
                  - ""
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("expected_dependency_package_ids", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenNonPublicEntryNoteIsMissing()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj
                product_family: forge_trust
                classification: proof_host
                publish_decision: do_not_publish
                order: 20
                start_here_path: Web/ForgeTrust.AppSurface.Docs/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Docs/README.md", "# AppSurfaceDocs");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("Note", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenStartHerePathEscapesRepositoryRoot()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: ../outside.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("outside the repository root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenManifestOmitsAppSurfaceWebPackage()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Start here for CLI apps.
                includes: Command hosting.
                does_not_include: Web hosting.
                start_here_path: Console/ForgeTrust.AppSurface.Console/README.md
            """);
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/README.md", "# Console");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "ForgeTrust.AppSurface.Console")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("ForgeTrust.AppSurface.Web", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenAppSurfaceWebPackageIsNotPublic()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: support
                publish_decision: support_publish
                order: 10
                note: This row should stay out of direct-install guidance.
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("exactly one public", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ForgeTrust.AppSurface.Web", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersRazorWireCliAsToolInstallSurface()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj
                product_family: razorwire
                classification: public
                publish_decision: publish
                order: 20
                use_when: Install this when you want to export RazorWire apps from a stable command-line tool.
                includes: The `razorwire` .NET tool command and static export workflow.
                does_not_include: The RazorWire runtime package or coordinated package publishing automation.
                start_here_path: Web/ForgeTrust.RazorWire.Cli/README.md
                tool_command_name: razorwire
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/README.md", "# RazorWire CLI");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/v0.1-preview.md", "# v0.1 Preview");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("| `ForgeTrust.RazorWire.Cli` |", markdown, StringComparison.Ordinal);
        Assert.Contains("`dotnet tool install --global ForgeTrust.RazorWire.Cli --prerelease`", markdown, StringComparison.Ordinal);
        Assert.Contains("Library package rows use `dotnet package add`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersChooserSectionsAndInstallCommands()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("examples/product-readiness-lab/README.md", "# Product readiness lab");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("# AppSurface v0.1 package chooser", markdown, StringComparison.Ordinal);
        Assert.Contains("```bash", markdown, StringComparison.Ordinal);
        Assert.Contains("dotnet package add ForgeTrust.AppSurface.Web", markdown, StringComparison.Ordinal);
        Assert.Contains("[Package-first quickstart](../start-here/first-success-path.md#package-first-path)", markdown, StringComparison.Ordinal);
        Assert.Contains("[v0.1.0 Release Preview](../releases/v0.1-preview.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("| `ForgeTrust.AppSurface.Web` |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', markdown);
        Assert.EndsWith("\n", markdown, StringComparison.Ordinal);
        Assert.Contains("### Support and runtime packages", markdown, StringComparison.Ordinal);
        Assert.Contains("### Docs and proof hosts", markdown, StringComparison.Ordinal);
        Assert.Contains("[Product readiness lab](../examples/product-readiness-lab/README.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("Its paired AppHost `verify` profile starts local Postgres", markdown, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global ForgeTrust.RazorWire.Cli --prerelease", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersSharedTaggedReleaseNoteInReadinessSummary()
    {
        await WriteProgramRepoAsync(releaseNotesPath: "releases/v0.1.0-rc.1.md");
        await WriteFileAsync("releases/v0.1.0-rc.1.md", "# AppSurface 0.1.0 RC 1");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("[v0.1.0-rc.1 release note](../releases/v0.1.0-rc.1.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("current package-facing story", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("v0.1.0 Release Preview", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_PreservesPreviewCopyForSharedPreviewReleaseNote()
    {
        await WriteProgramRepoAsync(releaseNotesPath: "releases/v0.1-preview.md");
        await WriteFileAsync("releases/v0.1-preview.md", "# v0.1 Preview");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("[v0.1.0 Release Preview](../releases/v0.1-preview.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("stays provisional until the tag is cut", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenStaticChooserLinkTargetIsMissing()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        File.Delete(Path.Combine(_repositoryRoot, "releases", "upgrade-policy.md"));

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("Pre-1.0 upgrade policy", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing documentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenWebPackageQuickstartFragmentTargetIsMissing()
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "start-here/first-success-path.md",
            """
            # First Success Path

            ## Fresh App Path
            """);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("start-here/first-success-path.md#package-first-path", error.Message, StringComparison.Ordinal);
        Assert.Contains("fragment target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""
        # First Success Path

        ## Fresh App Path {#package-first-path}
        """)]
    [InlineData("""
        # First Success Path

        ## Package-First Path ##
        """)]
    [InlineData("""
        # First Success Path

        ## Package First Path -
        """)]
    [InlineData("""
        <a id="package-first-path"></a>

        # First Success Path
        """)]
    [InlineData("""
        <a id='package-first-path'></a>

        # First Success Path
        """)]
    [InlineData("""
        <a name="package-first-path"></a>

        # First Success Path
        """)]
    [InlineData("""
        <a name='package-first-path'></a>

        # First Success Path
        """)]
    public async Task GenerateAsync_AcceptsExplicitWebPackageQuickstartFragmentTargets(string quickstartMarkdown)
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync("start-here/first-success-path.md", quickstartMarkdown);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("[Package-first quickstart](../start-here/first-success-path.md#package-first-path)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsWhenDeclaredReleaseNotesPathIsMissing()
    {
        await WriteProgramRepoAsync(releaseNotesPath: "releases/v0.1-preview.md");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.GenerateAsync(CreateRequest()));

        Assert.Contains("Chooser link target 'releases/v0.1-preview.md'", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing documentation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_RendersAlsoBuildingListAndSupportStartHereLinks()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface v0.1 package chooser");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Add this after the base web package when you want an OpenAPI document.
                includes: OpenAPI generation.
                does_not_include: A hosted API reference UI.
                start_here_path: Web/ForgeTrust.AppSurface.Web.OpenApi/README.md
                recipe_summary: Add `ForgeTrust.AppSurface.Web.OpenApi` when you want an OpenAPI document.
              - project: Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj
                product_family: internal_support
                classification: support
                publish_decision: support_publish
                order: 30
                note: Restored transitively on matching build hosts.
                start_here_path: Web/ForgeTrust.AppSurface.Web.Tailwind/README.md
                start_here_label: Runtime README
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.OpenApi/README.md", "# OpenApi");
        await WriteFileAsync(
            "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
            "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.Tailwind/README.md", "# Tailwind");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/v0.1-preview.md", "# v0.1 Preview");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("## Also building...", markdown, StringComparison.Ordinal);
        Assert.Contains("- Add `ForgeTrust.AppSurface.Web.OpenApi` when you want an OpenAPI document.", markdown, StringComparison.Ordinal);
        Assert.Contains("Start here: [Runtime README](../Web/ForgeTrust.AppSurface.Web.Tailwind/README.md)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ResolvesLinksRelativeToOutputDirectory()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("docs/guides/README.md.yml", "title: AppSurface chooser mirror");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest("docs/guides/README.md"));

        Assert.Contains("[Package-first quickstart](../../start-here/first-success-path.md#package-first-path)", markdown, StringComparison.Ordinal);
        Assert.Contains("[Package README](../../Web/ForgeTrust.AppSurface.Web/README.md)", markdown, StringComparison.Ordinal);
        Assert.Contains("[Release hub](../../releases/README.md)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_RendersDistinctTargetFrameworkSummary()
    {
        await WriteFileAsync("packages/README.md.yml", "title: AppSurface");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Start here for CLI apps.
                includes: Command hosting.
                does_not_include: Web hosting.
                start_here_path: Console/ForgeTrust.AppSurface.Console/README.md
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj", "<Project />");
        await WriteFileAsync("Console/ForgeTrust.AppSurface.Console/README.md", "# Console");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj"] = CreateMetadata(
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
                "ForgeTrust.AppSurface.Console",
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
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("Not published yet", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_OmitsOptionalReleaseReadinessLinksWhenTargetsAreMissing()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: false);
        File.Delete(Path.GetFullPath(Path.Join("releases", "v0.1-preview.md"), _repositoryRoot));
        File.Delete(Path.GetFullPath(Path.Join("releases", "unreleased.md"), _repositoryRoot));

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var markdown = await generator.GenerateAsync(CreateRequest());

        Assert.Contains("[Release hub](../releases/README.md)", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("v0.1.0 Release Preview", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("[Unreleased proof artifact]", markdown, StringComparison.Ordinal);
        Assert.Contains("Unreleased proof artifact: Not published yet", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRepositoryFilePath_ThrowsWhenPathIsRooted()
    {
        var rootedPath = Path.GetFullPath(Path.Join("releases", "v0.1-preview.md"), _repositoryRoot);

        var error = Assert.Throws<PackageIndexException>(
            () => PackageIndexGenerator.ResolveRepositoryFilePath(_repositoryRoot, rootedPath, "Release preview"));

        Assert.Contains("repository-relative", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rootedPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateToFileAsync_CreatesOutputDirectoryAndWritesChooser()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("docs/guides/README.md.yml", "title: AppSurface chooser mirror");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var request = CreateRequest("docs/guides/README.md");
        await generator.GenerateToFileAsync(request);

        Assert.True(File.Exists(request.ChooserOutputPath));
        Assert.True(File.Exists(request.ReadinessOutputPath));
        var markdown = await File.ReadAllTextAsync(request.ChooserOutputPath);
        Assert.Contains("# AppSurface v0.1 package chooser", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDocumentsAsync_RendersMaintainerReadinessDashboardWithoutLivePublishClaims()
    {
        await WriteProgramRepoAsync();
        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var documents = await generator.GenerateDocumentsAsync(CreateRequest());

        Assert.Contains("# Package readiness evidence", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("package-index evidence", documents.ReadinessMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a live NuGet publish", documents.ReadinessMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`ForgeTrust.AppSurface.Web`", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("manifest evidence complete", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("| Start here | Release notes |", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("package readiness evidence", documents.ChooserMarkdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateDocumentsAsync_RendersNonPublicReadinessStatusesAndExcludedChooserSection()
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj
                product_family: internal_support
                classification: support
                publish_decision: support_publish
                release_status: support_runtime
                commercial_status: not_applicable
                release_notes_path: releases/unreleased.md
                order: 20
                note: Restored transitively by the Tailwind package.
              - project: Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj
                product_family: forge_trust
                classification: proof_host
                publish_decision: support_publish
                release_status: proof_host
                commercial_status: not_applicable
                release_notes_path: releases/unreleased.md
                order: 30
                note: Internal docs host proof package.
              - project: Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj
                product_family: razorwire
                classification: excluded
                publish_decision: do_not_publish
                publish_reason: Held until the CLI package shape is stable.
                release_status: excluded
                commercial_status: not_applicable
                release_notes_path: releases/unreleased.md
                order: 40
                note: CLI package is excluded from direct install guidance.
            """);
        await WriteFileAsync(
            "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
            "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj", "<Project />");
        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli")
        });

        var documents = await generator.GenerateDocumentsAsync(CreateRequest());

        Assert.Contains("### Not in the direct-install matrix", documents.ChooserMarkdown, StringComparison.Ordinal);
        Assert.Contains("transitive package evidence complete", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("proof-host evidence complete", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("excluded by publish decision", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("Internal support", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("Forge Trust", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("RazorWire", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("`ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64`", documents.ReadinessMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDocumentsAsync_RendersSameRepositoryBlockerAndPlainTextNote()
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                readiness_blocker: "#264"
                readiness_note: Waiting on package owner sign-off after the same-repo blocker is closed.
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var documents = await generator.GenerateDocumentsAsync(CreateRequest());

        Assert.Contains("`ForgeTrust.AppSurface.Web`", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("blocked", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("Blocker: #264", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("Note: Waiting on package owner sign-off", documents.ReadinessMarkdown, StringComparison.Ordinal);
        Assert.Contains("Maintainer blocker #264 is set", documents.ReadinessMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_ThrowsWhenGeneratedReadinessDashboardIsMissing()
    {
        await WriteProgramRepoAsync();
        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });
        var request = CreateRequest();
        await generator.GenerateToFileAsync(request);
        File.Delete(request.ReadinessOutputPath);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.VerifyAsync(request));

        Assert.Contains("Missing generated package readiness dashboard", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_ThrowsWhenGeneratedReadinessDashboardIsStale()
    {
        await WriteProgramRepoAsync();
        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });
        var request = CreateRequest();
        await generator.GenerateToFileAsync(request);
        await File.WriteAllTextAsync(request.ReadinessOutputPath, "stale readiness", Encoding.UTF8);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.VerifyAsync(request));

        Assert.Contains("package readiness dashboard", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((int)PackageClassification.Public, (int)PackagePublishDecision.Publish, (int)PackageReleaseStatus.PublicPreview, (int)PackageCommercialStatus.CommercialReady, (int)PackageReadinessStatus.ManifestReady)]
    [InlineData((int)PackageClassification.Support, (int)PackagePublishDecision.SupportPublish, (int)PackageReleaseStatus.SupportRuntime, (int)PackageCommercialStatus.NotApplicable, (int)PackageReadinessStatus.TransitiveReady)]
    [InlineData((int)PackageClassification.ProofHost, (int)PackagePublishDecision.DoNotPublish, (int)PackageReleaseStatus.ProofHost, (int)PackageCommercialStatus.NotApplicable, (int)PackageReadinessStatus.ProofReady)]
    [InlineData((int)PackageClassification.Excluded, (int)PackagePublishDecision.DoNotPublish, (int)PackageReleaseStatus.Excluded, (int)PackageCommercialStatus.NotApplicable, (int)PackageReadinessStatus.Excluded)]
    public async Task PackageReadinessEvaluator_ComputesExpectedStatus(
        int classificationValue,
        int publishDecisionValue,
        int releaseStatusValue,
        int commercialStatusValue,
        int expectedStatusValue)
    {
        var classification = (PackageClassification)classificationValue;
        var publishDecision = (PackagePublishDecision)publishDecisionValue;
        var releaseStatus = (PackageReleaseStatus)releaseStatusValue;
        var commercialStatus = (PackageCommercialStatus)commercialStatusValue;
        var expectedStatus = (PackageReadinessStatus)expectedStatusValue;
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        var projectPath = $"packages/{classification}/Package.csproj";
        var entry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                projectPath,
                classification,
                publishDecision,
                releaseStatus: releaseStatus,
                commercialStatus: commercialStatus),
            CreateMetadata(projectPath, $"ForgeTrust.AppSurface.{classification}"));

        var readiness = PackageReadinessEvaluator.Evaluate(_repositoryRoot, [entry]).Single();

        Assert.Equal(expectedStatus, readiness.Status);
        Assert.Empty(readiness.BlockingReasons);
        Assert.NotEmpty(readiness.Evidence);
    }

    [Fact]
    public async Task PackageReadinessEvaluator_BlocksPublicPublishWhenReleaseMetadataIsMissing()
    {
        var projectPath = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
        var entry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                projectPath,
                PackageClassification.Public,
                PackagePublishDecision.Publish,
                releaseStatus: PackageReleaseStatus.Unknown,
                commercialStatus: PackageCommercialStatus.Unknown,
                releaseNotesPath: null),
            CreateMetadata(projectPath, "ForgeTrust.AppSurface.Web"));

        var readiness = PackageReadinessEvaluator.Evaluate(_repositoryRoot, [entry]).Single();

        Assert.Equal(PackageReadinessStatus.Blocked, readiness.Status);
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("release_status", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(readiness.FixHints, hint => hint.Contains("release_status: public_preview", StringComparison.Ordinal));
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("release_notes_path is missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackageReadinessEvaluator_BlocksDependencyEvidenceMismatch()
    {
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        var webPath = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
        var corePath = "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj";
        var webEntry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                webPath,
                PackageClassification.Public,
                PackagePublishDecision.Publish,
                expectedDependencyPackageIds: ["ForgeTrust.AppSurface.Config"]),
            CreateMetadata(
                webPath,
                "ForgeTrust.AppSurface.Web",
                projectReferences: [corePath]));
        var coreEntry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                corePath,
                PackageClassification.Excluded,
                PackagePublishDecision.DoNotPublish,
                releaseStatus: PackageReleaseStatus.Excluded,
                commercialStatus: PackageCommercialStatus.NotApplicable),
            CreateMetadata(corePath, "ForgeTrust.AppSurface.Core"));

        var readiness = PackageReadinessEvaluator.Evaluate(_repositoryRoot, [webEntry, coreEntry]).Single(entry => entry.ProjectPath == webPath);

        Assert.Equal(PackageReadinessStatus.Blocked, readiness.Status);
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("expected_dependency_package_ids", StringComparison.Ordinal));
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains("ForgeTrust.AppSurface.Core", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackageReadinessEvaluator_SeparatesConceptualDependsOnFromPackageDependencyEvidence()
    {
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        var webPath = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
        var entry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                webPath,
                PackageClassification.Public,
                PackagePublishDecision.Publish,
                dependsOn: ["ForgeTrust.AppSurface.Core"],
                expectedDependencyPackageIds: []),
            CreateMetadata(webPath, "ForgeTrust.AppSurface.Web"));

        var readiness = PackageReadinessEvaluator.Evaluate(_repositoryRoot, [entry]).Single();

        Assert.Equal(PackageReadinessStatus.ManifestReady, readiness.Status);
        Assert.Contains(readiness.Evidence, evidence => evidence.Contains("expected_dependency_package_ids match project references", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackageReadinessEvaluator_MatchesExpectedDependencyPackageIdsWithFirstPartyReferences()
    {
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        var webPath = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
        var corePath = "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj";
        var webEntry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                webPath,
                PackageClassification.Public,
                PackagePublishDecision.Publish,
                expectedDependencyPackageIds: ["ForgeTrust.AppSurface.Core"]),
            CreateMetadata(
                webPath,
                "ForgeTrust.AppSurface.Web",
                projectReferences: [corePath]));
        var coreEntry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                corePath,
                PackageClassification.Excluded,
                PackagePublishDecision.DoNotPublish,
                releaseStatus: PackageReleaseStatus.Excluded,
                commercialStatus: PackageCommercialStatus.NotApplicable),
            CreateMetadata(corePath, "ForgeTrust.AppSurface.Core"));

        var readiness = PackageReadinessEvaluator.Evaluate(_repositoryRoot, [webEntry, coreEntry]).Single(entry => entry.ProjectPath == webPath);

        Assert.Equal(PackageReadinessStatus.ManifestReady, readiness.Status);
        Assert.Empty(readiness.BlockingReasons);
        Assert.Contains(readiness.Evidence, evidence => evidence.Contains("expected_dependency_package_ids match project references", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackageReadinessEvaluator_IgnoresMissingPublishDecisionUntilManifestValidation()
    {
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        var projectPath = "Packages/SupportUndeclared/SupportUndeclared.csproj";
        var entry = new ResolvedPackageEntry(
            CreateReadinessManifestEntry(
                projectPath,
                PackageClassification.Support,
                null,
                releaseStatus: PackageReleaseStatus.SupportRuntime,
                commercialStatus: PackageCommercialStatus.NotApplicable),
            CreateMetadata(projectPath, "ForgeTrust.AppSurface.SupportUndeclared"));

        var readiness = PackageReadinessEvaluator.Evaluate(_repositoryRoot, [entry]).Single();

        Assert.Equal(PackageReadinessStatus.ManifestReady, readiness.Status);
        Assert.DoesNotContain(readiness.Evidence, evidence => evidence.Contains("publish_decision", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackageReadinessEvaluator_BlocksPublishAndProjectEvidenceProblems()
    {
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        var entries = new[]
        {
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/PublicHeld/PublicHeld.csproj",
                    PackageClassification.Public,
                    PackagePublishDecision.DoNotPublish,
                    publishReason: "Held for owner review."),
                CreateMetadata("Packages/PublicHeld/PublicHeld.csproj", "ForgeTrust.AppSurface.PublicHeld")),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/SupportDirect/SupportDirect.csproj",
                    PackageClassification.Support,
                    PackagePublishDecision.Publish,
                    releaseStatus: PackageReleaseStatus.SupportRuntime,
                    commercialStatus: PackageCommercialStatus.NotApplicable),
                CreateMetadata("Packages/SupportDirect/SupportDirect.csproj", "ForgeTrust.AppSurface.SupportDirect")),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/ProofDirect/ProofDirect.csproj",
                    PackageClassification.ProofHost,
                    PackagePublishDecision.Publish,
                    releaseStatus: PackageReleaseStatus.ProofHost,
                    commercialStatus: PackageCommercialStatus.NotApplicable),
                CreateMetadata("Packages/ProofDirect/ProofDirect.csproj", "ForgeTrust.AppSurface.ProofDirect")),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/ExcludedMaybe/ExcludedMaybe.csproj",
                    PackageClassification.Excluded,
                    PackagePublishDecision.SupportPublish,
                    releaseStatus: PackageReleaseStatus.Excluded,
                    commercialStatus: PackageCommercialStatus.NotApplicable),
                CreateMetadata("Packages/ExcludedMaybe/ExcludedMaybe.csproj", "ForgeTrust.AppSurface.ExcludedMaybe")),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/MissingReason/MissingReason.csproj",
                    PackageClassification.Excluded,
                    PackagePublishDecision.DoNotPublish,
                    releaseStatus: PackageReleaseStatus.Excluded,
                    commercialStatus: PackageCommercialStatus.NotApplicable,
                    publishReason: string.Empty),
                CreateMetadata("Packages/MissingReason/MissingReason.csproj", "ForgeTrust.AppSurface.MissingReason")),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/ToolDeps/ToolDeps.csproj",
                    PackageClassification.Public,
                    PackagePublishDecision.Publish,
                    expectedDependencyPackageIds: ["ForgeTrust.AppSurface.Core"]),
                CreateMetadata(
                    "Packages/ToolDeps/ToolDeps.csproj",
                    "ForgeTrust.AppSurface.ToolDeps",
                    outputType: "Exe",
                    isTool: true)),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/NotPackable/NotPackable.csproj",
                    PackageClassification.Public,
                    PackagePublishDecision.Publish),
                CreateMetadata(
                    "Packages/NotPackable/NotPackable.csproj",
                    "ForgeTrust.AppSurface.NotPackable",
                    isPackable: false)),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/ToolLibrary/ToolLibrary.csproj",
                    PackageClassification.Public,
                    PackagePublishDecision.Publish),
                CreateMetadata(
                    "Packages/ToolLibrary/ToolLibrary.csproj",
                    "ForgeTrust.AppSurface.ToolLibrary",
                    isTool: true)),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/PublicExe/PublicExe.csproj",
                    PackageClassification.Public,
                    PackagePublishDecision.Publish),
                CreateMetadata(
                    "Packages/PublicExe/PublicExe.csproj",
                    "ForgeTrust.AppSurface.PublicExe",
                    outputType: "Exe")),
            new ResolvedPackageEntry(
                CreateReadinessManifestEntry(
                    "Packages/BrokenNotes/BrokenNotes.csproj",
                    PackageClassification.Public,
                    PackagePublishDecision.Publish,
                    releaseNotesPath: "releases/missing.md"),
                CreateMetadata("Packages/BrokenNotes/BrokenNotes.csproj", "ForgeTrust.AppSurface.BrokenNotes"))
        };

        var readinessByPackage = PackageReadinessEvaluator.Evaluate(_repositoryRoot, entries)
            .ToDictionary(item => item.PackageId, StringComparer.Ordinal);

        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.PublicHeld", "Public package is not marked publish", "publish_decision: publish");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.SupportDirect", "Support package uses direct publish", "support_publish or do_not_publish");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.ProofDirect", "Proof-host package uses direct publish", "support_publish or do_not_publish");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.ExcludedMaybe", "Excluded package is not marked do_not_publish", "publish_decision: do_not_publish");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.MissingReason", "publish_reason is missing", "Add publish_reason");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.ToolDeps", "Tool packages must not define expected package dependencies", "Remove expected_dependency_package_ids");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.NotPackable", "not packable", "Make Packages/NotPackable/NotPackable.csproj packable");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.ToolLibrary", "Public tool package output type is Library", "Set OutputType Exe");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.PublicExe", "Public direct-install package output type is Exe", "Set OutputType Library");
        AssertBlocked(readinessByPackage, "ForgeTrust.AppSurface.BrokenNotes", "release_notes_path", "existing repository Markdown file");
    }

    [Fact]
    public async Task VerifyAsync_ThrowsWhenGeneratedReadmeIsStale()
    {
        await WriteCommonChooserFilesAsync(includeUnreleased: true);
        await WriteFileAsync("packages/README.md", "# stale");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
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
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj",
                "ForgeTrust.AppSurface.Web.OpenApi"),
            ["Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
                "ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64"),
            ["Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj",
                "ForgeTrust.AppSurface.Docs"),
            ["Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj"] = CreateMetadata(
                "Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj",
                "ForgeTrust.RazorWire.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.VerifyAsync(CreateRequest()));

        Assert.Contains("Missing generated package chooser", error.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Equal(Path.Join(_repositoryRoot, "packages", "package-index.yml"), defaults.Request.ManifestPath);
        Assert.Equal(Path.Join(_repositoryRoot, "packages", "README.md"), defaults.Request.ChooserOutputPath);
        Assert.Equal(Path.Join(_repositoryRoot, "packages", "readiness.md"), defaults.Request.ReadinessOutputPath);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "packages"), defaults.ArtifactsOutputPath);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "packages"), defaults.ArtifactsInputPath);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "package-artifact-manifest.json"), defaults.ArtifactManifestPath);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "package-validation-report.md"), defaults.ReportPath);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "package-publish-log.md"), defaults.PublishLogPath);
        Assert.Equal("https://api.nuget.org/v3/index.json", defaults.Source);
        Assert.Equal("NUGET_API_KEY", defaults.ApiKeyEnvironmentVariable);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "package-smoke"), defaults.SmokeWorkDirectory);
        Assert.Equal(Path.Join(_repositoryRoot, "artifacts", "package-smoke-report.md"), defaults.SmokeReportPath);
        Assert.Null(defaults.PackageVersion);

        var parsed = CommandLineOptions.Parse(
            [
                "--repo-root", "src",
                "--manifest", "manifest.yml",
                "--output", "chooser.md",
                "--readiness-output", "evidence.md",
                "--artifacts-output", "packages-out",
                "--artifacts-input", "packages-in",
                "--artifact-manifest", "artifact-manifest.json",
                "--package-version", "0.0.0-ci.99",
                "--report", "package-report.md",
                "--publish-log", "publish-log.md",
                "--source", "https://example.test/v3/index.json",
                "--api-key-env", "CUSTOM_NUGET_KEY",
                "--smoke-work-dir", "smoke",
                "--smoke-report", "smoke-report.md"
            ],
            _repositoryRoot);

        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src")), parsed.Request.RepositoryRoot);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "manifest.yml")), parsed.Request.ManifestPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "chooser.md")), parsed.Request.ChooserOutputPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "evidence.md")), parsed.Request.ReadinessOutputPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "packages-out")), parsed.ArtifactsOutputPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "packages-in")), parsed.ArtifactsInputPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "artifact-manifest.json")), parsed.ArtifactManifestPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "package-report.md")), parsed.ReportPath);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "publish-log.md")), parsed.PublishLogPath);
        Assert.Equal("https://example.test/v3/index.json", parsed.Source);
        Assert.Equal("CUSTOM_NUGET_KEY", parsed.ApiKeyEnvironmentVariable);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "smoke")), parsed.SmokeWorkDirectory);
        Assert.Equal(Path.GetFullPath(Path.Join(_repositoryRoot, "src", "smoke-report.md")), parsed.SmokeReportPath);
        Assert.Equal("0.0.0-ci.99", parsed.PackageVersion);

        var absoluteManifest = Path.Combine(_repositoryRoot, "abs", "manifest.yml");
        var absoluteOutput = Path.Combine(_repositoryRoot, "abs", "chooser.md");
        var absoluteArtifacts = Path.Combine(_repositoryRoot, "abs", "artifacts");
        var absoluteArtifactsInput = Path.Combine(_repositoryRoot, "abs", "artifacts-in");
        var absoluteArtifactManifest = Path.Combine(_repositoryRoot, "abs", "manifest.json");
        var absoluteReport = Path.Combine(_repositoryRoot, "abs", "report.md");
        var absolutePublishLog = Path.Combine(_repositoryRoot, "abs", "publish.md");
        var absoluteSmokeWorkDirectory = Path.Combine(_repositoryRoot, "abs", "smoke");
        var absoluteSmokeReport = Path.Combine(_repositoryRoot, "abs", "smoke.md");
        var absolute = CommandLineOptions.Parse(
            [
                "--manifest", absoluteManifest,
                "--output", absoluteOutput,
                "--artifacts-output", absoluteArtifacts,
                "--artifacts-input", absoluteArtifactsInput,
                "--artifact-manifest", absoluteArtifactManifest,
                "--report", absoluteReport,
                "--publish-log", absolutePublishLog,
                "--smoke-work-dir", absoluteSmokeWorkDirectory,
                "--smoke-report", absoluteSmokeReport
            ],
            _repositoryRoot);

        Assert.Equal(absoluteManifest, absolute.Request.ManifestPath);
        Assert.Equal(absoluteOutput, absolute.Request.ChooserOutputPath);
        Assert.Equal(Path.Join(_repositoryRoot, "packages", "readiness.md"), absolute.Request.ReadinessOutputPath);
        Assert.Equal(absoluteArtifacts, absolute.ArtifactsOutputPath);
        Assert.Equal(absoluteArtifactsInput, absolute.ArtifactsInputPath);
        Assert.Equal(absoluteArtifactManifest, absolute.ArtifactManifestPath);
        Assert.Equal(absoluteReport, absolute.ReportPath);
        Assert.Equal(absolutePublishLog, absolute.PublishLogPath);
        Assert.Equal(absoluteSmokeWorkDirectory, absolute.SmokeWorkDirectory);
        Assert.Equal(absoluteSmokeReport, absolute.SmokeReportPath);
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
    public void PackageIndexRequest_OutputPath_ReturnsChooserOutputPath()
    {
        var request = new PackageIndexRequest(
            _repositoryRoot,
            Path.Join(_repositoryRoot, "packages", "package-index.yml"),
            Path.Join(_repositoryRoot, "packages", "chooser.md"),
            Path.Join(_repositoryRoot, "packages", "readiness.md"));

        Assert.Equal(request.ChooserOutputPath, request.OutputPath);
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
    [InlineData("verify-packages", "--help")]
    [InlineData("publish-prerelease", "--help")]
    [InlineData("publish-stable", "--help")]
    [InlineData("smoke-install", "--help")]
    [InlineData("gate", "--help")]
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
        Assert.Contains("Generated packages/README.md and packages/readiness.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\\', stdout.ToString());
        Assert.Contains("Package chooser and readiness dashboard are up to date: packages/README.md, packages/readiness.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.True(File.Exists(Path.Join(_repositoryRoot, "packages", "README.md")));
        Assert.True(File.Exists(Path.Join(_repositoryRoot, "packages", "readiness.md")));
    }

    [Fact]
    public async Task RunPackageGateAsync_SucceedsWithReleaseMetadataAndCleanBrandScan()
    {
        await WriteProgramRepoAsync();

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var report = await generator.RunPackageGateAsync(CreateRequest());

        Assert.Equal(1, report.PackageCount);
        Assert.True(report.ScannedFileCount > 0);
    }

    [Fact]
    public async Task RunPackageGateAsync_AllowsPublicToolPackages()
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 20
                use_when: Install this when you want repository-level AppSurface tooling.
                includes: The `appsurface` .NET tool command.
                does_not_include: App runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# AppSurface CLI");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var report = await generator.RunPackageGateAsync(CreateRequest());

        Assert.Equal(2, report.PackageCount);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenPublicToolCommandNameIsMissing()
    {
        await WritePublicToolRepoAsync(toolCommandName: null);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
        Assert.Contains("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenSupportPublishToolCommandNameIsMissing()
    {
        await WritePublicToolRepoAsync(toolCommandName: null, publishDecision: "support_publish");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
        Assert.Contains("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPackageGateAsync_AllowsSupportPublishToolCommandNameWhenValid()
    {
        await WritePublicToolRepoAsync("appsurface", publishDecision: "support_publish");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var report = await generator.RunPackageGateAsync(CreateRequest());

        Assert.Equal(2, report.PackageCount);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenNonToolDefinesToolCommandName()
    {
        await WritePublicToolRepoAsync("appsurface");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
        Assert.Contains("PackAsTool=true", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app surface")]
    [InlineData("../appsurface")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("app:surface")]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("Com1")]
    [InlineData("clock$")]
    [InlineData("con.txt")]
    [InlineData("appsurface.")]
    public async Task RunPackageGateAsync_ThrowsWhenToolCommandNameIsInvalid(string toolCommandName)
    {
        await WritePublicToolRepoAsync(toolCommandName);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(toolCommandName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateToolCommandNameValue_ThrowsWhenCommandNameIsMissing()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => PackageIndexGenerator.ValidateToolCommandNameValue("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", string.Empty));

        Assert.Contains("tool_command_name", error.Message, StringComparison.Ordinal);
        Assert.Contains("must be provided", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPackageGateAsync_AllowsExcludedToolCommandNameWhenValid()
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: excluded
                publish_decision: do_not_publish
                publish_reason: Internal repository tool is not published.
                release_status: excluded
                commercial_status: not_applicable
                release_notes_path: releases/unreleased.md
                order: 20
                note: Internal tool.
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Exe")
        });

        var report = await generator.RunPackageGateAsync(CreateRequest());

        Assert.Equal(2, report.PackageCount);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenPublicToolIsNotExecutable()
    {
        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 20
                use_when: Install this when you want repository-level AppSurface tooling.
                includes: The `appsurface` .NET tool command.
                does_not_include: App runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
                tool_command_name: appsurface
            """);
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# AppSurface CLI");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web"),
            ["Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"] = CreateMetadata(
                "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
                "ForgeTrust.AppSurface.Cli",
                isTool: true,
                outputType: "Library")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("Public .NET tool packages must be executable projects", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenReleaseMetadataIsMissing()
    {
        await WriteProgramRepoAsync(includeReleaseMetadata: false);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("release_status", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenReleaseNotesPathEscapesRepositoryRoot()
    {
        await WriteProgramRepoAsync(releaseNotesPath: "../outside.md");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains("outside the repository root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRepositoryFilePath_ThrowsWhenRepositoryPathIsBlank()
    {
        var error = Assert.Throws<PackageIndexException>(
            () => PackageIndexGenerator.ResolveRepositoryFilePath(_repositoryRoot, " ", "Release note"));

        Assert.Contains("Release note must define a repository-relative file path.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenStaleBrandStringIsNotAllowlisted()
    {
        await WriteProgramRepoAsync();
        var staleBrand = "Run" + "nable";
        await WriteFileAsync("docs/stale.md", $"Old {staleBrand} naming should fail.");

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains($"Stale brand string '{staleBrand}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("docs/stale.md", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPackageGateAsync_ThrowsWhenUppercaseStaleBrandStringIsInProjectFile()
    {
        await WriteProgramRepoAsync();
        var staleBrand = "RUN" + "NABLE";
        await WriteFileAsync(
            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>ForgeTrust.AppSurface.Web</PackageId>
                <DefineConstants>{{staleBrand}}</DefineConstants>
              </PropertyGroup>
            </Project>
            """);

        var generator = CreateGenerator(new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"] = CreateMetadata(
                "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                "ForgeTrust.AppSurface.Web")
        });

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => generator.RunPackageGateAsync(CreateRequest()));

        Assert.Contains($"Stale brand string '{staleBrand}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.AppSurface.Web.csproj", error.Message, StringComparison.Ordinal);
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
                "--artifact-manifest", "reports/artifacts.json",
                "--report", "reports/packages.md",
                "--coverage-proof-work-dir", "reports/coverage-proof",
                "--coverage-proof-report", "reports/coverage-proof.md"
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
                            "ForgeTrust.AppSurface.Web",
                            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                            PackagePublishDecision.Publish,
                            [])
                    ]));
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(Path.Join(_repositoryRoot, "packages-out"), capturedRequest.ArtifactsOutputPath);
        Assert.Equal(Path.Join(_repositoryRoot, "reports", "packages.md"), capturedRequest.ReportPath);
        Assert.Equal(Path.Join(_repositoryRoot, "reports", "artifacts.json"), capturedRequest.ArtifactManifestPath);
        Assert.Equal(Path.Join(_repositoryRoot, "reports", "coverage-proof"), capturedRequest.CoverageProofWorkDirectory);
        Assert.Equal(Path.Join(_repositoryRoot, "reports", "coverage-proof.md"), capturedRequest.CoverageProofReportPath);
        Assert.Equal("https://api.nuget.org/v3/index.json", capturedRequest.Source);
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
    public async Task RunAsync_PublishPrerelease_UsesWorkflowAndReturnsFailureWhenLedgerFails()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        PackagePublishRequest? capturedRequest = null;

        var exitCode = await Program.RunAsync(
            [
                "publish-prerelease",
                "--artifacts-input", "packages-in",
                "--artifact-manifest", "packages-in/artifacts.json",
                "--publish-log", "reports/publish.md",
                "--source", "https://example.test/v3/index.json",
                "--api-key-env", "TEST_KEY"
            ],
            stdout,
            stderr,
            _repositoryRoot,
            publishPrereleaseAsync: (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new PackagePublishLedger(
                    "0.0.0-ci.99",
                    request.Source,
                    [
                        new PackagePublishLedgerEntry(
                            "ForgeTrust.AppSurface.Web",
                            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                            "ForgeTrust.AppSurface.Web.0.0.0-ci.99.nupkg",
                            PackagePublishStatus.Failed,
                            1,
                            "failed")
                    ]));
            });

        Assert.Equal(1, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(TestPathUtils.PathUnder(_repositoryRoot, "packages-in"), capturedRequest.ArtifactsInputPath);
        Assert.Equal(TestPathUtils.PathUnder(_repositoryRoot, "packages-in/artifacts.json"), capturedRequest.ArtifactManifestPath);
        Assert.Equal(TestPathUtils.PathUnder(_repositoryRoot, "reports/publish.md"), capturedRequest.PublishLogPath);
        Assert.Equal("https://example.test/v3/index.json", capturedRequest.Source);
        Assert.Equal("TEST_KEY", capturedRequest.ApiKeyEnvironmentVariable);
        Assert.Contains("Published 0 prerelease package artifacts for 0.0.0-ci.99. Log: reports/publish.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_PublishPrerelease_ReturnsSuccessWhenLedgerHasNoFailures()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "publish-prerelease",
                "--publish-log", "reports/publish.md",
                "--source", "https://example.test/v3/index.json"
            ],
            stdout,
            stderr,
            _repositoryRoot,
            publishPrereleaseAsync: (request, _) => Task.FromResult(new PackagePublishLedger(
                "0.1.0-rc.5",
                request.Source,
                [
                    new PackagePublishLedgerEntry(
                        "ForgeTrust.AppSurface.Core",
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core.0.1.0-rc.5.nupkg",
                        PackagePublishStatus.Pushed,
                        0,
                        string.Empty),
                    new PackagePublishLedgerEntry(
                        "ForgeTrust.AppSurface.Web",
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        "ForgeTrust.AppSurface.Web.0.1.0-rc.5.nupkg",
                        PackagePublishStatus.DuplicateReported,
                        0,
                        "already exists")
                ])));

        Assert.Equal(0, exitCode);
        Assert.Contains("Published 2 prerelease package artifacts for 0.1.0-rc.5. Log: reports/publish.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_PublishStable_UsesWorkflowAndReturnsFailureWhenLedgerFails()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        PackagePublishRequest? capturedRequest = null;

        var exitCode = await Program.RunAsync(
            [
                "publish-stable",
                "--artifacts-input", "packages-in",
                "--artifact-manifest", "packages-in/artifacts.json",
                "--publish-log", "reports/publish.md",
                "--source", "https://example.test/v3/index.json",
                "--api-key-env", "TEST_KEY"
            ],
            stdout,
            stderr,
            _repositoryRoot,
            publishStableAsync: (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new PackagePublishLedger(
                    "0.1.0",
                    request.Source,
                    [
                        new PackagePublishLedgerEntry(
                            "ForgeTrust.AppSurface.Web",
                            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                            "ForgeTrust.AppSurface.Web.0.1.0.nupkg",
                            PackagePublishStatus.Failed,
                            1,
                            "failed")
                    ]));
            });

        Assert.Equal(1, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(TestPathUtils.PathUnder(_repositoryRoot, "packages-in"), capturedRequest.ArtifactsInputPath);
        Assert.Equal(TestPathUtils.PathUnder(_repositoryRoot, "packages-in/artifacts.json"), capturedRequest.ArtifactManifestPath);
        Assert.Equal(TestPathUtils.PathUnder(_repositoryRoot, "reports/publish.md"), capturedRequest.PublishLogPath);
        Assert.Equal("https://example.test/v3/index.json", capturedRequest.Source);
        Assert.Equal("TEST_KEY", capturedRequest.ApiKeyEnvironmentVariable);
        Assert.Contains("Published 0 stable package artifacts for 0.1.0. Log: reports/publish.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_PublishStable_ReturnsSuccessWhenLedgerHasNoFailures()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            [
                "publish-stable",
                "--publish-log", "reports/publish.md",
                "--source", "https://example.test/v3/index.json"
            ],
            stdout,
            stderr,
            _repositoryRoot,
            publishStableAsync: (request, _) => Task.FromResult(new PackagePublishLedger(
                "0.1.0",
                request.Source,
                [
                    new PackagePublishLedgerEntry(
                        "ForgeTrust.AppSurface.Core",
                        "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
                        "ForgeTrust.AppSurface.Core.0.1.0.nupkg",
                        PackagePublishStatus.Pushed,
                        0,
                        string.Empty),
                    new PackagePublishLedgerEntry(
                        "ForgeTrust.AppSurface.Web",
                        "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                        "ForgeTrust.AppSurface.Web.0.1.0.nupkg",
                        PackagePublishStatus.DuplicateReported,
                        0,
                        "already exists")
                ])));

        Assert.Equal(0, exitCode);
        Assert.Contains("Published 2 stable package artifacts for 0.1.0. Log: reports/publish.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_PublishStable_DefaultWorkflowReportsMissingArtifacts()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await WriteFileAsync(
            "packages/package-index.yml",
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

        var exitCode = await Program.RunAsync(["publish-stable"], stdout, stderr, _repositoryRoot);

        Assert.Equal(1, exitCode);
        Assert.Contains("Package artifact input directory", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_SmokeInstall_UsesWorkflowAndReturnsSuccess()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        PackageSmokeInstallRequest? capturedRequest = null;

        var exitCode = await Program.RunAsync(
            [
                "smoke-install",
                "--artifact-manifest", "packages-in/artifacts.json",
                "--smoke-work-dir", "smoke",
                "--smoke-report", "reports/smoke.md",
                "--source", "https://example.test/v3/index.json"
            ],
            stdout,
            stderr,
            _repositoryRoot,
            smokeInstallAsync: (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new PackageSmokeInstallReport(
                    "0.0.0-ci.99",
                    request.Source,
                    [
                        new PackageSmokeInstallReportEntry(
                            "ForgeTrust.AppSurface.Web",
                            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                            IsTool: false,
                            PackageSmokeInstallStatus.Restored,
                            0,
                            "restored")
                    ]));
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedRequest);
        Assert.Equal(Path.Combine(_repositoryRoot, "packages", "package-index.yml"), capturedRequest.ManifestPath);
        Assert.Equal(Path.Combine(_repositoryRoot, "packages-in", "artifacts.json"), capturedRequest.ArtifactManifestPath);
        Assert.Equal(Path.Combine(_repositoryRoot, "smoke"), capturedRequest.WorkDirectory);
        Assert.Equal(Path.Combine(_repositoryRoot, "reports", "smoke.md"), capturedRequest.ReportPath);
        Assert.Equal("https://example.test/v3/index.json", capturedRequest.Source);
        Assert.Contains("Smoke installed 1 published packages for 0.0.0-ci.99. Report: reports/smoke.md.", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
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
        Assert.False(PackageProjectScanner.IsCandidateProject("tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("Web/ForgeTrust.AppSurface.Web/bin/Release/net10.0/Generated.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("Web/ForgeTrust.AppSurface.Web/obj/Release/net10.0/Generated.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("node_modules/package/Generated.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("examples/web-app/WebAppExample.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("tests/ForgeTrust.AppSurface.Testing/ForgeTrust.AppSurface.Testing.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("Web/ForgeTrust.RazorWire.IntegrationTests/ForgeTrust.RazorWire.IntegrationTests.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("Config/ForgeTrust.AppSurface.Config.Tests/ForgeTrust.AppSurface.Config.Tests.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("tests/Fixture/Fixture.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("src/BenchmarkHarness.Benchmarks.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject("src/FixtureTests.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject(".pnpm-store/v11/projects/cache-key/Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"));
        Assert.False(PackageProjectScanner.IsCandidateProject(".nuget/packages/example/1.0.0/contentFiles/any/net10.0/CachedProject.csproj"));
        Assert.True(PackageProjectScanner.IsCandidateProject("RootPackage.csproj"));
        Assert.True(PackageProjectScanner.IsCandidateProject("Web/ForgeTrust.AppSurface.Docs.Standalone/ForgeTrust.AppSurface.Docs.Standalone.csproj"));
    }

    [Fact]
    public async Task DiscoverProjects_PrunesHiddenCacheDirectories()
    {
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync(".pnpm-store/v11/projects/cache-key/Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync(".nuget/packages/example/1.0.0/contentFiles/any/net10.0/CachedProject.csproj", "<Project />");

        var projects = new PackageProjectScanner().DiscoverProjects(_repositoryRoot);

        Assert.Equal(["Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj"], projects);
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
                "PackageId": "ForgeTrust.AppSurface.Web",
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
                    "FullPath": "/repo/src/BuildTask/BuildTask.csproj",
                    "ReferenceOutputAssembly": "false"
                  },
                  {
                    "FullPath": "/repo/src/Dependency/Dependency.csproj"
                  }
                ]
              }
            }
            """;

        var metadata = DotNetProjectMetadataProvider.ParseMetadataJson(
            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
            standardOutput);

        Assert.Equal("ForgeTrust.AppSurface.Web", metadata.PackageId);
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
                "PackageId": "ForgeTrust.AppSurface.Console",
                "TargetFramework": "net10.0",
                "IsPackable": "false",
                "PackAsTool": "true",
                "OutputType": "Exe"
              }
            }
            """;

        var metadata = DotNetProjectMetadataProvider.ParseMetadataJson(
            "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
            standardOutput);

        Assert.Equal("ForgeTrust.AppSurface.Console", metadata.PackageId);
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
                "PackageId": "ForgeTrust.AppSurface.Console",
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
                "Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj",
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
    public async Task ProcessCommandRunner_ThrowsWhenProcessStartReturnsFalse()
    {
        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => new ProcessCommandRunner(_ => false).RunAsync(
                new CommandRunRequest(
                    "dotnet",
                    ["--info"],
                    _repositoryRoot,
                    "dotnet msbuild",
                    "missing/Nope.csproj",
                    "evaluate",
                    "evaluating",
                    100),
                CancellationToken.None));

        Assert.Contains("process did not start", error.Message, StringComparison.OrdinalIgnoreCase);
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
            TestPathUtils.PathUnder(_repositoryRoot, outputRelativePath));
    }

    private async Task WriteCommonChooserFilesAsync(bool includeUnreleased)
    {
        await WriteFileAsync(
            "packages/README.md.yml",
            """
            title: AppSurface v0.1 package chooser
            """);
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup, middleware composition, and endpoint registration.
                does_not_include: OpenAPI, hosted API docs UI, and Tailwind asset compilation.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
                recipe_summary: Add `ForgeTrust.AppSurface.Web.OpenApi` after `ForgeTrust.AppSurface.Web` when you want an OpenAPI document.
              - project: Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                order: 20
                use_when: Add this after the base web package when you want an OpenAPI document.
                includes: OpenAPI generation and endpoint explorer wiring.
                does_not_include: A hosted API reference UI.
                start_here_path: Web/ForgeTrust.AppSurface.Web.OpenApi/README.md
              - project: Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj
                product_family: internal_support
                classification: support
                publish_decision: support_publish
                order: 30
                note: Restored transitively by `ForgeTrust.AppSurface.Web.Tailwind` on matching build hosts. Do not install it directly.
              - project: Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj
                product_family: forge_trust
                classification: proof_host
                publish_decision: do_not_publish
                publish_reason: Proof-host package is not part of the prerelease package surface.
                order: 40
                note: Reusable docs package for hosting harvested repository docs.
                start_here_path: Web/ForgeTrust.AppSurface.Docs/README.md
              - project: Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj
                product_family: razorwire
                classification: public
                publish_decision: publish
                order: 50
                use_when: Install this when you want to export RazorWire apps from a stable command-line tool.
                includes: The `razorwire` .NET tool command and static export workflow.
                does_not_include: The RazorWire runtime package or coordinated package publishing automation.
                start_here_path: Web/ForgeTrust.RazorWire.Cli/README.md
                tool_command_name: razorwire
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.OpenApi/ForgeTrust.AppSurface.Web.OpenApi.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web.OpenApi/README.md", "# OpenApi");
        await WriteFileAsync(
            "Web/ForgeTrust.AppSurface.Web.Tailwind/runtimes/ForgeTrust.AppSurface.Web.Tailwind.Runtime.osx-arm64.csproj",
            "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Docs/ForgeTrust.AppSurface.Docs.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Docs/README.md", "# AppSurfaceDocs");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/ForgeTrust.RazorWire.Cli.csproj", "<Project />");
        await WriteFileAsync("Web/ForgeTrust.RazorWire.Cli/README.md", "# RazorWire CLI");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/v0.1-preview.md", "# v0.1 Preview");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");
        if (includeUnreleased)
        {
            await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        }
    }

    private async Task WriteProgramRepoAsync(bool includeReleaseMetadata = true, string releaseNotesPath = "releases/unreleased.md")
    {
        var releaseMetadata = includeReleaseMetadata
            ? $$"""
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: {{releaseNotesPath}}
            """
            : string.Empty;

        await WriteFileAsync(
            "packages/README.md.yml",
            """
            title: AppSurface v0.1 package chooser
            """);
        await WriteFileAsync(
            "packages/package-index.yml",
            $$"""
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
            {{releaseMetadata}}
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
            """);
        await WriteFileAsync(
            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>ForgeTrust.AppSurface.Web</PackageId>
              </PropertyGroup>
            </Project>
            """);
        await WriteFileAsync("Web/ForgeTrust.AppSurface.Web/README.md", "# Web");
        await WriteFileAsync("examples/web-app/README.md", "# Example");
        await WriteFileAsync("start-here/first-success-path.md", FirstSuccessPathMarkdown);
        await WriteFileAsync("releases/README.md", "# Releases");
        await WriteFileAsync("releases/unreleased.md", "# Unreleased");
        await WriteFileAsync("releases/upgrade-policy.md", "# Policy");
        await WriteFileAsync("CHANGELOG.md", "# Changelog");
        await WriteFileAsync(
            "rebrand/stale-brand-allowlist.txt",
            """
            # path|term|yyyy-mm-dd|reason
            """);
    }

    private async Task WritePublicToolRepoAsync(string? toolCommandName, string publishDecision = "publish")
    {
        var toolCommandYaml = toolCommandName is null
            ? string.Empty
            : $"    tool_command_name: '{toolCommandName}'";

        await WriteProgramRepoAsync();
        await WriteFileAsync(
            "packages/package-index.yml",
            $$"""
            packages:
              - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                product_family: appsurface
                classification: public
                publish_decision: publish
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 10
                use_when: Install this first for a normal ASP.NET Core app with AppSurface modules.
                includes: Base web startup.
                does_not_include: OpenAPI.
                start_here_path: Web/ForgeTrust.AppSurface.Web/README.md
              - project: Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj
                product_family: appsurface
                classification: public
                publish_decision: {{publishDecision}}
                release_status: public_preview
                commercial_status: commercial_ready
                release_notes_path: releases/unreleased.md
                order: 20
                use_when: Install this when you want repository-level AppSurface tooling.
                includes: The `appsurface` .NET tool command.
                does_not_include: App runtime packages.
                start_here_path: Cli/ForgeTrust.AppSurface.Cli/README.md
            {{toolCommandYaml}}
            """);
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj", "<Project />");
        await WriteFileAsync("Cli/ForgeTrust.AppSurface.Cli/README.md", "# AppSurface CLI");
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var fullPath = TestPathUtils.PathUnder(_repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
    }

    private static PackageProjectMetadata CreateMetadata(
        string projectPath,
        string packageId,
        string outputType = "Library",
        string targetFramework = "net10.0",
        bool isTool = false,
        bool isPackable = true,
        IReadOnlyList<string>? projectReferences = null)
    {
        return new PackageProjectMetadata(projectPath, packageId, targetFramework, isPackable, isTool, outputType, projectReferences ?? []);
    }

    private static PackageManifestEntry CreateReadinessManifestEntry(
        string projectPath,
        PackageClassification classification,
        PackagePublishDecision? publishDecision,
        string productFamily = "appsurface",
        PackageReleaseStatus releaseStatus = PackageReleaseStatus.PublicPreview,
        PackageCommercialStatus commercialStatus = PackageCommercialStatus.CommercialReady,
        string? releaseNotesPath = "releases/unreleased.md",
        string? publishReason = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? expectedDependencyPackageIds = null)
    {
        return new PackageManifestEntry
        {
            Project = projectPath,
            ProductFamily = productFamily,
            Classification = classification,
            PublishDecision = publishDecision,
            ReleaseStatus = releaseStatus,
            CommercialStatus = commercialStatus,
            ReleaseNotesPath = releaseNotesPath,
            PublishReason = publishReason ?? (publishDecision == PackagePublishDecision.DoNotPublish ? "Not part of the public package surface." : null),
            UseWhen = classification == PackageClassification.Public ? "Use when testing package readiness." : null,
            Includes = classification == PackageClassification.Public ? "Readiness evidence." : null,
            DoesNotInclude = classification == PackageClassification.Public ? "Live publish proof." : null,
            Note = classification == PackageClassification.Public ? null : "Maintainer-visible package row.",
            DependsOn = dependsOn?.ToList() ?? [],
            ExpectedDependencyPackageIds = expectedDependencyPackageIds?.ToList() ?? []
        };
    }

    private static void AssertBlocked(
        IReadOnlyDictionary<string, PackageReadinessEvidence> readinessByPackage,
        string packageId,
        string expectedReason,
        string expectedFixHint)
    {
        var readiness = readinessByPackage[packageId];
        Assert.Equal(PackageReadinessStatus.Blocked, readiness.Status);
        Assert.Contains(readiness.BlockingReasons, reason => reason.Contains(expectedReason, StringComparison.Ordinal));
        Assert.Contains(readiness.FixHints, hint => hint.Contains(expectedFixHint, StringComparison.Ordinal));
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
