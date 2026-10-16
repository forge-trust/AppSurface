using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Collection(nameof(RepositoryRecommendationCorpusCollection))]
public sealed class RepositoryRecommendationEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ApprovedFiveRoleCases_ShouldBeEvaluatedBeforeBroadIntegration()
    {
        var root = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var options = new AppSurfaceDocsOptions();
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Join(root,
            "Web/ForgeTrust.AppSurface.Docs.Standalone/appsettings.json")).Build();
        configuration.GetSection("AppSurfaceDocs").Bind(options);
        options.Source.RepositoryRoot = root;
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(root);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var aggregator = new DocAggregator(
            [new MarkdownHarvester(NullLogger<MarkdownHarvester>.Instance, File.ReadAllTextAsync, options),
                new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance),
                new JavaScriptDocHarvester(options, NullLogger<JavaScriptDocHarvester>.Instance),
                new PythonDocHarvester(options, NullLogger<PythonDocHarvester>.Instance)],
            options, environment, memo, new AppSurfaceDocsHtmlSanitizer(), NullLogger<DocAggregator>.Instance);
        var docs = await aggregator.GetDocsAsync();
        Assert.All((await aggregator.GetHarvestHealthAsync()).Harvesters, harvester => Assert.True(
            harvester.Status is DocHarvesterHealthStatus.Succeeded or DocHarvesterHealthStatus.ReturnedEmpty,
            $"{harvester.HarvesterType}: {harvester.Status}"));
        var sections = await aggregator.GetPublicSectionsAsync();
        var routes = DocRouteIdentityCatalog.Create(docs, new DocsUrlBuilder(options));
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(root,
            "Web/ForgeTrust.AppSurface.Docs.Tests/TestData/Issue255Recommendations/expected-next-reads.json")));
        Assert.Equal("maintainer-approved-before-algorithm-output", expected.RootElement.GetProperty("status").GetString());
        var cases = expected.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var facts = new List<DocRecommendationPageFacts>();
        foreach (var doc in docs)
        {
            if (!routes.TryGetPublicRoutePath(doc.Path, out var id)) continue;
            var section = sections.FirstOrDefault(section => section.VisiblePages.Any(page => page.Path == doc.Path));
            var sourceCase = cases.FirstOrDefault(item => item.GetProperty("sourcePath").GetString() == doc.Path);
            var details = sourceCase.ValueKind == JsonValueKind.Undefined ? null : await aggregator.GetDocDetailsAsync(doc.Path);
            var link = new DocPageLinkViewModel
            {
                Title = doc.Metadata?.Title ?? doc.Title,
                Href = DocAggregator.BuildSearchDocUrl("/docs", id),
                Summary = doc.Metadata?.Summary,
                PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(doc.Metadata?.PageType)
            };
            static string? Id(DocPageLinkViewModel? page) => page?.Href["/docs/".Length..];
            facts.Add(new DocRecommendationPageFacts(doc, id, link, section?.Section,
                section?.LandingDoc?.Path == doc.Path, Id(details?.PreviousPage), Id(details?.NextPage),
                details?.RelatedPages.Select(page => new DocRecommendationLink(Id(page)!, page)).ToArray()));
        }
        var result = DocRecommendationPolicy.Build(facts);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            labelStatus = "maintainer-approved-before-algorithm-output",
            corpusDocuments = docs.Count,
            result.Measurements,
            cases = cases.Select(source => new
            {
                sourceId = source.GetProperty("canonicalSourceId").GetString(),
                rows = result.Pages[source.GetProperty("canonicalSourceId").GetString()!]
                    .Select(row => new { row.TargetId, row.IsAutomatic, row.PrimaryReasonCode, row.ReasonCodes, row.ReasonText })
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(5, cases.Length);
        Assert.All(result.Pages.Values, rows => Assert.InRange(rows.Count(row => row.IsAutomatic), 0, 3));
    }
}
