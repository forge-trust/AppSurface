using System.Net;
using System.Text;
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

        Assert.Equal("--url or --base-url must be an absolute http or https URL.", exception.Message);
    }

    [Theory]
    [InlineData("wat", "--expect-icon must use WIDTHxHEIGHT")]
    [InlineData("512x512:", "--expect-icon purpose must not be blank")]
    public async Task ExecuteAsync_RejectsMalformedExpectedIcons(string expectedIcon, string expectedMessage)
    {
        var command = new PwaVerifyCommand(new PwaVerifier(new FakePwaHttpClient()))
        {
            Url = "https://app.example.test",
            ExpectedIcons = [expectedIcon]
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://app.example.test", "../admin", "--entry-path must be an app-root-relative path")]
    [InlineData("https://app.example.test/?token=secret", "/", "must not include a query string or fragment")]
    public async Task ExecuteAsync_ReportsUnsafeVerificationTargetsAsCommandErrors(
        string baseUrl,
        string entryPath,
        string expectedMessage)
    {
        var command = new PwaVerifyCommand(new PwaVerifier(new FakePwaHttpClient()))
        {
            BaseUrl = baseUrl,
            EntryPath = entryPath
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationOptions_NormalizesBlankEntryPathAndFormatsExpectedIconPurpose()
    {
        var options = PwaVerificationOptions.Create(new Uri("https://app.example.test"), entryPath: " ", expectedIcons: null);
        var expectedIcon = PwaExpectedIcon.Parse("512x512:maskable");

        Assert.Equal("/", options.EntryPath);
        Assert.Empty(options.ExpectedIcons);
        Assert.Equal("512x512:maskable", expectedIcon.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_RejectsConflictingUrlAndBaseUrl()
    {
        var command = new PwaVerifyCommand(new PwaVerifier(new FakePwaHttpClient()))
        {
            Url = "https://app.example.test",
            BaseUrl = "https://other.example.test"
        };
        using var console = new FakeInMemoryConsole();

        var exception = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(console));

        Assert.Equal("Use either --url or --base-url, not both.", exception.Message);
    }

    [Fact]
    public void Constructors_RejectNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new PwaVerifyCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new PwaVerifier(null!));
    }

    [Fact]
    public async Task HttpClientAdapter_ReturnsStatusContentTypeBodyAndSuccessState()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("adapter-body", Encoding.UTF8, "text/plain")
                }));
        var adapter = new PwaVerificationHttpClient(httpClient);

        var response = await adapter.GetAsync(new Uri("https://app.example.test/status"), 1024, CancellationToken.None);
        var failed = new PwaHttpResponse(HttpStatusCode.BadGateway, string.Empty, [], null, false);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("text/plain", response.ContentType);
        Assert.Equal("adapter-body", response.Body);
        Assert.True(response.IsSuccess);
        Assert.False(failed.IsSuccess);
    }

    [Fact]
    public async Task HttpClientAdapter_CapturesRedirectLocationWithoutFollowing()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("/next", UriKind.Relative) }
                }))
        {
            BaseAddress = new Uri("https://app.example.test")
        };
        var adapter = new PwaVerificationHttpClient(httpClient);

        var response = await adapter.GetAsync(new Uri("https://app.example.test/status"), 1024, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/next", response.RedirectLocation);
    }

    [Fact]
    public async Task HttpClientAdapter_TruncatesResponseBodiesAtLimit()
    {
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("abcdef", Encoding.UTF8, "text/plain")
                }));
        var adapter = new PwaVerificationHttpClient(httpClient);

        var response = await adapter.GetAsync(new Uri("https://app.example.test/status"), 3, CancellationToken.None);

        Assert.Equal("abc", response.Body);
        Assert.True(response.BodyTruncated);
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
    public async Task ExecuteAsync_WritesJsonV2ReportForEntryPathAndExpectedManifestValues()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "https://app.example.test/account/resume",
            HtmlWithManifestLink(),
            "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var command = new PwaVerifyCommand(new PwaVerifier(http))
        {
            Url = "https://app.example.test",
            EntryPath = "/account/resume",
            ExpectedStartUrl = "/",
            ExpectedScope = "/",
            ExpectedDisplay = "standalone",
            ExpectedThemeColor = "#2563eb",
            ExpectedBackgroundColor = "#ffffff",
            ExpectedIcons = ["192x192", "512x512"],
            Json = true
        };
        using var console = new FakeInMemoryConsole();

        await command.ExecuteAsync(console);

        var output = console.ReadOutputString();
        Assert.Contains("\"schemaVersion\": 2", output, StringComparison.Ordinal);
        Assert.Contains("\"entryPath\": \"/account/resume\"", output, StringComparison.Ordinal);
        Assert.Contains("\"startUrl\": \"/\"", output, StringComparison.Ordinal);
        Assert.Contains("\"icons\": [", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_FollowsSameOriginEntryRedirectForManifestDiscovery()
    {
        var http = new FakePwaHttpClient();
        http.AddRedirect("https://app.example.test/account/resume", "/home?token=secret");
        http.Add("https://app.example.test/home?token=secret", HtmlWithManifestLink(), "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == "ASPWA266"
                && diagnostic.Severity == "info"
                && diagnostic.Actual == "/home");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA262" or "ASPWA263");
    }

    [Fact]
    public void VerificationOptions_RejectsEntryPathWithQuery()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PwaVerificationTarget.Create(new Uri("https://app.example.test"), "/account/resume?token=secret"));

        Assert.Contains("without query strings", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void VerificationTarget_RejectsBlankEntryPaths(string entryPath)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PwaVerificationTarget.Create(new Uri("https://app.example.test"), entryPath));

        Assert.Contains("--entry-path must be an app-root-relative path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/../admin")]
    [InlineData("/%2e%2e/admin")]
    [InlineData(" /account/resume")]
    [InlineData("//cdn.example.test/app")]
    [InlineData("https://app.example.test/account/resume")]
    [InlineData("/account/resume?token=secret")]
    [InlineData("/account/resume#section")]
    [InlineData("/bad path")]
    [InlineData("/bad\tpath")]
    [InlineData("/bad{tenant}")]
    [InlineData("/bad%zz")]
    public void VerificationTarget_RejectsUnsafeEntryPaths(string entryPath)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PwaVerificationTarget.Create(new Uri("https://app.example.test"), entryPath));

        Assert.Contains("--entry-path must be an app-root-relative path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://app.example.test/?token=secret")]
    [InlineData("https://app.example.test/#install")]
    public void VerificationTarget_RejectsBaseUrlQueryOrFragment(string baseUrl)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => PwaVerificationTarget.Create(new Uri(baseUrl), "/"));

        Assert.Contains("must not include a query string or fragment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenEntryRedirectLeavesOrigin()
    {
        var http = new FakePwaHttpClient();
        http.AddRedirect("https://app.example.test/account/resume", "https://login.example.test/account/resume");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA262");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA266");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenEntryRedirectOmitsLocation()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/account/resume", string.Empty, string.Empty, HttpStatusCode.Found);
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA260");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenEntryRedirectLocationIsInvalid()
    {
        var http = new FakePwaHttpClient();
        http.AddRedirect("https://app.example.test/account/resume", "http://[::1");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA261");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenEntryRedirectLeavesPathBase()
    {
        var http = new FakePwaHttpClient();
        http.AddRedirect("https://app.example.test/tenant/account/resume", "/home");
        http.Add("https://app.example.test/tenant/manifest.webmanifest", ValidManifest("/tenant/", "/tenant/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"), "application/manifest+json");
        http.Add("https://app.example.test/tenant/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test/tenant/"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA263");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA266");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenEntryManifestLinkLeavesPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/account/resume", HtmlWithManifestLink("/manifest.webmanifest"), "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest("/tenant/", "/tenant/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"),
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test/tenant/"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA227");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenRedirectLoopExceedsLimit()
    {
        var http = new FakePwaHttpClient();
        http.AddRedirect("https://app.example.test/account/resume", "/account/resume");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), entryPath: "/account/resume"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA264");
        Assert.Equal(5, report.Diagnostics.Count(diagnostic => diagnostic.Code == "ASPWA266"));
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA201" or "ASPWA202");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Actual?.Contains("429", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task VerifyAsync_DoesNotReportHttpFailureWhenManifestRedirectLimitIsExceeded()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.AddRedirect("https://app.example.test/manifest.webmanifest", "/manifest.webmanifest");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA264");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA202");
    }

    [Fact]
    public async Task VerifyAsync_DoesNotReportHttpFailureWhenIconRedirectLimitIsExceeded()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.AddRedirect("https://app.example.test/icons/app-192.png", "/icons/app-192.png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA264");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA212");
    }

    [Fact]
    public async Task VerifyAsync_DoesNotReportHttpWarningWhenDiagnosticsRedirectLimitIsExceeded()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.AddRedirect("https://app.example.test/_appsurface/pwa/status.json", "/_appsurface/pwa/status.json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA264");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA221");
    }

    [Fact]
    public async Task VerifyAsync_DoesNotReportHttpFailuresWhenOfflineResourceRedirectLimitsAreExceeded()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js","offlineFallbackPath":"/offline.html"}""",
            "application/json");
        http.AddRedirect("https://app.example.test/service-worker.js", "/service-worker.js");
        http.AddRedirect("https://app.example.test/offline.html", "/offline.html");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(2, report.Diagnostics.Count(diagnostic => diagnostic.Code == "ASPWA264"));
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA226" or "ASPWA238");
    }

    [Fact]
    public async Task VerifyAsync_DoesNotReportReachabilityWhenServiceWorkerAbsenceRedirectLimitIsExceeded()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":false,"configuredServiceWorkerPath":"/service-worker.js"}""",
            "application/json");
        http.AddRedirect("https://app.example.test/service-worker.js", "/service-worker.js");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA264");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA240" or "ASPWA256");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenExpectedManifestValueDiffers()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(
                new Uri("https://app.example.test"),
                expectedStartUrl: "/account/resume",
                expectedDisplay: "fullscreen"),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA244");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA246");
    }

    [Fact]
    public async Task VerifyAsync_DecodesPngIconDimensionsIntoEvidence()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.AddBytes("https://app.example.test/icons/app-192.png", PngBytes(192, 192), "image/png");
        http.AddBytes("https://app.example.test/icons/app-512.png", PngBytes(512, 512), "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), expectedIcons: ["192x192", "512x512"]),
            CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Icons, icon => icon.Source == "/icons/app-192.png" && icon.Width == 192 && icon.Height == 192);
        Assert.Contains(report.Icons, icon => icon.Source == "/icons/app-512.png" && icon.Width == 512 && icon.Height == 512);
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenExpectedIconDeclarationIsMissing()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), expectedIcons: ["1024x1024"]),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA241");
    }

    [Fact]
    public async Task VerifyAsync_WarnsWhenExpectedIconDimensionsCannotBeDecoded()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(icon192: "/icons/app-192.svg"), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.svg", "<svg></svg>", "image/svg+xml");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), expectedIcons: ["192x192"]),
            CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA243" && diagnostic.Severity == "warning");
    }

    [Fact]
    public async Task VerifyAsync_MatchesExpectedIconPurposeToken()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            """
            {
              "name": "Field Notes",
              "short_name": "Notes",
              "start_url": "/",
              "scope": "/",
              "display": "standalone",
              "theme_color": "#2563eb",
              "background_color": "#ffffff",
              "icons": [
                { "src": "/icons/app-192.png", "sizes": "192x192", "type": "image/png" },
                { "src": "/icons/app-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any maskable" }
              ]
            }
            """,
            "application/manifest+json");
        http.AddBytes("https://app.example.test/icons/app-192.png", PngBytes(192, 192), "image/png");
        http.AddBytes("https://app.example.test/icons/app-512.png", PngBytes(512, 512), "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), expectedIcons: ["512x512:maskable"]),
            CancellationToken.None);

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA241");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenDecodedPngIconDimensionsDoNotMatchManifestSize()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.AddBytes("https://app.example.test/icons/app-192.png", PngBytes(128, 128), "image/png");
        http.AddBytes("https://app.example.test/icons/app-512.png", PngBytes(512, 512), "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA242");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenDecodedPngIconDimensionsDoNotMatchExpectedAssertion()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            """
            {
              "name": "Field Notes",
              "short_name": "Notes",
              "start_url": "/",
              "scope": "/",
              "display": "standalone",
              "theme_color": "#2563eb",
              "background_color": "#ffffff",
              "icons": [
                { "src": "/icons/app-192.png", "sizes": "128x128 192x192", "type": "image/png" },
                { "src": "/icons/app-512.png", "sizes": "512x512", "type": "image/png" }
              ]
            }
            """,
            "application/manifest+json");
        http.AddBytes("https://app.example.test/icons/app-192.png", PngBytes(128, 128), "image/png");
        http.AddBytes("https://app.example.test/icons/app-512.png", PngBytes(512, 512), "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(
            PwaVerificationOptions.Create(new Uri("https://app.example.test"), expectedIcons: ["192x192"]),
            CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == "ASPWA242"
                && diagnostic.Message.Contains("explicit verifier assertion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_RecordsUnfetchedEvidenceForBlankIconSource()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            """
            {
              "name": "Field Notes",
              "short_name": "Notes",
              "start_url": "/",
              "scope": "/",
              "display": "standalone",
              "theme_color": "#2563eb",
              "background_color": "#ffffff",
              "icons": [
                { "src": "/icons/app-192.png", "sizes": "192x192", "type": "image/png" },
                { "src": "/icons/app-512.png", "sizes": "512x512", "type": "image/png" },
                { "src": "", "sizes": "96x96", "type": "image/png" }
              ]
            }
            """,
            "application/manifest+json");
        http.AddBytes("https://app.example.test/icons/app-192.png", PngBytes(192, 192), "image/png");
        http.AddBytes("https://app.example.test/icons/app-512.png", PngBytes(512, 512), "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Icons, icon => icon is { Source: "", Fetched: false });
    }

    [Fact]
    public async Task VerifyAsync_ProvesServiceWorkerAbsenceWhenOfflineIsDisabled()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":false,"configuredServiceWorkerPath":"/service-worker.js"}""",
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA256");
    }

    [Fact]
    public async Task VerifyAsync_AcceptsLegacyStatusWithoutWorkerFields()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":false,"configuredServiceWorkerPath":"/service-worker.js"}""",
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA256");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA257");
    }

    [Fact]
    public async Task VerifyAsync_MixedVersionPushStatusDoesNotRunLegacyAbsenceProbe()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """
            {
              "enabled": false,
              "offlineEnabled": false,
              "serviceWorkerPath": null,
              "offlineFallbackPath": null,
              "configuredServiceWorkerPath": "/service-worker.js",
              "workerEnabled": true,
              "workerPath": "/service-worker.js",
              "pushEnabled": true,
              "workerScope": "/",
              "registrationHelperPath": "/_appsurface/pwa/register.js"
            }
            """,
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "js", "text/javascript");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        var diagnostic = Assert.Single(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA257");
        Assert.Equal("info", diagnostic.Severity);
        Assert.Equal(
            "Push service-worker configuration was observed. Registration, permission, subscription, and delivery were not evaluated.",
            diagnostic.Message);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA240" or "ASPWA256");
    }

    [Fact]
    public async Task VerifyAsync_AcceptsExactNewServerPushOnlyStatusShape()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """
            {
              "enabled": false,
              "manifestPath": "/manifest.webmanifest",
              "offlineEnabled": false,
              "workerEnabled": true,
              "workerPath": "/service-worker.js",
              "pushEnabled": true,
              "workerScope": "/",
              "registrationHelperPath": "/_appsurface/pwa/register.js"
            }
            """,
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA257");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA240" or "ASPWA256");
    }

    [Fact]
    public async Task VerifyAsync_ReportsPushObservationWhenInstallManifestIsAbsent()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", "<!doctype html><html><head></head><body></body></html>", "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", string.Empty, "text/plain", HttpStatusCode.NotFound);
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """
            {
              "enabled": false,
              "manifestPath": "/manifest.webmanifest",
              "offlineEnabled": false,
              "workerEnabled": true,
              "workerPath": "/service-worker.js",
              "pushEnabled": true,
              "workerScope": "/",
              "registrationHelperPath": "/_appsurface/pwa/register.js"
            }
            """,
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA257");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA224" or "ASPWA202");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA240" or "ASPWA256");
    }

    [Fact]
    public async Task VerifyAsync_ReportsPushObservationWhileValidatingCombinedOfflineResources()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """
            {
              "enabled": true,
              "offlineEnabled": true,
              "serviceWorkerPath": "/service-worker.js",
              "offlineFallbackPath": "/offline.html",
              "configuredServiceWorkerPath": "/service-worker.js",
              "workerEnabled": true,
              "workerPath": "/service-worker.js",
              "pushEnabled": true,
              "workerScope": "/",
              "registrationHelperPath": "/_appsurface/pwa/register.js"
            }
            """,
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "js", "text/javascript");
        http.Add("https://app.example.test/offline.html", "offline", "text/html");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA257");
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA226" or "ASPWA238");
    }

    [Fact]
    public async Task VerifyAsync_SkipsServiceWorkerAbsenceProofWhenConfiguredWorkerLeavesOrigin()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":false,"configuredServiceWorkerPath":"https://cdn.example.test/service-worker.js"}""",
            "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code is "ASPWA240" or "ASPWA256");
    }

    [Fact]
    public async Task VerifyAsync_FailsServiceWorkerAbsenceProofWhenOfflineIsDisabledButWorkerIsReachable()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":false,"configuredServiceWorkerPath":"/service-worker.js"}""",
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "js", "text/javascript");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA240");
    }

    [Fact]
    public async Task VerifyAsync_FailsServiceWorkerAbsenceProofWhenOfflineIsDisabledButWorkerReturnsServerError()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":false,"configuredServiceWorkerPath":"/service-worker.js"}""",
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "broken", "text/plain", HttpStatusCode.InternalServerError);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == "ASPWA240" && diagnostic.Actual == "HTTP 500");
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
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/tenant/service-worker.js","offlineFallbackPath":"/tenant/offline.html"}""",
            "application/json");
        http.Add("https://app.example.test/tenant/service-worker.js", "js", "text/javascript");
        http.Add("https://app.example.test/tenant/offline.html", "offline", "text/html");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Equal("https://app.example.test", report.Origin);
        Assert.Equal("https://app.example.test/tenant", report.BaseUrl);
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
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == "ASPWA231"
                && diagnostic.Subject == "manifest.start_url"
                && diagnostic.Expected == "/tenant/"
                && diagnostic.Actual == "/");
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
    public async Task VerifyAsync_FailsWhenManifestLinkIsOutsideHead()
    {
        var http = new FakePwaHttpClient();
        http.Add(
            "https://app.example.test/",
            """<!doctype html><html><head></head><body><link rel="manifest" href="/manifest.webmanifest"></body></html>""",
            "text/html");
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
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
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
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
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
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
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
        http.Add("http://localhost:5000/", HtmlWithManifestLink(), "text/html");
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
        http.Add(
            "http://localhost:5000/",
            """<!doctype html><html><head><link href="/manifest.webmanifest" rel="manifest"></head><body></body></html>""",
            "text/html");
        http.Add("http://localhost:5000/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("http://localhost:5000/icons/app-192.png", "png", "image/png");
        http.Add("http://localhost:5000/icons/app-512.png", "png", "image/png");
        http.Add(
            "http://localhost:5000/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js","offlineFallbackPath":"/offline.html"}""",
            "application/json");
        http.Add("http://localhost:5000/service-worker.js", string.Empty, "text/plain", HttpStatusCode.NotFound);
        http.Add("http://localhost:5000/offline.html", "offline", "text/html");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("http://localhost:5000"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA226");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenRootIsNotHtml()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", "ok", "text/plain");
        http.Add("https://app.example.test/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal("/manifest.webmanifest", report.ManifestPath);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA201" && diagnostic.Severity == "error");
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
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
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
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add("https://app.example.test/manifest.webmanifest", "{}", "application/manifest+json");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA205" && diagnostic.Subject == "manifest.name");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA206" && diagnostic.Subject == "manifest.short_name");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA207");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA208" && diagnostic.Subject == "manifest.start_url");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA209" && diagnostic.Subject == "manifest.scope");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA210");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA211");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenScopeLeavesVerifiedPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", HtmlWithManifestLink("/tenant/manifest.webmanifest"), "text/html");
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
        Assert.Contains(
            report.Diagnostics,
            diagnostic => diagnostic.Code == "ASPWA231"
                && diagnostic.Subject == "manifest.scope"
                && diagnostic.Expected == "/tenant/"
                && diagnostic.Actual == "/admin/");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenStartUrlLeavesManifestScope()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ValidManifest("/admin/?source=pwa", "/app/"),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA239");
    }

    [Fact]
    public async Task VerifyAsync_PassesWhenStartUrlEqualsManifestScopeWithoutTrailingSlash()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ValidManifest("/app", "/app"),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA239");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenStartUrlEqualsScopePathWithoutRequiredTrailingSlash()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/", HtmlWithManifestLink(), "text/html");
        http.Add(
            "https://app.example.test/manifest.webmanifest",
            ValidManifest("/app", "/app/"),
            "application/manifest+json");
        http.Add("https://app.example.test/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/icons/app-512.png", "png", "image/png");
        http.Add("https://app.example.test/_appsurface/pwa/status.json", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA239");
    }

    [Fact]
    public async Task VerifyAsync_FailsForOffOriginOutsideBaseAndNonImageIcons()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", HtmlWithManifestLink("/tenant/manifest.webmanifest"), "text/html");
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
    public async Task VerifyAsync_WarnsWhenDiagnosticsBodyIsTruncated()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.AddTruncated("https://app.example.test/_appsurface/pwa/status.json", "{", "application/json");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.True(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA265" && diagnostic.Severity == "warning");
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
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"https://cdn.example.test/service-worker.js","offlineFallbackPath":"/offline.html"}""",
            "application/json");
        http.Add("https://app.example.test/offline.html", "offline", "text/html");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA229");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsServiceWorkerLeavesPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", HtmlWithManifestLink("/tenant/manifest.webmanifest"), "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest("/tenant/", "/tenant/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"),
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/icons/app-512.png", "png", "image/png");
        http.Add(
            "https://app.example.test/tenant/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js","offlineFallbackPath":"/tenant/offline.html"}""",
            "application/json");
        http.Add("https://app.example.test/tenant/offline.html", "offline", "text/html");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA230");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsOmitFallbackPath()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js"}""",
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "js", "text/javascript");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA235");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsFallbackLeavesOrigin()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js","offlineFallbackPath":"https://cdn.example.test/offline.html"}""",
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "js", "text/javascript");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA236");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsFallbackLeavesPathBase()
    {
        var http = new FakePwaHttpClient();
        http.Add("https://app.example.test/tenant/", HtmlWithManifestLink("/tenant/manifest.webmanifest"), "text/html");
        http.Add(
            "https://app.example.test/tenant/manifest.webmanifest",
            ValidManifest("/tenant/", "/tenant/", "/tenant/icons/app-192.png", "/tenant/icons/app-512.png"),
            "application/manifest+json");
        http.Add("https://app.example.test/tenant/icons/app-192.png", "png", "image/png");
        http.Add("https://app.example.test/tenant/icons/app-512.png", "png", "image/png");
        http.Add(
            "https://app.example.test/tenant/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/tenant/service-worker.js","offlineFallbackPath":"/offline.html"}""",
            "application/json");
        http.Add("https://app.example.test/tenant/service-worker.js", "js", "text/javascript");
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test/tenant/"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA237");
    }

    [Fact]
    public async Task VerifyAsync_FailsWhenOfflineDiagnosticsFallbackIsMissing()
    {
        var http = new FakePwaHttpClient();
        AddValidInstallResponses(http, "https://app.example.test");
        http.Add(
            "https://app.example.test/_appsurface/pwa/status.json",
            """{"enabled":true,"offlineEnabled":true,"serviceWorkerPath":"/service-worker.js","offlineFallbackPath":"/offline.html"}""",
            "application/json");
        http.Add("https://app.example.test/service-worker.js", "js", "text/javascript");
        http.Add("https://app.example.test/offline.html", string.Empty, "text/plain", HttpStatusCode.NotFound);
        var verifier = new PwaVerifier(http);

        var report = await verifier.VerifyAsync(new Uri("https://app.example.test"), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ASPWA238");
    }

    private static void AddValidInstallResponses(FakePwaHttpClient http, string origin)
    {
        http.Add(origin + "/", HtmlWithManifestLink(), "text/html");
        http.Add(origin + "/manifest.webmanifest", ValidManifest(), "application/manifest+json");
        http.Add(origin + "/icons/app-192.png", "png", "image/png");
        http.Add(origin + "/icons/app-512.png", "png", "image/png");
    }

    private static string HtmlWithManifestLink(string href = "/manifest.webmanifest")
    {
        return $"""<!doctype html><html><head><link rel="manifest" href="{href}"></head><body></body></html>""";
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

    private static byte[] PngBytes(int width, int height)
    {
        var bytes = new byte[24];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4e;
        bytes[3] = 0x47;
        bytes[4] = 0x0d;
        bytes[5] = 0x0a;
        bytes[6] = 0x1a;
        bytes[7] = 0x0a;
        bytes[12] = 0x49;
        bytes[13] = 0x48;
        bytes[14] = 0x44;
        bytes[15] = 0x52;
        WriteBigEndian(bytes, 16, width);
        WriteBigEndian(bytes, 20, height);
        return bytes;
    }

    private static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
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
            _responses[url.TrimEnd('/')] = new PwaHttpResponse(statusCode, contentType, Encoding.UTF8.GetBytes(body), null, false);
        }

        public void AddBytes(
            string url,
            byte[] body,
            string contentType,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url.TrimEnd('/')] = new PwaHttpResponse(statusCode, contentType, body, null, false);
        }

        public void AddTruncated(
            string url,
            string body,
            string contentType,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responses[url.TrimEnd('/')] = new PwaHttpResponse(statusCode, contentType, Encoding.UTF8.GetBytes(body), null, true);
        }

        public void AddRedirect(
            string url,
            string location,
            HttpStatusCode statusCode = HttpStatusCode.Found)
        {
            _responses[url.TrimEnd('/')] = new PwaHttpResponse(statusCode, string.Empty, [], location, false);
        }

        public Task<PwaHttpResponse> GetAsync(Uri uri, int maxBodyBytes, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responses.TryGetValue(uri.ToString().TrimEnd('/'), out var response)
                ? response
                : new PwaHttpResponse(HttpStatusCode.NotFound, "text/plain", [], null, false));
        }
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}
