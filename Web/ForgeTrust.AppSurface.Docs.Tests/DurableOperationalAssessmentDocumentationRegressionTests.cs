using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Standalone;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Integration")]
public sealed class DurableOperationalAssessmentDocumentationRegressionTests
{
    [Fact]
    public void OperationalEvidenceLinks_ShouldKeepNonHarvestedArtifactsOutOfStaticExportRoutes()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var guide = File.ReadAllText(
            TestPathUtils.PathUnder(repoRoot, "Durable", "operational-assessments.md"));
        var adoptionEvidence = File.ReadAllText(
            TestPathUtils.PathUnder(
                repoRoot,
                "Durable",
                "evidence",
                "executable-contract-adoption.md"));

        Assert.Contains(
            "https://github.com/forge-trust/AppSurface/blob/main/Durable/evidence/measure-issue-794-adoption.sh",
            guide,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/forge-trust/AppSurface/blob/main/Durable/evidence/executable-contract-adoption.measurements.json",
            adoptionEvidence,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/forge-trust/AppSurface/blob/main/Durable/evidence/executable-contract-adoption.results.json",
            adoptionEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "](evidence/measure-issue-794-adoption.sh)",
            guide,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "](executable-contract-adoption.measurements.json)",
            adoptionEvidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "](executable-contract-adoption.results.json)",
            adoptionEvidence,
            StringComparison.Ordinal);
    }

    // Regression: ISSUE-001 — the release-note adoption-guide link rendered a 404.
    // Found by /qa on 2026-09-10.
    // Report: .gstack/qa-reports/qa-report-127-0-0-1-5055-2026-09-10.md
    [Fact]
    public async Task StandaloneHost_ShouldHarvestAndServeTheDurableOperationalAssessmentGuideFromTheReleaseNote()
    {
        var repoRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var appSettingsPath = Path.GetFullPath(
            Path.Join("Web", "ForgeTrust.AppSurface.Docs.Standalone", "appsettings.json"),
            repoRoot);

        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var includeGlobs = document.RootElement
            .GetProperty("AppSurfaceDocs")
            .GetProperty("Harvest")
            .GetProperty("Paths")
            .GetProperty("IncludeGlobs")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        const string guidePath = "Durable/operational-assessments.md";
        Assert.Contains(guidePath, includeGlobs);
        Assert.True(
            File.Exists(TestPathUtils.PathUnder(repoRoot, guidePath)),
            "The harvested Durable operational-assessment guide must exist at the configured path.");

        var sourceRoot = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "AppSurfaceDocsDurableRegressionTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            CopySourceDocument(repoRoot, sourceRoot, "README.md");
            CopySourceDocument(repoRoot, sourceRoot, "Durable/operational-assessments.md");
            CopySourceDocument(repoRoot, sourceRoot, "releases/unreleased.md");
            CopySourceDocument(
                repoRoot,
                sourceRoot,
                "releases/unreleased.entries/2026-09-10-durable-operational-assessments.md");

            var builder = AppSurfaceDocsStandaloneHost.CreateBuilder([]);
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSurfaceDocs:Source:RepositoryRoot"] = sourceRoot,
                            ["AppSurfaceDocs:Harvest:StartupMode"] = nameof(AppSurfaceDocsHarvestStartupMode.Blocking),
                        }));
            builder.ConfigureWebHost(webHost => webHost.UseUrls("http://127.0.0.1:0"));

            using var host = builder.Build();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(startup.Token);
            try
            {
                var server = host.Services.GetRequiredService<IServer>();
                var addresses = server.Features.Get<IServerAddressesFeature>();
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(Assert.Single(addresses!.Addresses)),
                    Timeout = TimeSpan.FromSeconds(5),
                };
                using var requests = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                using var releaseResponse = await client.GetAsync("/docs/releases/unreleased", requests.Token);
                var releaseHtml = await releaseResponse.Content.ReadAsStringAsync(requests.Token);
                using var guideResponse = await client.GetAsync(
                    "/docs/durable/operational-assessments",
                    requests.Token);
                var guideHtml = await guideResponse.Content.ReadAsStringAsync(requests.Token);

                Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);
                Assert.Equal(HttpStatusCode.OK, guideResponse.StatusCode);

                var parser = new HtmlParser();
                var releaseContent = parser.ParseDocument(releaseHtml).QuerySelector(".docs-content");
                Assert.NotNull(releaseContent);
                Assert.NotNull(
                    releaseContent.QuerySelector(
                        "a[href='/docs/durable/operational-assessments']"));
                var guideDocument = parser.ParseDocument(guideHtml);
                var guideTitle = guideDocument.QuerySelector("main h1");
                Assert.NotNull(guideTitle);
                Assert.Equal(
                    "Adopt Durable operational assessments",
                    guideTitle.TextContent.Trim());
                var guideContent = guideDocument.QuerySelector(".docs-content");
                Assert.NotNull(guideContent);
                Assert.Contains(
                    "This is the task guide for upgrading an existing PostgreSQL worker",
                    guideContent.TextContent,
                    StringComparison.Ordinal);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StopAsync(shutdown.Token);
            }
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, recursive: true);
            }
        }
    }

    private static void CopySourceDocument(string repoRoot, string sourceRoot, string relativePath)
    {
        var destination = TestPathUtils.PathUnder(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(TestPathUtils.PathUnder(repoRoot, relativePath), destination);
    }
}
