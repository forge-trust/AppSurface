using System.Text.Json;
using FakeItEasy;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class AppSurfaceDocsVersionCatalogServiceTests : IDisposable
{
    private readonly string _tempDirectory;

    public AppSurfaceDocsVersionCatalogServiceTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "appsurfacedocs-version-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void AppSurfaceDocsResolvedVersionCatalogStatus_ShouldPreserveNumericContract()
    {
        Assert.Equal(0, (int)AppSurfaceDocsResolvedVersionCatalogStatus.Resolved);
        Assert.Equal(1, (int)AppSurfaceDocsResolvedVersionCatalogStatus.Disabled);
        Assert.Equal(2, (int)AppSurfaceDocsResolvedVersionCatalogStatus.EnabledWithoutCatalog);
        Assert.Equal(3, (int)AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDependenciesAreNull()
    {
        var environment = new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory };
        var options = new AppSurfaceDocsOptions();

        Assert.Throws<ArgumentNullException>(() => new AppSurfaceDocsVersionCatalogService(null!, environment, NullLogger<AppSurfaceDocsVersionCatalogService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceDocsVersionCatalogService(options, null!, NullLogger<AppSurfaceDocsVersionCatalogService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new AppSurfaceDocsVersionCatalogService(options, environment, null!));
    }

    [Fact]
    public void GetCatalog_ShouldResolveRelativeTreePaths_AndRecommendedVersion()
    {
        var stableTree = CreateExactTree("stable");
        var deprecatedTree = CreateExactTree("deprecated");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        Label = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, stableTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.1.0",
                        Label = "1.1.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, deprecatedTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Deprecated,
                        AdvisoryState = AppSurfaceDocsVersionAdvisoryState.Vulnerable
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Resolved, catalog.Status);
        Assert.Equal(catalogPath, catalog.CatalogPath);
        var recommendedVersion = Assert.IsType<AppSurfaceDocsResolvedVersion>(catalog.RecommendedVersion);
        Assert.Equal("1.2.0", recommendedVersion.Version);
        Assert.Equal(2, catalog.PublicVersions.Count);
        Assert.All(catalog.PublicVersions, version => Assert.True(version.IsAvailable));
        Assert.Contains(catalog.PublicVersions, version => version.ExactRootUrl == "/docs/v/1.2.0");
        Assert.Contains(catalog.PublicVersions, version => version.AdvisoryState == AppSurfaceDocsVersionAdvisoryState.Vulnerable);
    }

    [Fact]
    public void GetCatalog_ShouldResolveExactTreePathFromTrustedReleaseRoot()
    {
        var releaseStore = Path.Join(_tempDirectory, "release-store");
        Directory.CreateDirectory(releaseStore);
        var stableTree = CreateExactTree($"release-store{Path.DirectorySeparatorChar}1.2.3");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.3",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = "1.2.3"
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath, trustedReleaseRootPath: "release-store");

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.True(version.IsAvailable);
        Assert.Equal(stableTree, version.ExactTreePath);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldAcceptDotSlashTreePathUnderDefaultCatalogDirectoryRoot()
    {
        var stableTree = CreateExactTree($"releases{Path.DirectorySeparatorChar}1.2.3");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = "./releases/1.2.3"
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.True(version.IsAvailable);
        Assert.Equal(stableTree, version.ExactTreePath);
    }

    [Fact]
    public void GetCatalog_ShouldMarkAbsoluteExactTreePathUnavailable_AndLogMigrationHint()
    {
        var stableTree = CreateExactTree("absolute-tree");
        var logger = A.Fake<ILogger<AppSurfaceDocsVersionCatalogService>>();
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.3",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = stableTree
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath, logger: logger);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Contains("relative", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDirectory, version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        AssertWarningLogged(logger, "Set TrustedReleaseRootPath");
    }

    [Fact]
    public void GetCatalog_ShouldMarkEscapingExactTreePathUnavailable()
    {
        var outsideTree = CreateExactTree("outside-tree");
        var catalogDirectory = Path.Join(_tempDirectory, "catalog");
        Directory.CreateDirectory(catalogDirectory);
        var catalogPath = Path.Join(catalogDirectory, "catalog.json");
        File.WriteAllText(
            catalogPath,
            JsonSerializer.Serialize(
                new AppSurfaceDocsVersionCatalog
                {
                    Versions =
                    [
                        new AppSurfaceDocsPublishedVersion
                        {
                            Version = "1.2.3",
                            ExactTreePath = "../outside-tree"
                        }
                    ]
                }));

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Contains("inside the trusted release root", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(outsideTree, version.ExactTreePath);
    }

    [Fact]
    public void GetCatalog_ShouldReturnCatalogAvailabilityIssue_WhenTrustedReleaseRootIsMissing()
    {
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = "1.2.3"
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath, trustedReleaseRootPath: "missing-store");

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Empty(catalog.PublicVersions);
        Assert.Contains("Trusted release root", catalog.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDirectory, catalog.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldReturnCatalogAvailabilityIssue_WhenTrustedReleaseRootPathIsInvalid()
    {
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = "1.2.3"
                    }
                ]
            });
        var logger = A.Fake<ILogger<AppSurfaceDocsVersionCatalogService>>();
        var service = CreateCatalogService(catalogPath, trustedReleaseRootPath: "bad\0root", logger: logger);

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Empty(catalog.PublicVersions);
        Assert.Contains("invalid", catalog.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bad", catalog.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        AssertWarningLogged(logger, "trusted release root configuration is invalid");
    }

    [Fact]
    public void GetCatalog_ShouldRejectTrustedReleaseRootSymlink()
    {
        if (!TryCreateSymbolicLinkTestDirectory(out _, out var linkPath))
        {
            return;
        }

        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = "1.2.3"
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath, trustedReleaseRootPath: Path.GetRelativePath(_tempDirectory, linkPath));

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Contains("ordinary directory", catalog.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldRejectExactTreeRootSymlink()
    {
        if (!TryCreateSymbolicLinkTestDirectory(out var targetPath, out var linkPath))
        {
            return;
        }

        CreateExactTree(Path.GetRelativePath(_tempDirectory, targetPath));
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, linkPath)
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Contains("ordinary directory", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldRejectIntermediateSymlinkBetweenTrustedRootAndExactTree()
    {
        if (!TryCreateSymbolicLinkTestDirectory(out var targetPath, out var linkPath))
        {
            return;
        }

        CreateExactTree(Path.Join(Path.GetRelativePath(_tempDirectory, targetPath), "1.2.3"));
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = Path.Join(Path.GetFileName(linkPath), "1.2.3")
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Contains("ordinary directory", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldRejectRequiredFileSymlink()
    {
        if (!TryCreateSymbolicLinkTestFile(out _))
        {
            return;
        }

        var stableTree = CreateExactTree("required-file-symlink");
        var outsideSearchIndex = Path.Join(_tempDirectory, "outside-search-index.json");
        File.WriteAllText(outsideSearchIndex, "{\"documents\":[]}");
        File.Delete(Path.Join(stableTree, "search-index.json"));
        File.CreateSymbolicLink(Path.Join(stableTree, "search-index.json"), outsideSearchIndex);
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.3",
                        ExactTreePath = "required-file-symlink"
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Contains("search-index.json", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldResolveExactRootUrlsFromConfiguredRouteRoot()
    {
        var stableTree = CreateExactTree("stable-custom-root");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, stableTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath, routeRootPath: "/foo/bar", docsRootPath: "/foo/bar/next");

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.True(version.IsAvailable);
        Assert.Equal("/foo/bar/v/1.2.0", version.ExactRootUrl);
    }

    [Fact]
    public void GetCatalog_ShouldKeepHealthyVersions_WhenOneExactTreeIsBroken()
    {
        var healthyTree = CreateExactTree("healthy");
        var brokenTree = Path.Join(_tempDirectory, "broken");
        Directory.CreateDirectory(brokenTree);
        File.WriteAllText(Path.Join(brokenTree, "index.html"), "<html>broken</html>");
        File.WriteAllText(Path.Join(brokenTree, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Join(brokenTree, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(brokenTree, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Join(brokenTree, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Join(brokenTree, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");

        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "2.0.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.9.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Maintained
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var brokenVersion = Assert.Single(catalog.PublicVersions, version => version.Version == "2.0.0");
        var healthyVersion = Assert.Single(catalog.PublicVersions, version => version.Version == "1.9.0");
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("search-index.json", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.True(healthyVersion.IsAvailable);
    }

    [Fact]
    public void GetCatalog_ShouldMarkVersionUnavailable_WhenRequiredSearchAssetIsMissing()
    {
        var brokenTree = CreateExactTree("broken");
        File.Delete(Path.Join(brokenTree, "search-client.js"));
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var brokenVersion = Assert.Single(catalog.PublicVersions);
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("search-client.js", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldNotCrawlHistoricalHtml_WhenOutlineAssetIsMissing()
    {
        var exactTree = CreateExactTree("historical-without-outline-asset");
        File.Delete(Path.Join(exactTree, "outline-client.js"));
        File.WriteAllText(
            Path.Join(exactTree, "api.html"),
            """<html><head><script src="/docs/outline-client.js"></script></head><body>API</body></html>""");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, exactTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var exactVersion = Assert.Single(catalog.PublicVersions);
        Assert.True(exactVersion.IsAvailable);
        Assert.Same(exactVersion, catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldKeepVersionAvailable_WhenMissingOutlineAssetIsNotReferenced()
    {
        var exactTree = CreateExactTree("historical-without-outline");
        File.Delete(Path.Join(exactTree, "outline-client.js"));
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, exactTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var exactVersion = Assert.Single(catalog.PublicVersions);
        Assert.True(exactVersion.IsAvailable);
        Assert.Same(exactVersion, catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldMarkVersionUnavailable_WhenSearchIndexPayloadIsMalformed()
    {
        var brokenTree = CreateExactTree("broken-search-index");
        File.WriteAllText(Path.Join(brokenTree, "search-index.json"), "{");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var brokenVersion = Assert.Single(catalog.PublicVersions);
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("search-index.json", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unreadable", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDirectory, brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldMarkVersionUnavailable_WhenSearchIndexPayloadOmitsDocumentsArray()
    {
        var brokenTree = CreateExactTree("broken-search-shape");
        File.WriteAllText(Path.Join(brokenTree, "search-index.json"), "{\"items\":[]}");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var brokenVersion = Assert.Single(catalog.PublicVersions);
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("documents array", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldMarkVersionUnavailable_WhenSearchIndexPayloadRootIsNotAnObject()
    {
        var brokenTree = CreateExactTree("broken-search-root");
        File.WriteAllText(Path.Join(brokenTree, "search-index.json"), "[]");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var brokenVersion = Assert.Single(catalog.PublicVersions);
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("not a JSON object", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldMarkVersionUnavailable_WhenSearchIndexDocumentOmitsRequiredPathOrTitle()
    {
        var brokenTree = CreateExactTree("broken-search-document");
        File.WriteAllText(Path.Join(brokenTree, "search-index.json"), "{\"documents\":[{\"title\":\"Guide\"}]}");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var brokenVersion = Assert.Single(catalog.PublicVersions);
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("required path/title fields", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldParseDocumentedStringEnumValues()
    {
        var stableTree = CreateExactTree("stable");
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": "1.2.3",
              "versions": [
                {
                  "version": "1.2.3",
                  "label": "1.2.3 (Current)",
                  "exactTreePath": "{{EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree))}}",
                  "supportState": "Current",
                  "visibility": "Public",
                  "advisoryState": "SecurityRisk"
                }
              ]
            }
            """);

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal(AppSurfaceDocsVersionSupportState.Current, version.SupportState);
        Assert.Equal(AppSurfaceDocsVersionVisibility.Public, version.Visibility);
        Assert.Equal(AppSurfaceDocsVersionAdvisoryState.SecurityRisk, version.AdvisoryState);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldTreatNullCatalogPayloadAsEmptyCatalog()
    {
        var catalogPath = WriteRawCatalogJson("null");
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(catalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldTreatNullVersionsPayloadAsEmptyCatalog()
    {
        var catalogPath = WriteRawCatalogJson(
            """
            {
              "recommendedVersion": "1.2.3",
              "versions": null
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(catalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.Versions);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldSkipNullVersionEntries_AndContinueResolvingHealthyVersions()
    {
        var stableTree = CreateExactTree("null-entry-stable");
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": "1.2.3",
              "versions": [
                null,
                {
                  "version": "1.2.3",
                  "exactTreePath": "{{EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree))}}",
                  "supportState": "Current"
                }
              ]
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.2.3", version.Version);
        Assert.True(version.IsAvailable);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldSkipEntriesWithInvalidEnumValues_AndContinueResolvingHealthyVersions()
    {
        var stableTree = CreateExactTree("invalid-enum-stable");
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": "1.2.3",
              "versions": [
                {
                  "version": "9.9.9",
                  "exactTreePath": "{{EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree))}}",
                  "supportState": "NotARealSupportState"
                },
                {
                  "version": "1.2.3",
                  "exactTreePath": "{{EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree))}}",
                  "supportState": "Current",
                  "visibility": "Public",
                  "advisoryState": "None"
                }
              ]
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.2.3", version.Version);
        Assert.True(version.IsAvailable);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldReturnDisabled_WhenVersioningIsOff()
    {
        var service = CreateCatalogService(catalogPath: null, versioningEnabled: false);

        var catalog = service.GetCatalog();

        Assert.Same(AppSurfaceDocsResolvedVersionCatalog.Disabled, catalog);
        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Disabled, catalog.Status);
        Assert.Empty(catalog.PublicVersions);
    }

    [Fact]
    public void GetCatalog_ShouldReturnDisabled_WhenVersioningOptionsAreMissing()
    {
        var service = new AppSurfaceDocsVersionCatalogService(
            new AppSurfaceDocsOptions
            {
                Routing = new AppSurfaceDocsRoutingOptions { DocsRootPath = "/docs/next" },
                Versioning = null!
            },
            new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory },
            NullLogger<AppSurfaceDocsVersionCatalogService>.Instance);

        var catalog = service.GetCatalog();

        Assert.Same(AppSurfaceDocsResolvedVersionCatalog.Disabled, catalog);
        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Disabled, catalog.Status);
    }

    [Fact]
    public void GetCatalog_ShouldReturnEnabledWithoutCatalog_WhenCatalogPathIsMissing()
    {
        var service = CreateCatalogService(catalogPath: null);

        var catalog = service.GetCatalog();

        Assert.Same(AppSurfaceDocsResolvedVersionCatalog.EnabledWithoutCatalog, catalog);
        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.EnabledWithoutCatalog, catalog.Status);
        Assert.Empty(catalog.PublicVersions);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenCatalogFileDoesNotExist()
    {
        var service = CreateCatalogService("missing/catalog.json");

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(Path.GetFullPath(Path.Join(_tempDirectory, "missing/catalog.json")), catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenCatalogPathIsInvalid()
    {
        var invalidCatalogPath = "\0catalog.json";
        var service = CreateCatalogService(invalidCatalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(invalidCatalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenCatalogJsonIsMalformed()
    {
        var catalogPath = WriteRawCatalogJson("{");
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(catalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldMarkMissingExactTreeDirectoryAsUnavailable()
    {
        var missingTreePath = Path.Join(_tempDirectory, "missing-tree");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, missingTreePath),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Contains("does not exist", version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDirectory, version.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldMarkVersionUnavailable_WhenExactTreePathIsInvalid_AndKeepHealthyVersions()
    {
        var healthyTree = CreateExactTree("healthy-invalid-path-sibling");
        const string invalidPathMarker = "sensitive-invalid-path";
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = "\0" + invalidPathMarker,
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.9.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Maintained
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var brokenVersion = Assert.Single(catalog.PublicVersions, version => version.Version == "2.0.0");
        var healthyVersion = Assert.Single(catalog.PublicVersions, version => version.Version == "1.9.0");
        Assert.False(brokenVersion.IsAvailable);
        Assert.Contains("invalid", brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invalidPathMarker, brokenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
        Assert.True(healthyVersion.IsAvailable);
    }

    [Fact]
    public void GetCatalog_ShouldSkipBlankAndDuplicateVersions_AndIgnoreMissingRecommendedVersion()
    {
        var healthyTree = CreateExactTree("healthy");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "9.9.9",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "   ",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = null!,
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.2.0", version.Version);
    }

    [Fact]
    public void GetCatalog_ShouldIgnoreHiddenRecommendedVersion_AndTreatMissingExactTreePathAsUnavailable()
    {
        var hiddenTree = CreateExactTree("hidden");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "2.0.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = hiddenTree,
                        SupportState = AppSurfaceDocsVersionSupportState.Current,
                        Visibility = AppSurfaceDocsVersionVisibility.Hidden
                    },
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.9.0",
                        ExactTreePath = " ",
                        SupportState = AppSurfaceDocsVersionSupportState.Maintained,
                        Visibility = AppSurfaceDocsVersionVisibility.Public
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        Assert.Equal(2, catalog.Versions.Count);
        var unavailableVersion = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.9.0", unavailableVersion.Version);
        Assert.False(unavailableVersion.IsAvailable);
        Assert.Contains("missing", unavailableVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldValidateHiddenVersionsWithoutPromotingThemToPublicVersions()
    {
        var hiddenBrokenTree = Path.Join(_tempDirectory, "hidden-broken");
        Directory.CreateDirectory(hiddenBrokenTree);
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, hiddenBrokenTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Archived,
                        Visibility = AppSurfaceDocsVersionVisibility.Hidden
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Empty(catalog.PublicVersions);
        var hiddenVersion = Assert.Single(catalog.Versions);
        Assert.False(hiddenVersion.IsAvailable);
        Assert.Contains("index.html", hiddenVersion.AvailabilityIssue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCatalog_ShouldTrimExactTreePathBeforeResolvingRelativePath()
    {
        var stableTree = CreateExactTree("trimmed-tree");
        var catalogPath = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = $"  {Path.GetRelativePath(_tempDirectory, stableTree)}  ",
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.True(version.IsAvailable);
        Assert.Equal(stableTree, version.ExactTreePath);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldTrimCatalogPathBeforeResolvingRelativePath()
    {
        var stableTree = CreateExactTree("trimmed-catalog-path-tree");
        _ = WriteCatalog(
            new AppSurfaceDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new AppSurfaceDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, stableTree),
                        SupportState = AppSurfaceDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService("  catalog.json  ");

        var catalog = service.GetCatalog();

        Assert.Equal(Path.Join(_tempDirectory, "catalog.json"), catalog.CatalogPath);
        var version = Assert.Single(catalog.PublicVersions);
        Assert.True(version.IsAvailable);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenCatalogRootIsNotObjectOrNull()
    {
        var catalogPath = WriteRawCatalogJson("[]");
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(catalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenVersionsPayloadIsNotArray()
    {
        var catalogPath = WriteRawCatalogJson(
            """
            {
              "versions": {}
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(AppSurfaceDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(catalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
    }

    [Fact]
    public void GetCatalog_ShouldIgnoreInvalidRecommendedVersionMetadata_AndSkipNonObjectEntries()
    {
        var stableTree = CreateExactTree("stable-invalid-recommended");
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": 123,
              "versions": [
                "not-an-entry",
                {
                  "version": "1.2.3",
                  "exactTreePath": "{{EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree))}}"
                }
              ]
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.2.3", version.Version);
        Assert.True(version.IsAvailable);
    }

    [Theory]
    [InlineData("label", "false")]
    [InlineData("summary", "[]")]
    [InlineData("releaseManifestSha256", "[]")]
    [InlineData("visibility", "\"\"")]
    [InlineData("visibility", "\"NotAVisibility\"")]
    [InlineData("advisoryState", "\"NotAnAdvisory\"")]
    public void GetCatalog_ShouldSkipEntriesWithInvalidOptionalProperties(string propertyName, string invalidJsonValue)
    {
        var stableTree = CreateExactTree("stable-invalid-optional");
        var relativeTree = EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree));
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": "1.2.3",
              "versions": [
                {
                  "version": "9.9.9",
                  "exactTreePath": "{{relativeTree}}",
                  "{{propertyName}}": {{invalidJsonValue}}
                },
                {
                  "version": "1.2.3",
                  "exactTreePath": "{{relativeTree}}"
                }
              ]
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.2.3", version.Version);
        Assert.True(version.IsAvailable);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldSkipEntriesWithInvalidExactTreePathMetadata()
    {
        var stableTree = CreateExactTree("stable-invalid-exact-tree-path");
        var relativeTree = EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree));
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": "1.2.3",
              "versions": [
                {
                  "version": "9.9.9",
                  "exactTreePath": 42
                },
                {
                  "version": "1.2.3",
                  "exactTreePath": "{{relativeTree}}"
                }
              ]
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        var version = Assert.Single(catalog.PublicVersions);
        Assert.Equal("1.2.3", version.Version);
        Assert.True(version.IsAvailable);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldMarkPinnedArchiveUnavailable_WhenReleaseManifestVerificationFails()
    {
        var stableTree = CreateExactTree("stable-invalid-release-manifest");
        var relativeTree = EscapeJson(Path.GetRelativePath(_tempDirectory, stableTree));
        var catalogPath = WriteRawCatalogJson(
            $$"""
            {
              "recommendedVersion": "1.2.3",
              "versions": [
                {
                  "version": "1.2.3",
                  "exactTreePath": "{{relativeTree}}",
                  "releaseManifestSha256": "{{new string('0', 64)}}"
                }
              ]
            }
            """);
        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Null(catalog.RecommendedVersion);
        var version = Assert.Single(catalog.PublicVersions);
        Assert.False(version.IsAvailable);
        Assert.Equal(AppSurfaceDocsReleaseArchiveVerificationState.Unavailable, version.ArchiveVerificationState);
        Assert.Contains("ASDOCSARCHIVE", version.AvailabilityIssue, StringComparison.Ordinal);
        Assert.Null(version.VerifiedReleaseArchive);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private AppSurfaceDocsVersionCatalogService CreateCatalogService(
        string? catalogPath,
        bool versioningEnabled = true,
        string routeRootPath = "/docs",
        string docsRootPath = "/docs/next",
        string? trustedReleaseRootPath = null,
        ILogger<AppSurfaceDocsVersionCatalogService>? logger = null)
    {
        var options = new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                RouteRootPath = routeRootPath,
                DocsRootPath = docsRootPath
            },
            Versioning = new AppSurfaceDocsVersioningOptions
            {
                Enabled = versioningEnabled,
                CatalogPath = catalogPath,
                TrustedReleaseRootPath = trustedReleaseRootPath
            }
        };

        return new AppSurfaceDocsVersionCatalogService(
            options,
            new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory },
            logger ?? NullLogger<AppSurfaceDocsVersionCatalogService>.Instance);
    }

    private string CreateExactTree(string name)
    {
        var root = Path.Join(_tempDirectory, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, "index.html"), "<html>ok</html>");
        File.WriteAllText(Path.Join(root, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Join(root, "search-index.json"), "{\"documents\":[]}");
        File.WriteAllText(Path.Join(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Join(root, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Join(root, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Join(root, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
        return root;
    }

    private bool TryCreateSymbolicLinkTestFile(out string linkPath)
    {
        var targetPath = Path.Join(_tempDirectory, "symlink-target.txt");
        linkPath = Path.Join(_tempDirectory, "symlink-link.txt");
        try
        {
            File.WriteAllText(targetPath, "target");
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private bool TryCreateSymbolicLinkTestDirectory(out string targetPath, out string linkPath)
    {
        targetPath = Path.Join(_tempDirectory, $"symlink-target-{Guid.NewGuid():N}");
        linkPath = Path.Join(_tempDirectory, $"symlink-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(targetPath);
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void AssertWarningLogged(
        ILogger<AppSurfaceDocsVersionCatalogService> logger,
        string expectedMessageFragment)
    {
        var logged = Fake.GetCalls(logger)
            .Any(call => IsWarningLog(call, expectedMessageFragment));

        Assert.True(logged, $"Expected warning log containing '{expectedMessageFragment}'.");
    }

    private static bool IsWarningLog(FakeItEasy.Core.IFakeObjectCall call, string expectedMessageFragment)
    {
        if (call.Method.Name != nameof(ILogger.Log) || call.GetArgument<LogLevel>(0) != LogLevel.Warning)
        {
            return false;
        }

        var message = call.GetArgument<object>(2)?.ToString();
        return message?.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) == true;
    }

    private string WriteCatalog(AppSurfaceDocsVersionCatalog catalog)
    {
        var path = Path.Join(_tempDirectory, "catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(catalog));
        return path;
    }

    private string WriteRawCatalogJson(string json)
    {
        var path = Path.Join(_tempDirectory, "catalog.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AppSurfaceDocsTests";

        public IFileProvider WebRootFileProvider { get; set; } = null!;

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
