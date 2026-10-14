using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class DurableHandoffDocumentationTests
{
    private const string GuidePath = "Durable/handoff-fence.md";
    private const string PackageReadmePath = "Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md";
    private const string HandoffReadmeHref =
        "ForgeTrust.AppSurface.Durable.PostgreSql/README.md#prepare-an-application-handoff";

    [Fact]
    public async Task HandoffFenceGuide_ShouldResolvePackageLinkAndRenderCanonicalConsumerSnippet()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var sourceRoot = Path.Join(
            Path.GetTempPath(),
            "AppSurfaceDocsDurableHandoffDocumentationTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            CopySourceDocument(repository, sourceRoot, GuidePath);
            CopySourceDocument(repository, sourceRoot, PackageReadmePath);

            var guideMarkdown = await File.ReadAllTextAsync(TestPathUtils.PathUnder(sourceRoot, GuidePath));
            var harvester = new MarkdownHarvester(NullLogger<MarkdownHarvester>.Instance);
            var docs = await harvester.HarvestAsync(sourceRoot);
            var resolver = DocPathResolver.Create(docs);

            var guide = Assert.Single(docs, doc => doc.Path == GuidePath);
            Assert.Same(guide, resolver.Resolve(GuidePath));

            var packageReadme = Assert.Single(docs, doc => doc.Path == PackageReadmePath);
            var parser = new HtmlParser();
            var guideDocument = parser.ParseDocument(guide.Content);
            var handoffLink = Assert.Single(
                guideDocument.QuerySelectorAll("a[href]"),
                anchor => anchor.GetAttribute("href") == HandoffReadmeHref);
            var fragmentIndex = HandoffReadmeHref.IndexOf('#');
            var targetPath = Path.Join(Path.GetDirectoryName(GuidePath)!, HandoffReadmeHref[..fragmentIndex])
                .Replace('\\', '/');

            Assert.Same(packageReadme, resolver.Resolve($"{targetPath}{HandoffReadmeHref[fragmentIndex..]}"));

            var readmeDocument = parser.ParseDocument(packageReadme.Content);
            Assert.NotNull(readmeDocument.QuerySelector($"#{HandoffReadmeHref[(fragmentIndex + 1)..]}"));

            var handoffSnippet = Assert.Single(
                guideDocument.QuerySelectorAll("pre > code"),
                code => code.TextContent.Contains("internal sealed class HandoffExecutor(", StringComparison.Ordinal));
            Assert.Contains("NpgsqlDataSource runtimeDataSource", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.Contains("ExecuteAsync", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.Contains("```csharp", guideMarkdown, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "docs:snippet durable-handoff-application-flow",
                handoffSnippet.TextContent,
                StringComparison.Ordinal);
            Assert.DoesNotContain("source placeholder", handoffSnippet.TextContent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TODO", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("appsurface:snippet", guideDocument.Body!.TextContent, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, recursive: true);
            }
        }
    }

    private static void CopySourceDocument(string repository, string sourceRoot, string relativePath)
    {
        var destination = TestPathUtils.PathUnder(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(TestPathUtils.PathUnder(repository, relativePath), destination);
    }
}
