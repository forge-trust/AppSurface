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

/// <summary>Verifies the activation reference, release entry, and executable example are reachable in hosted documentation.</summary>
[Trait("Category", "Integration")]
public sealed class DurableExternalActivationDocumentationTests
{
    [Fact]
    public async Task Standalone_host_harvests_the_external_activation_reference_and_release_link()
    {
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        const string guide = "Durable/external-activation-v1.md";
        const string example = "examples/durable-external-activation/README.md";
        const string entry = "releases/unreleased.entries/2026-10-01-durable-external-activation.md";
        var entryIsUnreleased = File.Exists(TestPathUtils.PathUnder(repository, entry));
        var releaseNote = entryIsUnreleased ? "releases/unreleased.md" : "releases/v0.2.0-preview.12.md";
        var releaseRoute = entryIsUnreleased ? "/docs/releases/unreleased" : "/docs/releases/v0.2.0-preview.12";
        using var settings = JsonDocument.Parse(File.ReadAllText(TestPathUtils.PathUnder(repository,
            "Web/ForgeTrust.AppSurface.Docs.Standalone/appsettings.json")));
        var includeGlobs = settings.RootElement.GetProperty("AppSurfaceDocs").GetProperty("Harvest")
            .GetProperty("Paths").GetProperty("IncludeGlobs").EnumerateArray().Select(value => value.GetString());
        Assert.Contains(guide, includeGlobs);
        Assert.Contains("examples/**/README.md", includeGlobs);

        var exampleReadme = File.ReadAllText(TestPathUtils.PathUnder(repository, example));
        Assert.Contains("Executable local first start", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("schema-apply-dev", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("epoch-bootstrap-dev", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("accept-demo-work --value first-start", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("NotStarted", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("ASDUR404", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("\"problemCode\": null", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("does not pin `discovered`", exampleReadme, StringComparison.Ordinal);
        Assert.Contains("#806", exampleReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("awaits focused host build/test verification", exampleReadme, StringComparison.Ordinal);

        var fixture = Path.Join(Path.GetTempPath(), "AppSurfaceExternalActivationDocs", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var path in new[] { "README.md", guide, example, releaseNote }
                         .Concat(entryIsUnreleased ? [entry] : Array.Empty<string>()))
            {
                var destination = TestPathUtils.PathUnder(fixture, path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(TestPathUtils.PathUnder(repository, path), destination);
            }

            var builder = AppSurfaceDocsStandaloneHost.CreateBuilder([]);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSurfaceDocs:Source:RepositoryRoot"] = fixture,
                    ["AppSurfaceDocs:Harvest:StartupMode"] = nameof(AppSurfaceDocsHarvestStartupMode.Blocking),
                    ["AppSurfaceDocs:Harvest:InitialRequestWaitBudgetMilliseconds"] = "0",
                }));
            builder.ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
            using var host = builder.Build();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await host.StartAsync(startup.Token);
            try
            {
                var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(Assert.Single(addresses!.Addresses)),
                    Timeout = TimeSpan.FromSeconds(10),
                };
                using var release = await client.GetAsync(releaseRoute);
                using var reference = await client.GetAsync("/docs/durable/external-activation-v1");
                using var examplePage = await client.GetAsync("/docs/examples/durable-external-activation");
                Assert.Equal(HttpStatusCode.OK, release.StatusCode);
                Assert.Equal(HttpStatusCode.OK, reference.StatusCode);
                Assert.Equal(HttpStatusCode.OK, examplePage.StatusCode);
                var parser = new HtmlParser();
                var releasePage = parser.ParseDocument(await release.Content.ReadAsStringAsync());
                Assert.NotNull(releasePage.QuerySelector(
                    ".docs-content a[href='/docs/durable/external-activation-v1']"));
                var guidePage = parser.ParseDocument(await reference.Content.ReadAsStringAsync());
                Assert.Equal("External Durable activation v1", guidePage.QuerySelector("main h1")!.TextContent.Trim());
                Assert.Contains("Operator actions and recovery", guidePage.QuerySelector(".docs-content")!.TextContent);
                var exampleDocument = parser.ParseDocument(await examplePage.Content.ReadAsStringAsync());
                var exampleText = exampleDocument.QuerySelector(".docs-content")!.TextContent;
                Assert.Contains("Executable local first start", exampleText, StringComparison.Ordinal);
                Assert.Contains("ASDUR404", exampleText, StringComparison.Ordinal);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StopAsync(shutdown.Token);
            }
        }
        finally
        {
            if (Directory.Exists(fixture))
            {
                Directory.Delete(fixture, recursive: true);
            }
        }
    }
}
