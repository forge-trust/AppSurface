using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class FrozenArtifactRouteManifestTests : IDisposable
{
    private readonly string _tempDirectory;

    public FrozenArtifactRouteManifestTests()
    {
        _tempDirectory = TestPathUtils.PathUnder(
            Path.GetTempPath(),
            "appsurface-docs-frozen-artifact-route-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void TryLoadVerified_ShouldLoadExportRootMappingsAndPreserveLogicalIdentityAndAliases()
    {
        const string json =
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "htmlUrlBase": "export-root",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide#overview",
                  "recoveryAliases": [ "guide.md" ],
                  "declaredAliases": [ "legacy-guide" ],
                  "canonicalArtifactPath": "docs/guide.html"
                },
                {
                  "sourcePath": "README.md",
                  "canonicalRoutePath": "",
                  "recoveryAliases": [ "README.md" ],
                  "declaredAliases": [],
                  "canonicalArtifactPath": "index.html"
                }
              ]
            }
            """;

        var loaded = AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            Encoding.UTF8.GetBytes(json),
            out var manifest,
            out var issue);

        Assert.True(loaded, issue);
        Assert.True(manifest.HtmlUsesExportRootUrls);
        Assert.Equal(
            new[] { "docs/guide.html", "index.html" },
            manifest.CanonicalArtifactPaths.OrderBy(path => path, StringComparer.Ordinal));
        Assert.True(manifest.TryResolveAlias("legacy-guide", out var canonicalRoute));
        Assert.Equal("guide#overview", canonicalRoute);
        Assert.True(manifest.TryResolveCanonicalArtifactPath("guide#caller-fragment", out var guideArtifact));
        Assert.Equal("docs/guide.html#overview", guideArtifact);
        Assert.True(manifest.TryResolveCanonicalArtifactPath(string.Empty, out var rootArtifact));
        Assert.Equal("index.html", rootArtifact);
        Assert.False(manifest.TryResolveCanonicalArtifactPath("missing", out _));
    }

    [Fact]
    public void Load_ShouldKeepLegacyAliasesWhenOptionalArtifactFieldsAreAbsent()
    {
        const string json =
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide#overview",
                  "recoveryAliases": [ "guide.md" ],
                  "declaredAliases": [ "legacy-guide" ]
                }
              ]
            }
            """;
        var manifest = Load(json);

        Assert.False(manifest.HtmlUsesExportRootUrls);
        Assert.Empty(manifest.CanonicalArtifactPaths);
        Assert.True(manifest.TryResolveAlias("legacy-guide", out var canonicalRoute));
        Assert.Equal("guide#overview", canonicalRoute);

        var verified = AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            Encoding.UTF8.GetBytes(json),
            out var verifiedManifest,
            out var issue);

        Assert.True(verified, issue);
        Assert.False(verifiedManifest.HtmlUsesExportRootUrls);
        Assert.Empty(verifiedManifest.CanonicalArtifactPaths);
        Assert.True(verifiedManifest.TryResolveAlias("guide.md", out canonicalRoute));
        Assert.Equal("guide#overview", canonicalRoute);
    }

    [Fact]
    public void SerializeForExport_ShouldStageLiveUrlsWhileSerializeKeepsLegacyV1Shape()
    {
        var routeManifest = CreateLiveRouteManifest();

        var legacyJson = AppSurfaceDocsFrozenRouteManifest.Serialize(routeManifest);
        var stagedJson = AppSurfaceDocsFrozenRouteManifest.SerializeForExport(routeManifest);

        Assert.Contains("\"canonicalRoutePath\": \"guide#overview\"", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"htmlUrlBase\"", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"canonicalArtifactPath\"", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"exportCanonicalLiveUrl\"", legacyJson, StringComparison.Ordinal);
        Assert.Contains("\"exportCanonicalLiveUrl\": \"/docs/guide#overview\"", stagedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"htmlUrlBase\"", stagedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"canonicalArtifactPath\"", stagedJson, StringComparison.Ordinal);

        var loaded = AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            Encoding.UTF8.GetBytes(stagedJson),
            out var manifest,
            out var issue);

        Assert.False(loaded);
        Assert.Same(AppSurfaceDocsFrozenRouteManifest.Empty, manifest);
        Assert.DoesNotContain("/docs/guide#overview", issue, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidModernManifests))]
    public void TryLoadVerified_ShouldRejectIncompleteModesStagingAndNamespaceCollisions(
        string scenario,
        string json)
    {
        var loaded = AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            Encoding.UTF8.GetBytes(json),
            out var manifest,
            out var issue);

        Assert.False(loaded);
        Assert.Same(AppSurfaceDocsFrozenRouteManifest.Empty, manifest);
        Assert.False(string.IsNullOrWhiteSpace(issue), scenario);
    }

    [Theory]
    [MemberData(nameof(UnsafeArtifactPaths))]
    public void TryLoadVerified_ShouldRejectUnsafeCanonicalArtifactPaths(string artifactPath)
    {
        var json = CreateManifest(
            "export-root",
            new EntrySpec("guide", artifactPath, [], []));

        var loaded = AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            Encoding.UTF8.GetBytes(json),
            out var manifest,
            out var issue);

        Assert.False(loaded);
        Assert.Same(AppSurfaceDocsFrozenRouteManifest.Empty, manifest);
        Assert.False(string.IsNullOrWhiteSpace(issue));
        Assert.DoesNotContain(artifactPath, issue, StringComparison.Ordinal);
        Assert.DoesNotContain(issue, char.IsControl);
    }

    [Theory]
    [MemberData(nameof(ModernControlRouteManifests))]
    public void TryLoadVerified_ShouldRejectModernRouteControlsWithoutEchoingThem(
        string json,
        string unsafeRouteText)
    {
        var loaded = AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(
            Encoding.UTF8.GetBytes(json),
            out var manifest,
            out var issue);

        Assert.False(loaded);
        Assert.Same(AppSurfaceDocsFrozenRouteManifest.Empty, manifest);
        Assert.False(string.IsNullOrWhiteSpace(issue));
        Assert.DoesNotContain(issue, char.IsControl);
        Assert.DoesNotContain(unsafeRouteText, issue, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ShouldFailClosedForAnInvalidModernManifestInsteadOfKeepingValidAliases()
    {
        var json = CreateManifest(
            "export-root",
            new EntrySpec("guide", "docs/guide.html", ["legacy-guide"], []),
            new EntrySpec("other", null, ["legacy-other"], []));

        var manifest = Load(json);

        Assert.Same(AppSurfaceDocsFrozenRouteManifest.Empty, manifest);
        Assert.False(manifest.HtmlUsesExportRootUrls);
        Assert.False(manifest.TryResolveAlias("legacy-guide", out _));
    }

    [Fact]
    public void Load_ShouldRetainLegacyControlPathSemantics()
    {
        const string json =
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide#advanced\nfragment",
                  "recoveryAliases": [ "legacy\nsource" ],
                  "declaredAliases": []
                }
              ]
            }
            """;

        var manifest = Load(json);

        Assert.False(manifest.HtmlUsesExportRootUrls);
        Assert.True(manifest.TryResolveAlias("legacy\nsource", out var canonicalRoute));
        Assert.Equal("guide#advanced\nfragment", canonicalRoute);
    }

    public static TheoryData<string, string> InvalidModernManifests
    {
        get
        {
            var first = new EntrySpec("guide", "docs/guide.html", ["legacy-guide"], []);
            return new TheoryData<string, string>
            {
                {
                    "partial mapping",
                    CreateManifest(
                        "export-root",
                        first,
                        new EntrySpec("other", null, ["legacy-other"], []))
                },
                {
                    "unknown URL base",
                    CreateManifest("logical-docs-root", first)
                },
                {
                    "missing URL base",
                    CreateManifest(null, first)
                },
                {
                    "alias collides with artifact",
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", ["docs/guide.html"], []))
                },
                {
                    "duplicate artifacts",
                    CreateManifest(
                        "export-root",
                        first,
                        new EntrySpec("other", "docs/guide.html", ["legacy-other"], []))
                },
                {
                    "alias collides with canonical route",
                    CreateManifest(
                        "export-root",
                        first,
                        new EntrySpec("other", "docs/other.html", ["guide"], []))
                },
                {
                    "unfinished exporter staging",
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", ["legacy-guide"], [], "/docs/guide"))
                },
                {
                    "present null exporter staging",
                    """
                    {
                      "schema": "appsurface-docs-route-manifest-v1",
                      "htmlUrlBase": "export-root",
                      "entries": [
                        {
                          "sourcePath": "guide.md",
                          "canonicalRoutePath": "guide#overview",
                          "recoveryAliases": [ "legacy-guide" ],
                          "declaredAliases": [],
                          "canonicalArtifactPath": "docs/guide.html",
                          "exportCanonicalLiveUrl": null
                        }
                      ]
                    }
                    """
                },
                {
                    "empty alias path",
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", [""], []))
                },
                {
                    "fragment-only alias path",
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", ["#orphan"], []))
                },
                {
                    "aliases duplicate after fragment removal",
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", ["legacy#one", "legacy#two"], []))
                }
            };
        }
    }

    public static TheoryData<string> UnsafeArtifactPaths
    {
        get
        {
            return new TheoryData<string>
            {
                "/docs/guide.html",
                "//cdn.example.test/guide.html",
                @"docs\guide.html",
                "C:docs/guide.html",
                "docs/guide.html?next=other",
                "docs/guide.html#overview",
                "docs/\u0001guide.html",
                "docs//guide.html",
                ".private/guide.html",
                "docs/../guide.html",
                "docs/%2e%2e/guide.html",
                "docs/guide.txt"
            };
        }
    }

    public static TheoryData<string, string> ModernControlRouteManifests
    {
        get
        {
            return new TheoryData<string, string>
            {
                {
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide#advanced\nfragment", "docs/guide.html", [], [])),
                    "advanced"
                },
                {
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", ["legacy\nsource"], [])),
                    "legacy"
                },
                {
                    CreateManifest(
                        "export-root",
                        new EntrySpec("\nguide", "docs/guide.html", [], [])),
                    "guide"
                },
                {
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide", "docs/guide.html", ["legacy\n"], [])),
                    "legacy"
                },
                {
                    CreateManifest(
                        "export-root",
                        new EntrySpec("guide?\nquery", "docs/guide.html", [], [])),
                    "guide"
                }
            };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private AppSurfaceDocsFrozenRouteManifest Load(string json)
    {
        File.WriteAllText(
            TestPathUtils.PathUnder(_tempDirectory, AppSurfaceDocsFrozenRouteManifest.FileName),
            json);
        using var provider = new PhysicalFileProvider(_tempDirectory, ExclusionFilters.None);
        return AppSurfaceDocsFrozenRouteManifest.Load(
            provider,
            NullLogger<FrozenArtifactRouteManifestTests>.Instance,
            _tempDirectory);
    }

    private static string CreateManifest(string? htmlUrlBase, params EntrySpec[] entries)
    {
        var serializedEntries = entries.Select(
            entry => new
            {
                sourcePath = entry.SourcePath,
                canonicalRoutePath = entry.CanonicalRoutePath,
                recoveryAliases = entry.RecoveryAliases,
                declaredAliases = entry.DeclaredAliases,
                canonicalArtifactPath = entry.CanonicalArtifactPath,
                exportCanonicalLiveUrl = entry.ExportCanonicalLiveUrl
            }).ToArray();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        object document = htmlUrlBase is null
            ? new
            {
                schema = "appsurface-docs-route-manifest-v1",
                entries = serializedEntries
            }
            : new
            {
                schema = "appsurface-docs-route-manifest-v1",
                htmlUrlBase,
                entries = serializedEntries
            };
        return JsonSerializer.Serialize(document, options);
    }

    private static AppSurfaceDocsRouteManifest CreateLiveRouteManifest()
    {
        return new AppSurfaceDocsRouteManifest(
            [
                new AppSurfaceDocsRouteManifestEntry(
                    "guide.md",
                    "guide#overview",
                    "/docs/guide#overview",
                    [
                        new AppSurfaceDocsRouteAlias(
                            "guide.md",
                            "/docs/guide.md",
                            AppSurfaceDocsRouteAliasKind.MarkdownSource)
                    ],
                    [
                        new AppSurfaceDocsRouteAlias(
                            "legacy-guide",
                            "/docs/legacy-guide",
                            AppSurfaceDocsRouteAliasKind.DeclaredRedirect)
                    ],
                    SourcePathIsMarkdown: true)
            ],
            []);
    }

    private sealed record EntrySpec(
        string SourcePath,
        string CanonicalRoutePath,
        string? CanonicalArtifactPath,
        string[] RecoveryAliases,
        string[] DeclaredAliases,
        string? ExportCanonicalLiveUrl = null)
    {
        public EntrySpec(
            string canonicalRoutePath,
            string? canonicalArtifactPath,
            string[] recoveryAliases,
            string[] declaredAliases,
            string? exportCanonicalLiveUrl = null)
            : this(
                canonicalRoutePath + ".md",
                canonicalRoutePath,
                canonicalArtifactPath,
                recoveryAliases,
                declaredAliases,
                exportCanonicalLiveUrl)
        {
        }
    }
}
