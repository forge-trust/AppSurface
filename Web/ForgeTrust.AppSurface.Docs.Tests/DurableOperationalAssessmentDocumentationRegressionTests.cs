using System.Net;
using System.Text.Json;
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
        CopySourceDocument(repoRoot, sourceRoot, "README.md");
        CopySourceDocument(repoRoot, sourceRoot, "Durable/operational-assessments.md");
        CopySourceDocument(repoRoot, sourceRoot, "releases/unreleased.md");
        CopySourceDocument(
            repoRoot,
            sourceRoot,
            "releases/unreleased.entries/2026-09-10-durable-operational-assessments.md");

        try
        {
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
            await host.StartAsync();
            try
            {
                var server = host.Services.GetRequiredService<IServer>();
                var addresses = server.Features.Get<IServerAddressesFeature>();
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(Assert.Single(addresses!.Addresses)),
                };

                using var releaseResponse = await client.GetAsync("/docs/releases/unreleased");
                var releaseHtml = await releaseResponse.Content.ReadAsStringAsync();
                using var guideResponse = await client.GetAsync("/docs/durable/operational-assessments");
                var guideHtml = await guideResponse.Content.ReadAsStringAsync();

                Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);
                Assert.Contains("href=\"/docs/durable/operational-assessments\"", releaseHtml, StringComparison.Ordinal);
                Assert.Equal(HttpStatusCode.OK, guideResponse.StatusCode);
                Assert.Contains("Adopt Durable operational assessments", guideHtml, StringComparison.Ordinal);
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    private static void CopySourceDocument(string repoRoot, string sourceRoot, string relativePath)
    {
        var destination = TestPathUtils.PathUnder(sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(TestPathUtils.PathUnder(repoRoot, relativePath), destination);
    }
}
