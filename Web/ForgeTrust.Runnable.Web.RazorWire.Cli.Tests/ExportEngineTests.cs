using System.Net;
using System.Text;
using FakeItEasy;
using ForgeTrust.Runnable.Web;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ForgeTrust.Runnable.Web.RazorWire.Cli.Tests;

public class ExportEngineTests
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExportEngine> _logger;
    private readonly ExportEngine _sut;

    public ExportEngineTests()
    {
        _httpClientFactory = A.Fake<IHttpClientFactory>();
        _logger = A.Fake<ILogger<ExportEngine>>();
        _sut = new ExportEngine(_logger, _httpClientFactory);
    }

    [Fact]
    public void Constructor_Should_Throw_If_Null_Dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new ExportEngine(null!, _httpClientFactory));
        Assert.Throws<ArgumentNullException>(() => new ExportEngine(_logger, null!));
    }

    [Theory]
    [InlineData("/css/style.css", "background.png", "/css/background.png")]
    [InlineData("/css/style.css", "../images/bg.png", "/images/bg.png")]
    [InlineData("/index.html", "script.js", "/script.js")]
    [InlineData("/blog/post", "assets/image.jpg", "/blog/assets/image.jpg")]
    [InlineData("/", "style.css", "/style.css")]
    public void ResolveRelativeUrl_Should_Resolve_Correctly(string baseRoute, string assetUrl, string expected)
    {
        // Act
        var result = _sut.ResolveRelativeUrl(baseRoute, assetUrl);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/about?query=1", "/about")]
    [InlineData("/contact#fragment", "/contact")]
    [InlineData("/home", "/home")]
    public void TryGetNormalizedRoute_Should_Normalize_Correctly(string raw, string expectedPath)
    {
        // Act
        var result = _sut.TryGetNormalizedRoute(raw, out var normalized);

        // Assert
        Assert.True(result);
        Assert.Equal(expectedPath, normalized);
    }

    [Theory]
    [InlineData("mailto:user@example.com")]
    [InlineData("tel:1234567890")]
    [InlineData("javascript:void(0)")]
    public void TryGetNormalizedRoute_Should_Return_False_For_Non_Http_Schemes(string raw)
    {
        // Act
        var result = _sut.TryGetNormalizedRoute(raw, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ExtractAssets_Should_Find_Css_Urls()
    {
        // Arrange
        var html = @"<html><head><style>body { background-image: url('bg.png'); }</style></head><body><div style=""background: url('../foo.jpg')""></div></body></html>";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        // Act
        _sut.ExtractAssets(html, "/page", context);

        // Assert
        Assert.Contains("/bg.png", context.Queue);
        Assert.Contains("/foo.jpg", context.Queue);
    }

    [Fact]
    public void ExtractAssets_Should_Filter_Data_And_Hash_Urls()
    {
        // Arrange
        var html = @"<style>
            .icon { background: url('data:image/png;base64,...'); }
            .filter { filter: url('#svg-filter'); }
            .valid { background: url('valid.png'); }
        </style>";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        // Act
        _sut.ExtractAssets(html, "/", context);

        // Assert
        Assert.Contains("/valid.png", context.Queue);
        Assert.DoesNotContain("data:image/png;base64,...", context.Queue);
        Assert.DoesNotContain("#svg-filter", context.Queue);
        Assert.Single(context.Queue); // Should only have one valid asset
    }

    [Fact]
    public void ExtractAssets_Should_Find_Img_Src_And_SrcSet()
    {
        // Arrange
        var html = @"<img src=""/logo.png"" srcset=""/logo-2x.png 2x, /logo-sm.png 300w"" />";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        // Act
        _sut.ExtractAssets(html, "/", context);

        // Assert
        Assert.Contains("/logo.png", context.Queue);
        Assert.Contains("/logo-2x.png", context.Queue);
        Assert.Contains("/logo-sm.png", context.Queue);
    }

    [Fact]
    public void ExtractAssets_Should_Find_Script_Src()
    {
        // Arrange
        var html = @"<script src=""app.js""></script>";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        // Act
        _sut.ExtractAssets(html, "/", context);

        // Assert
        Assert.Contains("/app.js", context.Queue);
    }

    [Fact]
    public void ExtractAssets_Should_Find_Link_Href_For_Stylesheets_Only()
    {
        // Arrange
        var html = @"
            <link rel=""stylesheet"" href=""style.css"">
            <link rel=""icon"" href=""favicon.ico"">
            <link rel=""canonical"" href=""http://example.com/page"">
            <link rel=""alternate"" href=""/fr/page"">";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        // Act
        _sut.ExtractAssets(html, "/", context);

        // Assert
        Assert.Contains("/style.css", context.Queue);
        Assert.Contains("/favicon.ico", context.Queue);
        Assert.DoesNotContain("http://example.com/page", context.Queue);
        Assert.DoesNotContain("/fr/page", context.Queue);
    }

    [Fact]
    public void ExtractLinks_Should_Find_Anchor_Href()
    {
        // Arrange
        var html = @"<a href=""/about"">About</a> <a href=""/contact"">Contact</a>";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        // Act
        _sut.ExtractLinks(html, context);

        // Assert
        Assert.Contains("/about", context.Queue);
        Assert.Contains("/contact", context.Queue);
    }

    [Fact]
    public void ExtractLinks_Should_Skip_Visited_Routes()
    {
        var html = @"<a href=""/about"">About</a> <a href=""/about"">About Again</a>";
        var context = new ExportContext("dist", null, "http://localhost:5000");
        context.Visited.Add("/about");

        _sut.ExtractLinks(html, context);

        Assert.DoesNotContain("/about", context.Queue);
    }

    [Fact]
    public async Task ExtractAssets_Should_Extract_Turbo_Frame_Dependencies_During_Run()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var handler = new FrameAwareHandler();
            var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");

            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Combine(tempDir, "frame", "content.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Throw_When_Seed_File_Is_Missing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var context = new ExportContext(
                tempDir,
                Path.Combine(tempDir, "missing-seeds.txt"),
                "http://localhost:5000");

            await Assert.ThrowsAsync<FileNotFoundException>(() => _sut.RunAsync(context));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Fallback_To_Root_When_Seed_File_Has_No_Valid_Routes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Combine(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["mailto:test@example.com", "javascript:void(0)", ""]);

        try
        {
            var handler = new TestHttpMessageHandler();
            var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Combine(tempDir, "index.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Respect_CancellationToken()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var client = new HttpClient(new SlowHandler());
        A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        try
        {
            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.RunAsync(context, cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Continue_When_Route_Throws_During_Export()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var client = new HttpClient(new ThrowingThenSuccessHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            context.Queue.Enqueue("/throws");
            context.Queue.Enqueue("/");

            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Combine(tempDir, "index.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("//cdn.example.com/app.js")]
    [InlineData("#hash-only")]
    public void TryGetNormalizedRoute_Should_Return_False_For_Invalid_Refs(string raw)
    {
        var ok = _sut.TryGetNormalizedRoute(raw, out var normalized);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public async Task RunAsync_Should_Export_Different_Content_Types_Correctly()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var baseUrl = "http://localhost:5000";

        try
        {
            var handler = new TestHttpMessageHandler();
            var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, baseUrl);
            context.Queue.Enqueue("/"); // Start at root

            // Act
            await _sut.RunAsync(context);

            // Assert
            // 1. Check HTML index
            var indexHtmlPath = Path.Combine(tempDir, "index.html");
            Assert.True(File.Exists(indexHtmlPath), "index.html should exist");
            var indexContent = await File.ReadAllTextAsync(indexHtmlPath);
            Assert.Contains("<h1>Home</h1>", indexContent);

            // 2. Check CSS file
            var cssPath = Path.Combine(tempDir, "style.css");
            Assert.True(File.Exists(cssPath), "style.css should exist");
            var cssContent = await File.ReadAllTextAsync(cssPath);
            Assert.Contains("body { background: white; }", cssContent);

            // 3. Check Binary Image
            var imgPath = Path.Combine(tempDir, "image.png");
            Assert.True(File.Exists(imgPath), "image.png should exist");
            var imgBytes = await File.ReadAllBytesAsync(imgPath);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, imgBytes);

        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Write_404Html_When_ReservedRoute_ReturnsHtml()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var client = new HttpClient(new ConventionalNotFoundPageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var notFoundFile = Path.Combine(tempDir, "404.html");
            Assert.True(File.Exists(notFoundFile));
            var html = await File.ReadAllTextAsync(notFoundFile);
            Assert.Contains("Exported 404 page", html);
            Assert.False(File.Exists(Path.Combine(tempDir, "_runnable", "errors", "404.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Skip_404Html_When_ReservedRoute_IsUnavailable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var client = new HttpClient(new TestHttpMessageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.False(File.Exists(Path.Combine(tempDir, "404.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Skip_404Html_When_ReservedRoute_IsNotHtml()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var client = new HttpClient(new NonHtmlNotFoundPageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.False(File.Exists(Path.Combine(tempDir, "404.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Export_Content_JavaScript_From_Html_Script_Sources()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var client = new HttpClient(new ContentScriptHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var scriptPath = Path.Combine(
                tempDir,
                "_content",
                "ForgeTrust.Runnable.Web.RazorWire",
                "razorwire",
                "razorwire.js");
            Assert.True(File.Exists(scriptPath), "RazorWire _content script should be exported.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Export_Redirected_Stylesheet_To_Original_Route_Path()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var client = new HttpClient(
                new RedirectFollowingHandler(new RedirectedStylesheetHandler()))
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var rootStylesheetPath = Path.Combine(tempDir, "css", "site.gen.css");
            Assert.True(File.Exists(rootStylesheetPath), "Expected redirected root stylesheet to be exported.");

            var stylesheet = await File.ReadAllTextAsync(rootStylesheetPath);
            Assert.Contains(".docs-content { color: cyan; }", stylesheet);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RedirectFollowingHandler_Should_StopAfterConfiguredRedirectLimit()
    {
        var loopHandler = new RedirectLoopHandler();
        using var client = new HttpClient(new RedirectFollowingHandler(loopHandler, maxRedirects: 3))
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/loop", response.Headers.Location?.OriginalString);
        Assert.Equal(4, loopHandler.CallCount);
    }

    [Fact]
    public void MapHtmlFilePathToPartialPath_Should_Append_Partial_Suffix()
    {
        var htmlPath = Path.Combine("dist", "docs", "topic.html");

        var partialPath = ExportEngine.MapHtmlFilePathToPartialPath(htmlPath);

        Assert.EndsWith(Path.Combine("docs", "topic.partial.html"), partialPath);
    }

    [Fact]
    public void ExtractDocContentFrame_Should_Return_Target_Frame_When_Present()
    {
        var html = "<html><body><turbo-frame id=\"doc-content\"><h1>Doc</h1></turbo-frame></body></html>";

        var frame = ExportEngine.ExtractDocContentFrame(html);

        Assert.Equal("<turbo-frame id=\"doc-content\"><h1>Doc</h1></turbo-frame>", frame);
    }

    [Fact]
    public void ExtractDocContentFrame_Should_Handle_Nested_TurboFrames()
    {
        var html = """
            <html><body>
            <turbo-frame id="doc-content">
              <h1>Doc</h1>
              <turbo-frame id="nested"><p>Nested frame</p></turbo-frame>
              <p>Tail</p>
            </turbo-frame>
            </body></html>
            """;

        var frame = ExportEngine.ExtractDocContentFrame(html);

        Assert.NotNull(frame);
        Assert.Contains("<turbo-frame id=\"nested\"><p>Nested frame</p></turbo-frame>", frame);
        Assert.EndsWith("</turbo-frame>", frame);
    }

    [Fact]
    public void AddDocsStaticPartialsMarker_Should_Inject_Meta_Tag_Into_Head()
    {
        var html = "<html><head><title>Docs</title></head><body>content</body></html>";

        var updated = ExportEngine.AddDocsStaticPartialsMarker(html);

        Assert.Contains("<meta name=\"rw-docs-static-partials\" content=\"1\" />", updated);
        Assert.Contains($"{Environment.NewLine}<meta name=\"rw-docs-static-partials\" content=\"1\" />", updated);
        Assert.Contains("</head>", updated);
        Assert.True(
            updated.IndexOf("rw-docs-static-partials", StringComparison.OrdinalIgnoreCase)
            < updated.IndexOf("</head>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddDocsStaticPartialsMarker_Should_Be_Idempotent()
    {
        var html = "<html><head><meta name=\"rw-docs-static-partials\" content=\"1\" /></head><body>content</body></html>";

        var updated = ExportEngine.AddDocsStaticPartialsMarker(html);

        Assert.Equal(1, updated.Split("rw-docs-static-partials", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void AddDocsStaticPartialsMarker_Should_Prepend_Marker_With_Newline_When_No_Head()
    {
        var html = "<html><body>content</body></html>";

        var updated = ExportEngine.AddDocsStaticPartialsMarker(html);

        Assert.StartsWith($"{Environment.NewLine}<meta name=\"rw-docs-static-partials\" content=\"1\" />", updated);
    }

    [Fact]
    public void IsDocsExportPage_ShouldDetectCustomRootRazorDocsClientConfig()
    {
        var html = """
            <html>
              <head>
                <script>window.__razorDocsConfig = {"docsRootPath":"/foo/bar/next"};</script>
              </head>
            </html>
            """;

        Assert.True(ExportEngine.IsDocsExportPage("/foo/bar/next/search", html));
    }

    [Fact]
    public async Task RunAsync_Should_Export_Docs_Partial_Fragments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Combine(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["/docs/start", "/docs"]);

        try
        {
            var client = new HttpClient(new DocsPartialHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            var fullPagePath = Path.Combine(tempDir, "docs", "start.html");
            var partialPath = Path.Combine(tempDir, "docs", "start.partial.html");
            var docsLandingPath = Path.Combine(tempDir, "docs.html");
            var docsLandingPartialPath = Path.Combine(tempDir, "docs.partial.html");

            Assert.True(File.Exists(fullPagePath), "Expected docs full page export.");
            Assert.True(File.Exists(partialPath), "Expected docs partial export.");
            Assert.True(File.Exists(docsLandingPath), "Expected /docs full page export.");
            Assert.False(
                File.Exists(docsLandingPartialPath),
                "Did not expect /docs partial export without a doc-content frame.");

            var partialHtml = await File.ReadAllTextAsync(partialPath);
            Assert.Contains("<turbo-frame id=\"doc-content\">", partialHtml);
            Assert.Contains("<turbo-frame id=\"nested-frame\"><p>Nested doc frame</p></turbo-frame>", partialHtml);
            Assert.DoesNotContain("<html", partialHtml, StringComparison.OrdinalIgnoreCase);

            var fullHtml = await File.ReadAllTextAsync(fullPagePath);
            Assert.Contains("<meta name=\"rw-docs-static-partials\" content=\"1\" />", fullHtml);

            var nextPartialPath = Path.Combine(tempDir, "docs", "next.partial.html");
            Assert.True(
                File.Exists(nextPartialPath),
                "Expected docs partial export for crawl-discovered /docs/next.");

            var nextPartialHtml = await File.ReadAllTextAsync(nextPartialPath);
            Assert.Contains("<turbo-frame id=\"doc-content\">", nextPartialHtml);
            Assert.Contains("<article>Next doc</article>", nextPartialHtml);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Export_CustomRoot_Docs_Partial_Fragments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Combine(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["/foo/bar/next"]);

        try
        {
            var client = new HttpClient(new CustomRootDocsPartialHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            var fullPagePath = Path.Combine(tempDir, "foo", "bar", "next.html");
            var partialPath = Path.Combine(tempDir, "foo", "bar", "next.partial.html");

            Assert.True(File.Exists(fullPagePath), "Expected custom-root docs full page export.");
            Assert.True(File.Exists(partialPath), "Expected custom-root docs partial export.");

            var fullHtml = await File.ReadAllTextAsync(fullPagePath);
            Assert.Contains("<meta name=\"rw-docs-static-partials\" content=\"1\" />", fullHtml);

            var partialHtml = await File.ReadAllTextAsync(partialPath);
            Assert.Contains("<turbo-frame id=\"doc-content\">", partialHtml);
            Assert.Contains("<article>Mounted doc</article>", partialHtml);
            Assert.DoesNotContain("<html", partialHtml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }


    private class TestHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";

            if (path == "/" || path == "/index")
            {
                var html = @"<html>
                    <body>
                        <h1>Home</h1>
                        <link rel=""stylesheet"" href=""style.css"">
                        <img src=""image.png"">
                    </body>
                </html>";
                var content = new StringContent(html, Encoding.UTF8, "text/html");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            if (path == "/style.css")
            {
                var content = new StringContent("body { background: white; }", Encoding.UTF8, "text/css");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            if (path == "/image.png")
            {
                var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
                var byteContent = new ByteArrayContent(bytes);
                byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = byteContent });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FrameAwareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/")
            {
                var html = @"<html><body><turbo-frame src=""/frame/content""></turbo-frame></body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            if (path == "/frame/content")
            {
                var html = "<html><body><h2>Frame</h2></body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>slow</body></html>", Encoding.UTF8, "text/html")
            };
        }
    }

    private sealed class ThrowingThenSuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/throws")
            {
                throw new InvalidOperationException("boom");
            }

            if (path == "/" || path == "/index")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><h1>Recovered</h1></body></html>", Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ContentScriptHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                var html = @"<html><body><script src=""/_content/ForgeTrust.Runnable.Web.RazorWire/razorwire/razorwire.js?v=abc123""></script></body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            if (path == "/_content/ForgeTrust.Runnable.Web.RazorWire/razorwire/razorwire.js")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("console.log('ok');", Encoding.UTF8, "text/javascript")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RedirectedStylesheetHandler : HttpMessageHandler
    {
        private const string RootStylesheetPath = "/css/site.gen.css";
        private const string PackagedStylesheetPath = "/_content/ForgeTrust.Runnable.Web.RazorDocs/css/site.gen.css";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";

            if (path == "/" || path == "/index")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"""<html><body><link rel="stylesheet" href="{RootStylesheetPath}"></body></html>""",
                        Encoding.UTF8,
                        "text/html")
                });
            }

            if (path == RootStylesheetPath)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = new Uri(PackagedStylesheetPath, UriKind.Relative)
                    }
                });
            }

            if (path == PackagedStylesheetPath)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(".docs-content { color: cyan; }", Encoding.UTF8, "text/css")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DocsPartialHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/docs")
            {
                var html = """
                    <html>
                      <body>
                        <main>Docs landing page</main>
                        <a href="/docs/start">Start</a>
                      </body>
                    </html>
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            if (path == "/docs/start")
            {
                var html = """
                    <html>
                      <body>
                        <turbo-frame id="doc-content">
                          <article>Start doc</article>
                          <turbo-frame id="nested-frame"><p>Nested doc frame</p></turbo-frame>
                        </turbo-frame>
                        <a href="/docs/next">Next</a>
                      </body>
                    </html>
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            if (path == "/docs/next")
            {
                var html = """
                    <html>
                      <body>
                        <turbo-frame id="doc-content">
                          <article>Next doc</article>
                        </turbo-frame>
                      </body>
                    </html>
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CustomRootDocsPartialHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/foo/bar/next")
            {
                var html = """
                    <html>
                      <head>
                        <script>window.__razorDocsConfig = {"docsRootPath":"/foo/bar/next","docsSearchUrl":"/foo/bar/next/search","docsSearchIndexUrl":"/foo/bar/next/search-index.json"};</script>
                      </head>
                      <body>
                        <turbo-frame id="doc-content">
                          <article>Mounted doc</article>
                        </turbo-frame>
                      </body>
                    </html>
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ConventionalNotFoundPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == ConventionalNotFoundPageDefaults.ReservedNotFoundRoute)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><h1>Exported 404 page</h1></body></html>", Encoding.UTF8, "text/html")
                });
            }

            if (path == "/" || path == "/index")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><h1>Home</h1></body></html>", Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NonHtmlNotFoundPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == ConventionalNotFoundPageDefaults.ReservedNotFoundRoute)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":404}", Encoding.UTF8, "application/json")
                });
            }

            if (path == "/" || path == "/index")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><h1>Home</h1></body></html>", Encoding.UTF8, "text/html")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RedirectLoopHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers =
                {
                    Location = new Uri("/loop", UriKind.Relative)
                }
            });
        }
    }

    private sealed class RedirectFollowingHandler : DelegatingHandler
    {
        private readonly int _maxRedirects;

        public RedirectFollowingHandler(HttpMessageHandler innerHandler, int maxRedirects = 10)
            : base(innerHandler)
        {
            _maxRedirects = maxRedirects;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var currentRequest = request;
            HttpRequestMessage? ownedRedirectRequest = null;
            var response = await base.SendAsync(currentRequest, cancellationToken);

            try
            {
                for (var redirectCount = 0; redirectCount < _maxRedirects; redirectCount++)
                {
                    if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                    {
                        return response;
                    }

                    var currentRequestUri = currentRequest.RequestUri;
                    if (currentRequestUri is null || !currentRequestUri.IsAbsoluteUri)
                    {
                        return response;
                    }

                    var redirectUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(currentRequestUri, response.Headers.Location);

                    response.Dispose();
                    ownedRedirectRequest?.Dispose();

                    ownedRedirectRequest = new HttpRequestMessage(currentRequest.Method, redirectUri);
                    currentRequest = ownedRedirectRequest;
                    response = await base.SendAsync(currentRequest, cancellationToken);
                }

                return response;
            }
            finally
            {
                ownedRedirectRequest?.Dispose();
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.Moved
                or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;
        }
    }

}
