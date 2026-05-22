using System.Net;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsSourceRouteRedirectTests
{
    [Theory]
    [InlineData(
        "/docs/examples/razorwire-mvc/README.md?view=compact",
        "/docs/examples/razorwire-mvc?view=compact")]
    [InlineData(
        "/docs/examples/razorwire-mvc/README.md.html?view=compact",
        "/docs/examples/razorwire-mvc?view=compact")]
    [InlineData(
        "/docs/guides/from-program-cs-to-module.md.html?view=compact",
        "/docs/guides/from-program-cs-to-module?view=compact")]
    public async Task SourceShapedMarkdownRoutes_ShouldRedirectToCanonicalDocsRoute(
        string requestPath,
        string expectedLocation)
    {
        var host = await AppSurfaceDocsInProcessHost.StartAsync("http://127.0.0.1:0");

        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(host.BaseUrl)
            };

            using var response = await client.GetAsync(requestPath);

            Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
            Assert.Equal(expectedLocation, response.Headers.Location?.ToString());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
