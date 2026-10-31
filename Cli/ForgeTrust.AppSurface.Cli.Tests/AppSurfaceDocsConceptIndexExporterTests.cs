using System.Text.Json;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class AppSurfaceDocsConceptIndexExporterTests
{
    [Fact]
    public async Task WriteAsync_ShouldProjectCanonicalWinnersFromVerifiedPhysicalMappingsAndCaptureEmittedIds()
    {
        using var output = new TemporaryDirectory();
        await WriteFrozenRoutesAsync(output.Path,
            ("guide.md", "guides/setup#installation", "rendered/setup.html"),
            ("home.md", "", "index.html"));
        Directory.CreateDirectory(Path.Join(output.Path, "rendered"));
        await File.WriteAllTextAsync(Path.Join(output.Path, "rendered", "setup.html"), "<html><body><h1 id=\"setup\">Setup</h1><h2 id=\"installation\">Install</h2></body></html>");
        await File.WriteAllTextAsync(Path.Join(output.Path, "index.html"), "<html><body><h1>Docs</h1><a id=\"home\"></a></body></html>");
        AppSurfaceDocsConceptExportPage[] pages =
        [
            new("guides/setup#installation", "en", "guides.configuration.setup", "Setup", SectionLanding: true, ConceptIdIsInvalid: false),
            new("", null, null, "Docs home", SectionLanding: false, ConceptIdIsInvalid: false)
        ];

        await AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, pages, 1024 * 1024, CancellationToken.None);

        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(output.Path, AppSurfaceDocsConceptIndexExporter.FileName)));
        var root = json.RootElement;
        Assert.Equal("appsurface-docs-concepts-v1", root.GetProperty("schema").GetString());
        Assert.Equal("partial", root.GetProperty("identityCoverage").GetString());
        var emittedPages = root.GetProperty("pages").EnumerateArray().ToArray();
        Assert.Equal(2, emittedPages.Length);
        Assert.Equal("guides/setup", emittedPages[0].GetProperty("canonicalRoutePath").GetString());
        Assert.Equal("en", emittedPages[0].GetProperty("locale").GetString());
        Assert.Equal("guides.configuration.setup", emittedPages[0].GetProperty("conceptId").GetString());
        Assert.Equal("Setup", emittedPages[0].GetProperty("title").GetString());
        Assert.True(emittedPages[0].GetProperty("sectionLanding").GetBoolean());
        Assert.Equal(["setup", "installation"], emittedPages[0].GetProperty("anchorIds").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(string.Empty, emittedPages[1].GetProperty("canonicalRoutePath").GetString());
        Assert.Equal(JsonValueKind.Null, emittedPages[1].GetProperty("locale").ValueKind);
        Assert.Equal(JsonValueKind.Null, emittedPages[1].GetProperty("conceptId").ValueKind);
        Assert.False(emittedPages[1].TryGetProperty("sectionLanding", out _));
        Assert.Equal(["home"], emittedPages[1].GetProperty("anchorIds").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectDuplicateConceptIdentityInOneLocale()
    {
        using var output = new TemporaryDirectory();
        await WriteFrozenRoutesAsync(output.Path,
            ("first.md", "guides/first", "rendered/first.html"),
            ("second.md", "guides/second", "rendered/second.html"));
        Directory.CreateDirectory(Path.Join(output.Path, "rendered"));
        await File.WriteAllTextAsync(Path.Join(output.Path, "rendered", "first.html"), "<h1>First</h1>");
        await File.WriteAllTextAsync(Path.Join(output.Path, "rendered", "second.html"), "<h1>Second</h1>");
        AppSurfaceDocsConceptExportPage[] pages =
        [
            new("guides/first", "en", "guides.setup", "First", false, false),
            new("guides/second", "en", "guides.setup", "Second", false, false)
        ];

        var exception = await Assert.ThrowsAsync<ExportValidationException>(
            () => AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, pages, 1024 * 1024, CancellationToken.None));

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "docs-concept-index-invalid");
        Assert.False(File.Exists(Path.Join(output.Path, AppSurfaceDocsConceptIndexExporter.FileName)));
    }

    [Fact]
    public async Task WriteAsync_ShouldRejectInvalidAuthoredIdentityAndUnknownFrozenWinner()
    {
        using var invalidOutput = new TemporaryDirectory();
        await WriteFrozenRoutesAsync(invalidOutput.Path, ("guide.md", "guides/guide", "rendered/guide.html"));
        Directory.CreateDirectory(Path.Join(invalidOutput.Path, "rendered"));
        await File.WriteAllTextAsync(Path.Join(invalidOutput.Path, "rendered", "guide.html"), "<h1>Guide</h1>");
        var invalidPage = new AppSurfaceDocsConceptExportPage("guides/guide", null, null, "Guide", false, ConceptIdIsInvalid: true);

        var invalidException = await Assert.ThrowsAsync<ExportValidationException>(
            () => AppSurfaceDocsConceptIndexExporter.WriteAsync(invalidOutput.Path, [invalidPage], 1024 * 1024, CancellationToken.None));

        Assert.Contains(invalidException.Diagnostics, diagnostic => diagnostic.Code == "docs-concept-id-invalid");
        Assert.Contains("invalid authored concept_id", Assert.Single(invalidException.Diagnostics).Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Join(invalidOutput.Path, AppSurfaceDocsConceptIndexExporter.FileName)));

        using var unmappedOutput = new TemporaryDirectory();
        await WriteFrozenRoutesAsync(unmappedOutput.Path, ("guide.md", "guides/guide", "rendered/guide.html"));
        var unlistedPage = new AppSurfaceDocsConceptExportPage("guides/other", null, null, "Other", false, ConceptIdIsInvalid: false);
        var unmappedException = await Assert.ThrowsAsync<ExportValidationException>(
            () => AppSurfaceDocsConceptIndexExporter.WriteAsync(unmappedOutput.Path, [unlistedPage], 1024 * 1024, CancellationToken.None));

        Assert.Contains(unmappedException.Diagnostics, diagnostic => diagnostic.Code == "docs-concept-index-invalid");
        Assert.False(File.Exists(Path.Join(unmappedOutput.Path, AppSurfaceDocsConceptIndexExporter.FileName)));
    }

    [Fact]
    public async Task WriteAsync_RejectsCompleteIndexOverflowAndExistingReservedArtifact()
    {
        using var output = new TemporaryDirectory();
        await WriteFrozenRoutesAsync(output.Path, ("guide.md", "guide", "guide.html"));
        await File.WriteAllTextAsync(Path.Join(output.Path, "guide.html"), "<h1 id=\"guide\">Guide</h1>");
        AppSurfaceDocsConceptExportPage[] pages = [new("guide", null, "guide", "Guide", false, false)];
        var overflow = await Assert.ThrowsAsync<ExportValidationException>(() => AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, pages, 30, CancellationToken.None));
        Assert.False(File.Exists(Path.Join(output.Path, AppSurfaceDocsConceptIndexExporter.FileName)));
        Assert.Contains("exceeds", Assert.Single(overflow.Diagnostics).Message);
        await AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, pages, 4194304, CancellationToken.None);
        var original = await File.ReadAllBytesAsync(Path.Join(output.Path, AppSurfaceDocsConceptIndexExporter.FileName));
        var collision = await Assert.ThrowsAsync<ExportValidationException>(() => AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, pages, 4194304, CancellationToken.None));
        Assert.Contains("reserved", Assert.Single(collision.Diagnostics).Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Join(output.Path, AppSurfaceDocsConceptIndexExporter.FileName)));
    }

    [Fact]
    public async Task WriteAsync_RejectsUnsupportedShellBytesAndIncompleteCanonicalInventory()
    {
        using var output = new TemporaryDirectory();
        await WriteFrozenRoutesAsync(output.Path, ("guide.md", "guide", "guide.html"));
        await File.WriteAllBytesAsync(Path.Join(output.Path, "guide.html"), [255, 255]);
        AppSurfaceDocsConceptExportPage[] pages = [new("guide", null, null, "Guide", false, false)];
        var invalidUtf8 = await Assert.ThrowsAsync<ExportValidationException>(() => AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, pages, 4194304, CancellationToken.None));
        Assert.Contains("UTF-8", Assert.Single(invalidUtf8.Diagnostics).Message);
        var incomplete = await Assert.ThrowsAsync<ExportValidationException>(() => AppSurfaceDocsConceptIndexExporter.WriteAsync(output.Path, [], 4194304, CancellationToken.None));
        Assert.Contains("every finalized", Assert.Single(incomplete.Diagnostics).Message);
    }

    private static async Task WriteFrozenRoutesAsync(string outputPath, params (string Source, string Route, string Artifact)[] routes)
    {
        var entries = routes.Select(route => new
        {
            sourcePath = route.Source,
            canonicalRoutePath = route.Route,
            recoveryAliases = Array.Empty<string>(),
            declaredAliases = Array.Empty<string>(),
            canonicalArtifactPath = route.Artifact
        }).ToArray();
        var document = new
        {
            schema = "appsurface-docs-route-manifest-v1",
            htmlUrlBase = "export-root",
            entries
        };
        await File.WriteAllTextAsync(
            Path.Join(outputPath, ".appsurface-docs-route-manifest.json"),
            JsonSerializer.Serialize(document));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "appsurface-concept-index-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
