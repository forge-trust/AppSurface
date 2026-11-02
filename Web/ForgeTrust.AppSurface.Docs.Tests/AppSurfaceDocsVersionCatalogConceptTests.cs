using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.ReleaseContracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed partial class AppSurfaceDocsVersionCatalogServiceTests
{
    [Fact]
    public void ConceptCatalog_LoadsDetachedSidecar_AndValidatesMapReferencesFromEitherDescriptor()
    {
        var root = CreateConceptTree("concept-valid");
        var map = JsonSerializer.SerializeToUtf8Bytes(new { schema = "appsurface-docs-concept-map-v1", concepts = Array.Empty<object>() });
        File.WriteAllBytes(Path.Combine(_tempDirectory, "bridge.json"), map);
        var mapPin = Convert.ToHexString(SHA256.HashData(map)).ToLowerInvariant();
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new
        {
            versions = new[] { new { version = "1", exactTreePath = "concept-valid", releaseManifestSha256 = pin, conceptMapPath = "bridge.json", conceptMapSha256 = mapPin } }
        }));
        var service = CreateCatalogService(path);
        var catalog = service.GetCatalog();
        Assert.True(Assert.Single(catalog.Versions).IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.NotNull(catalog.ConceptSwitching);
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-concepts.json"), "broken after startup");
        File.WriteAllText(Path.Combine(_tempDirectory, "bridge.json"), "changed after startup");
        Assert.Same(catalog, service.GetCatalog());
    }

    [Theory]
    [InlineData("{\"schema\":\"wrong\"}")]
    [InlineData("bad json")]
    [InlineData("")]
    public void ConceptCatalog_BadOptionalIndexPreservesVerifiedArchiveAvailability(string contents)
    {
        var root = CreateConceptTree("concept-invalid");
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-concepts.json"), contents);
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new { versions = new[] { new { version = "1", exactTreePath = "concept-invalid", releaseManifestSha256 = pin } } }));
        var catalog = CreateCatalogService(path).GetCatalog();
        Assert.True(Assert.Single(catalog.Versions).IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.NotNull(catalog.ConceptSwitching);
    }

    [Theory]
    [InlineData("bridge.json", null)]
    [InlineData(null, "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("../bridge.json", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("missing.json", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("bridge.json", "bad")]
    public void ConceptCatalog_InvalidOptionalBridgeCannotDisableHealthyArchive(string? mapPath, string? mapPin)
    {
        var root = CreateConceptTree("concept-map-invalid");
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new
        {
            versions = new[] { new { version = "1", exactTreePath = "concept-map-invalid", releaseManifestSha256 = pin, conceptMapPath = mapPath, conceptMapSha256 = mapPin } }
        }));
        var catalog = CreateCatalogService(path).GetCatalog();
        Assert.True(Assert.Single(catalog.Versions).IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.NotNull(catalog.ConceptSwitching);
    }

    [Fact]
    public void ConceptCatalog_NonStringMapAndUnavailableReleaseAreSafeStartupData()
    {
        var root = CreateConceptTree("concept-shape");
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new
        {
            versions = new object[]
            {
                new { version = "1", exactTreePath = "concept-shape", releaseManifestSha256 = pin, conceptMapPath = 3, conceptMapSha256 = true },
                new { version = "2", exactTreePath = "missing", releaseManifestSha256 = (string?)null }
            }
        }));
        var catalog = CreateCatalogService(path).GetCatalog();
        Assert.Equal(2, catalog.Versions.Count);
        Assert.True(catalog.Versions[0].IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.False(catalog.Versions[1].IsAvailable);
        Assert.NotNull(catalog.ConceptSwitching);
    }

    [Fact]
    public async Task ConceptCatalog_VerifiesLegacyCanonicalCandidatesWithoutInferringAliases()
    {
        var root = CreateExactTree("concept-legacy");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs/guide.html"), "<html><h1>Legacy guide</h1></html>");
        File.WriteAllText(Path.Combine(root, "root-guide.html"), "<html><h1>Root guide</h1></html>");
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-route-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "appsurface-docs-route-manifest-v1",
            entries = new[]
            {
                new { sourcePath = "guide.md", canonicalRoutePath = "guide", recoveryAliases = Array.Empty<string>(), declaredAliases = new[] { "old-guide" } },
                new { sourcePath = "root-guide.md", canonicalRoutePath = "root-guide", recoveryAliases = Array.Empty<string>(), declaredAliases = Array.Empty<string>() },
                new { sourcePath = "missing.md", canonicalRoutePath = "missing", recoveryAliases = Array.Empty<string>(), declaredAliases = Array.Empty<string>() }
            }
        }));
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new { versions = new[] { new { version = "1", exactTreePath = "concept-legacy", releaseManifestSha256 = pin } } }));
        var catalog = CreateCatalogService(path).GetCatalog();
        Assert.True(Assert.Single(catalog.Versions).IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.NotNull(catalog.ConceptSwitching);
        var switching = Assert.IsType<AppSurfaceDocsConceptSwitching>(catalog.ConceptSwitching);
        Assert.Equal(["guide", "root-guide", "missing"], catalog.Versions[0].VerifiedReleaseArchive!.FrozenRouteManifest.CanonicalRoutePaths);

        static async Task<int> RequestStatusAsync(AppSurfaceDocsConceptSwitching switching, string sourcePin, string route)
        {
            var source = new DocConceptPageIdentity("1", sourcePin, route, null, LocaleIsKnown: false);
            var key = DocConceptIdentityIndex.CreateSourceKey("Default", source);
            var context = new DefaultHttpContext();
            context.Request.Path = "/docs/versions/switch/" + key + ".html";
            context.Response.Body = new MemoryStream();
            Assert.True(await switching.TryHandleAsync(context, NullLogger.Instance));
            return context.Response.StatusCode;
        }

        Assert.Equal(StatusCodes.Status200OK, await RequestStatusAsync(switching, pin, "guide"));
        Assert.Equal(StatusCodes.Status200OK, await RequestStatusAsync(switching, pin, "root-guide"));
        Assert.Equal(StatusCodes.Status404NotFound, await RequestStatusAsync(switching, pin, "missing"));
    }

    [Fact]
    public void ConceptCatalog_InvalidPinnedSidecarConceptIdIsDiagnosedWithoutDisablingArchive()
    {
        var root = CreateConceptTree("concept-invalid-id");
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-concepts.json"), JsonSerializer.Serialize(new
        {
            schema = "appsurface-docs-concepts-v1",
            identityCoverage = "complete",
            pages = new[] { new { canonicalRoutePath = "guide", locale = (string?)null, conceptId = "../bad", title = "Guide", anchorIds = Array.Empty<string>() } }
        }));
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new
        {
            versions = new[] { new { version = "1", exactTreePath = "concept-invalid-id", releaseManifestSha256 = pin } }
        }));
        var logger = new CapturingLogger<AppSurfaceDocsVersionCatalogService>();

        var catalog = CreateCatalogService(path, logger: logger).GetCatalog();

        Assert.True(Assert.Single(catalog.Versions).IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.NotNull(catalog.ConceptSwitching);
        Assert.Contains(logger.Messages, message => message.Contains("docs-concept-id-invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void ConceptCatalog_OversizedOptionalIndexDoesNotChangeExistingInputRejectionBoundary()
    {
        var root = CreateConceptTree("concept-index-overflow");
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-concepts.json"), new string(' ', 5000));
        var pin = WriteReleaseManifest(root);
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new { versions = new[] { new { version = "1", exactTreePath = "concept-index-overflow", releaseManifestSha256 = pin } } }));
        var catalog = CreateCatalogService(path, maxRewrittenFileSizeBytes: 1000).GetCatalog();
        Assert.True(Assert.Single(catalog.Versions).IsAvailable, catalog.Versions[0].AvailabilityIssue);
        Assert.NotNull(catalog.ConceptSwitching);
    }

    [Fact]
    public void ConceptCatalog_HiddenBridgeParticipatesWithoutMakingHiddenVersionPublic()
    {
        var first = CreateConceptTree("concept-public");
        var second = CreateConceptTree("concept-hidden");
        var pin1 = WriteReleaseManifest(first);
        var pin2 = WriteReleaseManifest(second);
        File.WriteAllText(Path.Combine(_tempDirectory, "bad-bridge.json"), "{}");
        var path = WriteRawCatalogJson(JsonSerializer.Serialize(new
        {
            versions = new object[]
            {
                new { version = "1", exactTreePath = "concept-public", releaseManifestSha256 = pin1 },
                new { version = "hidden", visibility = "Hidden", exactTreePath = "concept-hidden", releaseManifestSha256 = pin2, conceptMapPath = "bad-bridge.json", conceptMapSha256 = new string('0', 64) }
            }
        }));
        var catalog = CreateCatalogService(path).GetCatalog();
        Assert.Equal(2, catalog.Versions.Count);
        Assert.Single(catalog.PublicVersions);
        Assert.NotNull(catalog.ConceptSwitching);
    }

    private string CreateConceptTree(string name)
    {
        var root = CreateExactTree(name);
        File.WriteAllText(Path.Combine(root, "guide.html"), "<html><head><title>Guide</title></head><body><main><h1>Guide</h1></main></body></html>");
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-route-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "appsurface-docs-route-manifest-v1",
            htmlUrlBase = "export-root",
            entries = new[] { new { sourcePath = "guide.md", canonicalRoutePath = "guide", canonicalArtifactPath = "guide.html", recoveryAliases = Array.Empty<string>(), declaredAliases = Array.Empty<string>() } }
        }));
        File.WriteAllText(Path.Combine(root, ".appsurface-docs-concepts.json"), JsonSerializer.Serialize(new
        {
            schema = "appsurface-docs-concepts-v1",
            identityCoverage = "complete",
            pages = new[] { new { canonicalRoutePath = "guide", locale = (string?)null, conceptId = "guide", title = "Guide", anchorIds = Array.Empty<string>() } }
        }));
        return root;
    }
}
