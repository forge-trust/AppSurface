using AngleSharp;
using AngleSharp.Html.Parser;
using FakeItEasy;
using ForgeTrust.Runnable.Caching;
using ForgeTrust.Runnable.Core;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Ganss.Xss;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

public class DocAggregatorTests : IDisposable
{
    private readonly IDocHarvester _harvesterFake;
    private readonly RazorDocsOptions _options;
    private readonly IWebHostEnvironment _envFake;
    private readonly ILogger<DocAggregator> _loggerFake;
    private readonly IRazorDocsHtmlSanitizer _sanitizerFake;
    private readonly IMemoryCache _cache;
    private readonly IMemo _memo;
    private readonly DocAggregator _aggregator;

    public DocAggregatorTests()
    {
        _harvesterFake = A.Fake<IDocHarvester>();
        _options = new RazorDocsOptions();
        _envFake = A.Fake<IWebHostEnvironment>();
        _loggerFake = A.Fake<ILogger<DocAggregator>>();
        _sanitizerFake = A.Fake<IRazorDocsHtmlSanitizer>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _memo = new Memo(_cache);

        A.CallTo(() => _envFake.ContentRootPath).Returns(Path.GetTempPath());

        // Default: just return input for sanitization in most tests
        A.CallTo(() => _sanitizerFake.Sanitize(A<string>._))
            .ReturnsLazily((string input) => input);

        _aggregator = new DocAggregator(
            new[] { _harvesterFake },
            _options,
            _envFake,
            _memo,
            _sanitizerFake,
            _loggerFake
        );
    }

    [Fact]
    public async Task GetDocsAsync_ShouldReturnCachedResults_WhenCacheExists()
    {
        // Arrange
        var cachedDocs = new List<DocNode> { new("Cached", "path", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(cachedDocs);

        _ = await _aggregator.GetDocsAsync();

        // Act
        var result = await _aggregator.GetDocsAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("Cached", result.First().Title);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetDocsAsync_ShouldHarvestAndCache_WhenCacheMiss()
    {
        // Arrange
        var harvestedDocs = new List<DocNode> { new DocNode("Fresh", "path", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var firstResult = await _aggregator.GetDocsAsync();
        var secondResult = await _aggregator.GetDocsAsync();

        // Assert
        Assert.Single(firstResult);
        Assert.Single(secondResult);
        Assert.Equal("Fresh", firstResult.First().Title);
        Assert.Equal("Fresh", secondResult.First().Title);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetDocsAsync_ShouldExpireSnapshotUsingConfiguredCacheExpiration()
    {
        var harvester = A.Fake<IDocHarvester>();
        var harvestCount = 0;
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var currentHarvest = Interlocked.Increment(ref harvestCount);
                return new[] { new DocNode($"Harvest {currentHarvest}", "path", "<p>content</p>") };
            });

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var aggregator = new DocAggregator(
            [harvester],
            new RazorDocsOptions
            {
                CacheExpirationMinutes = RazorDocsOptions.MinCacheExpirationMinutes,
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = Path.GetTempPath()
                }
            },
            _envFake,
            memo,
            _sanitizerFake,
            _loggerFake);

        var first = await aggregator.GetDocsAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var second = await aggregator.GetDocsAsync();

        Assert.Equal("Harvest 1", Assert.Single(first).Title);
        Assert.Equal("Harvest 2", Assert.Single(second).Title);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task GetDocsAsync_ShouldSanitizeContent_WhenHarvested()
    {
        // Arrange
        var unsafeHtml = "<script>alert('xss')</script><p>Safe</p>";
        var safeHtml = "<p>Safe</p>";
        var harvestedDocs = new List<DocNode> { new DocNode("Title", "path", unsafeHtml) };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);
        A.CallTo(() => _sanitizerFake.Sanitize(unsafeHtml))
            .Returns(safeHtml);

        // Act
        var result = await _aggregator.GetDocsAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal(safeHtml, result.First().Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldReplaceSymbolSourcePlaceholders_BeforeSanitizing()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """<h2>Calculator</h2><span data-razordocs-symbol-source="Test-Calculator"></span>""",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Test-Calculator",
                            SourcePath = "src/Calculator.cs",
                            StartLine = 12
                        }
                    ])
            ]);

        A.CallTo(() => _sanitizerFake.Sanitize(A<string>._))
            .ReturnsLazily(
                (string input) =>
                {
                    Assert.Contains("href=\"https://example.com/blob/abc123/src/Calculator.cs#L12\"", input);
                    Assert.Contains("doc-symbol-source-link", input);
                    Assert.Contains("aria-label=\"View source\"", input);
                    Assert.DoesNotContain("data-razordocs-symbol-source", input);
                    return input;
                });

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceRef = "abc123",
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.Contains("href=\"https://example.com/blob/abc123/src/Calculator.cs#L12\"", result.Content);
        Assert.Contains("aria-label=\"View source\"", result.Content);
        Assert.Contains(">Source</a>", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldUseDefaultBranch_WhenSymbolSourceRefIsMissing()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """<h2>Calculator</h2><span data-razordocs-symbol-source="Test-Calculator"></span>""",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Test-Calculator",
                            SourcePath = "src/Calculator.cs",
                            StartLine = 12
                        }
                    ])
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.Contains("href=\"https://example.com/blob/main/src/Calculator.cs#L12\"", result.Content);
        Assert.Contains("aria-label=\"View source\"", result.Content);
        Assert.Contains(">Source</a>", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldRemoveSymbolSourcePlaceholders_WhenHrefIsUnsafeOrAnchorIsAmbiguous()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """
                    <span data-razordocs-symbol-source="Unsafe"></span>
                    <span data-razordocs-symbol-source="Duplicate"></span>
                    <span data-razordocs-symbol-source="Duplicate"></span>
                    <span data-razordocs-symbol-source="Missing"></span>
                    """,
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Unsafe",
                            SourcePath = "src/Unsafe.cs",
                            StartLine = 5
                        },
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Duplicate",
                            SourcePath = "src/Duplicate.cs",
                            StartLine = 7
                        }
                    ])
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                SymbolSourceUrlTemplate = "javascript:{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.DoesNotContain("doc-symbol-source-link", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldIgnoreInvalidDuplicateAndOrphanedSymbolSourceProvenance()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """<span data-razordocs-symbol-source="Duplicate"></span>""",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "   ",
                            SourcePath = "src/Blank.cs",
                            StartLine = 3
                        },
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Duplicate",
                            SourcePath = "src/Duplicate.cs",
                            StartLine = 7
                        },
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Duplicate",
                            SourcePath = "src/DuplicateReplacement.cs",
                            StartLine = 9
                        },
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Orphan",
                            SourcePath = "src/Orphan.cs",
                            StartLine = 11
                        }
                    ])
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                SourceRef = "abc123",
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.DoesNotContain("doc-symbol-source-link", result.Content);
        Assert.DoesNotContain("src/Duplicate.cs", result.Content);
        Assert.DoesNotContain("DuplicateReplacement", result.Content);
        Assert.DoesNotContain("Orphan", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldRemoveSymbolSourcePlaceholders_WhenRequiredBranchIsMissing()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """<span data-razordocs-symbol-source="Calculator"></span>""",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Calculator",
                            SourcePath = "src/Calculator.cs",
                            StartLine = 12
                        }
                    ])
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                SourceRef = "abc123",
                SymbolSourceUrlTemplate = "https://example.com/blob/{branch}/{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.DoesNotContain("doc-symbol-source-link", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldRemoveSymbolSourcePlaceholders_WhenRequiredRefIsMissing()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """<span data-razordocs-symbol-source="Calculator"></span>""",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Calculator",
                            SourcePath = "src/Calculator.cs",
                            StartLine = 12
                        }
                    ])
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.DoesNotContain("doc-symbol-source-link", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldSuppressSymbolSourceLinks_WhenContributorRenderingIsDisabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    "<span data-razordocs-symbol-source=\"Calculator\"></span>",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Calculator",
                            SourcePath = "src/Calculator.cs",
                            StartLine = 12
                        }
                    ])
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = false,
                SourceRef = "abc123",
                SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
            },
            null);

        var result = Assert.Single((await aggregator.GetDocsAsync()).ToList());

        Assert.DoesNotContain("doc-symbol-source-link", result.Content);
        Assert.DoesNotContain("data-razordocs-symbol-source", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldPreserveMetadata_WhenSanitizingHarvestedNodes()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Title",
                "path",
                "<p>content</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Summary",
                    PageType = "guide",
                    HideFromSearch = true
                })
        };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var result = Assert.Single((await _aggregator.GetDocsAsync()).ToList());

        Assert.Equal("Summary", result.Metadata?.Summary);
        Assert.Equal("guide", result.Metadata?.PageType);
        Assert.True(result.Metadata?.HideFromSearch);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldRewriteInternalDocLinks_AfterSanitizing()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Releases",
                "releases/README.md",
                "<p><a href=\"./unreleased.md\">Unreleased</a> <a href=\"https://example.com/releases\">External</a></p>"),
            new("Unreleased", "releases/unreleased.md", "<p>Draft</p>")
        };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var result = (await _aggregator.GetDocsAsync()).Single(doc => doc.Path == "releases/README.md");

        Assert.Contains("href=\"/docs/releases/unreleased.md.html\"", result.Content);
        Assert.Contains("data-turbo-frame=\"doc-content\"", result.Content);
        Assert.Contains("data-turbo-action=\"advance\"", result.Content);
        Assert.Contains("href=\"https://example.com/releases\"", result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldRewriteTitledMarkdownLinks_WithQuotedGreaterThanInTitle()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Guide",
                "README.md",
                "<p><a href=\"guide.md\" title=\"1 > 0\">guide</a></p>"),
            new("Guide", "guide.md", "<p>Guide body</p>")
        };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var result = (await _aggregator.GetDocsAsync()).Single(doc => doc.Path == "README.md");
        var document = new HtmlParser().ParseDocument(result.Content);
        var anchor = Assert.Single(document.QuerySelectorAll("a"));

        Assert.Equal("/docs/guide.md.html", anchor.GetAttribute("href"));
        Assert.Equal("1 > 0", anchor.GetAttribute("title"));
        Assert.Equal("guide", anchor.TextContent);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldNotRewriteSourceLikeLinks_WhenTargetWasNotHarvested()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Releases",
                "releases/README.md",
                "<p><a href=\"./missing.md\">Missing</a> <a href=\"./unreleased.md\">Unreleased</a></p>"),
            new("Unreleased", "releases/unreleased.md", "<p>Draft</p>")
        };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var result = (await _aggregator.GetDocsAsync()).Single(doc => doc.Path == "releases/README.md");

        Assert.Contains("href=\"./missing.md\"", result.Content);
        Assert.Contains("href=\"/docs/releases/unreleased.md.html\"", result.Content);
        Assert.DoesNotContain("href=\"/docs/releases/missing.md.html\"", result.Content);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetDocsAsync_ShouldRewritePackageChooserTableLinks_FromRealRepoDocs()
    {
        var repositoryRoot = ForgeTrust.Runnable.Core.PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var markdownLogger = A.Fake<ILogger<MarkdownHarvester>>();
        var csharpLogger = A.Fake<ILogger<CSharpDocHarvester>>();
        var localEnv = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => localEnv.ContentRootPath).Returns(repositoryRoot);

        var aggregator = new DocAggregator(
            [
                new MarkdownHarvester(markdownLogger),
                new CSharpDocHarvester(csharpLogger)
            ],
            new RazorDocsOptions
            {
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = repositoryRoot
                }
            },
            localEnv,
            _memo,
            new RazorDocsHtmlSanitizer(),
            _loggerFake);

        var chooser = await aggregator.GetDocByPathAsync("packages/README.md");

        Assert.NotNull(chooser);
        Assert.Contains(
            "href=\"/docs/Web/ForgeTrust.Runnable.Web.OpenApi/README.md.html\"",
            chooser!.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldFallbackToEmptyContent_WhenSanitizerReturnsNull()
    {
        var harvestedDocs = new List<DocNode>
        {
            new("Title", "path", "<p>content</p>")
        };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);
        A.CallTo(() => _sanitizerFake.Sanitize("<p>content</p>"))
            .ReturnsLazily(static (string _) => (string)null!);

        var result = Assert.Single((await _aggregator.GetDocsAsync()).ToList());

        Assert.Equal(string.Empty, result.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldHandleDuplicatePaths_ByKeepingFirst()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new DocNode("First", "duplicate-path", "content1"),
            new DocNode("Second", "duplicate-path", "content2")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var result = await _aggregator.GetDocsAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("First", result.First().Title);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldHandleHarvesterExceptions_ByLoggingAndSkipping()
    {
        // Arrange
        var failingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => failingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new Exception("Harvester boom"));

        var workingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => workingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(new List<DocNode> { new DocNode("Success", "path", "content") });

        var aggregator = new DocAggregator(
            new[] { failingHarvester, workingHarvester },
            _options,
            _envFake,
            _memo,
            _sanitizerFake,
            _loggerFake
        );

        // Act
        var result = await aggregator.GetDocsAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("Success", result.First().Title);
    }

    [Fact]
    public async Task GetDocByPathAsync_WhenDocNotFound_ReturnsNull()
    {
        // Arrange
        var aggregator = new DocAggregator(
            Enumerable.Empty<IDocHarvester>(),
            new RazorDocsOptions(),
            _envFake,
            _memo,
            _sanitizerFake,
            _loggerFake);

        // Act
        var result = await aggregator.GetDocByPathAsync("non-existent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDocByPathAsync_WhenPathIsNull_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _aggregator.GetDocByPathAsync(null!));
    }

    [Fact]
    public async Task GetDocsAsync_ShouldExposeCanonicalHtmlPaths_AndResolveByCanonicalPath()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new("Guide", "docs/readme.md", "content"),
            new("Method", "docs/service.cs#MethodId", "content", "docs/service.cs")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var docs = await _aggregator.GetDocsAsync();
        var byCanonical = await _aggregator.GetDocByPathAsync("docs/readme.md.html");
        var byCanonicalWithoutAnchor = await _aggregator.GetDocByPathAsync("docs/service.cs.html");

        // Assert
        Assert.Contains(docs, d => d.Path == "docs/readme.md" && d.CanonicalPath == "docs/readme.md.html");
        Assert.Contains(
            docs,
            d => d.Path == "docs/service.cs#MethodId" && d.CanonicalPath == "docs/service.cs.html#MethodId");
        Assert.NotNull(byCanonical);
        Assert.Equal("Guide", byCanonical!.Title);
        Assert.NotNull(byCanonicalWithoutAnchor);
        Assert.Equal("Method", byCanonicalWithoutAnchor!.Title);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldUseConfiguredRepositoryRoot_WhenProvided()
    {
        // Arrange
        var configuredRoot = Path.Combine(Path.GetTempPath(), "repo-root");
        var options = new RazorDocsOptions
        {
            Source = new RazorDocsSourceOptions { RepositoryRoot = $"  {configuredRoot}  " }
        };
        string? capturedRoot = null;
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string root, CancellationToken _) => capturedRoot = root)
            .Returns(Array.Empty<DocNode>());
        var aggregator = new DocAggregator(
            new[] { _harvesterFake },
            options,
            _envFake,
            _memo,
            _sanitizerFake,
            _loggerFake);

        // Act
        _ = await aggregator.GetDocsAsync();

        // Assert
        Assert.Equal(configuredRoot, capturedRoot);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldMapEmptyAndWhitespacePaths_ToIndexCanonicalPath()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new("Home", " ", "content"),
            new("AnchoredHome", "#overview", "content")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var docs = await _aggregator.GetDocsAsync();

        // Assert
        Assert.Contains(docs, d => d.Path == " " && d.CanonicalPath == "index.html");
        Assert.Contains(docs, d => d.Path == "#overview" && d.CanonicalPath == "index.html#overview");
    }

    [Fact]
    public async Task GetPublicSectionsAsync_ShouldReturnDefensiveCopiesOfCachedSectionSnapshots()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true
                }),
            new(
                "Install",
                "guides/install.md",
                "<p>Install</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var firstSections = await _aggregator.GetPublicSectionsAsync();
        var secondSections = await _aggregator.GetPublicSectionsAsync();

        var first = Assert.Single(firstSections);
        var second = Assert.Single(secondSections);
        Assert.NotSame(firstSections, secondSections);
        Assert.NotSame(first, second);
        Assert.NotSame(first.VisiblePages, second.VisiblePages);
        Assert.Equal(first.VisiblePages.Select(doc => doc.Path), second.VisiblePages.Select(doc => doc.Path));
    }

    [Fact]
    public async Task GetPublicSectionAsync_ShouldReturnDefensiveCopyOfCachedSectionSnapshot()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Quickstart",
                "guides/quickstart.md",
                "<p>Quickstart</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here",
                    SectionLanding = true
                }),
            new(
                "Install",
                "guides/install.md",
                "<p>Install</p>",
                Metadata: new DocMetadata
                {
                    NavGroup = "Start Here"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var first = await _aggregator.GetPublicSectionAsync(DocPublicSection.StartHere);
        var second = await _aggregator.GetPublicSectionAsync(DocPublicSection.StartHere);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.NotSame(first!.VisiblePages, second!.VisiblePages);
        Assert.Equal(first.VisiblePages.Select(doc => doc.Path), second.VisiblePages.Select(doc => doc.Path));
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldMatchCanonicalPath_WithFragmentInLookup()
    {
        // Arrange
        var harvestedDocs = new List<DocNode> { new("Method", "docs/service.cs#MethodId", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var result = await _aggregator.GetDocByPathAsync("docs/service.cs.html#MethodId");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Method", result!.Title);
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldPreferNamespacePage_WhenCanonicalLookupHasNoFragment()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new("FooType", "Namespaces/Foo#Foo-Type", string.Empty),
            new("Foo", "Namespaces/Foo", "<p>Namespace page</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var result = await _aggregator.GetDocByPathAsync("Namespaces/Foo.html");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Namespaces/Foo", result!.Path);
        Assert.Equal("Foo", result.Title);
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldPreferSourceFragment_WhenBasePageAlsoExists()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new("Foo", "Namespaces/Foo", "<p>Namespace page</p>"),
            new("FooType", "Namespaces/Foo#Foo-Type", string.Empty)
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var result = await _aggregator.GetDocByPathAsync("Namespaces/Foo#Foo-Type");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Namespaces/Foo#Foo-Type", result!.Path);
        Assert.Equal("FooType", result.Title);
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldFallbackWhenLookupFragmentMissing_AndDocHasNoFragment()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new("Service", "docs/service.cs", "<p>Service</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var result = await _aggregator.GetDocByPathAsync("docs/service.cs.html#MissingFragment");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Service", result!.Title);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveOutlineSequenceNeighbors_AndRelatedPages()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Intro",
                "guides/intro.md",
                "<p>Intro</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 10,
                    Summary = "Start here."
                }),
            new(
                "Example",
                "guides/example.md",
                "<p>Example</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 20,
                    Summary = "Middle step.",
                    RelatedPages = ["guides/troubleshooting.md"]
                },
                Outline:
                [
                    new DocOutlineItem
                    {
                        Title = "Install",
                        Id = "install",
                        Level = 2
                    }
                ]),
            new(
                "Troubleshooting",
                "guides/troubleshooting.md",
                "<p>Troubleshooting</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 30,
                    Summary = "Recover quickly."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var details = await _aggregator.GetDocDetailsAsync("guides/example.md");

        Assert.NotNull(details);
        Assert.Equal("Example", details!.Document.Title);
        Assert.Equal("Install", Assert.Single(details.Outline).Title);
        Assert.Equal("/docs/guides/intro.md.html", details.PreviousPage?.Href);
        Assert.Equal("/docs/guides/troubleshooting.md.html", details.NextPage?.Href);
        Assert.Empty(details.RelatedPages);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldSuppressSequence_WhenCurrentPageCannotParticipate()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "No Key",
                "guides/no-key.md",
                "<p>No key</p>",
                Metadata: new DocMetadata
                {
                    Order = 20
                }),
            new(
                "No Metadata",
                "guides/no-metadata.md",
                "<p>No metadata</p>"),
            new(
                "No Order",
                "guides/no-order.md",
                "<p>No order</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof"
                }),
            new(
                "Empty",
                "guides/empty.md",
                string.Empty,
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 20
                }),
            new(
                "Anchor",
                "guides/anchor.md#section",
                string.Empty,
                "guides/anchor.md",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 20
                }),
            new(
                "Neighbor",
                "guides/neighbor.md",
                "<p>Neighbor</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 10
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var noKey = await _aggregator.GetDocDetailsAsync("guides/no-key.md");
        var noMetadata = await _aggregator.GetDocDetailsAsync("guides/no-metadata.md");
        var noOrder = await _aggregator.GetDocDetailsAsync("guides/no-order.md");
        var empty = await _aggregator.GetDocDetailsAsync("guides/empty.md");
        var anchor = await _aggregator.GetDocDetailsAsync("guides/anchor.md.html#section");

        Assert.Null(noKey?.PreviousPage);
        Assert.Null(noKey?.NextPage);
        Assert.Null(noMetadata?.PreviousPage);
        Assert.Null(noMetadata?.NextPage);
        Assert.Null(noOrder?.PreviousPage);
        Assert.Null(noOrder?.NextPage);
        Assert.Null(empty?.PreviousPage);
        Assert.Null(empty?.NextPage);
        Assert.Null(anchor?.PreviousPage);
        Assert.Null(anchor?.NextPage);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldIgnoreIneligibleSequenceCandidates_AndHandleSequenceBounds()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Hidden",
                "guides/hidden.md",
                "<p>Hidden</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 1,
                    HideFromPublicNav = true
                }),
            new(
                "Anchor",
                "guides/anchor.md#section",
                string.Empty,
                "guides/anchor.md",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 2
                }),
            new(
                "Empty",
                "guides/empty.md",
                string.Empty,
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 3
                }),
            new(
                "Different Sequence",
                "guides/different.md",
                "<p>Different</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "other",
                    Order = 4
                }),
            new(
                "No Order",
                "guides/no-order.md",
                "<p>No order</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof"
                }),
            new(
                "No Metadata",
                "guides/no-metadata.md",
                "<p>No metadata</p>"),
            new(
                "First",
                "guides/first.md",
                "<p>First</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 10
                }),
            new(
                "Last",
                "guides/last.md",
                "<p>Last</p>",
                Metadata: new DocMetadata
                {
                    SequenceKey = "proof",
                    Order = 20
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var first = await _aggregator.GetDocDetailsAsync("guides/first.md");
        var last = await _aggregator.GetDocDetailsAsync("guides/last.md");

        Assert.Null(first?.PreviousPage);
        Assert.Equal("/docs/guides/last.md.html", first?.NextPage?.Href);
        Assert.Equal("/docs/guides/first.md.html", last?.PreviousPage?.Href);
        Assert.Null(last?.NextPage);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveRelatedPagesByTitle_AndSkipInvalidHiddenDuplicateEntries()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Current",
                "guides/current.md",
                "<p>Current</p>",
                Metadata: new DocMetadata
                {
                    RelatedPages =
                    [
                        "   ",
                        "Missing Destination",
                        "Hidden Destination",
                        "Visible Destination",
                        "Visible Destination",
                        "guides/current.md"
                    ]
                }),
            new(
                "Hidden",
                "guides/hidden.md",
                "<p>Hidden</p>",
                Metadata: new DocMetadata
                {
                    Title = "Hidden Destination",
                    HideFromPublicNav = true,
                    Order = 1
                }),
            new(
                "Other",
                "guides/other.md",
                "<p>Other</p>",
                Metadata: new DocMetadata
                {
                    Order = 2
                }),
            new(
                "Visible",
                "guides/visible.md",
                "<p>Visible</p>",
                Metadata: new DocMetadata
                {
                    Title = "Visible Destination"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var details = await _aggregator.GetDocDetailsAsync("guides/current.md");

        var relatedPage = Assert.Single(details!.RelatedPages);
        Assert.Equal("Visible Destination", relatedPage.Title);
        Assert.Equal("/docs/guides/visible.md.html", relatedPage.Href);
        Assert.Null(relatedPage.Summary);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveRelatedPagesBySourceFragmentBeforeBasePage()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Current",
                "guides/current.md",
                "<p>Current</p>",
                Metadata: new DocMetadata
                {
                    RelatedPages = ["Namespaces/Foo#Foo-Type"]
                }),
            new(
                "Foo Namespace",
                "Namespaces/Foo",
                "<p>Namespace page</p>",
                Metadata: new DocMetadata
                {
                    Title = "Foo Namespace"
                }),
            new(
                "Foo Type",
                "Namespaces/Foo#Foo-Type",
                string.Empty,
                "Namespaces/Foo",
                Metadata: new DocMetadata
                {
                    Title = "Foo Type"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var details = await _aggregator.GetDocDetailsAsync("guides/current.md");

        var relatedPage = Assert.Single(details!.RelatedPages);
        Assert.Equal("Foo Type", relatedPage.Title);
        Assert.Equal("/docs/Namespaces/Foo.html#Foo-Type", relatedPage.Href);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveRelatedPagesWithDocsRootPrefixedCanonicalPaths()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Current",
                "guides/current.md",
                "<p>Current</p>",
                Metadata: new DocMetadata
                {
                    RelatedPages = ["/docs/guides/related.md.html"]
                }),
            new(
                "Related",
                "guides/related.md",
                "<p>Related</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Follow the next step."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var details = await _aggregator.GetDocDetailsAsync("guides/current.md");

        var relatedPage = Assert.Single(details!.RelatedPages);
        Assert.Equal("Related", relatedPage.Title);
        Assert.Equal("/docs/guides/related.md.html", relatedPage.Href);
        Assert.Equal("Follow the next step.", relatedPage.Summary);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveRelatedPagesWithCurrentDocsRootPrefixedCanonicalPaths()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Current",
                "guides/current.md",
                "<p>Current</p>",
                Metadata: new DocMetadata
                {
                    RelatedPages = ["/docs/next/guides/related.md.html"]
                }),
            new(
                "Related",
                "guides/related.md",
                "<p>Related</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Follow the next preview step."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var aggregator = new DocAggregator(
            [_harvesterFake],
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions
                {
                    DocsRootPath = "/docs/next"
                }
            },
            _envFake,
            memo,
            _sanitizerFake,
            _loggerFake);

        var details = await aggregator.GetDocDetailsAsync("guides/current.md");

        var relatedPage = Assert.Single(details!.RelatedPages);
        Assert.Equal("Related", relatedPage.Title);
        Assert.Equal("/docs/next/guides/related.md.html", relatedPage.Href);
        Assert.Equal("Follow the next preview step.", relatedPage.Summary);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveRelatedPagesWithMissingMetadata()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Current",
                "guides/current.md",
                "<p>Current</p>",
                Metadata: new DocMetadata
                {
                    RelatedPages = ["No Metadata"]
                }),
            new(
                "No Metadata",
                "guides/no-metadata.md",
                "<p>No metadata</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var details = await _aggregator.GetDocDetailsAsync("guides/current.md");

        var relatedPage = Assert.Single(details!.RelatedPages);
        Assert.Equal("No Metadata", relatedPage.Title);
        Assert.Equal("/docs/guides/no-metadata.md.html", relatedPage.Href);
        Assert.Null(relatedPage.Summary);
        Assert.Null(relatedPage.PageTypeBadge);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveMarkdownContributorProvenance_FromTemplatesAndGitFreshness()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var expectedTimestamp = new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero);
        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (sourcePath, _) =>
            {
                resolverCalls++;
                Assert.Equal("guides/quickstart.md", sourcePath);
                return Task.FromResult<DateTimeOffset?>(expectedTimestamp);
            });

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/quickstart.md", details!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/edit/main/guides/quickstart.md", details.ContributorProvenance.EditHref);
        Assert.Equal(expectedTimestamp, details.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldSkipContributorResolution_WhenContributorRenderingIsDisabled()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = false,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details);
        Assert.Null(details!.ContributorProvenance);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldCacheGitFreshnessPerSnapshotGeneration()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (sourcePath, _) =>
            {
                resolverCalls++;
                Assert.Equal("guides/quickstart.md", sourcePath);
                return Task.FromResult<DateTimeOffset?>(
                    new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero));
            });

        _ = await aggregator.GetDocDetailsAsync("guides/quickstart.md");
        _ = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldOmitGitFreshness_WhenResolverReturnsNull_ButKeepLinks()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) => Task.FromResult<DateTimeOffset?>(null));

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/quickstart.md", details!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/edit/main/guides/quickstart.md", details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldTreatDefaultContributorLastUpdatedOverrideAsMissing()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Quickstart",
                    "guides/quickstart.md",
                    "<p>Guide</p>",
                    Metadata: new DocMetadata
                    {
                        Contributor = new DocContributorMetadata
                        {
                            LastUpdatedOverride = default
                        }
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/quickstart.md", details!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/edit/main/guides/quickstart.md", details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldOmitGitFreshness_WhenDefaultResolverRunsOutsideGitRepo()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var aggregator = new DocAggregator(
            [harvester],
            new RazorDocsOptions
            {
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = Path.GetTempPath()
                },
                Contributor = new RazorDocsContributorOptions
                {
                    Enabled = true,
                    DefaultBranch = "main",
                    SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                    LastUpdatedMode = RazorDocsLastUpdatedMode.Git
                }
            },
            _envFake,
            _memo,
            _sanitizerFake,
            _loggerFake);

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/quickstart.md", details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldSuppressAutomaticContributorProvenance_ForApiReferenceDocs()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web"))
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details);
        Assert.Null(details!.ContributorProvenance);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldResolveAutomaticContributorProvenance_ForMarkdownDocs_EvenWhenPageTypeIsApiReference()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Quickstart",
                    "guides/quickstart.md",
                    "<p>Guide</p>",
                    Metadata: new DocMetadata
                    {
                        PageType = "api-reference"
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/quickstart.md", details!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/edit/main/guides/quickstart.md", details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldHonorExplicitContributorOverrides_ForSyntheticDocs()
    {
        var harvester = A.Fake<IDocHarvester>();
        var expectedTimestamp = new DateTimeOffset(2026, 4, 22, 23, 19, 0, TimeSpan.Zero);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = "Web/README.md",
                            SourceUrlOverride = "https://example.com/source",
                            LastUpdatedOverride = expectedTimestamp
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/source", details!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/edit/main/Web/README.md", details.ContributorProvenance.EditHref);
        Assert.Equal(expectedTimestamp, details.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldHideContributorProvenance_WhenMetadataSuppressesIt()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Quickstart",
                    "guides/quickstart.md",
                    "<p>Guide</p>",
                    Metadata: new DocMetadata
                    {
                        Contributor = new DocContributorMetadata
                        {
                            HideContributorInfo = true,
                            SourcePathOverride = "guides/quickstart.md"
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details);
        Assert.Null(details!.ContributorProvenance);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldOmitLastUpdated_WhenContributorFreshnessTimesOut()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DateTimeOffset.UtcNow;
            },
            contributorFreshnessTimeout: TimeSpan.FromMilliseconds(25));

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/quickstart.md", details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldCacheTimedOutContributorFreshness_ForSharedSourceOverrides()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "First",
                    "guides/first.md",
                    "<p>First</p>",
                    Metadata: new DocMetadata
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = "shared/source.md"
                        }
                    }),
                new DocNode(
                    "Second",
                    "guides/second.md",
                    "<p>Second</p>",
                    Metadata: new DocMetadata
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = "shared/source.md"
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            async (_, cancellationToken) =>
            {
                resolverCalls++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DateTimeOffset.UtcNow;
            },
            contributorFreshnessTimeout: TimeSpan.FromMilliseconds(25));

        var firstDetails = await aggregator.GetDocDetailsAsync("guides/first.md");
        var secondDetails = await aggregator.GetDocDetailsAsync("guides/second.md");

        Assert.NotNull(firstDetails?.ContributorProvenance);
        Assert.NotNull(secondDetails?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/shared/source.md", firstDetails!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/blob/main/shared/source.md", secondDetails!.ContributorProvenance!.SourceHref);
        Assert.Null(firstDetails.ContributorProvenance.LastUpdatedUtc);
        Assert.Null(secondDetails.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldCapContributorFreshnessBudget_AcrossUniqueSourcePaths()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("First", "guides/first.md", "<p>First</p>"),
                new DocNode("Second", "guides/second.md", "<p>Second</p>")
            ]);

        var now = new DateTimeOffset(2026, 5, 2, 20, 30, 0, TimeSpan.Zero);
        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            async (_, cancellationToken) =>
            {
                resolverCalls++;
                now = now.AddMilliseconds(30);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DateTimeOffset.UtcNow;
            },
            contributorFreshnessTimeout: TimeSpan.FromMilliseconds(25),
            utcNow: () => now);

        var firstDetails = await aggregator.GetDocDetailsAsync("guides/first.md");
        var secondDetails = await aggregator.GetDocDetailsAsync("guides/second.md");

        Assert.NotNull(firstDetails?.ContributorProvenance);
        Assert.NotNull(secondDetails?.ContributorProvenance);
        Assert.Equal("https://example.com/blob/main/guides/first.md", firstDetails!.ContributorProvenance!.SourceHref);
        Assert.Equal("https://example.com/blob/main/guides/second.md", secondDetails!.ContributorProvenance!.SourceHref);
        Assert.Null(firstDetails.ContributorProvenance.LastUpdatedUtc);
        Assert.Null(secondDetails.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldDropContributorOverrides_WithUnsafeHrefSchemes()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourceUrlOverride = "javascript:alert('xss')",
                            EditUrlOverride = "/docs/contribute/edit.md"
                        }
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Equal("/docs/contribute/edit.md", details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldDropContributorOverrides_WithRelativeHrefValues()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourceUrlOverride = "guides/local-only.md",
                            EditUrlOverride = "/docs/contribute/edit.md"
                        }
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Equal("/docs/contribute/edit.md", details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldDropContributorOverrides_WithUnsafeEditHrefSchemes()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourceUrlOverride = "https://example.com/source",
                            EditUrlOverride = "javascript:alert('xss')"
                        }
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("https://example.com/source", details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldHideContributorProvenance_WhenProtocolRelativeHrefValuesAreOnlyEvidence()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourceUrlOverride = "//evil.example/source.md",
                            EditUrlOverride = "//evil.example/edit.md"
                        }
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.Null(details?.ContributorProvenance);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldDropProtocolRelativeHrefValues_WhenExplicitTimestampKeepsContributorProvenanceVisible()
    {
        var expectedLastUpdatedUtc = new DateTimeOffset(2026, 5, 1, 12, 34, 56, TimeSpan.Zero);
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourceUrlOverride = "//evil.example/source.md",
                            EditUrlOverride = "//evil.example/edit.md",
                            LastUpdatedOverride = expectedLastUpdatedUtc
                        }
                    })
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.EditHref);
        Assert.Equal(expectedLastUpdatedUtc, details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldRejectRootedContributorSourcePathOverrides()
    {
        var harvester = A.Fake<IDocHarvester>();
        var expectedLastUpdatedUtc = new DateTimeOffset(2026, 5, 2, 22, 15, 0, TimeSpan.Zero);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = "/shared/source.md",
                            LastUpdatedOverride = expectedLastUpdatedUtc
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.EditHref);
        Assert.Equal(expectedLastUpdatedUtc, details.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldRejectWhitespacePrefixedWindowsAbsoluteContributorSourcePathOverrides()
    {
        var harvester = A.Fake<IDocHarvester>();
        var expectedLastUpdatedUtc = new DateTimeOffset(2026, 5, 2, 22, 16, 0, TimeSpan.Zero);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = " C:\\shared\\source.md ",
                            LastUpdatedOverride = expectedLastUpdatedUtc
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.EditHref);
        Assert.Equal(expectedLastUpdatedUtc, details.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldRejectWhitespacePrefixedWindowsDriveRelativeContributorSourcePathOverrides()
    {
        var harvester = A.Fake<IDocHarvester>();
        var expectedLastUpdatedUtc = new DateTimeOffset(2026, 5, 2, 22, 17, 0, TimeSpan.Zero);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = " C:repo\\source.md ",
                            LastUpdatedOverride = expectedLastUpdatedUtc
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.EditHref);
        Assert.Equal(expectedLastUpdatedUtc, details.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldRejectContributorSourcePathOverrides_ThatEscapeRepoScope()
    {
        var harvester = A.Fake<IDocHarvester>();
        var expectedLastUpdatedUtc = new DateTimeOffset(2026, 5, 2, 22, 18, 0, TimeSpan.Zero);
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Runnable.Web",
                    "<p>Namespace page</p>",
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web") with
                    {
                        Contributor = new DocContributorMetadata
                        {
                            SourcePathOverride = "../../README.md",
                            LastUpdatedOverride = expectedLastUpdatedUtc
                        }
                    })
            ]);

        var resolverCalls = 0;
        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.Git
            },
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
            });

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Runnable.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Null(details!.ContributorProvenance!.SourceHref);
        Assert.Null(details.ContributorProvenance.EditHref);
        Assert.Equal(expectedLastUpdatedUtc, details.ContributorProvenance.LastUpdatedUtc);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldPreserveContributorBranchSegments_WhenExpandingLinks()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode("Quickstart", "guides/quickstart.md", "<p>Guide</p>")
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "feature/issue-143",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}",
                LastUpdatedMode = RazorDocsLastUpdatedMode.None
            },
            resolveGitLastUpdatedUtcAsync: null);

        var details = await aggregator.GetDocDetailsAsync("guides/quickstart.md");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal(
            "https://example.com/blob/feature/issue-143/guides/quickstart.md",
            details!.ContributorProvenance!.SourceHref);
        Assert.Equal(
            "https://example.com/edit/feature/issue-143/guides/quickstart.md",
            details.ContributorProvenance.EditHref);
        Assert.Null(details.ContributorProvenance.LastUpdatedUtc);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnNull_WhenGitTimestampIsUnparseable()
    {
        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            _loggerFake,
            CancellationToken.None,
            (_, _, _, _, _) => Task.FromResult(new CommandResult(0, "not-a-timestamp", string.Empty)));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnNull_WhenGitExitCodeIsNonZero()
    {
        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            _loggerFake,
            CancellationToken.None,
            (_, _, _, _, _) => Task.FromResult(new CommandResult(128, string.Empty, "fatal: not a git repository")));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnNull_WhenGitExitCodeIsNonZero_AndDebugLoggingIsEnabled()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger<DocAggregator>();

        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            logger,
            CancellationToken.None,
            (_, _, _, _, _) => Task.FromResult(new CommandResult(128, string.Empty, "fatal: not a git repository")));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnNull_WhenGitExitCodeIsNonZero_AndStderrIsEmpty()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
        var logger = loggerFactory.CreateLogger<DocAggregator>();

        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            logger,
            CancellationToken.None,
            (_, _, _, _, _) => Task.FromResult(new CommandResult(128, string.Empty, "   ")));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnNull_WhenGitOutputIsEmpty()
    {
        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            _loggerFake,
            CancellationToken.None,
            (_, _, _, _, _) => Task.FromResult(new CommandResult(0, "   ", string.Empty)));

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnUtcTimestamp_WhenGitTimestampIsParseable()
    {
        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            _loggerFake,
            CancellationToken.None,
            (_, _, _, _, _) => Task.FromResult(new CommandResult(0, "2026-04-22T23:19:00+02:00", string.Empty)));

        Assert.Equal(new DateTimeOffset(2026, 4, 22, 21, 19, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldPropagateOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DocAggregator.ResolveGitLastUpdatedUtcAsync(
                Path.GetTempPath(),
                "guides/quickstart.md",
                _loggerFake,
                cts.Token,
                (_, _, _, _, cancellationToken) => Task.FromCanceled<CommandResult>(cancellationToken)));
    }

    [Fact]
    public async Task ResolveGitLastUpdatedUtcAsync_ShouldReturnNull_WhenGitProcessThrows()
    {
        var result = await DocAggregator.ResolveGitLastUpdatedUtcAsync(
            Path.GetTempPath(),
            "guides/quickstart.md",
            _loggerFake,
            CancellationToken.None,
            (_, _, _, _, _) => throw new InvalidOperationException("boom"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldUseTypedOutlineHeadings()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Guide",
                "guides/guide.md",
                "<p>Body</p>",
                Metadata: new DocMetadata
                {
                    Summary = "Guide summary."
                },
                Outline:
                [
                    new DocOutlineItem
                    {
                        Title = "Install",
                        Id = "install",
                        Level = 2
                    },
                    new DocOutlineItem
                    {
                        Title = "Verify",
                        Id = "verify",
                        Level = 3
                    }
                ])
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var payload = await _aggregator.GetSearchIndexPayloadAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var headings = document.RootElement
            .GetProperty("documents")[0]
            .GetProperty("headings")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["Install", "Verify"], headings);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldUseEmptyBodyText_WhenContentIsNull()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Guide",
                "guides/guide.md",
                null!,
                Metadata: new DocMetadata
                {
                    Summary = "Guide summary."
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var payload = await _aggregator.GetSearchIndexPayloadAsync();

        var indexedDocument = Assert.Single(payload.Documents);
        Assert.Equal("/docs/guides/guide.md", indexedDocument.Path);
        Assert.Equal(string.Empty, indexedDocument.BodyText);
        Assert.Equal(string.Empty, indexedDocument.Snippet);
        Assert.Equal("Guide summary.", indexedDocument.Summary);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldHonorConfiguredDocsRoot()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Guide",
                "guides/guide.md",
                "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var memo = new Memo(cache);
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                DocsRootPath = "/docs/next"
            }
        };
        var aggregator = new DocAggregator(
            [_harvesterFake],
            options,
            _envFake,
            memo,
            _sanitizerFake,
            _loggerFake);

        var payload = await aggregator.GetSearchIndexPayloadAsync();

        var indexedDocument = Assert.Single(payload.Documents);
        Assert.Equal("/docs/next/guides/guide.md", indexedDocument.Path);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldSynthesizeFallbackTitle_WhenDocTitleIsBlank()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "   ",
                "guides/untitled-guide.md",
                "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var payload = await _aggregator.GetSearchIndexPayloadAsync();

        var indexedDocument = Assert.Single(payload.Documents);
        Assert.Equal("untitled-guide.md", indexedDocument.Title);
        Assert.Equal("/docs/guides/untitled-guide.md", indexedDocument.Path);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldUseUntitledFallback_WhenPathHasNoUsableSegment()
    {
        var harvestedDocs = new List<DocNode>
        {
            new(
                "   ",
                "/",
                "<p>Guide body</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var payload = await _aggregator.GetSearchIndexPayloadAsync();

        var indexedDocument = Assert.Single(payload.Documents);
        Assert.Equal("Untitled document", indexedDocument.Title);
        Assert.Equal("/docs", indexedDocument.Path);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldOmitGeneratedSymbolSourceLinkText()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """<p>Calculator behavior.</p><span data-razordocs-symbol-source="Test-Calculator"></span>""",
                    SymbolSourceProvenance:
                    [
                        new DocSymbolSourceProvenance
                        {
                            AnchorId = "Test-Calculator",
                            SourcePath = "src/Calculator.cs",
                            StartLine = 12
                        }
                    ])
            ]);

        var aggregator = new DocAggregator(
            [harvester],
            new RazorDocsOptions
            {
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = Path.GetTempPath()
                },
                Contributor = new RazorDocsContributorOptions
                {
                    Enabled = true,
                    DefaultBranch = "main",
                    SourceRef = "abc123",
                    SymbolSourceUrlTemplate = "https://example.com/blob/{ref}/{path}#L{line}"
                }
            },
            _envFake,
            _memo,
            new RazorDocsHtmlSanitizer(),
            _loggerFake,
            resolveGitLastUpdatedUtcAsync: null);

        var payload = await aggregator.GetSearchIndexPayloadAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var bodyText = document.RootElement
            .GetProperty("documents")[0]
            .GetProperty("bodyText")
            .GetString();
        var snippet = document.RootElement
            .GetProperty("documents")[0]
            .GetProperty("snippet")
            .GetString();

        Assert.Equal("Calculator behavior.", bodyText);
        Assert.Equal("Calculator behavior.", snippet);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ShouldOmitGeneratedSymbolSourceLinkText_RegardlessOfAttributeOrder()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Calculator",
                    "Namespaces/Test",
                    """
                    <p>Calculator behavior.</p>
                    <a aria-label="View source" href="https://example.com/blob/abc123/src/Calculator.cs#L12" class="chip doc-symbol-source-link">Source</a>
                    <a href="https://example.com/source-help" class="not-doc-symbol-source-link">Source help remains searchable.</a>
                    """)
            ]);

        var payload = await _aggregator.GetSearchIndexPayloadAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var bodyText = document.RootElement
            .GetProperty("documents")[0]
            .GetProperty("bodyText")
            .GetString();
        var snippet = document.RootElement
            .GetProperty("documents")[0]
            .GetProperty("snippet")
            .GetString();

        Assert.Equal("Calculator behavior. Source help remains searchable.", bodyText);
        Assert.Equal("Calculator behavior. Source help remains searchable.", snippet);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldSkipCanceledHarvester_AndContinue()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException());

        var docs = await _aggregator.GetDocsAsync();

        Assert.Empty(docs);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldUseInstanceScopedMemoKeys_WhenMemoIsShared()
    {
        using var sharedCache = new MemoryCache(new MemoryCacheOptions());
        using var sharedMemo = new Memo(sharedCache);

        var sharedEnv = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => sharedEnv.ContentRootPath).Returns(Path.GetTempPath());
        var sharedSanitizer = A.Fake<IRazorDocsHtmlSanitizer>();
        A.CallTo(() => sharedSanitizer.Sanitize(A<string>._))
            .ReturnsLazily((string input) => input);
        var sharedLogger = A.Fake<ILogger<DocAggregator>>();

        var harvesterA = A.Fake<IDocHarvester>();
        var harvesterB = A.Fake<IDocHarvester>();
        A.CallTo(() => harvesterA.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(new[] { new DocNode("DocA", "a", "content") });
        A.CallTo(() => harvesterB.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(new[] { new DocNode("DocB", "b", "content") });

        var sharedOptions = new RazorDocsOptions
        {
            Source = new RazorDocsSourceOptions { RepositoryRoot = Path.GetTempPath() }
        };

        var aggregatorA = new DocAggregator(
            new[] { harvesterA },
            sharedOptions,
            sharedEnv,
            sharedMemo,
            sharedSanitizer,
            sharedLogger);
        var aggregatorB = new DocAggregator(
            new[] { harvesterB },
            sharedOptions,
            sharedEnv,
            sharedMemo,
            sharedSanitizer,
            sharedLogger);

        var docsA = await aggregatorA.GetDocsAsync();
        var docsB = await aggregatorB.GetDocsAsync();

        Assert.Single(docsA);
        Assert.Single(docsB);
        Assert.Equal("DocA", docsA[0].Title);
        Assert.Equal("DocB", docsB[0].Title);
        A.CallTo(() => harvesterA.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => harvesterB.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task InvalidateCache_ShouldNotResurfaceOlderSnapshots_AfterMultipleRefreshes()
    {
        var harvestCount = 0;
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                harvestCount++;
                return new[] { new DocNode($"Doc{harvestCount}", $"path-{harvestCount}", "content") };
            });

        var first = (await _aggregator.GetDocsAsync()).Single();
        _aggregator.InvalidateCache();
        var second = (await _aggregator.GetDocsAsync()).Single();
        _aggregator.InvalidateCache();
        var third = (await _aggregator.GetDocsAsync()).Single();

        Assert.Equal("Doc1", first.Title);
        Assert.Equal("Doc2", second.Title);
        Assert.Equal("Doc3", third.Title);
        Assert.Equal(3, harvestCount);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldPassTimeoutScopedToken_ToHarvester()
    {
        CancellationToken? observedToken = null;
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string _, CancellationToken ct) => observedToken = ct)
            .Returns(Array.Empty<DocNode>());

        _ = await _aggregator.GetDocsAsync();

        Assert.NotNull(observedToken);
        Assert.True(observedToken!.Value.CanBeCanceled);
        Assert.False(observedToken.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldCancelCallerWait_WithoutPoisoningSharedSnapshot()
    {
        // Arrange
        var harvestedDocs = new List<DocNode> { new("Recovered", "path", "content") };
        var releaseHarvester = new TaskCompletionSource<IReadOnlyList<DocNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken? observedToken = null;
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string _, CancellationToken ct) => observedToken = ct)
            .ReturnsLazily(() => releaseHarvester.Task);

        using var cts = new CancellationTokenSource();
        var canceledCall = _aggregator.GetDocsAsync(cts.Token);

        // Act
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = (await canceledCall).ToList());
        releaseHarvester.SetResult(harvestedDocs);
        var recovered = await _aggregator.GetDocsAsync();

        // Assert
        Assert.NotNull(observedToken);
        Assert.True(observedToken!.Value.CanBeCanceled);
        Assert.False(observedToken.Value.IsCancellationRequested);
        Assert.Single(recovered);
        Assert.Equal("Recovered", recovered[0].Title);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldReturnHealthySnapshot_AndReuseCachedDocsSnapshot()
    {
        var generatedUtc = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Guide", "docs/guide.md", "<p>Guide</p>")]);
        var aggregator = CreateHarvestHealthAggregator([_harvesterFake], utcNow: () => generatedUtc);

        var health = await aggregator.GetHarvestHealthAsync();
        var docs = await aggregator.GetDocsAsync();

        Assert.Equal(DocHarvestHealthStatus.Healthy, health.Status);
        Assert.Equal(generatedUtc, health.GeneratedUtc);
        Assert.Equal(Path.GetTempPath(), health.RepositoryRoot);
        Assert.Equal(1, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(0, health.FailedHarvesters);
        Assert.Equal(1, health.TotalDocs);
        var harvester = Assert.Single(health.Harvesters);
        Assert.Equal(DocHarvesterHealthStatus.Succeeded, harvester.Status);
        Assert.Equal(1, harvester.DocCount);
        Assert.Null(harvester.Diagnostic);
        Assert.Empty(health.Diagnostics);
        Assert.Single(docs);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldReturnEmpty_WhenRegisteredHarvestersReturnNoDocs()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(Array.Empty<DocNode>());

        var health = await _aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Empty, health.Status);
        Assert.Equal(1, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(0, health.FailedHarvesters);
        Assert.Equal(0, health.TotalDocs);
        var harvester = Assert.Single(health.Harvesters);
        Assert.Equal(DocHarvesterHealthStatus.ReturnedEmpty, harvester.Status);
        Assert.Equal(0, harvester.DocCount);
        Assert.Empty(health.Diagnostics);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldTreatNullHarvesterResultAsEmpty()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<DocNode>>(null!));

        var health = await _aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Empty, health.Status);
        var harvester = Assert.Single(health.Harvesters);
        Assert.Equal(DocHarvesterHealthStatus.ReturnedEmpty, harvester.Status);
        Assert.Equal(0, harvester.DocCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectNonPositiveHarvesterTimeout(int milliseconds)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateHarvestHealthAggregator([_harvesterFake], harvesterTimeout: TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("harvesterTimeout", exception.ParamName);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldReturnEmptyDiagnostic_WhenNoHarvestersAreRegistered()
    {
        var aggregator = CreateHarvestHealthAggregator([]);

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Empty, health.Status);
        Assert.Equal(0, health.TotalHarvesters);
        Assert.Equal(0, health.SuccessfulHarvesters);
        Assert.Equal(0, health.FailedHarvesters);
        Assert.Equal(0, health.TotalDocs);
        Assert.Empty(health.Harvesters);
        var diagnostic = Assert.Single(health.Diagnostics);
        Assert.Equal(DocHarvestDiagnosticCodes.NoHarvesters, diagnostic.Code);
        Assert.Equal(DocHarvestDiagnosticSeverity.Information, diagnostic.Severity);
        Assert.Null(diagnostic.HarvesterType);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldReturnDegraded_WhenOneHarvesterFailsAndAnotherSucceeds()
    {
        var failingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => failingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Harvester boom"));
        var workingHarvester = A.Fake<IDocHarvester>();
        A.CallTo(() => workingHarvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns([new DocNode("Success", "path", "content")]);
        var aggregator = CreateHarvestHealthAggregator([failingHarvester, workingHarvester]);

        var docs = await aggregator.GetDocsAsync();
        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Single(docs);
        Assert.Equal(DocHarvestHealthStatus.Degraded, health.Status);
        Assert.Equal(2, health.TotalHarvesters);
        Assert.Equal(1, health.SuccessfulHarvesters);
        Assert.Equal(1, health.FailedHarvesters);
        Assert.Equal(1, health.TotalDocs);
        Assert.Contains(health.Harvesters, item => item.Status == DocHarvesterHealthStatus.Succeeded);
        var failed = Assert.Single(health.Harvesters, item => item.Status == DocHarvesterHealthStatus.Failed);
        Assert.Equal(DocHarvestDiagnosticCodes.HarvesterFailed, failed.Diagnostic?.Code);
        Assert.DoesNotContain(health.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.AllFailed);
        Assert.Equal(0, CountLogCalls(_loggerFake, LogLevel.Critical));
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldReturnFailedAndLogCriticalOnce_PerFailedSnapshot()
    {
        var harvesterA = A.Fake<IDocHarvester>();
        var harvesterB = A.Fake<IDocHarvester>();
        A.CallTo(() => harvesterA.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Harvester boom A"));
        A.CallTo(() => harvesterB.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Harvester boom B"));
        var aggregator = CreateHarvestHealthAggregator([harvesterA, harvesterB]);

        var docs = await aggregator.GetDocsAsync();
        var firstHealth = await aggregator.GetHarvestHealthAsync();
        var secondHealth = await aggregator.GetHarvestHealthAsync();

        Assert.Empty(docs);
        Assert.Equal(DocHarvestHealthStatus.Failed, firstHealth.Status);
        Assert.Equal(DocHarvestHealthStatus.Failed, secondHealth.Status);
        Assert.Equal(2, firstHealth.TotalHarvesters);
        Assert.Equal(0, firstHealth.SuccessfulHarvesters);
        Assert.Equal(2, firstHealth.FailedHarvesters);
        Assert.Equal(0, firstHealth.TotalDocs);
        Assert.All(firstHealth.Harvesters, item => Assert.Equal(DocHarvesterHealthStatus.Failed, item.Status));
        Assert.Contains(firstHealth.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.AllFailed);
        Assert.DoesNotContain(
            firstHealth.Diagnostics.SelectMany(diagnostic => new[] { diagnostic.Problem, diagnostic.Cause, diagnostic.Fix }),
            value => value.Contains("Harvester boom", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, CountLogCalls(_loggerFake, LogLevel.Critical, "All RazorDocs harvesters failed"));

        aggregator.InvalidateCache();
        _ = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(2, CountLogCalls(_loggerFake, LogLevel.Critical, "All RazorDocs harvesters failed"));
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldRecordTimedOutHarvester_WhenTimeoutTokenCancelsWork()
    {
        var harvester = new DelayingHarvester();
        var aggregator = CreateHarvestHealthAggregator([harvester], harvesterTimeout: TimeSpan.FromMilliseconds(10));

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Failed, health.Status);
        var harvesterHealth = Assert.Single(health.Harvesters);
        Assert.Equal(DocHarvesterHealthStatus.TimedOut, harvesterHealth.Status);
        Assert.Equal(DocHarvestDiagnosticCodes.HarvesterTimedOut, harvesterHealth.Diagnostic?.Code);
        Assert.Contains(health.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.AllFailed);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldRecordCanceledHarvester_WhenHarvesterCancelsOutsideTimeout()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new OperationCanceledException());

        var health = await _aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Failed, health.Status);
        var harvesterHealth = Assert.Single(health.Harvesters);
        Assert.Equal(DocHarvesterHealthStatus.Canceled, harvesterHealth.Status);
        Assert.Equal(DocHarvestDiagnosticCodes.HarvesterCanceled, harvesterHealth.Diagnostic?.Code);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldReturnDefensiveCopyOfSnapshotLists()
    {
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Harvester boom"));

        var firstHealth = await _aggregator.GetHarvestHealthAsync();
        var firstHarvesters = Assert.IsType<DocHarvesterHealth[]>(firstHealth.Harvesters);
        var firstDiagnostics = Assert.IsType<DocHarvestDiagnostic[]>(firstHealth.Diagnostics);
        firstHarvesters[0] = firstHarvesters[0] with { Status = DocHarvesterHealthStatus.Succeeded };
        firstDiagnostics[0] = firstDiagnostics[0] with { Code = "mutated" };

        var secondHealth = await _aggregator.GetHarvestHealthAsync();

        Assert.NotSame(firstHealth.Harvesters, secondHealth.Harvesters);
        Assert.NotSame(firstHealth.Diagnostics, secondHealth.Diagnostics);
        var secondHarvester = Assert.Single(secondHealth.Harvesters);
        Assert.Equal(DocHarvesterHealthStatus.Failed, secondHarvester.Status);
        Assert.DoesNotContain(secondHealth.Diagnostics, diagnostic => diagnostic.Code == "mutated");
        Assert.Contains(secondHealth.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.HarvesterFailed);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_ShouldCancelCallerWait_WithoutPoisoningSharedSnapshot()
    {
        var releaseHarvester = new TaskCompletionSource<IReadOnlyList<DocNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => releaseHarvester.Task);
        using var cts = new CancellationTokenSource();
        var canceledCall = _aggregator.GetHarvestHealthAsync(cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await canceledCall);
        releaseHarvester.SetResult([new DocNode("Recovered", "path", "content")]);
        var health = await _aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Healthy, health.Status);
        Assert.Equal(1, health.TotalDocs);
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetDocsAsync_ShouldHandleRootFileCanonicalization()
    {
        var harvestedDocs = new List<DocNode>
        {
            new("RootFile", "readme.md", "content")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        Assert.Contains(docs, d => d.Title == "RootFile" && d.CanonicalPath == "readme.md.html");
    }

    [Fact]
    public async Task BuildCanonicalPath_ShouldPreserve_DotsToAvoidCollisions()
    {
        var harvestedDocs = new List<DocNode>
        {
            new("Dotdotted", "docs/readme.md", "content"),
            new("Underscored", "docs/readme_md.md", "content"),
            new("ApiV2", "docs/api.v2.md", "content")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        Assert.Contains(docs, d => d.Path == "docs/readme.md" && d.CanonicalPath == "docs/readme.md.html");
        Assert.Contains(docs, d => d.Path == "docs/readme_md.md" && d.CanonicalPath == "docs/readme_md.md.html");
        Assert.Contains(docs, d => d.Path == "docs/api.v2.md" && d.CanonicalPath == "docs/api.v2.md.html");
        Assert.NotEqual("docs/readme.md.html", "docs/readme_md.md.html");
    }

    [Fact]
    public async Task Constructor_ShouldPreferConfiguredRoot_OverEnvironmentFallback()
    {
        var configuredRoot = Path.Combine(Path.GetTempPath(), "configured-root");
        var localOptions = new RazorDocsOptions
        {
            Source = new RazorDocsSourceOptions { RepositoryRoot = configuredRoot }
        };
        var localEnv = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => localEnv.ContentRootPath).Returns("/definitely/not/used");
        string? capturedRoot = null;
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string root, CancellationToken _) => capturedRoot = root)
            .Returns(Array.Empty<DocNode>());
        var aggregator = new DocAggregator(
            new[] { _harvesterFake },
            localOptions,
            localEnv,
            _memo,
            _sanitizerFake,
            _loggerFake);

        _ = await aggregator.GetDocsAsync();

        Assert.Equal(configuredRoot, capturedRoot);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenMemoIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                _options,
                _envFake,
                null!,
                _sanitizerFake,
                _loggerFake));

        Assert.Equal("memo", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenHarvestersIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new DocAggregator(
                null!,
                _options,
                _envFake,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Equal("harvesters", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenEnvironmentIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                _options,
                null!,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Equal("environment", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenSanitizerIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                _options,
                _envFake,
                _memo,
                null!,
                _loggerFake));

        Assert.Equal("sanitizer", ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenLoggerIsNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                _options,
                _envFake,
                _memo,
                _sanitizerFake,
                null!));

        Assert.Equal("logger", ex.ParamName);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldMergeNamespaceReadmeIntoNamespaceNode_AndRemoveReadmeNode()
    {
        // Arrange
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new("Web", "Namespaces/ForgeTrust.Web", namespaceContent),
            new(
                "README",
                "docs/ForgeTrust.Web/README.md",
                "<p>Namespace intro <a href=\"./README.md\">self</a> <a href=\"./guide.md\">guide</a></p>"),
            new("Guide", "docs/ForgeTrust.Web/guide.md", "<p>Guide</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var docs = await _aggregator.GetDocsAsync();

        // Assert
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");
        Assert.DoesNotContain(docs, d => d.Path == "docs/ForgeTrust.Web/README.md");
        Assert.Contains("doc-namespace-intro", namespaceDoc.Content);
        Assert.Contains("Namespace intro", namespaceDoc.Content);
        Assert.Contains("href=\"./README.md\"", namespaceDoc.Content);
        Assert.Contains("href=\"/docs/docs/ForgeTrust.Web/guide.md.html\"", namespaceDoc.Content);
        Assert.DoesNotContain("href=\"/docs/docs/ForgeTrust.Web/README.md.html\"", namespaceDoc.Content);
        Assert.Contains("</section><section class=\"doc-namespace-intro\">", namespaceDoc.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldPreserveSymbolSourceProvenance_WhenNamespaceReadmeIsMerged()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Web",
                "Namespaces/ForgeTrust.Web",
                namespaceContent,
                SymbolSourceProvenance:
                [
                    new DocSymbolSourceProvenance
                    {
                        AnchorId = "ForgeTrust-Web-Type",
                        SourcePath = "src/Type.cs",
                        StartLine = 10
                    }
                ]),
            new("README", "docs/ForgeTrust.Web/README.md", "<p>Namespace intro</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");
        var provenance = Assert.Single(namespaceDoc.SymbolSourceProvenance!);
        Assert.Equal("ForgeTrust-Web-Type", provenance.AnchorId);
        Assert.Equal("src/Type.cs", provenance.SourcePath);
        Assert.Equal(10, provenance.StartLine);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldNotMergeSourceFolderPackageReadmes_IntoNamespacePages()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new("Web", "Namespaces/ForgeTrust.Web", namespaceContent),
            new("README", "src/ForgeTrust.Web/README.md", "<p>Package overview</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        Assert.Contains(docs, d => d.Path == "src/ForgeTrust.Web/README.md");
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");
        Assert.DoesNotContain("Package overview", namespaceDoc.Content);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldNotMergeRootLevelPackageReadmes_IntoNamespacePages()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new("Core namespace", "Namespaces/ForgeTrust.Runnable.Core", namespaceContent),
            new("Core package", "ForgeTrust.Runnable.Core/README.md", "<p>Package overview</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        Assert.Contains(docs, d => d.Path == "ForgeTrust.Runnable.Core/README.md");
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Runnable.Core");
        Assert.DoesNotContain("Package overview", namespaceDoc.Content);
    }

    [Fact]
    public async Task GetDocDetailsAsync_ShouldLabelMergedNamespaceReadmeContributorProvenance_AsNamespaceIntroSource()
    {
        var harvester = A.Fake<IDocHarvester>();
        A.CallTo(() => harvester.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Returns(
            [
                new DocNode(
                    "Web",
                    "Namespaces/ForgeTrust.Web",
                    "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>"),
                new DocNode("README", "docs/ForgeTrust.Web/README.md", "<p>Namespace intro</p>")
            ]);

        var aggregator = CreateContributorAggregator(
            harvester,
            new RazorDocsContributorOptions
            {
                Enabled = true,
                DefaultBranch = "main",
                SourceUrlTemplate = "https://example.com/blob/{branch}/{path}",
                EditUrlTemplate = "https://example.com/edit/{branch}/{path}"
            },
            null);

        var details = await aggregator.GetDocDetailsAsync("Namespaces/ForgeTrust.Web");

        Assert.NotNull(details?.ContributorProvenance);
        Assert.Equal("Namespace intro source", details!.ContributorProvenance!.Label);
        Assert.Equal(
            "https://example.com/blob/main/docs/ForgeTrust.Web/README.md",
            details.ContributorProvenance.SourceHref);
        Assert.Equal(
            "https://example.com/edit/main/docs/ForgeTrust.Web/README.md",
            details.ContributorProvenance.EditHref);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldKeepReadmeOutline_WhenNamespaceOutlineIsEmpty()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new("Web", "Namespaces/ForgeTrust.Web", namespaceContent),
            new(
                "README",
                "docs/ForgeTrust.Web/README.md",
                "<h2 id='overview'>Overview</h2>",
                Outline:
                [
                    new DocOutlineItem
                    {
                        Title = "Overview",
                        Id = "overview",
                        Level = 2
                    }
                ])
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");
        var outlineItem = Assert.Single(namespaceDoc.Outline!);
        Assert.Equal("Overview", outlineItem.Title);
        Assert.Equal("overview", outlineItem.Id);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldMergeNamespaceReadmeMetadataIntoNamespaceNode()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Web",
                "Namespaces/ForgeTrust.Web",
                namespaceContent,
                Metadata: new DocMetadata
                {
                    Title = "Web",
                    PageType = "api-reference",
                    NavGroup = "API Reference"
                }),
            new(
                "README",
                "docs/ForgeTrust.Web/README.md",
                "<p>Namespace intro</p>",
                Metadata: new DocMetadata
                {
                    Title = "ForgeTrust Web",
                    Summary = "Namespace summary",
                    Aliases = ["web docs"],
                    HideFromSearch = true
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = (await _aggregator.GetDocsAsync()).ToList();
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");

        Assert.Equal("ForgeTrust Web", namespaceDoc.Title);
        Assert.Equal("ForgeTrust Web", namespaceDoc.Metadata?.Title);
        Assert.Equal("Namespace summary", namespaceDoc.Metadata?.Summary);
        Assert.Equal(["web docs"], namespaceDoc.Metadata?.Aliases);
        Assert.True(namespaceDoc.Metadata?.HideFromSearch);
        Assert.Equal("api-reference", namespaceDoc.Metadata?.PageType);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldMergeNamespaceReadmeMetadata_WhenReadmeContentIsBlank()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new("Web", "Namespaces/ForgeTrust.Web", namespaceContent),
            new(
                "README",
                "docs/ForgeTrust.Web/README.md",
                "   ",
                Metadata: new DocMetadata
                {
                    Summary = "Metadata-only namespace summary"
                })
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = (await _aggregator.GetDocsAsync()).ToList();
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");

        Assert.DoesNotContain(docs, d => d.Path == "docs/ForgeTrust.Web/README.md");
        Assert.Equal(namespaceContent, namespaceDoc.Content);
        Assert.Equal("Metadata-only namespace summary", namespaceDoc.Metadata?.Summary);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldKeepApiClassification_WhenNamespaceReadmeMetadataOnlyProvidesDerivedDefaults()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Web",
                "Namespaces/ForgeTrust.Runnable.Web",
                namespaceContent,
                Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web")),
            new(
                "README",
                "docs/ForgeTrust.Runnable.Web/README.md",
                "<p>Namespace intro</p>",
                Metadata: DocMetadataFactory.CreateMarkdownMetadata(
                    "docs/ForgeTrust.Runnable.Web/README.md",
                    "ForgeTrust.Runnable.Web",
                    null,
                    "Namespace intro."))
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = (await _aggregator.GetDocsAsync()).ToList();
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Runnable.Web");

        Assert.Equal("api-reference", namespaceDoc.Metadata?.PageType);
        Assert.Equal("developer", namespaceDoc.Metadata?.Audience);
        Assert.Equal("API Reference", namespaceDoc.Metadata?.NavGroup);
        Assert.Equal("Namespace intro.", namespaceDoc.Metadata?.Summary);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldAllowExplicitNamespaceReadmeClassificationOverrides()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Web",
                "Namespaces/ForgeTrust.Runnable.Web",
                namespaceContent,
                Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web")),
            new(
                "README",
                "docs/ForgeTrust.Runnable.Web/README.md",
                "<p>Namespace intro</p>",
                Metadata: DocMetadataFactory.CreateMarkdownMetadata(
                    "docs/ForgeTrust.Runnable.Web/README.md",
                    "ForgeTrust.Runnable.Web",
                    new DocMetadata
                    {
                        PageType = "concept",
                        Audience = "implementer",
                        Component = "Docs",
                        NavGroup = "Concepts"
                    },
                    "Namespace intro."))
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = (await _aggregator.GetDocsAsync()).ToList();
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Runnable.Web");

        Assert.Equal("concept", namespaceDoc.Metadata?.PageType);
        Assert.Equal("implementer", namespaceDoc.Metadata?.Audience);
        Assert.Equal("Docs", namespaceDoc.Metadata?.Component);
        Assert.Equal("Concepts", namespaceDoc.Metadata?.NavGroup);
    }

    [Fact]
    public async Task GetDocsAsync_ShouldAllowNamespaceReadmeSidecarClassificationOverrides()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section><section class='doc-type'>Type body</section>";
        var sidecarMetadata = new DocMetadata
        {
            PageType = "concept",
            Audience = "implementer",
            Component = "Docs",
            NavGroup = "Concepts"
        };
        var harvestedDocs = new List<DocNode>
        {
            new(
                "Web",
                "Namespaces/ForgeTrust.Runnable.Web",
                namespaceContent,
                Metadata: DocMetadataFactory.CreateApiReferenceMetadata("Web", "ForgeTrust.Runnable.Web")),
            new(
                "README",
                "docs/ForgeTrust.Runnable.Web/README.md",
                "<p>Namespace intro</p>",
                Metadata: DocMetadataFactory.CreateMarkdownMetadata(
                    "docs/ForgeTrust.Runnable.Web/README.md",
                    "ForgeTrust.Runnable.Web",
                    DocMetadata.Merge(null, sidecarMetadata),
                    "Namespace intro."))
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = (await _aggregator.GetDocsAsync()).ToList();
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Runnable.Web");

        Assert.Equal("concept", namespaceDoc.Metadata?.PageType);
        Assert.Equal("implementer", namespaceDoc.Metadata?.Audience);
        Assert.Equal("Docs", namespaceDoc.Metadata?.Component);
        Assert.Equal("Concepts", namespaceDoc.Metadata?.NavGroup);
    }

    [Fact]
    public void MergeNamespaceIntroIntoContent_ShouldHandleMalformedNamespaceSections()
    {
        // Act
        var noMarker = DocAggregator.MergeNamespaceIntroIntoContent("<section>Body</section>", "<p>Intro</p>");
        var noSectionStart = DocAggregator.MergeNamespaceIntroIntoContent("doc-namespace-groups", "<p>Intro</p>");
        var noStartTagEnd = DocAggregator.MergeNamespaceIntroIntoContent("<section class='doc-namespace-groups'", "<p>Intro</p>");
        var noEndTag = DocAggregator.MergeNamespaceIntroIntoContent("<section class='doc-namespace-groups'>open", "<p>Intro</p>");
        var malformedNestedOpen = DocAggregator.MergeNamespaceIntroIntoContent(
            "<section class='doc-namespace-groups'><section class='inner'</section></section>",
            "<p>Intro</p>");
        var unbalancedNested = DocAggregator.MergeNamespaceIntroIntoContent(
            "<section class='doc-namespace-groups'><section></section>",
            "<p>Intro</p>");

        // Assert
        Assert.Equal("<section class=\"doc-namespace-intro\"><p>Intro</p></section><section>Body</section>", noMarker);
        Assert.Equal("<section class=\"doc-namespace-intro\"><p>Intro</p></section>doc-namespace-groups", noSectionStart);
        Assert.Equal("<section class=\"doc-namespace-intro\"><p>Intro</p></section><section class='doc-namespace-groups'", noStartTagEnd);
        Assert.Equal("<section class=\"doc-namespace-intro\"><p>Intro</p></section><section class='doc-namespace-groups'>open", noEndTag);
        Assert.Equal(
            "<section class=\"doc-namespace-intro\"><p>Intro</p></section><section class='doc-namespace-groups'><section class='inner'</section></section>",
            malformedNestedOpen);
        Assert.Equal(
            "<section class=\"doc-namespace-intro\"><p>Intro</p></section><section class='doc-namespace-groups'><section></section>",
            unbalancedNested);
    }

    [Fact]
    public void ExtractNamespaceNameFromReadmePath_ShouldReturnNull_WhenPathIsNotReadme()
    {
        // Act
        var namespaceName = DocAggregator.ExtractNamespaceNameFromReadmePath("docs/ForgeTrust.Web/NOTES.md");

        // Assert
        Assert.Null(namespaceName);
    }

    [Fact]
    public async Task Constructor_ShouldFallbackToDiscoveredRepositoryRoot_WhenSourceRootIsMissing()
    {
        // Arrange
        var localOptions = new RazorDocsOptions();
        var localEnv = A.Fake<IWebHostEnvironment>();
        var contentRoot = Path.Combine(Path.GetTempPath(), "repo-fallback-root");
        A.CallTo(() => localEnv.ContentRootPath).Returns(contentRoot);
        var expectedRoot = ForgeTrust.Runnable.Core.PathUtils.FindRepositoryRoot(contentRoot);
        string? capturedRoot = null;
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string root, CancellationToken _) => capturedRoot = root)
            .Returns(Array.Empty<DocNode>());
        var aggregator = new DocAggregator(
            new[] { _harvesterFake },
            localOptions,
            localEnv,
            _memo,
            _sanitizerFake,
            _loggerFake);

        // Act
        _ = await aggregator.GetDocsAsync();

        // Assert
        Assert.Equal(expectedRoot, capturedRoot);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenConfiguredRepositoryRootIsWhitespace()
    {
        var options = new RazorDocsOptions
        {
            Source = new RazorDocsSourceOptions { RepositoryRoot = "   " }
        };

        var ex = Assert.Throws<ArgumentException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                options,
                _envFake,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Equal(nameof(RazorDocsSourceOptions.RepositoryRoot), ex.ParamName);
        Assert.Contains("whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.Epsilon)]
    [InlineData(0.333)]
    [InlineData(double.MaxValue)]
    public void Constructor_ShouldThrow_WhenCacheExpirationIsInvalid(double cacheExpirationMinutes)
    {
        var options = new RazorDocsOptions
        {
            CacheExpirationMinutes = cacheExpirationMinutes
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                options,
                _envFake,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Equal(nameof(RazorDocsOptions.CacheExpirationMinutes), ex.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenBundleModeIsRequestedBeforeItIsImplemented()
    {
        var options = new RazorDocsOptions
        {
            Mode = RazorDocsMode.Bundle,
            Bundle = new RazorDocsBundleOptions { Path = "/tmp/docs.bundle.json" }
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                options,
                _envFake,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Contains("bundle mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenModeIsUnsupported()
    {
        var options = new RazorDocsOptions
        {
            Mode = (RazorDocsMode)999
        };

        var ex = Assert.Throws<NotSupportedException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                options,
                _envFake,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Contains("Unsupported RazorDocs mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenSourceOptionsAreNullInSourceMode()
    {
        var options = new RazorDocsOptions
        {
            Source = null!
        };

        var ex = Assert.Throws<ArgumentNullException>(
            () => new DocAggregator(
                new[] { _harvesterFake },
                options,
                _envFake,
                _memo,
                _sanitizerFake,
                _loggerFake));

        Assert.Equal("Source", ex.ParamName);
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldUsePathFallback_WhenCachedCanonicalPathIsNull()
    {
        // Arrange
        var harvestedDocs = new List<DocNode> { new("Method", "docs/service.cs#DoWork", "content") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var result = await _aggregator.GetDocByPathAsync("DOCS/service.cs#DoWork");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Method", result!.Title);
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldMatchNormalizedSourcePath_WhenCanonicalPathDoesNotMatch()
    {
        var harvestedDocs = new List<DocNode> { new("Guide", "docs/guide.md", "<p>Guide</p>") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var result = await _aggregator.GetDocByPathAsync("/DOCS/guide.md/");

        Assert.NotNull(result);
        Assert.Equal("Guide", result!.Title);
    }

    [Fact]
    public async Task GetDocByPathAsync_ShouldMatchNormalizedSourcePath_WhenLookupStartsWithBackslashes()
    {
        var harvestedDocs = new List<DocNode> { new("Guide", "docs/guide.md", "<p>Guide</p>") };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var result = await _aggregator.GetDocByPathAsync("\\DOCS\\guide.md\\");

        Assert.NotNull(result);
        Assert.Equal("Guide", result!.Title);
    }

    [Fact]
    public async Task BuildCanonicalPath_ShouldNotAppendHtml_WhenSourceAlreadyHtml()
    {
        // Arrange
        var harvestedDocs = new List<DocNode>
        {
            new("AlreadyHtml", "docs/page.html", "content"),
            new("RootHtml", "index.html", "content")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var docs = await _aggregator.GetDocsAsync();

        // Assert
        Assert.Contains(docs, d => d.Path == "docs/page.html" && d.CanonicalPath == "docs/page.html");
        Assert.Contains(docs, d => d.Path == "index.html" && d.CanonicalPath == "index.html");
    }

    [Fact]
    public async Task GetDocsAsync_ShouldMergeNamespaceReadmes_WhenNamespaceNodesContainDuplicates()
    {
        // Arrange
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section>";
        var harvestedDocs = new List<DocNode>
        {
            new("Web", "Namespaces/ForgeTrust.Web", namespaceContent),
            new("Web-duplicate", "Namespaces/ForgeTrust.Web", namespaceContent),
            new("README", "docs/ForgeTrust.Web/README.md", "<p>Namespace intro</p>")
        };
        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        // Act
        var docs = await _aggregator.GetDocsAsync();

        // Assert
        var namespaceDoc = docs.Single(d => d.Path == "Namespaces/ForgeTrust.Web");
        Assert.Contains("doc-namespace-intro", namespaceDoc.Content);
        Assert.DoesNotContain(docs, d => d.Path == "docs/ForgeTrust.Web/README.md");
    }

    private DocAggregator CreateContributorAggregator(
        IDocHarvester harvester,
        RazorDocsContributorOptions contributorOptions,
        Func<string, CancellationToken, Task<DateTimeOffset?>>? resolveGitLastUpdatedUtcAsync,
        TimeSpan? contributorFreshnessTimeout = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        return new DocAggregator(
            [harvester],
            new RazorDocsOptions
            {
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = Path.GetTempPath()
                },
                Contributor = contributorOptions
            },
            _envFake,
            _memo,
            _sanitizerFake,
            _loggerFake,
            resolveGitLastUpdatedUtcAsync,
            harvesterTimeout: null,
            contributorFreshnessTimeout: contributorFreshnessTimeout,
            utcNow: utcNow);
    }

    private DocAggregator CreateHarvestHealthAggregator(
        IEnumerable<IDocHarvester> harvesters,
        ILogger<DocAggregator>? logger = null,
        TimeSpan? harvesterTimeout = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        return new DocAggregator(
            harvesters,
            new RazorDocsOptions
            {
                Source = new RazorDocsSourceOptions
                {
                    RepositoryRoot = Path.GetTempPath()
                }
            },
            _envFake,
            _memo,
            _sanitizerFake,
            logger ?? _loggerFake,
            resolveGitLastUpdatedUtcAsync: null,
            harvesterTimeout: harvesterTimeout,
            contributorFreshnessTimeout: null,
            utcNow: utcNow);
    }

    private static int CountLogCalls(
        ILogger<DocAggregator> logger,
        LogLevel level,
        string? expectedMessageFragment = null)
    {
        return Fake.GetCalls(logger)
            .Count(
                call =>
                    call.Method.Name == nameof(ILogger.Log)
                    && call.GetArgument<LogLevel>(0) == level
                    && (expectedMessageFragment is null
                        || (call.GetArgument<object>(2)?.ToString()?.Contains(
                            expectedMessageFragment,
                            StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    private sealed class DelayingHarvester : IDocHarvester
    {
        public async Task<IReadOnlyList<DocNode>> HarvestAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    [Fact]
    public async Task GetDocsAsync_ShouldKeepPackageReadmes_WhenNamespacePagesShareTheSameName()
    {
        var namespaceContent = "<section class='doc-namespace-groups'><h4>Namespaces</h4></section>";
        var harvestedDocs = new List<DocNode>
        {
            new("OpenApi namespace", "Namespaces/ForgeTrust.Runnable.Web.OpenApi", namespaceContent),
            new(
                "Packages",
                "packages/README.md",
                "<table><tbody><tr><td><a href=\"../Web/ForgeTrust.Runnable.Web.OpenApi/README.md\">Package README</a></td></tr></tbody></table>"),
            new("OpenApi package", "Web/ForgeTrust.Runnable.Web.OpenApi/README.md", "<p>Package guide</p>")
        };

        A.CallTo(() => _harvesterFake.HarvestAsync(A<string>._, A<CancellationToken>._)).Returns(harvestedDocs);

        var docs = await _aggregator.GetDocsAsync();

        Assert.Contains(docs, d => d.Path == "Web/ForgeTrust.Runnable.Web.OpenApi/README.md");

        var chooser = docs.Single(d => d.Path == "packages/README.md");
        Assert.Contains(
            "href=\"/docs/Web/ForgeTrust.Runnable.Web.OpenApi/README.md.html\"",
            chooser.Content);
    }

    public void Dispose()
    {
        (_memo as IDisposable)?.Dispose();
        _cache.Dispose();
    }
}
