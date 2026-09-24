using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.RazorWire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

/// <summary>
/// Proves the checked-in Issue 164 fixture survives the built-in aggregation boundary without reverting to legacy HTML.
/// </summary>
public sealed class Issue164CSharpAggregationContractTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "AppSurfaceDocsIssue164Aggregation", Guid.NewGuid().ToString("N"));

    public Issue164CSharpAggregationContractTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Fixture_ShouldMergeNamespaceIntroAndEntryPoints_IntoTheTypedSnapshot()
    {
        File.Copy(FixturePath("ApiFixtures.cs"), Path.Join(_root, "ApiFixtures.cs"));
        File.Copy(FixturePath("ChildFixtures.cs"), Path.Join(_root, "ChildFixtures.cs"));
        var entryPointTarget = StringUtils.ToSafeId("Issue164.Api.FixtureService`1.Process.method-group");
        var readme = new DocNode(
            "README",
            "docs/Issue164.Api/README.md",
            "<p>Fixture introduction explains typed setup.</p>",
            Metadata: new DocMetadata
            {
                EntryPoints =
                [
                    new DocNamespaceEntryPoint
                    {
                        Label = "Process fixture",
                        Summary = "Run the generated API sample.",
                        Target = entryPointTarget,
                        Keywords = ["fixture bootstrap"]
                    },
                    new DocNamespaceEntryPoint
                    {
                        Label = "Missing fixture",
                        Target = "Issue164-Api-Missing"
                    },
                    new DocNamespaceEntryPoint
                    {
                        Label = "Fixture intro",
                        Target = "fixture-intro"
                    },
                    new DocNamespaceEntryPoint
                    {
                        Label = "Child namespace",
                        Href = "/docs/namespaces/Issue164.Api.Child"
                    },
                    new DocNamespaceEntryPoint
                    {
                        Label = "Outside docs",
                        Href = "/account/settings"
                    }
                ]
            },
            Outline:
            [
                new DocOutlineItem { Id = "fixture-intro", Title = "Fixture introduction", Level = 2 }
            ]);
        var options = new AppSurfaceDocsOptions
        {
            Source = new AppSurfaceDocsSourceOptions { RepositoryRoot = _root }
        };
        var environment = new TestWebHostEnvironment(_root);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [
                new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance),
                new StaticHarvester([readme])
            ],
            options,
            environment,
            new Memo(cache),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var docs = await aggregator.GetDocsAsync();
        var health = await aggregator.GetHarvestHealthAsync();
        var search = await aggregator.GetSearchIndexPayloadAsync();

        var namespaceNode = Assert.Single(docs, node => node.Path == "Namespaces/Issue164.Api");
        var typedDocument = Assert.IsType<CSharpNamespaceDocument>(namespaceNode.CSharpNamespaceDocument);
        var entryPoints = Assert.IsAssignableFrom<IReadOnlyList<DocNamespaceEntryPoint>>(typedDocument.EntryPoints);
        var searchDocument = Assert.Single(search.Documents, document => document.Id == "Namespaces/Issue164.Api.html");

        Assert.Equal(string.Empty, namespaceNode.Content);
        Assert.Contains("doc-namespace-intro", typedDocument.IntroHtml, StringComparison.Ordinal);
        Assert.Contains("Fixture introduction explains typed setup.", typedDocument.IntroHtml, StringComparison.Ordinal);
        Assert.Equal(
            ["Process fixture", "Missing fixture", "Fixture intro", "Child namespace", "Outside docs"],
            entryPoints.Select(entry => entry.Label));
        Assert.Equal(
            "Namespaces/Issue164.Api.Child.html",
            Assert.Single(typedDocument.ChildNamespaces).Path);
        Assert.Equal(
            "/docs/Namespaces/Issue164.Api.Child.html",
            Assert.Single(entryPoints, entry => entry.Label == "Child namespace").Href);
        Assert.Null(Assert.Single(entryPoints, entry => entry.Label == "Outside docs").Href);
        Assert.Contains(typedDocument.Outline, item => item.Id == "fixture-intro");
        Assert.Contains(typedDocument.Outline, item => item.Id == entryPointTarget);
        Assert.Contains("Fixture introduction explains typed setup.", typedDocument.ReaderText, StringComparison.Ordinal);
        Assert.Contains("Run the generated API sample.", typedDocument.ReaderText, StringComparison.Ordinal);
        Assert.Contains("fixture bootstrap", typedDocument.ReaderText, StringComparison.Ordinal);
        Assert.Contains("Fixture introduction explains typed setup.", searchDocument.BodyText, StringComparison.Ordinal);
        Assert.Contains("fixture bootstrap", searchDocument.BodyText, StringComparison.Ordinal);
        Assert.Contains(
            health.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.NamespaceEntryPointTargetUnresolved
                          && diagnostic.Severity == DocHarvestDiagnosticSeverity.Warning
                          && diagnostic.Problem.Contains("Missing fixture", StringComparison.Ordinal));
        Assert.DoesNotContain(
            health.Diagnostics,
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.NamespaceEntryPointTargetUnresolved
                          && diagnostic.Problem.Contains("Fixture intro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuiltInTypedSnapshot_ShouldIndexEveryRenderedSignatureComponent()
    {
        await File.WriteAllTextAsync(
            Path.Join(_root, "SearchableSignatures.cs"),
            """
            namespace Issue164.Search;

            public interface IRunner
            {
                void Run();
            }

            public sealed class SearchableService : IRunner
            {
                /// <summary>Runs through the explicit interface contract.</summary>
                void IRunner.Run() { }

                /// <summary>Gets the configured item count.</summary>
                public int Count { get; init; }
            }
            """);

        var options = new AppSurfaceDocsOptions
        {
            Source = new AppSurfaceDocsSourceOptions { RepositoryRoot = _root }
        };
        var environment = new TestWebHostEnvironment(_root);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance)],
            options,
            environment,
            new Memo(cache),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var search = await aggregator.GetSearchIndexPayloadAsync();
        var searchDocument = Assert.Single(search.Documents, document => document.Id == "Namespaces/Issue164.Search.html");

        Assert.Contains("IRunner.", searchDocument.BodyText, StringComparison.Ordinal);
        Assert.Contains("{ get; init; }", searchDocument.BodyText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string FixturePath(string name)
    {
        return Path.Join(AppContext.BaseDirectory, "TestData", "Issue164CSharpApi", name);
    }

    private sealed class StaticHarvester(IReadOnlyList<DocNode> docs) : IDocHarvester
    {
        public Task<IReadOnlyList<DocNode>> HarvestAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(docs);
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Issue164CSharpAggregationContractTests";

        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(contentRootPath);

        public string WebRootPath { get; set; } = contentRootPath;

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = contentRootPath;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(contentRootPath);
    }
}
