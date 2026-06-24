using System.Net;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class PwaVerifierTests
{
    [Fact]
    public async Task VerifyAsync_PassesForValidAppWithHiddenProductionDiagnostics()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "https://app.example.test/",
            """
            <!doctype html><html><head><link rel="manifest" href="/manifest.webmanifest"></head><body></body></html>
            """,
            "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ValidManifest(),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA220" && diagnostic.Severity == "info");
    }

    [Fact]
    public async Task VerifyAsync_PreservesPathBaseFromUrl()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "https://app.example.test/tenant/",
            """
            <!doctype html><html><head><link rel="manifest" href="/tenant/manifest.webmanifest"></head><body></body></html>
            """,
            "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest("/tenant/", "/tenant/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"),
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/icons/app-512.png", "png", "image/png");
        http.Add(
            "https://app.example.test/tenant/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/tenant/service-worker.js"}""",
            "application/json");
        http.Add("https://app.example.test/tenant/service-worker.js", "js", "text/javascript");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal("https://app.example.test/tenant", report.Origin);
        Assert.Equal("/tenant/manifest.webmanifest", report.ManifestPath);
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenManifestLeavesVerifiedPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "https://app.example.test/tenant/",
            """
            <!doctype html><html><head><link rel="manifest" href="/tenant/manifest.webmanifest"></head><body></body></html>
            """,
            "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest(),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA231");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA228");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenManifestIsMissing()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", "<html></html>", "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA202");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenRootHtmlOmitsManifestLink()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", "<!doctype html><html><head></head><body></body></html>", "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA224");
    }

    [Fact]
    public async Task VerifyAsync_FailsUnsafeHttpOriginAndOffOriginStartUrl()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "http://app.example.test/",
            """<html><head><link rel="manifest" href="/manifest.webmanifest"></head></html>""",
            "text/html");
        http.Add(
            "http://app.example.test/manifest.webmanifest",
            ValidManifest("""https://other.example.test/"""),
            "application/manifest+json");
        http.Add("http://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("http://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("http://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("http://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA200");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA208");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenIconIsNotReachable()
    {
        var http = new FakePwaHttpClient();
        http.Add("http://localhost:5000/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add("http://localhost:5000/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("http://localhost:5000/icons/app-192.png", string.Empty, "text/plain", HttpStatusCode.NotFound);
        http.Add("http://localhost:5000/icons/app-512.png", "png", "image/png");
        http.Add("http://localhost:5000/_appsurface/pwa/status.json", """{"enabled":true,"offlineEnabled":false}""", "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("http://localhost:5000"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA212");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsServiceWorkerIsMissing()
    {
        var http = new FakePwaHttpClient();
        http.Add("http://localhost:5000/", """<link href="/manifest.webmanifest" rel="manifest">""", "text/html");
        http.Add("http://localhost:5000/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("http://localhost:5000/icons/app-192.png", "png", "image/png");
        http.Add("http://localhost:5000/icons/app-512.png", "png", "image/png");
        http.Add(
            "http://localhost:5000/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js"}""",
            "application/json");
        http.Add("http://localhost:5000/service-worker.js", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("http://localhost:5000"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA226");
    }

    private static string ValidManifest(
        string startUrl = "/",
        string scope = "/",
        string icon192 = "/icons/app-192.png",
        string icon512 = "/icons/app-512.png")
    {
        return $$"""
        {
          "name": "Field Notes",
          "short_name": "Notes",
          "start_url": {{System.Text.Json.JsonSerializer.Serialize(startUrl)}},
          "scope": {{System.Text.Json.JsonSerializer.Serialize(scope)}},
          "display": "standalone",
          "theme_color": "#2563eb",
          "background_color": "#ffffff",
          "icons": [
            { "src": {{System.Text.Json.JsonSerializer.Serialize(icon192)}}, "sizes": "192x192", "type": "image/png" },
            { "src": {{System.Text.Json.JsonSerializer.Serialize(icon512)}}, "sizes": "512x512", "type": "image/png" }
          ]
        }
        """;
    }

    private sealed class FakePwaHttpClient : IPwaVerificationHttpClient
    {
        private readonly Dictionary<string, PwaHttpResponse> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void Add(
            string url,
            string body,
            string contentType,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url.TrimEnd('/')] = new PwaHttpResponse(statusCode, contentType, body);
        }

        public Task<PwaHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responses.TryGetValue(uri.ToString().TrimEnd('/'), out var response)
                ? response
                : new PwaHttpResponse(HttpStatusCode.NotFound, "text/plain", string.Empty));
        }
    }
}
