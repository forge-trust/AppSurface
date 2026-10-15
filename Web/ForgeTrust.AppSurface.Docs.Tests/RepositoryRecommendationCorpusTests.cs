using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Collection(nameof(RepositoryRecommendationCorpusCollection))]
public sealed class RepositoryRecommendationCorpusTests
{
    [Fact]
    public async Task ConfigValidationSample_ShouldPublishItsAuthoredExampleRoleAndCanonicalRoute()
    {
        const string sourcePath = "examples/config-validation/README.md";
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var options = CreateRepositoryOptions(repoRoot);
        var pathPolicy = new AppSurfaceDocsHarvestPathPolicy(
            options,
            NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
        Assert.True(pathPolicy.ShouldIncludeFilePath(sourcePath, AppSurfaceDocsHarvestSourceKind.Markdown));

        var corpusRoot = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "AppSurfaceDocsTests_RecommendationCorpus",
            Guid.NewGuid().ToString("N"));
        var sampleDirectory = TestPathUtils.PathUnder(corpusRoot, "examples", "config-validation");
        Directory.CreateDirectory(sampleDirectory);
        try
        {
            File.Copy(TestPathUtils.PathUnder(repoRoot, sourcePath), Path.Join(sampleDirectory, "README.md"));
            File.Copy(TestPathUtils.PathUnder(repoRoot, sourcePath + ".yml"), Path.Join(sampleDirectory, "README.md.yml"));

            var harvester = new MarkdownHarvester(
                NullLogger<MarkdownHarvester>.Instance,
                File.ReadAllTextAsync,
                options);
            var document = Assert.Single(await harvester.HarvestAsync(corpusRoot));
            Assert.Equal(sourcePath, document.Path);
            Assert.True(document.HasReaderContent);
            Assert.Equal("Config validation example", document.Metadata?.Title);
            Assert.Equal("example", document.Metadata?.PageType);
            Assert.Equal("Examples", document.Metadata?.NavGroup);
            Assert.Equal("ForgeTrust.AppSurface.Config", document.Metadata?.Component);
            Assert.False(document.Metadata?.PageTypeIsDerived);
            Assert.False(document.Metadata?.ComponentIsDerived);
            Assert.NotEqual(true, document.Metadata?.HideFromPublicNav);
            Assert.NotEqual(true, document.Metadata?.HideFromSearch);
            Assert.Equal(
                ["troubleshooting/startup-and-modules.md", "Config/ForgeTrust.AppSurface.Config/README.md"],
                document.Metadata?.RelatedPages);

            var routes = DocRouteIdentityCatalog.Create([document], new DocsUrlBuilder(options));
            Assert.True(routes.TryGetPublicRoutePath(sourcePath, out var publicRoute));
            Assert.Equal("examples/config-validation", publicRoute);
            Assert.Equal(DocRouteResolutionKind.Canonical, routes.ResolvePublicRoute(publicRoute).Kind);
            Assert.Equal(DocRouteResolutionKind.AliasRedirect, routes.ResolvePublicRoute(sourcePath).Kind);
        }
        finally
        {
            Directory.Delete(corpusRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FiveRoleCorpus_ShouldKeepItsSourceRolesAndExpectedDestinationsPubliclyResolvable()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var options = CreateRepositoryOptions(repoRoot);
        using var expectations = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(
            repoRoot,
            "Web",
            "ForgeTrust.AppSurface.Docs.Tests",
            "TestData",
            "Issue255Recommendations",
            "expected-next-reads.json")));
        var cases = expectations.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(
            ["api-reference", "example", "guide", "section-landing", "troubleshooting"],
            cases.Select(item => item.GetProperty("role").GetString()).OrderBy(role => role, StringComparer.Ordinal));

        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(repoRoot);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var aggregator = new DocAggregator(
            [
                new MarkdownHarvester(NullLogger<MarkdownHarvester>.Instance, File.ReadAllTextAsync, options),
                new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance),
                new JavaScriptDocHarvester(options, NullLogger<JavaScriptDocHarvester>.Instance),
                new PythonDocHarvester(options, NullLogger<PythonDocHarvester>.Instance)
            ],
            options,
            environment,
            memo,
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);
        var payload = await aggregator.GetSearchIndexPayloadAsync();
        Assert.All((await aggregator.GetHarvestHealthAsync()).Harvesters, harvester => Assert.True(
            harvester.Status is DocHarvesterHealthStatus.Succeeded or DocHarvesterHealthStatus.ReturnedEmpty,
            $"{harvester.HarvesterType}: {harvester.Status}"));
        var sections = await aggregator.GetPublicSectionsAsync();

        foreach (var sourceCase in cases)
        {
            var sourcePath = sourceCase.GetProperty("sourcePath").GetString()!;
            var sourceId = sourceCase.GetProperty("canonicalSourceId").GetString()!;
            var role = sourceCase.GetProperty("role").GetString()!;
            var source = Assert.Single(payload.Documents, document => document.SourcePath == sourcePath);
            Assert.Equal(sourceId, source.Id);
            var sourceNode = await aggregator.GetDocByPathAsync(sourcePath);
            Assert.NotNull(sourceNode);
            Assert.True(sourceNode.HasReaderContent);
            Assert.False(sourceNode.IsFragmentStub);
            Assert.NotEqual(true, sourceNode.Metadata?.HideFromSearch);
            Assert.NotEqual(true, sourceNode.Metadata?.HideFromPublicNav);

            var sourceRoute = await aggregator.ResolvePublicRouteAsync(sourceId);
            Assert.Equal(DocRouteResolutionKind.Canonical, sourceRoute.Kind);
            Assert.Equal(sourcePath, sourceRoute.SourcePath);
            if (role == "section-landing")
            {
                Assert.True(source.IsSectionLanding);
                var section = Assert.Single(sections, item => item.Slug == source.PublicSection);
                Assert.Equal(sourcePath, section.LandingDoc?.Path);
            }
            else
            {
                Assert.Equal(role, source.PageTypeVariant);
            }

            foreach (var nextRead in sourceCase.GetProperty("expectedNextReads").EnumerateArray())
            {
                var targetPath = nextRead.GetProperty("sourcePath").GetString()!;
                var targetId = nextRead.GetProperty("canonicalTargetId").GetString()!;
                var target = Assert.Single(payload.Documents, document => document.SourcePath == targetPath);
                Assert.Equal(targetId, target.Id);
                var targetRoute = await aggregator.ResolvePublicRouteAsync(targetId);
                Assert.Equal(DocRouteResolutionKind.Canonical, targetRoute.Kind);
                Assert.Equal(targetPath, targetRoute.SourcePath);
                var targetNode = await aggregator.GetDocByPathAsync(targetPath);
                Assert.NotNull(targetNode);
                Assert.True(targetNode.HasReaderContent);
                Assert.False(targetNode.IsFragmentStub);
                Assert.NotEqual(true, targetNode.Metadata?.HideFromPublicNav);
            }
        }
    }

    private static AppSurfaceDocsOptions CreateRepositoryOptions(string repoRoot)
    {
        var options = new AppSurfaceDocsOptions();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(TestPathUtils.PathUnder(
                repoRoot,
                "Web",
                "ForgeTrust.AppSurface.Docs.Standalone",
                "appsettings.json"))
            .Build();
        configuration.GetSection("AppSurfaceDocs").Bind(options);
        options.Source.RepositoryRoot = repoRoot;
        return options;
    }
}
