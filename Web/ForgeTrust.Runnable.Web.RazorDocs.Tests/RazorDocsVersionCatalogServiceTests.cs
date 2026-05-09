using System.Text.Json;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.Runnable.Web.RazorDocs.Tests;

[Trait("Category", "Unit")]
public sealed class RazorDocsVersionCatalogServiceTests : IDisposable
{
    private readonly string _tempDirectory;

    public RazorDocsVersionCatalogServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "razordocs-version-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void RazorDocsResolvedVersionCatalogStatus_ShouldPreserveNumericContract()
    {
        Assert.Equal(0, (int)RazorDocsResolvedVersionCatalogStatus.Resolved);
        Assert.Equal(1, (int)RazorDocsResolvedVersionCatalogStatus.Disabled);
        Assert.Equal(2, (int)RazorDocsResolvedVersionCatalogStatus.EnabledWithoutCatalog);
        Assert.Equal(3, (int)RazorDocsResolvedVersionCatalogStatus.Unavailable);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDependenciesAreNull()
    {
        var environment = new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory };
        var options = new RazorDocsOptions();

        Assert.Throws<ArgumentNullException>(() => new RazorDocsVersionCatalogService(null!, environment, NullLogger<RazorDocsVersionCatalogService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RazorDocsVersionCatalogService(options, null!, NullLogger<RazorDocsVersionCatalogService>.Instance));
        Assert.Throws<ArgumentNullException>(() => new RazorDocsVersionCatalogService(options, environment, null!));
    }

    [Fact]
    public void GetCatalog_ShouldResolveRelativeTreePaths_AndRecommendedVersion()
    {
        var stableTree = CreateExactTree("stable");
        var deprecatedTree = CreateExactTree("deprecated");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        Label = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, stableTree),
                        SupportState = RazorDocsVersionSupportState.Current
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.1.0",
                        Label = "1.1.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, deprecatedTree),
                        SupportState = RazorDocsVersionSupportState.Deprecated,
                        AdvisoryState = RazorDocsVersionAdvisoryState.Vulnerable
                    }
                ]
            });

        var service = CreateCatalogService(catalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.Resolved, catalog.Status);
        Assert.Equal(catalogPath, catalog.CatalogPath);
        var recommendedVersion = Assert.IsType<RazorDocsResolvedVersion>(catalog.RecommendedVersion);
        Assert.Equal("1.2.0", recommendedVersion.Version);
        Assert.Equal(2, catalog.PublicVersions.Count);
        Assert.All(catalog.PublicVersions, version => Assert.True(version.IsAvailable));
        Assert.Contains(catalog.PublicVersions, version => version.ExactRootUrl == "/docs/v/1.2.0");
        Assert.Contains(catalog.PublicVersions, version => version.AdvisoryState == RazorDocsVersionAdvisoryState.Vulnerable);
    }

    [Fact]
    public void GetCatalog_ShouldResolveExactRootUrlsFromConfiguredRouteRoot()
    {
        var stableTree = CreateExactTree("stable-custom-root");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, stableTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        var brokenTree = Path.Combine(_tempDirectory, "broken");
        Directory.CreateDirectory(brokenTree);
        File.WriteAllText(Path.Combine(brokenTree, "index.html"), "<html>broken</html>");
        File.WriteAllText(Path.Combine(brokenTree, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Combine(brokenTree, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(brokenTree, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Combine(brokenTree, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Combine(brokenTree, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");

        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "2.0.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = RazorDocsVersionSupportState.Current
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.9.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree),
                        SupportState = RazorDocsVersionSupportState.Maintained
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
        File.Delete(Path.Combine(brokenTree, "search-client.js"));
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        File.Delete(Path.Combine(exactTree, "outline-client.js"));
        File.WriteAllText(
            Path.Combine(exactTree, "api.html"),
            """<html><head><script src="/docs/outline-client.js"></script></head><body>API</body></html>""");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, exactTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        File.Delete(Path.Combine(exactTree, "outline-client.js"));
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, exactTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        File.WriteAllText(Path.Combine(brokenTree, "search-index.json"), "{");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        File.WriteAllText(Path.Combine(brokenTree, "search-index.json"), "{\"items\":[]}");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        File.WriteAllText(Path.Combine(brokenTree, "search-index.json"), "[]");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        File.WriteAllText(Path.Combine(brokenTree, "search-index.json"), "{\"documents\":[{\"title\":\"Guide\"}]}");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, brokenTree),
                        SupportState = RazorDocsVersionSupportState.Current
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
        Assert.Equal(RazorDocsVersionSupportState.Current, version.SupportState);
        Assert.Equal(RazorDocsVersionVisibility.Public, version.Visibility);
        Assert.Equal(RazorDocsVersionAdvisoryState.SecurityRisk, version.AdvisoryState);
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

        Assert.Same(RazorDocsResolvedVersionCatalog.Disabled, catalog);
        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.Disabled, catalog.Status);
        Assert.Empty(catalog.PublicVersions);
    }

    [Fact]
    public void GetCatalog_ShouldReturnDisabled_WhenVersioningOptionsAreMissing()
    {
        var service = new RazorDocsVersionCatalogService(
            new RazorDocsOptions
            {
                Routing = new RazorDocsRoutingOptions { DocsRootPath = "/docs/next" },
                Versioning = null!
            },
            new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory },
            NullLogger<RazorDocsVersionCatalogService>.Instance);

        var catalog = service.GetCatalog();

        Assert.Same(RazorDocsResolvedVersionCatalog.Disabled, catalog);
        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.Disabled, catalog.Status);
    }

    [Fact]
    public void GetCatalog_ShouldReturnEnabledWithoutCatalog_WhenCatalogPathIsMissing()
    {
        var service = CreateCatalogService(catalogPath: null);

        var catalog = service.GetCatalog();

        Assert.Same(RazorDocsResolvedVersionCatalog.EnabledWithoutCatalog, catalog);
        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.EnabledWithoutCatalog, catalog.Status);
        Assert.Empty(catalog.PublicVersions);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenCatalogFileDoesNotExist()
    {
        var service = CreateCatalogService("missing/catalog.json");

        var catalog = service.GetCatalog();

        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempDirectory, "missing/catalog.json")), catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldReturnUnavailable_WhenCatalogPathIsInvalid()
    {
        var invalidCatalogPath = "\0catalog.json";
        var service = CreateCatalogService(invalidCatalogPath);

        var catalog = service.GetCatalog();

        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
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

        Assert.Equal(RazorDocsResolvedVersionCatalogStatus.Unavailable, catalog.Status);
        Assert.Equal(catalogPath, catalog.CatalogPath);
        Assert.Empty(catalog.PublicVersions);
        Assert.Null(catalog.RecommendedVersion);
    }

    [Fact]
    public void GetCatalog_ShouldMarkMissingExactTreeDirectoryAsUnavailable()
    {
        var missingTreePath = Path.Combine(_tempDirectory, "missing-tree");
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, missingTreePath),
                        SupportState = RazorDocsVersionSupportState.Current
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
            new RazorDocsVersionCatalog
            {
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = "\0" + invalidPathMarker,
                        SupportState = RazorDocsVersionSupportState.Current
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.9.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree),
                        SupportState = RazorDocsVersionSupportState.Maintained
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
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "9.9.9",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "   ",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = null!,
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, healthyTree)
                    },
                    new RazorDocsPublishedVersion
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
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "2.0.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = hiddenTree,
                        SupportState = RazorDocsVersionSupportState.Current,
                        Visibility = RazorDocsVersionVisibility.Hidden
                    },
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.9.0",
                        ExactTreePath = " ",
                        SupportState = RazorDocsVersionSupportState.Maintained,
                        Visibility = RazorDocsVersionVisibility.Public
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
        var hiddenBrokenTree = Path.Combine(_tempDirectory, "hidden-broken");
        Directory.CreateDirectory(hiddenBrokenTree);
        var catalogPath = WriteCatalog(
            new RazorDocsVersionCatalog
            {
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "2.0.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, hiddenBrokenTree),
                        SupportState = RazorDocsVersionSupportState.Archived,
                        Visibility = RazorDocsVersionVisibility.Hidden
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
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = $"  {Path.GetRelativePath(_tempDirectory, stableTree)}  ",
                        SupportState = RazorDocsVersionSupportState.Current
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
            new RazorDocsVersionCatalog
            {
                RecommendedVersion = "1.2.0",
                Versions =
                [
                    new RazorDocsPublishedVersion
                    {
                        Version = "1.2.0",
                        ExactTreePath = Path.GetRelativePath(_tempDirectory, stableTree),
                        SupportState = RazorDocsVersionSupportState.Current
                    }
                ]
            });

        var service = CreateCatalogService("  catalog.json  ");

        var catalog = service.GetCatalog();

        Assert.Equal(Path.Combine(_tempDirectory, "catalog.json"), catalog.CatalogPath);
        var version = Assert.Single(catalog.PublicVersions);
        Assert.True(version.IsAvailable);
        Assert.NotNull(catalog.RecommendedVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private RazorDocsVersionCatalogService CreateCatalogService(
        string? catalogPath,
        bool versioningEnabled = true,
        string routeRootPath = "/docs",
        string docsRootPath = "/docs/next")
    {
        var options = new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = routeRootPath,
                DocsRootPath = docsRootPath
            },
            Versioning = new RazorDocsVersioningOptions
            {
                Enabled = versioningEnabled,
                CatalogPath = catalogPath
            }
        };

        return new RazorDocsVersionCatalogService(
            options,
            new TestWebHostEnvironment { ContentRootPath = _tempDirectory, WebRootPath = _tempDirectory },
            NullLogger<RazorDocsVersionCatalogService>.Instance);
    }

    private string CreateExactTree(string name)
    {
        var root = Path.Combine(_tempDirectory, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"), "<html>ok</html>");
        File.WriteAllText(Path.Combine(root, "search.html"), "<html>search</html>");
        File.WriteAllText(Path.Combine(root, "search-index.json"), "{\"documents\":[]}");
        File.WriteAllText(Path.Combine(root, "search.css"), "body { color: #fff; }");
        File.WriteAllText(Path.Combine(root, "search-client.js"), "window.__searchClientLoaded = true;");
        File.WriteAllText(Path.Combine(root, "outline-client.js"), "window.__outlineClientLoaded = true;");
        File.WriteAllText(Path.Combine(root, "minisearch.min.js"), "window.MiniSearch = window.MiniSearch || {};");
        return root;
    }

    private string WriteCatalog(RazorDocsVersionCatalog catalog)
    {
        var path = Path.Combine(_tempDirectory, "catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(catalog));
        return path;
    }

    private string WriteRawCatalogJson(string json)
    {
        var path = Path.Combine(_tempDirectory, "catalog.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "RazorDocsTests";

        public IFileProvider WebRootFileProvider { get; set; } = null!;

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
