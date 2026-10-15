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

    [Theory]
    [InlineData(GuidePath)]
    [InlineData("docs/plans/issue-869-plan-completion-audit.md")]
    [InlineData("docs/plans/issue-869-transaction-bound-handoff-fence.md")]
    public async Task HandoffDocumentation_RepositoryLinksTargetExportableSources(string documentPath)
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var sourceRoot = Path.Join(Path.GetTempPath(), "AppSurfaceDocsHandoffLinks", Guid.NewGuid().ToString("N"));
        try
        {
            CopySourceDocument(repository, sourceRoot, documentPath);
            var harvester = new MarkdownHarvester(NullLogger<MarkdownHarvester>.Instance);
            var docs = await harvester.HarvestAsync(sourceRoot);
            var document = new HtmlParser().ParseDocument(Assert.Single(docs).Content);
            var anchors = document.QuerySelectorAll("a[href]");
            Assert.NotEmpty(anchors);
            foreach (var href in anchors.Select(anchor => anchor.GetAttribute("href")!))
            {
                if (href.StartsWith('#') || Uri.TryCreate(href, UriKind.Absolute, out var external)
                    && external.Scheme is "https" or "http" or "mailto")
                {
                    continue;
                }

                // These authored pages link harvested Markdown/C# sources relatively. Raw repository artifacts
                // must use external source URLs; local workspace paths cannot be served by the docs exporter.
                Assert.False(Path.IsPathRooted(href), $"{documentPath} links a local absolute path: {href}");
                var path = href.Split('#', '?')[0];
                Assert.True(Path.GetExtension(path) is ".md" or ".cs",
                    $"{documentPath} links a source the docs exporter does not harvest: {href}");
                var documentDirectory = TestPathUtils.PathUnder(repository, Path.GetDirectoryName(documentPath)!);
                var relativeTarget = Path.GetRelativePath(repository, Path.GetFullPath(path, documentDirectory));
                Assert.False(relativeTarget.StartsWith("..", StringComparison.Ordinal),
                    $"{documentPath} links outside the repository: {href}");
                var target = TestPathUtils.PathUnder(repository, relativeTarget);
                Assert.True(File.Exists(target), $"{documentPath} links a missing source: {href}");
            }
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

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
                code => code.TextContent.Contains("internal sealed class HandoffApplicationExecutor(", StringComparison.Ordinal));
            Assert.Contains("IDurableWorkHandoffFence handoffFence", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.Contains("ON CONFLICT DO NOTHING", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.Contains("CommitAsync", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.Contains("provider.SendAsync", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("HandoffExecutionProbe", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("ObservePreparationAsync", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("CrossScope", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("AmbiguousExternalOutcome", handoffSnippet.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("IOException", handoffSnippet.TextContent, StringComparison.Ordinal);
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
