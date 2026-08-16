using System.Diagnostics;
using System.Net;
using ForgeTrust.AppSurface.Docs.ConsumerFixture;
using Xunit.Abstractions;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Exercises the executable consumer-host proof for public and authenticated internal Docs products.
/// </summary>
[Trait("Category", "Integration")]
[Collection(RazorWireIntegrationCollection.Name)]
public sealed class AppSurfaceDocsMultiInstanceConsumerFixtureTests
{
    private static readonly TimeSpan FixtureWalkthroughTarget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly ITestOutputHelper _output;

    public AppSurfaceDocsMultiInstanceConsumerFixtureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task MultiInstanceConsumerFixture_ShouldProvePublicAndAuthenticatedInternalDocsWithinFiveMinutes()
    {
        var stopwatch = Stopwatch.StartNew();
        await using var host = await AppSurfaceDocsInProcessHost.StartMultiInstanceConsumerAsync("http://127.0.0.1:0");
        using var anonymousClient = CreateClient(host.BaseUrl);
        using var contributorClient = CreateClient(host.BaseUrl);
        contributorClient.DefaultRequestHeaders.Add(ConsumerFixtureProofAuth.UserHeaderName, "contributor-alice");

        var publicSearchIndex = await WaitForSearchIndexAsync(
            anonymousClient,
            "/docs/search-index.json",
            "Public fixture search marker");
        using var anonymousInternalSearchIndexResponse = await anonymousClient.GetAsync("/internal/docs/search-index.json");
        var anonymousInternalSearchIndex = await anonymousInternalSearchIndexResponse.Content.ReadAsStringAsync();
        var internalSearchIndex = await WaitForSearchIndexAsync(
            contributorClient,
            "/internal/docs/search-index.json",
            "Internal fixture search marker");

        using var publicResponse = await anonymousClient.GetAsync("/docs");
        var publicHtml = await publicResponse.Content.ReadAsStringAsync();
        using var anonymousInternalResponse = await anonymousClient.GetAsync("/internal/docs");
        var anonymousInternalHtml = await anonymousInternalResponse.Content.ReadAsStringAsync();
        using var contributorInternalResponse = await contributorClient.GetAsync("/internal/docs");
        var contributorInternalHtml = await contributorInternalResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Contains("Public Docs", publicHtml, StringComparison.Ordinal);
        Assert.Contains("data-docs-theme-preset=\"appsurface-dark\"", publicHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Contributor Docs", publicHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal fixture search marker", publicSearchIndex, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousInternalSearchIndexResponse.StatusCode);
        Assert.DoesNotContain("Internal fixture search marker", anonymousInternalSearchIndex, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousInternalResponse.StatusCode);
        Assert.DoesNotContain("Contributor Docs", anonymousInternalHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal fixture search marker", anonymousInternalHtml, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, contributorInternalResponse.StatusCode);
        Assert.Contains("Contributor Docs", contributorInternalHtml, StringComparison.Ordinal);
        Assert.Contains("data-docs-theme-preset=\"graphite-dark\"", contributorInternalHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Public Docs", contributorInternalHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Public fixture search marker", internalSearchIndex, StringComparison.Ordinal);

        stopwatch.Stop();
        _output.WriteLine($"Named public/internal ConsumerFixture walkthrough completed in {stopwatch.Elapsed.TotalSeconds:F2}s.");
        Assert.True(
            stopwatch.Elapsed < FixtureWalkthroughTarget,
            $"The ConsumerFixture walkthrough took {stopwatch.Elapsed.TotalSeconds:F2}s; expected less than {FixtureWalkthroughTarget.TotalMinutes:F0} minutes.");
    }

    private static HttpClient CreateClient(string baseUrl)
    {
        return new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
        {
            BaseAddress = new Uri(baseUrl)
        };
    }

    private static async Task<string> WaitForSearchIndexAsync(
        HttpClient client,
        string path,
        string expectedMarker)
    {
        using var timeout = new CancellationTokenSource(ReadinessTimeout);
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(path, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK
                    && body.Contains(expectedMarker, StringComparison.Ordinal))
                {
                    return body;
                }
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                // The in-process host has not accepted the request yet; retry until the bounded readiness deadline.
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The ConsumerFixture search index at '{path}' did not expose '{expectedMarker}' within {ReadinessTimeout.TotalSeconds:F0} seconds.");
            }

            await Task.Delay(ReadinessPollInterval, timeout.Token);
        }
    }
}
