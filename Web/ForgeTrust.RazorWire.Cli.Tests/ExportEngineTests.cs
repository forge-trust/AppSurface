using System.Net;
using System.Text;
using FakeItEasy;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ForgeTrust.RazorWire.Cli.Tests;

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
    [InlineData("/docs/../about?query=1#fragment", "/about")]
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

    [Theory]
    [InlineData("//docs/example", "/docs/example", "aliasRoute")]
    [InlineData("/docs/example/README.md", "//docs/example", "canonicalRoute")]
    public void AddRedirectArtifact_Should_Reject_Protocol_Relative_Routes(
        string aliasRoute,
        string canonicalRoute,
        string expectedParamName)
    {
        var context = new ExportContext("dist", null, "http://localhost:5000");

        var exception = Assert.Throws<ArgumentException>(
            () => context.AddRedirectArtifact(aliasRoute, canonicalRoute));

        Assert.Equal(expectedParamName, exception.ParamName);
        Assert.Contains("not protocol-relative", exception.Message);
    }

    [Fact]
    public void AddRedirectAlias_Should_Register_Normalized_Routes()
    {
        var context = new ExportContext("dist", null, "http://localhost:5000");

        context.AddRedirectAlias(" /docs/example/README.md/ ", " /docs/example/ ");

        var artifact = Assert.Single(context.RedirectArtifacts);
        Assert.Equal("/docs/example/README.md", artifact.AliasRoute);
        Assert.Equal("/docs/example", artifact.CanonicalRoute);
    }

    [Theory]
    [InlineData("/docs/example\n", "/docs/example", "aliasRoute")]
    [InlineData("/docs/example\r", "/docs/example", "aliasRoute")]
    [InlineData("/docs/example", "/docs/example\n", "canonicalRoute")]
    [InlineData("/docs/example", "/docs/example\t", "canonicalRoute")]
    public void AddRedirectAlias_Should_Reject_Control_Characters(
        string aliasRoute,
        string canonicalRoute,
        string expectedParamName)
    {
        var context = new ExportContext("dist", null, "http://localhost:5000");

        var exception = Assert.Throws<ArgumentException>(
            () => context.AddRedirectAlias(aliasRoute, canonicalRoute));

        Assert.Equal(expectedParamName, exception.ParamName);
        Assert.Contains("must not contain newline, carriage return, or tab", exception.Message);
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
    public void ExtractAssets_Should_Find_Source_SrcSet()
    {
        var html = """
            <picture>
              <source srcset="/hero.avif 1x, /hero.webp 2x" type="image/avif">
              <img src="/hero.png" alt="">
            </picture>
            """;
        var context = new ExportContext("dist", null, "http://localhost:5000");

        _sut.ExtractAssets(html, "/", context);

        Assert.Contains("/hero.avif", context.Queue);
        Assert.Contains("/hero.webp", context.Queue);
        Assert.Contains("/hero.png", context.Queue);
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
    public void ExtractLinks_Should_Resolve_Relative_Urls_Against_CurrentRoute()
    {
        var html = @"<a href=""next"">Next</a>";
        var context = new ExportContext("dist", null, "http://localhost:5000");

        _sut.ExtractLinks(html, context, "/docs/start");

        var reference = Assert.Single(context.References);
        Assert.Equal("/docs/next", reference.Path);
        Assert.Equal("/docs/start", reference.SourceRoute);
        Assert.Contains("/docs/next", context.Queue);
    }

    [Fact]
    public async Task ExtractAssets_Should_Extract_Turbo_Frame_Dependencies_During_Run()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new FrameAwareHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);

            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "frame", "content.html")));
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var context = new ExportContext(
                tempDir,
                Path.Join(tempDir, "missing-seeds.txt"),
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Join(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["mailto:test@example.com", "javascript:void(0)", ""]);

        try
        {
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
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
    public async Task RunAsync_Should_Use_InMemory_Seed_Routes_When_No_Seed_File_Is_Provided()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new DocsSeedHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/docs"],
                baseUrl: "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "docs.html")));
            Assert.Contains("/docs", handler.RequestPaths);
            Assert.DoesNotContain("/", context.RouteOutcomes.Keys);
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
    public async Task RunAsync_Should_Use_InMemory_Seed_Routes_When_Seed_File_Is_Blank()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new DocsSeedHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: " ",
                initialSeedRoutes: ["/docs"],
                baseUrl: "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "docs.html")));
            Assert.Contains("/docs", handler.RequestPaths);
            Assert.DoesNotContain("/", context.RouteOutcomes.Keys);
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
    public async Task RunAsync_Should_Fallback_To_Root_When_InMemory_Seed_Routes_Have_No_Valid_Routes()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["mailto:test@example.com", "javascript:void(0)", ""],
                baseUrl: "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
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
    public async Task RunAsync_Should_Normalize_Absolute_Seed_Urls_Against_BaseUrl_PathBase()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Join(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["http://localhost:5000/app/docs"]);

        try
        {
            var handler = new PathBaseSeedHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000/app");
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "docs.html")));
            Assert.DoesNotContain("/app/docs", context.RouteOutcomes.Keys);
            Assert.Contains("/docs", context.RouteOutcomes.Keys);
            Assert.Contains("/app/docs", handler.RequestPaths);
            Assert.DoesNotContain("/app/app/docs", handler.RequestPaths);
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        using var client = new HttpClient(new SlowHandler());
        A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        try
        {
            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            using var client = new HttpClient(new ThrowingThenSuccessHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            context.Queue.Enqueue("/throws");
            context.Queue.Enqueue("/");

            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
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
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var baseUrl = "http://localhost:5000";

        try
        {
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, baseUrl);
            context.Queue.Enqueue("/"); // Start at root

            // Act
            await _sut.RunAsync(context);

            // Assert
            // 1. Check HTML index
            var indexHtmlPath = TestPathUtils.PathUnder(tempDir, "index.html");
            Assert.True(File.Exists(indexHtmlPath), "index.html should exist");
            var indexContent = await File.ReadAllTextAsync(indexHtmlPath);
            Assert.Contains("<h1>Home</h1>", indexContent);

            // 2. Check CSS file
            var cssPath = TestPathUtils.PathUnder(tempDir, "style.css");
            Assert.True(File.Exists(cssPath), "style.css should exist");
            var cssContent = await File.ReadAllTextAsync(cssPath);
            Assert.Contains("body { background: white; }", cssContent);

            // 3. Check Binary Image
            var imgPath = TestPathUtils.PathUnder(tempDir, "image.png");
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
    public async Task RunAsync_ShouldWriteReleaseArchiveManifest_WhenExplicitlyEnabled()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var baseUrl = "http://localhost:5000";

        try
        {
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, baseUrl);
            context.EnableReleaseArchiveManifest();

            await _sut.RunAsync(context);

            var manifestPath = TestPathUtils.PathUnder(tempDir, ".appsurface-docs-release-manifest.json");
            Assert.True(File.Exists(manifestPath));
            Assert.NotNull(context.ReleaseArchiveManifest);
            Assert.Equal(manifestPath, context.ReleaseArchiveManifest.ManifestPath);
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
    public async Task RunAsync_Should_Copy_Deployment_Extra_After_Export()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var sourceRoot = Directory.CreateTempSubdirectory("razorwire-export-extra-source-").FullName;
        try
        {
            var sourcePath = Path.Join(sourceRoot, "CNAME");
            await File.WriteAllTextAsync(sourcePath, "docs.example.com");
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);
            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            context.AddDeploymentExtra(sourcePath, "/CNAME");

            await _sut.RunAsync(context);

            var targetPath = Path.Join(tempDir, "CNAME");
            Assert.True(File.Exists(targetPath));
            Assert.Equal("docs.example.com", await File.ReadAllTextAsync(targetPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Fail_When_Deployment_Extra_Targets_Generated_Output()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var sourceRoot = Directory.CreateTempSubdirectory("razorwire-export-extra-source-").FullName;
        try
        {
            var sourcePath = Path.Join(sourceRoot, "index.html");
            await File.WriteAllTextAsync(sourcePath, "<html>extra</html>");
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);
            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            context.AddDeploymentExtra(sourcePath, "/index.html");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-generated-collision]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Fail_Before_Crawl_When_ReleaseArchive_Has_Deployment_Extras()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var sourceRoot = Directory.CreateTempSubdirectory("razorwire-export-extra-source-").FullName;
        try
        {
            var sourcePath = Path.Join(sourceRoot, "CNAME");
            await File.WriteAllTextAsync(sourcePath, "docs.example.com");
            using var handler = new CountingHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);
            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            context.AddDeploymentExtra(sourcePath, "/CNAME");
            context.EnableReleaseArchiveManifest();

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[release-archive-incompatible]", diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Fail_When_Deployment_Extra_Target_Already_Exists()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var sourceRoot = Directory.CreateTempSubdirectory("razorwire-export-extra-source-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Join(tempDir, "CNAME"), "existing.example.com");
            var sourcePath = Path.Join(sourceRoot, "CNAME");
            await File.WriteAllTextAsync(sourcePath, "docs.example.com");
            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);
            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            context.AddDeploymentExtra(sourcePath, "/CNAME");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-exists]", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Validate_All_Deployment_Extra_Target_Parents_Before_Copying()
    {
        var tempDir = Directory.CreateTempSubdirectory("razorwire-export-engine-").FullName;
        var sourceRoot = Directory.CreateTempSubdirectory("razorwire-export-extra-source-").FullName;
        var outsideRoot = Directory.CreateTempSubdirectory("razorwire-export-extra-outside-").FullName;
        try
        {
            var cnameSourcePath = Path.Join(sourceRoot, "CNAME");
            await File.WriteAllTextAsync(cnameSourcePath, "docs.example.com");
            var securitySourcePath = Path.Join(sourceRoot, "security.txt");
            await File.WriteAllTextAsync(securitySourcePath, "Contact: mailto:security@example.com");
            var linkedParent = Path.Join(tempDir, "linked");
            if (!TryCreateDirectorySymlink(linkedParent, outsideRoot))
            {
                return;
            }

            using var handler = new TestHttpMessageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);
            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            context.AddDeploymentExtra(cnameSourcePath, "/CNAME");
            context.AddDeploymentExtra(securitySourcePath, "/linked/security.txt");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("RWEXPORT007", diagnostic.Code);
            Assert.Contains("[target-parent-symlink]", diagnostic.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Join(tempDir, "CNAME")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_Should_Write_404Html_When_ReservedRoute_ReturnsHtml()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var handler = new ConventionalNotFoundPageHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var notFoundFile = Path.Join(tempDir, "404.html");
            Assert.True(File.Exists(notFoundFile));
            var html = await File.ReadAllTextAsync(notFoundFile);
            var decodedHtml = Uri.UnescapeDataString(html);
            Assert.Contains("Exported 404 page", html);
            Assert.Contains("href=\"/about.html\"", html);
            Assert.Contains("href=\"/docs/sections/start-here\" data-rw-export-ignore=\"true\"", html);
            Assert.Contains("href=\"/docs/sections/packages\" data-rw-export-ignore=\"true\"", html);
            Assert.Contains("src=\"/img/error.png\"", html);
            Assert.DoesNotContain("Diagnostics", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_health", decodedHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_routes", decodedHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(handler.RequestPaths, path => path.Contains("_health", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Contains("_routes", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Join(tempDir, "_appsurface", "errors", "404.html")));
            Assert.False(File.Exists(Path.Join(tempDir, "401.html")));
            Assert.False(File.Exists(Path.Join(tempDir, "403.html")));
            Assert.True(File.Exists(Path.Join(tempDir, "about.html")));
            Assert.False(File.Exists(Path.Join(tempDir, "docs", "sections", "start-here.html")));
            Assert.False(File.Exists(Path.Join(tempDir, "docs", "sections", "packages.html")));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/docs/sections/start-here", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/docs/sections/packages", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/401", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/401.html", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/403", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/403.html", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/404", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Equals("/404.html", StringComparison.OrdinalIgnoreCase));
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
    public async Task RunAsync_Should_Preserve_Reserved_404Html_When_SeedFile_Includes_404Html()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Join(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["/404.html"]);

        try
        {
            using var client = new HttpClient(new ConventionalNotFoundPageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            var notFoundFile = Path.Join(tempDir, "404.html");
            Assert.True(File.Exists(notFoundFile));
            var html = await File.ReadAllTextAsync(notFoundFile);
            Assert.Contains("Exported 404 page", html);
            Assert.True(context.RouteOutcomes.TryGetValue("/404.html", out var outcome));
            Assert.True(outcome.Succeeded);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_404Html_References_Missing_Asset()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new MissingConventionalNotFoundAssetHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT003", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/missing-404.js", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_CdnMode_Should_Report_Reference_Provenance_For_Missing_Assets()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new MissingParserDiscoveredAssetHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));
            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT003");

            Assert.Contains("<img src>", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("Normalized path: '/img/missing.png'", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("Add the route or asset", diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal("/img/missing.png", diagnostic.Reference?.Path);
            Assert.Equal("<img src>", diagnostic.Reference?.Provenance?.DisplaySource);
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new TestHttpMessageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.False(File.Exists(Path.Join(tempDir, "404.html")));
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new NonHtmlNotFoundPageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.False(File.Exists(Path.Join(tempDir, "404.html")));
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new ContentScriptHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var scriptPath = Path.Join(
                tempDir,
                "_content",
                "ForgeTrust.RazorWire",
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(
                new RedirectFollowingHandler(new RedirectedStylesheetHandler()))
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var rootStylesheetPath = Path.Join(tempDir, "css", "site.gen.css");
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
    public async Task RunAsync_CdnMode_Should_Rewrite_Managed_Urls_To_Static_Artifacts()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var handler = new CdnRewriteHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var indexHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("href=\"/about.html\"", indexHtml);
            Assert.Contains("data-copy=\"/about\" href=\"/about.html\"", indexHtml);
            Assert.Contains("href=\"/docs/start.html#intro\"", indexHtml);
            Assert.Contains("src=\"/docs/start.partial.html\"", indexHtml);
            Assert.Contains("src=\"/_content/pkg/app.js?v=abc123\"", indexHtml);
            Assert.Contains("href=\"/css/site.css\"", indexHtml);
            Assert.Contains("src=\"/img/logo.png\"", indexHtml);
            Assert.Contains("data-copy=\"img/hero.avif 1x, img/hero.webp 2x\" srcset=\"/img/hero.avif 1x, /img/hero.webp 2x\"", indexHtml);
            Assert.Contains("srcset=\"/img/hero.avif 1x, /img/hero.webp 2x\"", indexHtml);
            Assert.Contains("srcset=\"/img/logo-2x.png 2x, /img/logo-small.png 300w\"", indexHtml);
            Assert.Contains("srcset=\"/img/a.png 1x, /img/a.png?version=1 2x\"", indexHtml);
            Assert.DoesNotContain("//img/a.png?version=1", indexHtml);
            Assert.Contains("data-copy=\".hero { background: url('img/inline.png'); }\">.hero { background: url('/img/inline.png'); }</style>", indexHtml);
            Assert.Contains("data-copy=\"background: url('img/attr.png')\" style=\"background: url('/img/attr.png')\"", indexHtml);

            var aboutHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "about.html"));
            Assert.Contains("<h1>About</h1>", aboutHtml);
            Assert.True(File.Exists(Path.Join(tempDir, "docs", "start.html")));
            Assert.True(File.Exists(Path.Join(tempDir, "docs", "start.partial.html")));

            var css = await File.ReadAllTextAsync(Path.Join(tempDir, "css", "site.css"));
            Assert.Contains("url('/img/bg.png?v=1')", css);
            Assert.True(File.Exists(Path.Join(tempDir, "_content", "pkg", "app.js")));
            Assert.True(File.Exists(Path.Join(tempDir, "img", "bg.png")));
            Assert.True(File.Exists(Path.Join(tempDir, "img", "hero.avif")));
            Assert.True(File.Exists(Path.Join(tempDir, "img", "hero.webp")));
            Assert.All(
                context.RouteOutcomes.Values.Where(outcome => outcome.Succeeded && (outcome.IsHtml || outcome.IsCss)),
                outcome => Assert.Null(outcome.TextBody));
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
    public async Task RunAsync_CdnMode_Should_Rewrite_Source_Preserving_Parser_Discovered_References()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new ParserBackedRewriteHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var indexHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.StartsWith("<!DOCTYPE html>\n<HTML data-kind=raw>", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<!-- keep me -->", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<A HREF=/about.html data-extra='keep'>About</A>", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<a DATA-RW-EXPORT-IGNORE href=/ignored>Ignored</a>", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<a href=./Program.cs>Program</a>", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<LINK REL='stylesheet preload' HREF=/styles/app data-copy=1>", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<LINK REL='notstylesheet' HREF=/wrong.css>", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""<script SRC=/app>const fake = "<img src=/about>";""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<!-- <a href=/about>Comment fake link</a> -->", indexHtml, StringComparison.Ordinal);
            Assert.Contains("<!-- <style>.comment-fake { background:url(/about); }</style> -->", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""const fake = "<img src=/about>";""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""const close = "</script-not-real><img src=/about>";""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""const fakeStyle = "<style>.script-fake { background:url(/about); }</style>";""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""/* url('/img/comment.png') */""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""content: "url('/img/string.png')";""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""@import "/about.html";""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("background:URL(/about.html)", indexHtml, StringComparison.Ordinal);
            Assert.Contains("srcset=\"data:image/svg+xml,%3Csvg%3E,%3C/svg%3E 1x, /img/logo-2x.png?v=1 2x\"", indexHtml, StringComparison.Ordinal);

            var stylesheet = await File.ReadAllTextAsync(Path.Join(tempDir, "styles", "app"));
            Assert.Contains("url('/about.html')", stylesheet, StringComparison.Ordinal);
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
    public async Task RunAsync_CdnMode_Should_Export_Favicon_Link_Assets()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new FaviconLinkHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var faviconPath = Path.Join(tempDir, "branding", "appsurface-site-icon.svg");
            Assert.True(File.Exists(faviconPath), "Expected favicon link asset to be exported.");
            Assert.Contains("Exported AppSurface icon", await File.ReadAllTextAsync(faviconPath), StringComparison.Ordinal);
            Assert.True(context.RouteOutcomes.TryGetValue("/branding/appsurface-site-icon.svg", out var faviconOutcome));
            Assert.True(faviconOutcome.Succeeded);
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
    public async Task RunAsync_CdnMode_Should_Append_Html_For_Dotted_Extensionless_Page_Routes()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new DottedPageRouteHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var indexHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            var packageHtmlPath = Path.Join(tempDir, "docs", "web", "forgetrust.razorwire.html");
            var childHtmlPath = Path.Join(tempDir, "docs", "web", "forgetrust.razorwire", "docs.html");

            Assert.Contains("href=\"/docs/web/forgetrust.razorwire.html\"", indexHtml);
            Assert.True(File.Exists(packageHtmlPath), "Expected dotted page route to export as an HTML artifact.");
            Assert.True(File.Exists(childHtmlPath), "Expected child route under dotted slug to export without colliding with its parent.");

            var packageHtml = await File.ReadAllTextAsync(packageHtmlPath);
            Assert.Contains("href=\"/docs/web/forgetrust.razorwire/docs.html\"", packageHtml);
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
    public async Task RunAsync_CdnMode_Should_Write_Redirect_Alias_Artifacts_After_Canonical_Routes()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, ["/"], "http://localhost:5000");
            context.AddRedirectArtifact("/docs/example/README.md", "/docs/example");
            context.AddRedirectArtifact("/docs/example/README.md.html", "/docs/example");

            await _sut.RunAsync(context);

            var canonicalHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "docs", "example.html"));
            Assert.Contains("""<link rel="canonical" href="/docs/example.html">""", canonicalHtml);

            var aliasArtifactPath = Path.Join(tempDir, "docs", "example", "README.md.html");
            Assert.True(File.Exists(aliasArtifactPath));

            var aliasHtml = await File.ReadAllTextAsync(aliasArtifactPath);
            Assert.Contains("""<meta name="appsurface-docs-redirect-alias" content="1">""", aliasHtml);
            Assert.Contains("""<meta name="appsurface-docs-canonical-artifact" content="/docs/example.html">""", aliasHtml);
            Assert.Contains("""<link rel="canonical" href="/docs/example.html">""", aliasHtml);
            Assert.DoesNotContain("<h1>Example</h1>", aliasHtml);
            Assert.True(context.RouteOutcomes["/docs/example/README.md"].IsRedirectAliasArtifact);
            Assert.True(context.RouteOutcomes["/docs/example/README.md.html"].IsRedirectAliasArtifact);
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
    public async Task RunAsync_CdnMode_Should_Write_Netlify_Redirect_Rules_Without_Alias_Html()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/example/README.md.html", "/docs/example");
            context.AddRedirectAlias("/docs/example/README.md", "/docs/example");

            await _sut.RunAsync(context);

            var canonicalHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "docs", "example.html"));
            Assert.Contains("""<link rel="canonical" href="/docs/example.html">""", canonicalHtml);
            Assert.False(File.Exists(Path.Join(tempDir, "docs", "example", "README.md.html")));

            var redirects = await File.ReadAllLinesAsync(Path.Join(tempDir, "_redirects"));
            Assert.Equal(
                [
                    "/docs/example/README.md /docs/example 301!",
                    "/docs/example/README.md.html /docs/example 301!"
                ],
                redirects);
            Assert.False(context.RouteOutcomes.ContainsKey("/docs/example/README.md"));
            Assert.False(context.RouteOutcomes.ContainsKey("/docs/example/README.md.html"));
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
    public async Task RunAsync_CdnMode_Should_Sort_Dedupe_And_Percent_Encode_Netlify_Redirect_Rules()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/Legacy Space/guide:one*%25", "/docs/example");
            context.AddRedirectAlias("/docs/alpha/éxample", "/docs/example");
            context.AddRedirectAlias("/docs/Legacy Space/guide:one*%25", "/docs/example");

            await _sut.RunAsync(context);

            var redirects = await File.ReadAllLinesAsync(Path.Join(tempDir, "_redirects"));
            Assert.Equal(
                [
                    "/docs/Legacy%20Space/guide%3Aone%2A%25 /docs/example 301!",
                    "/docs/alpha/%C3%A9xample /docs/example 301!"
                ],
                redirects);
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
    public async Task RunAsync_CdnMode_Should_Preserve_Custom_Roots_In_Netlify_Redirect_Rules()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/reference/next/packages/README.md", "/docs/example");
            context.AddRedirectAlias("/packages/README.md", "/docs/example");

            await _sut.RunAsync(context);

            var redirects = await File.ReadAllLinesAsync(Path.Join(tempDir, "_redirects"));
            Assert.Equal(
                [
                    "/packages/README.md /docs/example 301!",
                    "/reference/next/packages/README.md /docs/example 301!"
                ],
                redirects);
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
    public async Task RunAsync_CdnMode_Should_Crawl_Registered_Seed_Routes_Before_Redirect_Validation()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, ["/"], "http://localhost:5000");
            context.AddSeedRoute("/docs/other");
            context.AddRedirectArtifact("/docs/other/README.md.html", "/docs/other");

            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "docs", "other.html")));
            Assert.True(File.Exists(Path.Join(tempDir, "docs", "other", "README.md.html")));
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Redirect_Alias_Canonical_Artifact_Is_Missing()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, ["/"], "http://localhost:5000");
            context.AddRedirectArtifact("/docs/missing/README.md.html", "/docs/missing");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/missing/README.md.html", diagnostic.Route);
            Assert.Contains("could not resolve canonical artifact", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Redirect_Alias_Targets_Two_Canonicals()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/other"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/legacy", "/docs/example");
            context.AddRedirectAlias("/docs/legacy", "/docs/other");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(
                exception.Diagnostics,
                item => item.Code == "RWEXPORT005"
                        && item.Message.Contains("targets both '/docs/example' and '/docs/other'", StringComparison.Ordinal));
            Assert.Equal("/docs/legacy", diagnostic.Route);
            Assert.Contains("targets both '/docs/example' and '/docs/other'", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Redirect_Alias_Serializes_To_Canonical()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/example", "/docs/example");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(
                exception.Diagnostics,
                item => item.Code == "RWEXPORT005"
                        && item.Message.Contains("serializes to the same path as canonical route", StringComparison.Ordinal));
            Assert.Equal("/docs/example", diagnostic.Route);
            Assert.Contains("serializes to the same path as canonical route", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Redirect_Alias_Serialized_Path_Targets_Two_Canonicals()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/other"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/legacy guide", "/docs/example");
            context.AddRedirectAlias("/docs/legacy%20guide", "/docs/other");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/legacy%20guide", diagnostic.Route);
            Assert.Contains("serializes to '/docs/legacy%20guide'", diagnostic.Message);
            Assert.Contains("already targets '/docs/example'", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Redirect_Route_Cannot_Be_Serialized()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.RedirectArtifacts.Add(new ExportRedirectArtifact("//bad", "/docs/example"));

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(
                exception.Diagnostics,
                item => item.Code == "RWEXPORT005"
                        && item.Message.Contains("cannot be represented in _redirects output", StringComparison.Ordinal));
            Assert.Equal("//bad", diagnostic.Route);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Redirects_File_Would_Overwrite_Exported_Artifact()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new RedirectsFileCollisionHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/_redirects"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/example/README.md", "/docs/example");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/_redirects", diagnostic.Route);
            Assert.Contains("would overwrite the artifact for exported route '/_redirects'", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Alias_Route_Is_Redirects_File()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/_redirects", "/docs/example");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/_redirects", diagnostic.Route);
            Assert.Contains("reserves the root '_redirects' file", diagnostic.Message);
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
    public async Task RunAsync_HybridMode_Should_Fail_When_Netlify_Redirect_Strategy_Is_Selected()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/example/README.md", "/docs/example");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/_redirects", diagnostic.Route);
            Assert.Contains("require CDN export mode", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Netlify_Redirect_Alias_Collides_With_NonHtml_Route()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/example.txt"],
                baseUrl: "http://localhost:5000",
                redirectStrategy: ExportRedirectStrategy.Netlify);
            context.AddRedirectAlias("/docs/example.txt", "/docs/example");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/example.txt", diagnostic.Route);
            Assert.Contains("conflicts with an exported route at the same published path", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Redirect_Alias_Is_Crawled_As_Html_Body()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler(aliasReturnsBody: true)) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/example/README.md.html"],
                baseUrl: "http://localhost:5000");
            context.AddRedirectArtifact("/docs/example/README.md.html", "/docs/example");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/example/README.md.html", diagnostic.Route);
            Assert.Contains("normal HTML page body", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Redirect_Alias_Would_Overwrite_Another_Artifact()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/other"],
                baseUrl: "http://localhost:5000");
            context.AddRedirectArtifact("/docs/example.html", "/docs/other");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/example.html", diagnostic.Route);
            Assert.Contains("would overwrite the artifact for exported route '/docs/example'", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Redirect_Alias_Would_Overwrite_Docs_Partial_Artifact()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/other"],
                baseUrl: "http://localhost:5000");
            context.AddRedirectArtifact("/docs/example.partial", "/docs/other");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/example.partial", diagnostic.Route);
            Assert.Contains("would overwrite the artifact for exported route '/docs/example partial'", diagnostic.Message);
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
    public async Task RunAsync_HybridMode_Should_Fail_When_Redirect_Alias_Would_Overwrite_Another_Artifact()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            using var client = new HttpClient(new DocsRedirectArtifactHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/", "/docs/other"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid);
            context.AddRedirectArtifact("/docs/example.html", "/docs/other");

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT005");
            Assert.Equal("/docs/example.html", diagnostic.Route);
            Assert.Contains("would overwrite the artifact for exported route '/docs/example'", diagnostic.Message);
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
    public async Task RunAsync_HybridMode_Should_Preserve_Extensionless_Managed_Urls()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new CdnRewriteHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            await _sut.RunAsync(context);

            var indexHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("href=\"/about\"", indexHtml);
            Assert.Contains("href=\"/docs/start#intro\"", indexHtml);
            Assert.Contains("src=\"/docs/start\"", indexHtml);
            Assert.DoesNotContain("href=\"/about.html\"", indexHtml);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Frame_Route_Is_Missing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingFrameHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT001", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Frame_Source_Has_Query()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new QueryFrameHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT002", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Required_Asset_Is_Missing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingAssetHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT003", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_HybridMode_Should_Fail_When_Required_Asset_Is_Missing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingAssetHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT003", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Hybrid export can leave page routes", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/missing.js", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("use hybrid mode", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task RunAsync_HybridMode_Should_Fail_When_Css_References_Missing_Image()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingCssImageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT003", ex.Message, StringComparison.Ordinal);
            Assert.Contains("stylesheet url()", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/img/map-image.png", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_HybridMode_Should_Fail_When_ModulePreload_Asset_Is_Missing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingModulePreloadHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT003", ex.Message, StringComparison.Ordinal);
            Assert.Contains("<link href>", ex.Message, StringComparison.Ordinal);
            Assert.Contains("rel 'modulepreload'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("/assets/app.js", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_HybridMode_Should_Tolerate_Page_Metadata_And_Ambiguous_Hints()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridToleratedReferenceHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
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
    public async Task RunAsync_CdnMode_Should_Fail_When_Anchor_Cannot_Rewrite()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingAnchorHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            var ex = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains("RWEXPORT004", ex.Message, StringComparison.Ordinal);
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
    public async Task RunAsync_CdnMode_Should_Leave_Source_Navigation_Anchors_Unvalidated_And_Unrewritten()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new SourceNavigationAnchorHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var indexHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("""<a href="./Program.cs">Program</a>""", indexHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("./Program.cs.html", indexHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(context.RouteOutcomes.Keys, route => route == "/Program.cs");
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
    public async Task RunAsync_CdnMode_Should_Not_Rewrite_Anchors_With_DataRwExportIgnore()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new ExportIgnoreAnchorHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var indexHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("""<a href="./source.txt" data-rw-export-ignore="true">Source</a>""", indexHtml, StringComparison.Ordinal);
            Assert.Contains("""<a href="/about.html">About</a>""", indexHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("./source.txt.html", indexHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(context.RouteOutcomes.Keys, route => route == "/source.txt");
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
    public async Task RunAsync_CdnMode_Should_Export_RootSeed_And_Allow_404HomeRecoveryLink_To_Be_ExportIgnored()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Join(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["/", "/docs"]);

        try
        {
            using var client = new HttpClient(new RedirectFollowingHandler(new IgnoredRootRecoveryNotFoundPageHandler()))
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            var notFoundHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "404.html"));
            Assert.Contains("href=\"/\" data-rw-export-ignore=\"true\"", notFoundHtml, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
            Assert.True(File.Exists(Path.Join(tempDir, "docs.html")));
            Assert.True(context.RouteOutcomes.TryGetValue("/", out var rootOutcome));
            Assert.True(rootOutcome.Succeeded);
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
    public async Task RunAsync_HybridMode_Should_Continue_When_Managed_Dependency_Is_Missing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new MissingFrameHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_RewriteManagedLiveReferencesAndLazyForms()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridLiveOriginHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("data-rw-live-origin=\"https://api.example.com\"", html);
            Assert.Contains("data-rw-hybrid-credentials=\"include\"", html);
            Assert.Contains("data-rw-antiforgery-endpoint=\"/_rw/antiforgery/token\"", html);
            Assert.Contains("src=\"https://api.example.com/rw/stream?channel=profile\"", html);
            Assert.Contains("src=\"https://api.example.com/islands/profile\"", html);
            Assert.Contains("action=\"https://api.example.com/profile/save\"", html);
            Assert.Contains("data-rw-antiforgery=\"lazy\"", html);
            Assert.DoesNotContain("crawler-token", html);
            Assert.False(File.Exists(Path.Join(tempDir, "islands", "profile.html")));
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_RewritePathBaseAbsoluteFormActions()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridPathBaseLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"https://api.example.com/profile/save\"", html);
            Assert.DoesNotContain("action=\"https://api.example.com/app/profile/save\"", html);
            Assert.Contains("data-rw-antiforgery-endpoint=\"/_rw/antiforgery/token\"", html);
            Assert.DoesNotContain("data-rw-antiforgery-endpoint=\"/app/_rw/antiforgery/token\"", html);
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_RewritePathBaseRootRelativeFormActions()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridPathBaseRootRelativeLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"https://api.example.com/profile/save\"", html);
            Assert.DoesNotContain("action=\"https://api.example.com/app/profile/save\"", html);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_RewritePathBaseRootRelativeFormActions()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridPathBaseRootRelativeLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"/profile/save\"", html);
            Assert.DoesNotContain("action=\"/app/profile/save\"", html);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_RejectRootRelativeFormActionsOutsidePathBase()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridRootRelativeOutsidePathBaseLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT006"
                                                                 && diagnostic.Message.Contains("points outside the exported application origin", StringComparison.Ordinal));
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_RewritePathBaseAbsoluteFormActions()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridPathBaseLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"/profile/save\"", html);
            Assert.DoesNotContain("action=\"/app/profile/save\"", html);
            Assert.DoesNotContain("action=\"http://localhost:5000", html);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_RejectAbsoluteFormActionsOutsidePathBase()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridOutsidePathBaseLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT006"
                                                                 && diagnostic.Message.Contains("points outside the exported application origin", StringComparison.Ordinal));
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_RejectAbsoluteFormActionsOutsidePathBase()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridOutsidePathBaseLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT006"
                                                                 && diagnostic.Message.Contains("points outside the exported application origin", StringComparison.Ordinal));
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_RejectRootRelativeFormActionsOutsidePathBase()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridRootRelativeOutsidePathBaseLiveOriginHandler())
            {
                BaseAddress = new Uri("http://localhost:5000")
            };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000/app",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT006"
                                                                 && diagnostic.Message.Contains("points outside the exported application origin", StringComparison.Ordinal));
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_FailUnsafeStaticTokenForms()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridUnsafeTokenHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT006"
                                                                 && diagnostic.Message.Contains("not managed by RazorWire", StringComparison.Ordinal));
            Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "RWEXPORT006"
                                                                 && diagnostic.Message.Contains("points outside the exported application origin", StringComparison.Ordinal));
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_FailLazyAntiforgeryWhenCredentialsAreOmitted()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridLiveOriginHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Omit
                });

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("hybrid credentials are explicitly omitted", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_FailStaticAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new StaticTokenFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Cdn,
                redirectStrategy: ExportRedirectStrategy.Html);

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("CDN mode", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_FailFormAssociatedStaticAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new FormAssociatedStaticTokenHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Cdn,
                redirectStrategy: ExportRedirectStrategy.Html);

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("CDN mode", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_FailUnownedStaticAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new UnownedStaticTokenHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Cdn,
                redirectStrategy: ExportRedirectStrategy.Html);

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("not owned by any form", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_FailLazyAntiforgeryWithoutStaticToken()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new LazyAntiforgeryFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Cdn,
                redirectStrategy: ExportRedirectStrategy.Html);

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("CDN mode", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_FailCustomNamedStaticAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new CustomNamedTokenFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Cdn,
                redirectStrategy: ExportRedirectStrategy.Html);

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("CDN mode", diagnostic.Message);
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
    public async Task RunAsync_CdnMode_Should_FailXsrfNamedStaticAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new XsrfNamedTokenFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Cdn,
                redirectStrategy: ExportRedirectStrategy.Html);

            var exception = await Assert.ThrowsAsync<ExportValidationException>(() => _sut.RunAsync(context));

            var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == "RWEXPORT006");
            Assert.Contains("CDN mode", diagnostic.Message);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_ConvertLazyFormsForSameOriginPassthrough()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new StaticTokenFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    CredentialsMode = RazorWireHybridCredentialsMode.Omit
                });

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"/profile/save\"", html);
            Assert.Contains("data-rw-antiforgery=\"lazy\"", html);
            Assert.DoesNotContain("crawler-token", html);
            Assert.Empty(context.Diagnostics);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_ConvertXsrfNamedAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new XsrfNamedTokenFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"/profile/save\"", html);
            Assert.Contains("data-rw-antiforgery=\"lazy\"", html);
            Assert.DoesNotContain("xsrf-token", html);
            Assert.DoesNotContain("crawler-token", html);
            Assert.Empty(context.Diagnostics);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_ConvertFormAssociatedAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new FormAssociatedStaticTokenHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"/profile/save\"", html);
            Assert.Contains("data-rw-antiforgery=\"lazy\"", html);
            Assert.Contains("id=\"profile\"", html);
            Assert.DoesNotContain("crawler-token", html);
            Assert.Empty(context.Diagnostics);
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
    public async Task RunAsync_HybridMode_WithoutLiveOrigin_Should_RemoveAllOwnedStaticAntiforgeryTokens()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new DuplicateStaticTokenFormHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions());

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("action=\"/profile/save\"", html);
            Assert.Contains("data-rw-antiforgery=\"lazy\"", html);
            Assert.DoesNotContain("__RequestVerificationToken", html);
            Assert.DoesNotContain("crawler-token", html);
            Assert.Empty(context.Diagnostics);
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_PreserveCustomAntiforgeryEndpoint()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new CustomEndpointHybridHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("data-rw-antiforgery-endpoint=\"/tokens/antiforgery\"", html);
            Assert.DoesNotContain("data-rw-antiforgery-endpoint=\"/_rw/antiforgery/token\"", html);
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
    public async Task RunAsync_HybridMode_WithLiveOrigin_Should_PreserveFragmentShapeWhenRewritingFrameForms()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HybridLiveFragmentHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(
                tempDir,
                seedRoutesPath: null,
                initialSeedRoutes: ["/"],
                baseUrl: "http://localhost:5000",
                mode: ExportMode.Hybrid,
                redirectStrategy: ExportRedirectStrategy.Html,
                hybridOptions: new ExportHybridOptions
                {
                    LiveOrigin = "https://api.example.com",
                    CredentialsMode = RazorWireHybridCredentialsMode.Auto
                });

            await _sut.RunAsync(context);

            var fragmentHtml = await File.ReadAllTextAsync(Path.Join(tempDir, "frame", "content.html"));
            Assert.StartsWith("<turbo-frame", fragmentHtml.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<html", fragmentHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<body", fragmentHtml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("action=\"https://api.example.com/frame/save\"", fragmentHtml);
            Assert.Contains("data-rw-antiforgery=\"lazy\"", fragmentHtml);
            Assert.DoesNotContain("crawler-token", fragmentHtml);
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
    public async Task RunAsync_HybridMode_Should_Write_Text_Artifacts_Without_Buffering_Bodies()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new CdnRewriteHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000", ExportMode.Hybrid);
            await _sut.RunAsync(context);

            Assert.True(File.Exists(Path.Join(tempDir, "index.html")));
            Assert.True(File.Exists(Path.Join(tempDir, "css", "site.css")));
            Assert.All(
                context.RouteOutcomes.Values.Where(outcome => outcome.Succeeded && (outcome.IsHtml || outcome.IsCss)),
                outcome => Assert.Null(outcome.TextBody));
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
    public void ExtractReferences_Should_Preserve_Raw_Resolved_Path_Query_Fragment_And_Provenance()
    {
        var html = """
            <a href="details/page?tab=api#usage">API</a>
            <a href="details/page#usage?expanded=true">Fragment Query Text</a>
            """;

        var references = _sut.ExtractReferences(html, "/docs/start", htmlScope: true);
        var queryThenFragment = references[0];
        var fragmentWithQuestionMark = references[1];

        Assert.Equal("/docs/start", queryThenFragment.SourceRoute);
        Assert.Equal(ExportReferenceKind.AnchorHref, queryThenFragment.Kind);
        Assert.Equal("details/page?tab=api#usage", queryThenFragment.RawValue);
        Assert.Equal("/docs/details/page?tab=api#usage", queryThenFragment.ResolvedUrl);
        Assert.Equal("/docs/details/page", queryThenFragment.Path);
        Assert.Equal("?tab=api", queryThenFragment.Query);
        Assert.Equal("#usage", queryThenFragment.Fragment);

        Assert.Equal("/docs/details/page", fragmentWithQuestionMark.Path);
        Assert.Equal(string.Empty, fragmentWithQuestionMark.Query);
        Assert.Equal("#usage?expanded=true", fragmentWithQuestionMark.Fragment);
    }

    [Fact]
    public void ExtractReferences_Should_Ignore_Hash_Only_Html_And_Css_References()
    {
        var content = """
            <a href="#intro">Intro</a>
            <style>.filter { filter: url(#svg-filter); }</style>
            <div style="clip-path: url('#clip-path')"></div>
            <a href="/docs#usage">Docs</a>
            """;

        var reference = Assert.Single(_sut.ExtractReferences(content, "/docs/start", htmlScope: true));

        Assert.Equal("/docs", reference.Path);
        Assert.Equal("#usage", reference.Fragment);
    }

    [Fact]
    public void ExtractReferences_Should_Ignore_Anchors_Marked_For_Export_Ignore()
    {
        var html = """
            <a href="./source.txt" data-rw-export-ignore="true">Source</a>
            <a data-rw-export-ignore href="./source.bin">Source</a>
            <a href="./README.md">Readme</a>
            """;

        var reference = Assert.Single(_sut.ExtractReferences(
            html,
            "/docs/Web/ForgeTrust.AppSurface.Docs.Standalone/README.md",
            htmlScope: true));

        Assert.Equal(ExportReferenceKind.AnchorHref, reference.Kind);
        Assert.Equal("./README.md", reference.RawValue);
        Assert.Equal("/docs/Web/ForgeTrust.AppSurface.Docs.Standalone/README.md", reference.Path);
    }

    [Fact]
    public void ExtractReferences_Should_Ignore_Relative_Source_Navigation_Anchors()
    {
        var html = """
            <a href="./Program.cs">Program</a>
            <a href="../Project.csproj?plain=1#top">Project</a>
            <a href="/downloads/Program.cs">Download</a>
            """;

        var reference = Assert.Single(_sut.ExtractReferences(
            html,
            "/docs/Web/ForgeTrust.AppSurface.Docs.Standalone/README.md.html",
            htmlScope: true));

        Assert.Equal(ExportReferenceKind.AnchorHref, reference.Kind);
        Assert.Equal("/downloads/Program.cs", reference.RawValue);
        Assert.Equal("/downloads/Program.cs", reference.Path);
    }

    [Fact]
    public void ExtractReferences_Should_Use_Parser_Backed_Html_And_Stateful_Css_Discovery()
    {
        var html = """
            <A HREF=/about>About</A>
            <a href=/ignored data-rw-export-ignore>Ignored</a>
            <a href=./Program.cs>Program</a>
            <link rel="notstylesheet" href="/wrong.css">
            <link rel="stylesheet preload" href="/styles/site">
            <img SRC=/img/logo.png srcset="data:image/svg+xml,%3Csvg%3E,%3C/svg%3E 1x, /img/logo-2x.png 2x, /img/logo.png?version=1#frag 3x">
            <div style="background:url('/img/attr.png')"></div>
            <style>
            /* url('/img/comment.png') */
            .literal::before { content: "url('/img/string.png')"; }
            @import "css/theme.css";
            @import url('/css/print.css');
            .hero { background: URL(/img/bg.png?x=1#frag); }
            .bad { background: url('/img/missing.png' }
            </style>
            """;

        var references = _sut.ExtractReferences(html, "/docs/start", htmlScope: true);
        var paths = references.Select(reference => reference.Path).ToArray();

        Assert.Contains("/about", paths);
        Assert.Contains("/styles/site", paths);
        Assert.Contains("/img/logo.png", paths);
        Assert.Contains("/img/logo-2x.png", paths);
        Assert.Contains("/img/attr.png", paths);
        Assert.Contains("/docs/css/theme.css", paths);
        Assert.Contains("/css/print.css", paths);
        Assert.Contains("/img/bg.png", paths);
        Assert.DoesNotContain("/ignored", paths);
        Assert.DoesNotContain("/docs/Program.cs", paths);
        Assert.DoesNotContain("/wrong.css", paths);
        Assert.DoesNotContain("/img/comment.png", paths);
        Assert.DoesNotContain("/img/string.png", paths);
        Assert.DoesNotContain("/img/missing.png", paths);
        Assert.DoesNotContain(paths, path => path.Contains("%3Csvg", StringComparison.Ordinal));

        var aboutReference = Assert.Single(references, reference => reference.Path == "/about");
        Assert.Equal(ExportReferenceKind.AnchorHref, aboutReference.Kind);
        Assert.Equal("<a href>", aboutReference.Provenance?.DisplaySource);
        Assert.NotNull(aboutReference.Provenance?.Line);

        var styleAttributeReference = Assert.Single(references, reference => reference.Path == "/img/attr.png");
        Assert.Equal("style url() <div style>", styleAttributeReference.Provenance?.DisplaySource);

        var styleBlockReference = Assert.Single(references, reference => reference.Path == "/img/bg.png");
        Assert.Equal("style url() <style>", styleBlockReference.Provenance?.DisplaySource);

        var versionedLogo = Assert.Single(references, reference => reference.RawValue == "/img/logo.png?version=1#frag");
        Assert.Equal("?version=1", versionedLogo.Query);
        Assert.Equal("#frag", versionedLogo.Fragment);
    }

    [Fact]
    public void ExtractReferences_Should_Classify_Link_Roles_For_Static_Validation()
    {
        var html = """
            <link rel="stylesheet" href="/styles/site.css">
            <link rel="icon" href="/favicon.ico">
            <link rel="modulepreload" href="/assets/module.js">
            <link rel=" preload " as=" script " href="/assets/app">
            <link rel="prefetch" href="/assets/app.js">
            <link rel="prefetch" href="/next-page">
            <link rel="preload" as="fetch" href="/api/bootstrap">
            <link rel="canonical" href="/docs/start">
            <link rel="dns-prefetch" href="/dns">
            """;

        var references = _sut.ExtractReferences(html, "/", htmlScope: true);

        Assert.Equal(ExportReferenceRole.StaticAsset, Assert.Single(references, reference => reference.Path == "/styles/site.css").Role);
        Assert.Equal(ExportReferenceRole.StaticAsset, Assert.Single(references, reference => reference.Path == "/favicon.ico").Role);
        Assert.Equal(ExportReferenceRole.StaticAsset, Assert.Single(references, reference => reference.Path == "/assets/module.js").Role);
        var preloadReference = Assert.Single(references, reference => reference.Path == "/assets/app");
        Assert.Equal(ExportReferenceRole.StaticAsset, preloadReference.Role);
        Assert.Equal("rel 'preload', as 'script'", preloadReference.LinkMetadata?.Display);
        Assert.Equal(ExportReferenceRole.StaticAsset, Assert.Single(references, reference => reference.Path == "/assets/app.js").Role);
        Assert.Equal(ExportReferenceRole.PageRoute, Assert.Single(references, reference => reference.Path == "/next-page").Role);
        Assert.Equal(ExportReferenceRole.PageRoute, Assert.Single(references, reference => reference.Path == "/api/bootstrap").Role);
        Assert.Equal(ExportReferenceRole.Metadata, Assert.Single(references, reference => reference.Path == "/docs/start").Role);
        Assert.Equal(ExportReferenceRole.Metadata, Assert.Single(references, reference => reference.Path == "/dns").Role);
    }

    [Fact]
    public void ExtractReferences_Should_Ignore_Hash_Only_Css_References()
    {
        var css = """
            .filter { filter: url(#svg-filter); }
            .clip { clip-path: url(" #clip-path"); }
            .asset { background: url(images/bg.png); }
            """;

        var reference = Assert.Single(_sut.ExtractReferences(css, "/docs/start", htmlScope: false));

        Assert.Equal("/docs/images/bg.png", reference.Path);
    }

    [Fact]
    public void ExtractReferences_Should_Include_Css_Import_Strings_But_Not_Comments_Or_Strings()
    {
        var css = """
            /* url('/comment.png') */
            .literal::before { content: "url('/string.png')"; }
            @import "theme.css";
            @import url('/print.css');
            .hero { background: image-set(url('/hero.png') 1x, URL("/hero@2x.png") 2x); }
            .escaped { background: url('/hero\ 2x.png'); }
            .oversized-escape { background: url('/hero\FFFFFF.png'); }
            .broken { background: url('/broken.png' }
            """;

        var references = _sut.ExtractReferences(css, "/css/site.css", htmlScope: false);
        var paths = references.Select(reference => reference.Path).ToArray();

        Assert.Contains("/css/theme.css", paths);
        Assert.Contains("/print.css", paths);
        Assert.Contains("/hero.png", paths);
        Assert.Contains("/hero@2x.png", paths);
        Assert.Contains(references, reference => reference.RawValue == "/hero 2x.png");
        Assert.Contains(references, reference => reference.Provenance?.DisplaySource == "stylesheet @import string");
        Assert.Contains(references, reference => reference.Provenance?.DisplaySource == "stylesheet url()");
        Assert.DoesNotContain(paths, path => path.Contains('\uFFFD'));
        Assert.DoesNotContain("/comment.png", paths);
        Assert.DoesNotContain("/string.png", paths);
        Assert.DoesNotContain("/broken.png", paths);
        Assert.All(references, reference => Assert.NotNull(reference.Provenance));
    }

    [Fact]
    public async Task RunAsync_CdnMode_Should_Preserve_Hash_Only_References()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new HashOnlyReferenceHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            var html = await File.ReadAllTextAsync(Path.Join(tempDir, "index.html"));
            Assert.Contains("href=\"#intro\"", html, StringComparison.Ordinal);
            Assert.Contains("url(#svg-filter)", html, StringComparison.Ordinal);
            Assert.Contains("style=\"clip-path:url('#clip-path')\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/index.html#intro", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/index.html#svg-filter", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/index.html#clip-path", html, StringComparison.Ordinal);
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
    public async Task RunAsync_Should_Record_Duplicate_Reference_Provenance_Without_Duplicate_Fetches()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var handler = new DuplicateReferenceHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.Equal(2, context.References.Count(r => r.Path == "/about"));
            Assert.Equal(1, handler.AboutRequestCount);
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
    public async Task RunAsync_Should_Stream_Binary_Assets_Without_Text_Body()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var client = new HttpClient(new TestHttpMessageHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, null, "http://localhost:5000");
            await _sut.RunAsync(context);

            Assert.True(context.RouteOutcomes.TryGetValue("/image.png", out var imageOutcome));
            Assert.True(imageOutcome.Succeeded);
            Assert.Null(imageOutcome.TextBody);
            Assert.True(File.Exists(Path.Join(tempDir, "image.png")));
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
        var htmlPath = Path.Join("dist", "docs", "topic.html");

        var partialPath = ExportEngine.MapHtmlFilePathToPartialPath(htmlPath);

        Assert.EndsWith(Path.Join("docs", "topic.partial.html"), partialPath);
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
    public void IsDocsExportPage_ShouldDetectCustomRootAppSurfaceDocsClientConfig()
    {
        var html = """
            <html>
              <head>
                <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/foo/bar/next"};</script>
              </head>
            </html>
            """;

        Assert.True(ExportEngine.IsDocsExportPage("/foo/bar/next/search", html));
    }

    [Fact]
    public void StripAppSurfaceDocsDiagnosticsChrome_ShouldRemoveMarkedDiagnosticsDisclosure()
    {
        var html = """
            <html>
              <body>
                <nav>
                  <details data-docs-diagnostics-chrome="true">
                    <summary>Diagnostics</summary>
                    <a href="/docs/_health">Harvest health</a>
                    <a href="/docs/%5Froutes">Encoded routes</a>
                  </details>
                  <a href="/docs/start">Start</a>
                  <details data-docs-diagnostics-chrome="true">
                    <summary>More diagnostics</summary>
                    <a href="/docs/_routes.json">Routes JSON</a>
                  </details>
                  <a href="/docs/next">Next</a>
                </nav>
              </body>
            </html>
            """;

        var stripped = ExportEngine.StripAppSurfaceDocsDiagnosticsChrome(html);
        var decoded = Uri.UnescapeDataString(stripped);

        Assert.DoesNotContain("Diagnostics", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_health", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("_routes", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/docs/start\"", stripped);
        Assert.Contains("href=\"/docs/next\"", stripped);
    }

    [Fact]
    public async Task RunAsync_Should_Export_Docs_Partial_Fragments()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Join(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["/docs/start", "/docs"]);

        try
        {
            var handler = new DocsPartialHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            var fullPagePath = Path.Join(tempDir, "docs", "start.html");
            var partialPath = Path.Join(tempDir, "docs", "start.partial.html");
            var docsLandingPath = Path.Join(tempDir, "docs.html");
            var docsLandingPartialPath = Path.Join(tempDir, "docs.partial.html");

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

            var nextPartialPath = Path.Join(tempDir, "docs", "next.partial.html");
            Assert.True(
                File.Exists(nextPartialPath),
                "Expected docs partial export for crawl-discovered /docs/next.");

            var nextPartialHtml = await File.ReadAllTextAsync(nextPartialPath);
            Assert.Contains("<turbo-frame id=\"doc-content\">", nextPartialHtml);
            Assert.Contains("<article>Next doc</article>", nextPartialHtml);
            Assert.DoesNotContain(handler.RequestPaths, path => path.Contains("_health", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.RequestPaths, path => path.Contains("_routes", StringComparison.OrdinalIgnoreCase));

            foreach (var artifactPath in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
                         .Where(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                        || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
            {
                var artifact = Uri.UnescapeDataString(await File.ReadAllTextAsync(artifactPath));
                Assert.DoesNotContain("Diagnostics", artifact, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("_health", artifact, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("_routes", artifact, StringComparison.OrdinalIgnoreCase);
            }
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
        var tempDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        var seedFile = Path.Join(tempDir, "seeds.txt");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllLinesAsync(seedFile, ["/foo/bar/next"]);

        try
        {
            using var client = new HttpClient(new CustomRootDocsPartialHandler()) { BaseAddress = new Uri("http://localhost:5000") };
            A.CallTo(() => _httpClientFactory.CreateClient("ExportEngine")).Returns(client);

            var context = new ExportContext(tempDir, seedFile, "http://localhost:5000");
            await _sut.RunAsync(context);

            var fullPagePath = Path.Join(tempDir, "foo", "bar", "next.html");
            var partialPath = Path.Join(tempDir, "foo", "bar", "next.partial.html");

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


    private sealed class CdnRewriteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                var html = """
                    <html>
                      <head>
                        <link rel="stylesheet" href="/css/site.css">
                        <style data-copy=".hero { background: url('img/inline.png'); }">.hero { background: url('img/inline.png'); }</style>
                      </head>
                      <body data-copy="background: url('img/attr.png')" style="background: url('img/attr.png')">
                        <a data-copy="/about" href="/about">About</a>
                        <a href="/docs/start#intro">Docs</a>
                        <turbo-frame id="doc-content" src="/docs/start"></turbo-frame>
                        <script src="/_content/pkg/app.js?v=abc123"></script>
                        <picture><source data-copy="img/hero.avif 1x, img/hero.webp 2x" srcset="img/hero.avif 1x, img/hero.webp 2x" type="image/avif"></picture>
                        <img src="/img/logo.png" srcset="/img/logo-2x.png 2x, /img/logo-small.png 300w">
                        <img srcset="img/a.png 1x, img/a.png?version=1 2x">
                      </body>
                    </html>
                    """;
                return Html(html);
            }

            if (path == "/about")
            {
                return Html("<html><body><h1>About</h1></body></html>");
            }

            if (path == "/docs/start")
            {
                return Html("""
                    <html>
                      <body>
                        <turbo-frame id="doc-content"><article id="intro">Start doc</article></turbo-frame>
                      </body>
                    </html>
                    """);
            }

            if (path == "/css/site.css")
            {
                return Text(".masthead { background-image: url('../img/bg.png?v=1'); }", "text/css");
            }

            if (path is "/_content/pkg/app.js")
            {
                return Text("console.log('cdn');", "text/javascript");
            }

            if (path.StartsWith("/img/", StringComparison.Ordinal))
            {
                return Bytes("image/png");
            }

            return NotFound();
        }
    }

    private sealed class ParserBackedRewriteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/")
            {
                var html = """
<!DOCTYPE html>
<HTML data-kind=raw>
<HEAD>
<!-- keep me -->
<LINK REL='stylesheet preload' HREF=/styles/app data-copy=1>
<LINK REL='notstylesheet' HREF=/wrong.css>
<style>
/* url('/img/comment.png') */
.literal::before { content: "url('/img/string.png')"; }
@import "about";
.hero { background:URL(/about); }
.broken { background: url('/img/broken.png' }
</style>
</HEAD>
<BODY>
<A HREF=/about data-extra='keep'>About</A>
<a DATA-RW-EXPORT-IGNORE href=/ignored>Ignored</a>
<a href=./Program.cs>Program</a>
<!-- <a href=/about>Comment fake link</a> -->
<!-- <style>.comment-fake { background:url(/about); }</style> -->
<script SRC=/app>const fake = "<img src=/about>"; const close = "</script-not-real><img src=/about>"; const fakeStyle = "<style>.script-fake { background:url(/about); }</style>";</script>
<img srcset="data:image/svg+xml,%3Csvg%3E,%3C/svg%3E 1x, /img/logo-2x.png?v=1 2x">
</BODY>
</HTML>
""";
                return Html(html);
            }

            if (path == "/about")
            {
                return Html("<html><body><h1>About</h1></body></html>");
            }

            if (path == "/styles/app")
            {
                return Text(".sheet { background: url('/about'); }", "text/css");
            }

            if (path == "/app")
            {
                return Text("console.log('parser-backed');", "text/javascript");
            }

            if (path.StartsWith("/img/", StringComparison.Ordinal))
            {
                return Bytes("image/png");
            }

            return NotFound();
        }
    }

    private sealed class MissingParserDiscoveredAssetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/"
                ? Html("<html><body><img src=/img/missing.png></body></html>")
                : NotFound();
        }
    }

    private sealed class FaviconLinkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";

            if (path == "/")
            {
                return Html("""
                    <html>
                      <head>
                        <link rel="icon" type="image/svg+xml" href="/branding/appsurface-site-icon.svg">
                      </head>
                      <body><h1>Docs</h1></body>
                    </html>
                    """);
            }

            if (path == "/branding/appsurface-site-icon.svg")
            {
                return Text(
                    """
                    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
                      <title>Exported AppSurface icon</title>
                      <rect width="16" height="16" fill="#123456" />
                    </svg>
                    """,
                    "image/svg+xml");
            }

            return NotFound();
        }
    }

    private sealed class DottedPageRouteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <a href="/docs/web/forgetrust.razorwire">RazorWire package</a>
                      </body>
                    </html>
                    """);
            }

            if (path == "/docs/web/forgetrust.razorwire")
            {
                return Html("""
                    <html>
                      <body>
                        <a href="/docs/web/forgetrust.razorwire/docs">API docs</a>
                      </body>
                    </html>
                    """);
            }

            if (path == "/docs/web/forgetrust.razorwire/docs")
            {
                return Html("<html><body><h1>API docs</h1></body></html>");
            }

            return Html(string.Empty, HttpStatusCode.NotFound);
        }
    }

    private sealed class PathBaseSeedHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            RequestPaths.Add(path);

            return path == "/app/docs"
                ? Html("<html><body><h1>Path base docs</h1></body></html>")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DocsSeedHandler : TestHttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            RequestPaths.Add(path);

            return path == "/docs"
                ? Html("<html><body><h1>Docs</h1></body></html>")
                : base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class MissingFrameHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/" || path == "/index"
                ? Html("""<html><body><turbo-frame src="/missing-frame"></turbo-frame></body></html>""")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class HybridLiveOriginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <!doctype html>
                    <html>
                      <body>
                        <script src="/_content/ForgeTrust.RazorWire/razorwire/razorwire.js"></script>
                        <rw-stream-source src="/rw/stream?channel=profile"></rw-stream-source>
                        <turbo-frame id="profile" data-rw-island="true" src="/islands/profile"></turbo-frame>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                          <button>Save</button>
                        </form>
                      </body>
                    </html>
                    """);
            }

            if (path == "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
            {
                return Text("window.RazorWire = window.RazorWire || {};", "text/javascript");
            }

            return NotFound();
        }
    }

    private sealed class HybridUnsafeTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="https://payments.example.test/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                        <form method="post" action="/newsletter">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """);
            }

            if (path == "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
            {
                return Text("window.RazorWire = window.RazorWire || {};", "text/javascript");
            }

            return NotFound();
        }
    }

    private sealed class HybridPathBaseLiveOriginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/app" || path == "/app/")
            {
                return Html("""
                    <html>
                      <body>
                        <script src="/app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js" data-rw-antiforgery-endpoint="/app/_rw/antiforgery/token"></script>
                        <form data-rw-form="true" method="post" action="http://localhost:5000/app/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """);
            }

            if (path is "/app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js"
                or "/app/app/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
            {
                return Text("window.RazorWire = window.RazorWire || {};", "text/javascript");
            }

            return NotFound();
        }
    }

    private sealed class HybridPathBaseRootRelativeLiveOriginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/app" || path == "/app/")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="/app/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class HybridOutsidePathBaseLiveOriginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/app" || path == "/app/")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="http://localhost:5000/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class HybridRootRelativeOutsidePathBaseLiveOriginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/app" || path == "/app/")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class StaticTokenFormHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                          <button>Save</button>
                        </form>
                      </body>
                    </html>
                    """);
            }

            if (path == "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
            {
                return Text("window.RazorWire = window.RazorWire || {};", "text/javascript");
            }

            return NotFound();
        }
    }

    private sealed class FormAssociatedStaticTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form id="profile" data-rw-form="true" method="post" action="/profile/save">
                          <button>Save</button>
                        </form>
                        <input type="hidden" form="profile" name="__RequestVerificationToken" value="crawler-token">
                      </body>
                    </html>
                    """);
            }

            if (path == "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
            {
                return Text("window.RazorWire = window.RazorWire || {};", "text/javascript");
            }

            return NotFound();
        }
    }

    private sealed class DuplicateStaticTokenFormHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form id="profile" data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token-a">
                          <button>Save</button>
                        </form>
                        <input type="hidden" form="profile" name="__RequestVerificationToken" value="crawler-token-b">
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class UnownedStaticTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <input type="hidden" form="missing" name="__RequestVerificationToken" value="crawler-token">
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class LazyAntiforgeryFormHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" data-rw-antiforgery="lazy" method="post" action="/profile/save">
                          <button>Save</button>
                        </form>
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class CustomNamedTokenFormHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="csrf-token" value="crawler-token">
                          <button>Save</button>
                        </form>
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class XsrfNamedTokenFormHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="xsrf-token" value="crawler-token">
                          <button>Save</button>
                        </form>
                      </body>
                    </html>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class CustomEndpointHybridHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <script src="/_content/ForgeTrust.RazorWire/razorwire/razorwire.js" data-rw-antiforgery-endpoint="/tokens/antiforgery"></script>
                        <form data-rw-form="true" method="post" action="/profile/save">
                          <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                        </form>
                      </body>
                    </html>
                    """);
            }

            if (path == "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
            {
                return Text("window.RazorWire = window.RazorWire || {};", "text/javascript");
            }

            return NotFound();
        }
    }

    private sealed class HybridLiveFragmentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <turbo-frame src="/frame/content"></turbo-frame>
                      </body>
                    </html>
                    """);
            }

            if (path == "/frame/content")
            {
                return Html("""
                    <turbo-frame id="content">
                      <form data-rw-form="true" method="post" action="/frame/save">
                        <input type="hidden" name="__RequestVerificationToken" value="crawler-token">
                      </form>
                    </turbo-frame>
                    """);
            }

            return NotFound();
        }
    }

    private sealed class QueryFrameHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""<html><body><turbo-frame src="/frame/content?id=1"></turbo-frame></body></html>""");
            }

            return path == "/frame/content"
                ? Html("""<turbo-frame id="content"><p>Frame</p></turbo-frame>""")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class MissingAssetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/" || path == "/index"
                ? Html("""<html><body><script src="/missing.js"></script></body></html>""")
                : NotFound();
        }
    }

    private sealed class MissingCssImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path switch
            {
                "/" or "/index" => Html("""<html><head><link rel="stylesheet" href="/styles/site.css"></head><body></body></html>"""),
                "/styles/site.css" => Text(".map { background-image: url('/img/map-image.png'); }", "text/css"),
                _ => NotFound()
            };
        }
    }

    private sealed class MissingModulePreloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/" || path == "/index"
                ? Html("""<html><head><link rel="modulepreload" href="/assets/app.js"></head><body></body></html>""")
                : NotFound();
        }
    }

    private sealed class HybridToleratedReferenceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/" || path == "/index"
                ? Html("""
                    <html>
                      <head>
                        <link rel="canonical" href="/missing-canonical">
                        <link rel="dns-prefetch" href="/dns">
                        <link rel="prefetch" href="/next-page">
                        <link rel="preload" as="fetch" href="/api/bootstrap">
                      </head>
                      <body>
                        <a href="/missing-page">Missing page</a>
                        <turbo-frame src="/missing-frame"></turbo-frame>
                      </body>
                    </html>
                    """)
                : NotFound();
        }
    }

    private sealed class MissingAnchorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/" || path == "/index"
                ? Html("""<html><body><a href="/missing-page">Missing page</a></body></html>""")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SourceNavigationAnchorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path == "/" || path == "/index"
                ? Html("""<html><body><a href="./Program.cs">Program</a></body></html>""")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ExportIgnoreAnchorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                      <body>
                        <a href="./source.txt" data-rw-export-ignore="true">Source</a>
                        <a href="/about">About</a>
                      </body>
                    </html>
                    """);
            }

            return path == "/about"
                ? Html("<html><body><h1>About</h1></body></html>")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DuplicateReferenceHandler : HttpMessageHandler
    {
        public int AboutRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""<html><body><a href="/about">About</a><a href="/about">About again</a></body></html>""");
            }

            if (path == "/about")
            {
                AboutRequestCount++;
                return Html("<html><body><h1>About</h1></body></html>");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class HashOnlyReferenceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""
                    <html>
                    <head>
                    <style>.filter{filter:url(#svg-filter)}</style>
                    </head>
                    <body>
                    <a href="#intro">Intro</a>
                    <div style="clip-path:url('#clip-path')"></div>
                    </body>
                    </html>
                    """);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static Task<HttpResponseMessage> Html(string html, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return Text(html, "text/html", statusCode);
    }

    private static Task<HttpResponseMessage> Text(
        string content,
        string mediaType,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        });
    }

    private static Task<HttpResponseMessage> NotFound()
    {
        return Text(string.Empty, "text/plain", HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> Bytes(string mediaType)
    {
        var content = new ByteArrayContent([0x01, 0x02, 0x03]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
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
                var html = @"<html><body><script src=""/_content/ForgeTrust.RazorWire/razorwire/razorwire.js?v=abc123""></script></body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }

            if (path == "/_content/ForgeTrust.RazorWire/razorwire/razorwire.js")
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
        private const string PackagedStylesheetPath = "/_content/ForgeTrust.AppSurface.Docs/css/site.gen.css";

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
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri?.AbsolutePath ?? "/");
            RequestPaths.Add(path);
            if (path == "/docs")
            {
                var html = """
                    <html>
                      <body>
                        <main>Docs landing page</main>
                        <details data-docs-diagnostics-chrome="true">
                          <summary>Diagnostics</summary>
                          <a href="/docs/_health">Harvest health</a>
                          <a href="/docs/_health.json">Health JSON</a>
                          <a href="/docs/_routes">Route inspector</a>
                          <a href="/docs/_routes.json">Routes JSON</a>
                        </details>
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
                        <details data-docs-diagnostics-chrome="true">
                          <summary>Diagnostics</summary>
                          <a href="/docs/_health">Harvest health</a>
                          <a href="/docs/%5Froutes">Encoded routes</a>
                        </details>
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
                        <script>window.__appSurfaceDocsConfig = {"docsRootPath":"/foo/bar/next","docsSearchUrl":"/foo/bar/next/search","docsSearchIndexUrl":"/foo/bar/next/search-index.json"};</script>
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
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri?.AbsolutePath ?? "/");
            RequestPaths.Add(path);
            if (path == BrowserStatusPageDefaults.ReservedNotFoundRoute)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        <html>
                          <body>
                            <h1>Exported 404 page</h1>
                            <details data-docs-diagnostics-chrome="true">
                              <summary>Diagnostics</summary>
                              <a href="/docs/_health">Harvest health</a>
                              <a href="/docs/%5Froutes">Encoded routes</a>
                            </details>
                            <a href="/about">About</a>
                            <a href="/docs/search">Search documentation</a>
                            <a href="/docs/sections/start-here" data-rw-export-ignore="true">Start Here</a>
                            <a href="/docs/sections/packages" data-rw-export-ignore="true">Packages</a>
                            <img src="/img/error.png">
                          </body>
                        </html>
                        """,
                        Encoding.UTF8,
                        "text/html")
                });
            }

            if (path == "/about")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><h1>About</h1></body></html>", Encoding.UTF8, "text/html")
                });
            }

            if (path == "/docs/search")
            {
                return Html("<html><body><h1>Search</h1></body></html>");
            }

            if (path == "/img/error.png")
            {
                return Bytes("image/png");
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

    private sealed class DocsRedirectArtifactHandler : HttpMessageHandler
    {
        private readonly bool _aliasReturnsBody;

        public DocsRedirectArtifactHandler(bool aliasReturnsBody = false)
        {
            _aliasReturnsBody = aliasReturnsBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""<html><body><a href="/docs/example">Example</a></body></html>""");
            }

            if (path == "/docs/example")
            {
                return Html("""
                    <html>
                    <head>
                      <link rel="canonical" href="/docs/example">
                    </head>
                    <body>
                      <h1>Example</h1>
                      <turbo-frame id="doc-content"><article>Example content</article></turbo-frame>
                    </body>
                    </html>
                    """);
            }

            if (path == "/docs/other")
            {
                return Html("<html><body><h1>Other</h1></body></html>");
            }

            if (path == "/docs/example.txt")
            {
                return Text("Example text", "text/plain");
            }

            if (_aliasReturnsBody && path == "/docs/example/README.md.html")
            {
                return Html("<html><body><h1>Stale alias body</h1></body></html>");
            }

            return Text(string.Empty, "text/plain", HttpStatusCode.NotFound);
        }
    }

    private sealed class RedirectsFileCollisionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index")
            {
                return Html("""<html><body><a href="/docs/example">Example</a></body></html>""");
            }

            if (path == "/docs/example")
            {
                return Html("<html><body><h1>Example</h1></body></html>");
            }

            if (path == "/_redirects")
            {
                return Text("/old /new 301!", "text/plain");
            }

            return Text(string.Empty, "text/plain", HttpStatusCode.NotFound);
        }
    }

    private sealed class MissingConventionalNotFoundAssetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == BrowserStatusPageDefaults.ReservedNotFoundRoute)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<html><body><h1>Exported 404 page</h1><script src="/missing-404.js"></script></body></html>""",
                        Encoding.UTF8,
                        "text/html")
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

    private sealed class IgnoredRootRecoveryNotFoundPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == BrowserStatusPageDefaults.ReservedNotFoundRoute)
            {
                return Html("""
                    <html>
                      <body>
                        <h1>Exported 404 page</h1>
                        <a href="/" data-rw-export-ignore="true">Return home</a>
                      </body>
                    </html>
                    """);
            }

            if (path == "/")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = new Uri("/docs", UriKind.Relative)
                    }
                });
            }

            if (path == "/docs")
            {
                return Html("<html><body><h1>Docs</h1></body></html>");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NonHtmlNotFoundPageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            if (path == BrowserStatusPageDefaults.ReservedNotFoundRoute)
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><h1>Home</h1></body></html>", Encoding.UTF8, "text/html")
            });
        }
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
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
