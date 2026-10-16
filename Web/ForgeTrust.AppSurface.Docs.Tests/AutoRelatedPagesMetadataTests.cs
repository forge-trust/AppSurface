using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AutoRelatedPagesMetadataTests
{
    [Fact]
    public void ExtractWithDiagnostics_ShouldLeaveAutoRelatedPagesUnspecified_WhenAbsent()
    {
        var markdown = "---\ntitle: Guide\n---\n# Guide";

        var (body, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Equal("# Guide", body);
        Assert.Null(result.Metadata?.AutoRelatedPages);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("NULL", null)]
    [InlineData("~", null)]
    [InlineData("!!str null", null)]
    [InlineData("", null)]
    public void ExtractWithDiagnostics_ShouldParseNullableAutoRelatedPagesBoolean(string value, bool? expected)
    {
        var markdown = $"---\nauto_related_pages: {value}\n---\n# Guide";

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(expected, result.Metadata?.AutoRelatedPages);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    [InlineData("!!str null", null)]
    [InlineData("", null)]
    public void ParseMetadataYamlWithDiagnostics_ShouldParseNullableSidecarAutoRelatedPagesBoolean(
        string value,
        bool? expected)
    {
        var result = MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics($"auto_related_pages: {value}\n");

        Assert.Equal(expected, result.Metadata?.AutoRelatedPages);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ExtractWithDiagnostics_ShouldKeepEmptyRelatedPagesIndependentFromAutoRelatedPages()
    {
        const string markdown = "---\nrelated_pages: []\nauto_related_pages: true\n---\n# Guide";

        var (_, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Empty(result.Metadata?.RelatedPages!);
        Assert.True(result.Metadata?.AutoRelatedPages);
    }

    [Fact]
    public void Merge_ShouldPreferPrimaryTrueOverFallbackFalse()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata { AutoRelatedPages = true },
            new DocMetadata { AutoRelatedPages = false });

        Assert.True(merged?.AutoRelatedPages);
    }

    [Fact]
    public void Merge_ShouldPreferPrimaryFalseOverFallbackTrue()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata { AutoRelatedPages = false },
            new DocMetadata { AutoRelatedPages = true });

        Assert.False(merged?.AutoRelatedPages);
    }

    [Fact]
    public void Merge_ShouldUseFallbackWhenPrimaryAutoRelatedPagesIsAbsent()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata(),
            new DocMetadata { AutoRelatedPages = false });

        Assert.False(merged?.AutoRelatedPages);
    }

    [Fact]
    public void Merge_ShouldKeepEmptyRelatedPagesIndependentFromFallbackAutoRelatedPages()
    {
        var merged = DocMetadata.Merge(
            new DocMetadata { RelatedPages = [] },
            new DocMetadata
            {
                RelatedPages = ["fallback.md"],
                AutoRelatedPages = true
            });

        Assert.Empty(merged?.RelatedPages!);
        Assert.True(merged?.AutoRelatedPages);
    }

    [Theory]
    [InlineData("true", "false", true)]
    [InlineData("false", "true", false)]
    public async Task HarvestAsync_ShouldPreferExplicitInlineAutoRelatedPagesOverSidecar(
        string inlineValue,
        string sidecarValue,
        bool expected)
    {
        var metadata = await HarvestMetadataAsync(
            $"---\nauto_related_pages: {inlineValue}\n---\n# Guide",
            $"auto_related_pages: {sidecarValue}\n");

        Assert.Equal(expected, metadata?.AutoRelatedPages);
    }

    [Fact]
    public async Task HarvestAsync_ShouldUseSidecarAutoRelatedPagesWhenInlineValueIsAbsent()
    {
        var metadata = await HarvestMetadataAsync(
            "# Guide",
            "auto_related_pages: false\n");

        Assert.False(metadata?.AutoRelatedPages);
    }

    [Theory]
    [InlineData("not-a-boolean")]
    [InlineData("\"false\"")]
    [InlineData("!!str false")]
    [InlineData("!!bool true")]
    [InlineData("!!bool false")]
    [InlineData("!!null null")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("!!bool maybe")]
    public void ExtractWithDiagnostics_ShouldUseInvalidYamlDiagnosticForMalformedAutoRelatedPages(string value)
    {
        var markdown = $"---\nauto_related_pages: {value}\n---\n# Guide";

        var (body, result) = MarkdownFrontMatterParser.ExtractWithDiagnostics(markdown);

        Assert.Equal(markdown, body);
        Assert.Null(result.Metadata);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalid-yaml");
    }

    [Theory]
    [InlineData("not-a-boolean")]
    [InlineData("\"false\"")]
    [InlineData("!!str false")]
    [InlineData("!!bool true")]
    [InlineData("!!bool false")]
    [InlineData("!!null null")]
    [InlineData("1")]
    [InlineData("!!bool maybe")]
    public void ParseMetadataYamlWithDiagnostics_ShouldThrowForMalformedSidecarAutoRelatedPages(string value)
    {
        Assert.Throws<YamlException>(
            () => MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics($"auto_related_pages: {value}\n"));
    }

    [Fact]
    public void ParseMetadataYamlWithDiagnostics_ShouldRejectCollectionAutoRelatedPagesValue()
    {
        Assert.Throws<YamlException>(
            () => MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics(
                "auto_related_pages: { enabled: true }\n"));
    }

    private static async Task<DocMetadata?> HarvestMetadataAsync(string markdown, string sidecar)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "AppSurfaceDocsAutoRelatedPagesTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Guide.md"), markdown);
            await File.WriteAllTextAsync(Path.Combine(rootPath, "Guide.md.yml"), sidecar);

            var harvester = new MarkdownHarvester(NullLogger<MarkdownHarvester>.Instance);
            var documents = await harvester.HarvestAsync(rootPath);

            return Assert.Single(documents).Metadata;
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
