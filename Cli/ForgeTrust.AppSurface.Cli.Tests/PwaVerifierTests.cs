using System.Net;
using CliFx;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class PwaVerifierTests
{
    [Fact]
    public async Task ExecuteAsync_WritesTextReport_WhenVerificationPasses()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var command = new PwaVerifyCommand(new PwaVerifier(http)) { Url = "https://app.example.test" };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.Contains("PWA verification passed.", output, StringComparison.Ordinal);
        Assert.Contains("INFO ASPWA220:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WritesJsonReportAndThrows_WhenVerificationFails()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", "<html></html>", "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var command = new PwaVerifyCommand(new PwaVerifier(http))
        {
            Url = "https://app.example.test",
            Json = true
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Equal("PWA verification failed.", exception.Message);
        var output = console.ReadOutputString();
        Assert.Contains("\"passed\": false", output, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"ASPWA224\"", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/app")]
    public async Task ExecuteAsync_RejectsInvalidUrl(string? url)
    {
        var command = new PwaVerifyCommand(new PwaVerifier(new FakePwaHttpClient())) { Url = url };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Equal("--url must be an absolute http or https URL.", exception.Message);
    }

    [Fact]
    public void Constructors_RejectNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new PwaVerifyCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new PwaVerifier(null!));
    }

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
    public async Task VerifyAsync_FailsWhenManifestOmitsInstallColors()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ManifestWithoutInstallColors(),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA232");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA233");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenManifestInstallColorsAreInvalid()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ValidManifest(themeColor: "blue", backgroundColor: "ffffff"),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA232");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA233");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenManifestDisplayIsUnsupported()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ValidManifest(display: "wat"),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA234");
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

    [Fact]
    public async Task VerifyAsync_WarnsAndUsesDefaultManifestWhenRootIsNotHtml()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", "ok", "text/plain");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal("/manifest.webmanifest", report.ManifestPath);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA201" && diagnostic.Severity == "warning");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenRootManifestLinkLeavesOrigin()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "https://app.example.test/",
            """<!doctype html><html><head><link rel="manifest" href="https://cdn.example.test/manifest.webmanifest"></head></html>""",
            "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA225");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenManifestHasWrongContentTypeAndInvalidJson()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", "{", "text/plain");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA203");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA204");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenManifestOmitsRequiredInstallFields()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", "{}", "application/manifest+json");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA205");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA206");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA207");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA208");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA209");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA210");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA211");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenScopeLeavesVerifiedPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", """<link rel="manifest" href="/tenant/manifest.webmanifest">""", "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest("/tenant/", "/admin/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"),
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA231");
    }

    [Fact]
    public async Task VerifyAsync_FailsForOffOriginOutsideBaseAndNonImageIcons()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", """<link rel="manifest" href="/tenant/manifest.webmanifest">""", "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            """
            {
              "name": "Field Notes",
              "short_name": "Notes",
              "start_url": "/tenant/",
              "scope": "/tenant/",
              "display": "standalone",
              "theme_color": "#2563eb",
              "background_color": "#ffffff",
              "icons": [
                { "src": "https://cdn.example.test/icons/app-192.png", "sizes": "192x192", "type": "image/png" },
                { "src": "/icons/app-512.png", "sizes": "512x512", "type": "image/png" },
                { "src": "/tenant/icons/badge.svg", "sizes": "96x96", "type": "image/svg+xml" }
              ]
            }
            """,
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/badge.svg", "<svg></svg>", "text/plain");
        http.Add("https://app.example.test/tenant/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA214");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA228");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA213");
    }

    [Fact]
    public async Task VerifyAsync_WarnsWhenDiagnosticsReturnError()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", "nope", "text/plain", HttpStatusCode.InternalServerError);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA221" && diagnostic.Severity == "warning");
    }

    [Fact]
    public async Task VerifyAsync_WarnsWhenDiagnosticsJsonCannotBeParsed()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", "{", "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA223" && diagnostic.Severity == "warning");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsOmitServiceWorkerPath()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", """{"enabled":true,"offlineEnabled":true}""", "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA222");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsServiceWorkerLeavesOrigin()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"https://cdn.example.test/service-worker.js"}""",
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA229");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsServiceWorkerLeavesPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", """<link rel="manifest" href="/tenant/manifest.webmanifest">""", "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest("/tenant/", "/tenant/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"),
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/icons/app-512.png", "png", "image/png");
        http.Add(
            "https://app.example.test/tenant/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js"}""",
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA230");
    }

    private static void AddValidInstallResponses(FakePwaHttpClient http, string origin)
    {
        http.Add(origin + "/", """<link rel="manifest" href="/manifest.webmanifest">""", "text/html");
        http.Add(origin + "/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add(origin + "/icons/app-192.png", "png", "image/png");
        http.Add(origin + "/icons/app-512.png", "png", "image/png");
    }

    private static string ValidManifest(
        string startUrl = "/",
        string scope = "/",
        string icon192 = "/icons/app-192.png",
        string icon512 = "/icons/app-512.png",
        string themeColor = "#2563eb",
        string backgroundColor = "#ffffff",
        string display = "standalone")
    {
        return $$"""
        {
          "name": "Field Notes",
          "short_name": "Notes",
          "start_url": {{System.Text.Json.JsonSerializer.Serialize(startUrl)}},
          "scope": {{System.Text.Json.JsonSerializer.Serialize(scope)}},
          "display": {{System.Text.Json.JsonSerializer.Serialize(display)}},
          "theme_color": {{System.Text.Json.JsonSerializer.Serialize(themeColor)}},
          "background_color": {{System.Text.Json.JsonSerializer.Serialize(backgroundColor)}},
          "icons": [
            { "src": {{System.Text.Json.JsonSerializer.Serialize(icon192)}}, "sizes": "192x192", "type": "image/png" },
            { "src": {{System.Text.Json.JsonSerializer.Serialize(icon512)}}, "sizes": "512x512", "type": "image/png" }
          ]
        }
        """;
    }

    private static string ManifestWithoutInstallColors()
    {
        return """
        {
          "name": "Field Notes",
          "short_name": "Notes",
          "start_url": "/",
          "scope": "/",
          "display": "standalone",
          "icons": [
            { "src": "/icons/app-192.png", "sizes": "192x192", "type": "image/png" },
            { "src": "/icons/app-512.png", "sizes": "512x512", "type": "image/png" }
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
