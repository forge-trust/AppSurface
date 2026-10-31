using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseDocsConceptCoverageTests : IDisposable
{
    private const string ReleaseManifestName = ".appsurface-docs-release-manifest.json";
    private const string RouteManifestName = ".appsurface-docs-route-manifest.json";
    private const string ConceptIndexName = ".appsurface-docs-concepts.json";
    private readonly string _root = TestPathUtils.PathUnder(Path.GetTempPath(), "release-docs-concept-coverage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateAsync_RejectsMalformedCatalogWithoutReplacingPriorOutput()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.Root, "versions.json"), "{ not-json");
        var sourceSnapshot = SnapshotFiles(fixture.Root);
        var outputRoot = ExistingOutput("malformed-catalog");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.Root));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"not-an-object\"")]
    [InlineData("{\"schema\":\"missing-versions\"}")]
    [InlineData("{\"versions\":null}")]
    [InlineData("{\"versions\":{}}")]
    [InlineData("{\"versions\":\"not-an-array\"}")]
    [InlineData("{\"versions\":7}")]
    public async Task GenerateAsync_RejectsCatalogRootsAndVersionsFieldsWithWrongJsonShapes(string catalogJson)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        await WriteCatalogAsync(fixture, catalogJson);
        var outputRoot = ExistingOutput("malformed-catalog-shape-" + Guid.NewGuid().ToString("N"));
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_SkipsNonObjectAndBlankVersionCatalogEntries()
    {
        var fixture = await CreateFixtureAsync([]);
        await WriteCatalogAsync(fixture, "{\"versions\":[null,7,\"ignored\",{\"version\":\"  \"}]}");
        var outputRoot = TestPathUtils.PathUnder(_root, "ignored-catalog-entries");

        var result = await GenerateAsync(fixture, outputRoot);

        Assert.Equal(0, result.GeneratedFileCount);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_ReturnsEmptyWhenCatalogHasNoReleaseEntries()
    {
        var fixture = await CreateFixtureAsync([]);
        var outputRoot = TestPathUtils.PathUnder(_root, "empty-catalog-output");

        var result = await GenerateAsync(fixture, outputRoot);

        Assert.Equal(outputRoot, result.RootPath);
        Assert.Equal(0, result.GeneratedFileCount);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RejectsSelectedReleaseWhoseExactTreeIsMissingWithoutReplacingPriorOutput()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        await SetCatalogFieldAsync(fixture, "1.0.0", "exactTreePath", JsonValue.Create("releases/missing"));
        var outputRoot = ExistingOutput("missing-selected-tree");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Root, "releases", "missing")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/outside")]
    [InlineData("releases/../outside")]
    [InlineData("releases/%2e%2e/outside")]
    [InlineData("releases/one?query")]
    [InlineData("releases/a:b")]
    [InlineData(".hidden/releases/1.0.0")]
    [InlineData("releases\\..\\outside")]
    public async Task GenerateAsync_RejectsUnsafeCatalogTreePathsWithoutReplacingPriorOutput(string exactTreePath)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        await SetCatalogFieldAsync(fixture, "1.0.0", "exactTreePath", JsonValue.Create(exactTreePath));
        var outputRoot = ExistingOutput("unsafe-tree-path-" + Guid.NewGuid().ToString("N"));
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Theory]
    [InlineData("version", "null")]
    [InlineData("version", "{}")]
    [InlineData("version", "17")]
    [InlineData("exactTreePath", "null")]
    [InlineData("exactTreePath", "[]")]
    [InlineData("exactTreePath", "17")]
    [InlineData("releaseManifestSha256", "null")]
    [InlineData("releaseManifestSha256", "{}")]
    [InlineData("releaseManifestSha256", "17")]
    public async Task GenerateAsync_RejectsMalformedRequiredCatalogStringsWithoutReplacingPriorOutput(string field, string jsonValue)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        await SetCatalogFieldAsync(fixture, "1.0.0", field, JsonNode.Parse(jsonValue));
        var outputRoot = ExistingOutput("malformed-required-field-" + field + "-" + Guid.NewGuid().ToString("N"));
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Theory]
    [InlineData("path-without-pin")]
    [InlineData("pin-without-path")]
    [InlineData("short-pin")]
    [InlineData("nonhex-pin")]
    [InlineData("unsafe-path")]
    [InlineData("digest-mismatch")]
    public async Task GenerateAsync_RejectsIncompleteOrInvalidOptionalBridgePinsWithoutReplacingPriorOutput(string shape)
    {
        var fixture = shape == "digest-mismatch"
            ? await CreateFixtureAsync([SingleRelease()], new Bridge("setup", "1.0.0",
                [new Membership("1.0.0", "page", "guides/start")]))
            : await CreateFixtureAsync([SingleRelease()]);
        if (shape is "path-without-pin")
        {
            await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapPath", JsonValue.Create("bridges/concepts.json"));
        }
        else if (shape is "pin-without-path")
        {
            await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapSha256", JsonValue.Create(new string('0', 64)));
        }
        else if (shape == "digest-mismatch")
        {
            await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapSha256", JsonValue.Create(new string('0', 64)));
        }
        else
        {
            var path = shape == "unsafe-path" ? "../outside.json" : "bridges/concepts.json";
            var pin = shape switch
            {
                "short-pin" => "abc",
                "nonhex-pin" => new string('g', 64),
                _ => new string('0', 64)
            };
            await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapPath", JsonValue.Create(path));
            await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapSha256", JsonValue.Create(pin));
        }

        var outputRoot = ExistingOutput("invalid-optional-bridge-" + shape);
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Theory]
    [InlineData("missing-tree")]
    [InlineData("tampered-archive")]
    public async Task GenerateAsync_RepresentsUnavailableUnselectedReleaseWithoutMountingIt(string availabilityFailure)
    {
        var fixture = await CreateFixtureAsync(
        [
            SingleRelease(),
            new ReleaseInput("2.0.0",
            [
                new PageInput("guides/start", "export/start.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>")
            ])]);
        if (availabilityFailure == "missing-tree")
        {
            await SetCatalogFieldAsync(fixture, "2.0.0", "exactTreePath", JsonValue.Create("releases/missing-target"));
        }
        else
        {
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.Root, "releases", "2.0.0", "export", "start.html"),
                "tampered bytes no longer match the pinned archive manifest");
        }
        var outputRoot = TestPathUtils.PathUnder(_root, "unavailable-unselected-release");

        var result = await GenerateAsync(fixture, outputRoot);

        Assert.Equal(new[] { "1.0.0" }, result.VerifiedVersions);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(outputRoot, "docs", "v", "2.0.0")));
        var choice = await ReadChoiceAsync(fixture, outputRoot, "1.0.0", "guides/start");
        var targetChoice = Assert.Single(choice.QuerySelectorAll("main section"), section =>
            section.TextContent.Contains("2.0.0", StringComparison.Ordinal));
        Assert.Contains("This version is unavailable", targetChoice.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(targetChoice.QuerySelectorAll("a[href]"), link =>
            link.GetAttribute("href")!.StartsWith("/docs/v/2.0.0/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "docs/v/1.0.0/export/start.html")]
    [InlineData("   ", "docs/v/1.0.0/export/start.html")]
    [InlineData("/", "v/1.0.0/export/start.html")]
    public async Task GenerateAsync_NormalizesEmptyAndRootRouteRoots(string routeRoot, string mountedArtifact)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var outputRoot = TestPathUtils.PathUnder(_root, "normalized-route-root-" + Guid.NewGuid().ToString("N"));

        var result = await GenerateAsync(fixture, outputRoot, routeRoot: routeRoot);

        Assert.Contains("1.0.0", result.VerifiedVersions);
        Assert.True(File.Exists(TestPathUtils.PathUnder(outputRoot, mountedArtifact.Split('/'))));
    }

    [Fact]
    public async Task GenerateAsync_RejectsUnsupportedModernRouteProjectionWithoutReplacingPriorOutput()
    {
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0", [
            new PageInput("guides/start", "export/start.html", "en", "setup")
        ], HtmlUrlBase: "absolute-url")]);
        var outputRoot = ExistingOutput("unsupported-route-projection");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_ReplacesAbandonedStagingDirectoryBeforePublishing()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var outputRoot = TestPathUtils.PathUnder(_root, "recover-stale-staging");
        var stagingRoot = outputRoot + ".switch-staging";
        Directory.CreateDirectory(stagingRoot);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(stagingRoot, "partial-generation.txt"), "stale");

        var result = await GenerateAsync(fixture, outputRoot);

        Assert.True(File.Exists(TestPathUtils.PathUnder(outputRoot, "docs", "v", "1.0.0", "export", "start.html")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(outputRoot, "partial-generation.txt")));
        Assert.False(Directory.Exists(stagingRoot));
        Assert.True(File.Exists(result.InventoryPath));
    }

    [Fact]
    public async Task GenerateAsync_InfersLegacyArtifactCandidatesAndRebasesOnlyLegacyRootedResources()
    {
        const string guideHtml = "<html><head><link rel=\"stylesheet\" href=\"/docs/theme.css\">"
            + "<meta http-equiv=\"refresh\" content=\"0; URL=/docs/contact\">"
            + "<link rel=\"canonical\" href=\"/docs/guides/start\"></head>"
            + "<body><h1>Guide</h1><img src=\"//cdn.example/logo.svg\"><a href=\"relative.html\">relative</a>"
            + "<form action=\"/docs/contact\"></form><img src=\"/global.svg\"></body></html>";
        const string statusHtml = "<html><head><link rel=\"canonical\" href=\"https://manual.example/status\">"
            + "<meta http-equiv=\"refresh\" content=\"5\"></head><body><h1>Status</h1></body></html>";
        var fixture = await CreateFixtureAsync(
        [
            new ReleaseInput("1.0.0",
            [
                new PageInput("", "docs/index.html", "en", "home", "Home"),
                new PageInput("status.html", "status.html", "en", "status", "Status", Html: statusHtml),
                new PageInput("guides/start", "guides/start/index.html", "en", "start", "Start", Html: guideHtml),
                new PageInput("legacy/path", "docs/legacy/path.html", "en", "legacy", "Legacy"),
                new PageInput("guides/orphan", "missing/orphan", "en", null, "Orphan", WriteArtifact: false)
            ], LegacyRouteManifest: true, IncludeSidecar: false)
        ]);
        var outputRoot = TestPathUtils.PathUnder(_root, "legacy-artifact-inference");

        var result = await GenerateAsync(fixture, outputRoot);

        var inferredRoot = TestPathUtils.PathUnder(outputRoot, "docs", "v", "1.0.0", "docs", "index.html");
        var inferredHtmlFile = TestPathUtils.PathUnder(outputRoot, "docs", "v", "1.0.0", "status.html");
        var inferredIndexFile = TestPathUtils.PathUnder(outputRoot, "docs", "v", "1.0.0", "guides", "start", "index.html");
        var inferredDocsPrefixFile = TestPathUtils.PathUnder(outputRoot, "docs", "v", "1.0.0", "docs", "legacy", "path.html");
        Assert.True(File.Exists(inferredRoot));
        Assert.True(File.Exists(inferredHtmlFile));
        Assert.True(File.Exists(inferredIndexFile));
        Assert.True(File.Exists(inferredDocsPrefixFile));

        var rebased = await File.ReadAllTextAsync(inferredIndexFile);
        Assert.Contains("href=\"/docs/v/1.0.0/theme.css\"", rebased, StringComparison.Ordinal);
        Assert.Contains("action=\"/docs/v/1.0.0/contact\"", rebased, StringComparison.Ordinal);
        Assert.Contains("src=\"/docs/v/1.0.0/global.svg\"", rebased, StringComparison.Ordinal);
        Assert.Contains("url=/docs/v/1.0.0/contact", rebased, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src=\"//cdn.example/logo.svg\"", rebased, StringComparison.Ordinal);
        Assert.Contains("href=\"relative.html\"", rebased, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/v/1.0.0/guides/start/index.html\"", rebased, StringComparison.Ordinal);
        var absoluteCanonical = await File.ReadAllTextAsync(inferredHtmlFile);
        Assert.Contains("href=\"https://manual.example/docs/v/1.0.0/status.html\"", absoluteCanonical, StringComparison.Ordinal);
        Assert.Contains("content=\"5\"", absoluteCanonical, StringComparison.Ordinal);
        using var inventory = JsonDocument.Parse(await File.ReadAllBytesAsync(result.InventoryPath));
        var guideRecords = inventory.RootElement.GetProperty("files").EnumerateArray()
            .Where(file => file.GetProperty("kind").GetString() == "guide").ToArray();
        Assert.Equal(4, guideRecords.Length);
        Assert.DoesNotContain(guideRecords, file => file.GetProperty("path").GetString()!.Contains("orphan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_RejectsSelectionThatIsMissingOrHidden()
    {
        var publicFixture = await CreateFixtureAsync([SingleRelease()]);
        var missingOutput = ExistingOutput("missing-selection");
        var missingSnapshot = SnapshotFiles(missingOutput);

        var missing = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(publicFixture, missingOutput, "2.0.0"));

        Assert.Equal("docs-switch-processing-failed", missing.Diagnostic.Code);
        Assert.Equal(missingSnapshot, SnapshotFiles(missingOutput));

        var hiddenFixture = await CreateFixtureAsync([SingleRelease(visibility: "Hidden")]);
        var hiddenOutput = ExistingOutput("hidden-selection");
        var hiddenSnapshot = SnapshotFiles(hiddenOutput);

        var hidden = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(hiddenFixture, hiddenOutput));

        Assert.Equal("docs-switch-processing-failed", hidden.Diagnostic.Code);
        Assert.Equal(hiddenSnapshot, SnapshotFiles(hiddenOutput));
    }

    [Fact]
    public async Task GenerateAsync_EmitsCatalogAndInventoryWithoutStaticPagesWhenNoCanonicalRoutesExist()
    {
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0", [], IncludeRouteManifest: false)]);
        var outputRoot = TestPathUtils.PathUnder(_root, "route-free-output");

        var result = await GenerateAsync(fixture, outputRoot);

        Assert.Equal(0, result.GeneratedFileCount);
        Assert.True(File.Exists(TestPathUtils.PathUnder(outputRoot, "versions.json")));
        Assert.True(File.Exists(result.InventoryPath));
        Assert.False(File.Exists(TestPathUtils.PathUnder(outputRoot, "docs", "versions", "index.html")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(outputRoot, "docs", "versions", "switch", "runtime.js")));
        using var inventory = JsonDocument.Parse(await File.ReadAllBytesAsync(result.InventoryPath));
        Assert.Empty(inventory.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task GenerateAsync_RejectsModernRouteThatNamesMissingVerifiedArtifact()
    {
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0",
        [
            new PageInput("guides/start", "export/missing.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>", WriteArtifact: false)
        ])]);
        var outputRoot = ExistingOutput("missing-artifact");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("number")]
    public async Task GenerateAsync_RejectsModernRouteManifestWithoutAUsablePhysicalMapping(string mappingShape)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var routeManifestPath = TestPathUtils.PathUnder(fixture.Root, "releases", "1.0.0", RouteManifestName);
        var routeManifest = JsonNode.Parse(await File.ReadAllBytesAsync(routeManifestPath))!.AsObject();
        var routeEntry = routeManifest["entries"]!.AsArray()[0]!.AsObject();
        if (mappingShape == "missing") routeEntry.Remove("canonicalArtifactPath");
        else if (mappingShape == "null") routeEntry["canonicalArtifactPath"] = null;
        else routeEntry["canonicalArtifactPath"] = 17;
        await RewriteReleaseManifestAsync(fixture, "1.0.0", RouteManifestName,
            Encoding.UTF8.GetBytes(routeManifest.ToJsonString(WebJson)));
        var outputRoot = ExistingOutput("missing-modern-map-" + mappingShape);
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RejectsModernRouteMappedToNonHtmlManifestMember()
    {
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0",
        [
            new PageInput("guides/start", "guides/start.css", "en", "setup", "Start", Html: "body { color: black; }")
        ])]);
        var outputRoot = ExistingOutput("modern-non-html-mapping");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RewritesNestedPinnedSearchPathsUnderTheConfiguredVersionMount()
    {
        var search = "{\"documents\":[{\"path\":\"/docs/guides/start.html\",\"nested\":["
            + "{\"path\":\"/docs/assets/related.html\"},{\"path\":\"/elsewhere\"},{\"path\":7}]},"
            + "{\"path\":\"/docs\"}]}";
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0", [
            new PageInput("guides/start", "export/start.html", "en", "setup")
        ], AdditionalFiles: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["search-index.json"] = search
        })]);
        var outputRoot = TestPathUtils.PathUnder(_root, "nested-search-paths");

        await GenerateAsync(fixture, outputRoot, routeRoot: "/manual");

        using var index = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(outputRoot,
            "manual", "v", "1.0.0", "search-index.json")));
        var first = index.RootElement.GetProperty("documents")[0];
        Assert.Equal("/manual/v/1.0.0/guides/start.html", first.GetProperty("path").GetString());
        var nested = first.GetProperty("nested");
        Assert.Equal("/manual/v/1.0.0/assets/related.html", nested[0].GetProperty("path").GetString());
        Assert.Equal("/elsewhere", nested[1].GetProperty("path").GetString());
        Assert.Equal(7, nested[2].GetProperty("path").GetInt32());
        Assert.Equal("/docs", index.RootElement.GetProperty("documents")[1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task GenerateAsync_RejectsMalformedPinnedSearchIndexWithoutReplacingPriorOutput()
    {
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0",
        [
            new PageInput("guides/start", "export/start.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>")
        ], AdditionalFiles: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["search-index.json"] = "{ malformed"
        })]);
        var outputRoot = ExistingOutput("malformed-search-index");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.True(File.Exists(TestPathUtils.PathUnder(fixture.Root, "releases", "1.0.0", "search-index.json")));
    }

    [Theory]
    [InlineData("missing-h1")]
    [InlineData("existing-csp")]
    [InlineData("reserved-marker")]
    public async Task GenerateAsync_RejectsUnsupportedGuideShellsWithoutReplacingPriorOutput(string shell)
    {
        var html = shell == "missing-h1"
            ? "<html><head><title>Start</title></head><body><p>No insertable heading</p></body></html>"
            : shell == "reserved-marker"
                ? "<html><head></head><body><h1>Start</h1><div data-docs-version-switch></div></body></html>"
                : "<html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'\"></head><body><h1>Start</h1></body></html>";
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0",
        [
            new PageInput("guides/start", "export/start.html", "en", "setup", Html: html)
        ])]);
        var outputRoot = ExistingOutput("unsupported-shell-" + shell);
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RejectsAuthoredReservedMountAndNamespacePaths()
    {
        foreach (var reservedPath in new[] { "docs/v/stale.html", "docs/versions/switch/authored.html" })
        {
            var fixture = await CreateFixtureAsync([SingleRelease()]);
            var authoredPath = TestPathUtils.PathUnder(fixture.Root, reservedPath.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(authoredPath)!);
            await File.WriteAllTextAsync(authoredPath, "authored output cannot own this generated path");
            var sourceSnapshot = SnapshotFiles(fixture.Root);
            var outputRoot = ExistingOutput("reserved-" + reservedPath.Replace('/', '-'));
            var previousSnapshot = SnapshotFiles(outputRoot);

            var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

            Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
            Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
            Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.Root));
        }
    }

    [Fact]
    public async Task GenerateAsync_ReplacesPriorManagedFilesOnlyWithMatchingGeneratedInventory()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var firstRoot = TestPathUtils.PathUnder(_root, "prior-generated-serving");
        await GenerateAsync(fixture, firstRoot);
        foreach (var file in Directory.EnumerateFiles(firstRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(firstRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != ReleaseDocsStaticConceptPublication.InventoryFileName
                && !relative.StartsWith("docs/v/", StringComparison.Ordinal)
                && !relative.StartsWith("docs/versions/switch/", StringComparison.Ordinal)) continue;
            var destination = TestPathUtils.PathUnder(fixture.Root, relative.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        var sourceSnapshot = SnapshotFiles(fixture.Root);
        var outputRoot = TestPathUtils.PathUnder(_root, "owned-reserved-output");

        var result = await GenerateAsync(fixture, outputRoot);

        Assert.Equal(SnapshotFiles(firstRoot), SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.Root));
        Assert.True(File.Exists(result.InventoryPath));
    }

    [Theory]
    [InlineData("wrong-schema")]
    [InlineData("wrong-transform")]
    [InlineData("missing-files")]
    [InlineData("wrong-files")]
    [InlineData("null-record")]
    [InlineData("unlisted")]
    [InlineData("unsafe-path")]
    [InlineData("duplicate-path")]
    [InlineData("missing-length")]
    [InlineData("wrong-length-type")]
    [InlineData("negative-length")]
    [InlineData("length-mismatch")]
    [InlineData("digest-mismatch")]
    [InlineData("short-digest")]
    [InlineData("invalid-digest")]
    public async Task GenerateAsync_RejectsInvalidPriorOwnershipWithoutChangingInputsOrPublishedOutput(string invalid)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        const string relative = "docs/versions/switch/prior.html";
        var member = TestPathUtils.PathUnder(fixture.Root, relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(member)!);
        var bytes = Encoding.UTF8.GetBytes("a complete prior generated file");
        await File.WriteAllBytesAsync(member, bytes);
        var record = new JsonObject { ["path"] = relative, ["length"] = bytes.Length, ["sha256"] = Sha256(bytes) };
        var files = new JsonArray(record);
        var inventory = new JsonObject
        {
            ["schema"] = ReleaseDocsStaticConceptPublication.InventorySchema,
            ["transformVersion"] = ReleaseDocsStaticConceptPublication.TransformVersion,
            ["files"] = files
        };
        switch (invalid)
        {
            case "wrong-schema": inventory["schema"] = "unknown"; break;
            case "wrong-transform": inventory["transformVersion"] = "unknown"; break;
            case "missing-files": inventory.Remove("files"); break;
            case "wrong-files": inventory["files"] = "invalid"; break;
            case "null-record": files[0] = null; break;
            case "unlisted": files.Clear(); break;
            case "unsafe-path": record["path"] = "../escaped"; break;
            case "duplicate-path": files.Add(record.DeepClone()); break;
            case "missing-length": record.Remove("length"); break;
            case "wrong-length-type": record["length"] = "invalid"; break;
            case "negative-length": record["length"] = -1; break;
            case "length-mismatch": record["length"] = bytes.Length + 1; break;
            case "digest-mismatch": record["sha256"] = new string('0', 64); break;
            case "short-digest": record["sha256"] = "bad"; break;
            case "invalid-digest": record["sha256"] = new string('z', 64); break;
        }
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.Root, ReleaseDocsStaticConceptPublication.InventoryFileName), inventory.ToJsonString());
        var before = SnapshotFiles(fixture.Root);
        var output = ExistingOutput("invalid-prior-ownership-" + invalid);
        var published = SnapshotFiles(output);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, output));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(before, SnapshotFiles(fixture.Root));
        Assert.Equal(published, SnapshotFiles(output));
    }

    [Theory]
    [InlineData("Start", "This guide is available", "The documented guide is available at the same location.")]
    [InlineData("Configure", "This guide has a different title", "This guide has a different title in 2.0.0.")]
    public async Task GenerateAsync_ExplainsSameRouteIdentityAndTitleChanges(string targetTitle, string heading, string explanation)
    {
        var fixture = await CreateFixtureAsync([
            new ReleaseInput("1.0.0", [new PageInput("guides/start", "export/start.html", "en", "setup", "Start")]),
            new ReleaseInput("2.0.0", [new PageInput("guides/start", "export/start.html", "en", "setup", targetTitle)])
        ]);
        var output = TestPathUtils.PathUnder(_root, "same-route-" + targetTitle);
        await GenerateAsync(fixture, output);
        var choice = await ReadChoiceAsync(fixture, output, "1.0.0", "guides/start");
        var target = Assert.Single(choice.QuerySelectorAll("main section"), section => section.TextContent.Contains("2.0.0", StringComparison.Ordinal));
        Assert.Contains(heading, target.TextContent, StringComparison.Ordinal);
        Assert.Contains(explanation, target.TextContent, StringComparison.Ordinal);
        Assert.Contains(target.QuerySelectorAll("a"), link => link.TextContent == "Open this guide"
            && link.GetAttribute("href")!.StartsWith("/docs/v/2.0.0/export/start.html?docsFrom=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_RejectsReservedPathsWhenInventoryMarkerIsMalformedWithoutReplacingPriorOutput()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var authoredPath = TestPathUtils.PathUnder(fixture.Root, "docs", "v", "stale.html");
        Directory.CreateDirectory(Path.GetDirectoryName(authoredPath)!);
        await File.WriteAllTextAsync(authoredPath, "authored content must not be claimed by an inventory marker");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.Root, ReleaseDocsStaticConceptPublication.InventoryFileName), "{}");
        var sourceSnapshot = SnapshotFiles(fixture.Root);
        var outputRoot = ExistingOutput("malformed-serving-inventory");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.Root));
    }

    [Fact]
    public async Task GenerateAsync_UsesFallbackActionsOnlyWhenVerifiedTargetDestinationsExist()
    {
        var source = new ReleaseInput("1.0.0",
        [
            new PageInput("guides/start", "export/start.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>")
        ]);
        var target = new ReleaseInput("2.0.0",
        [
            new PageInput("guides/section", "export/guides/section.html", "en", null, "Section", true,
                "<html><head></head><body><h1>Section</h1></body></html>"),
            new PageInput("guides/other", "export/guides/other.html", "en", null, "Other",
                Html: "<html><head></head><body><h1>Other</h1></body></html>")
        ], IncludeSearchIndex: false);
        var bridge = new Bridge("setup", "1.0.0",
        [
            new Membership("1.0.0", "page", "guides/start"),
            new Membership("2.0.0", "absent", FallbackSectionRoute: "guides/section")
        ]);
        var fixture = await CreateFixtureAsync([source, target], bridge);
        var outputRoot = TestPathUtils.PathUnder(_root, "fallback-without-search-or-home");

        await GenerateAsync(fixture, outputRoot);

        var choice = await ReadChoiceAsync(fixture, outputRoot, "1.0.0", "guides/start");
        var targetSection = Assert.Single(choice.QuerySelectorAll("main section"), section =>
            section.TextContent.Contains("2.0.0", StringComparison.Ordinal));
        var links = targetSection.QuerySelectorAll("a[href]").ToArray();
        Assert.Contains(links, link => link.TextContent.Contains("related section", StringComparison.Ordinal)
            && link.GetAttribute("href") == "/docs/v/2.0.0/export/guides/section.html");
        Assert.DoesNotContain(links, link => link.GetAttribute("href") == "/docs/v/2.0.0/search.html"
            || link.GetAttribute("href") == "/docs/v/2.0.0/export/index.html");
        Assert.Contains(links, link => link.GetAttribute("href") == "/docs/versions");
    }

    [Fact]
    public async Task GenerateAsync_RejectsRecoveryActionWhoseSearchDestinationIsMissing()
    {
        var source = new ReleaseInput("1.0.0",
        [
            new PageInput("guides/start", "export/start.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>")
        ]);
        var target = new ReleaseInput("2.0.0",
        [
            new PageInput("guides/other", "export/other.html", "en", "different", Html: "<html><head></head><body><h1>Other</h1></body></html>")
        ], AdditionalFiles: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["search-index.json"] = "{\"documents\":[]}"
        });
        var fixture = await CreateFixtureAsync([source, target]);
        var outputRoot = ExistingOutput("missing-search-destination");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RejectsCanonicalRouteInGeneratedSwitchNamespace()
    {
        var fixture = await CreateFixtureAsync([new ReleaseInput("1.0.0",
        [
            new PageInput("versions/switch/authored", "export/start.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>")
        ])]);
        var outputRoot = ExistingOutput("reserved-canonical-route");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Theory]
    [InlineData("/docs/../outside")]
    [InlineData("docs")]
    [InlineData("//outside")]
    [InlineData("/docs?query")]
    [InlineData("/docs#fragment")]
    [InlineData("/docs\\outside")]
    [InlineData("/docs\ncontrol")]
    public async Task GenerateAsync_RejectsInvalidRouteRootBeforeWritingOutput(string routeRoot)
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var outputRoot = ExistingOutput("invalid-route-root");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, routeRoot: routeRoot));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RejectsOutputRootNestedInsideVerificationTree()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var nestedOutput = TestPathUtils.PathUnder(fixture.Root, "serving");

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, nestedOutput));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.False(Directory.Exists(nestedOutput));
    }

    [Fact]
    public async Task GenerateAsync_PropagatesCancellationWithoutReplacingPriorOutput()
    {
        var fixture = await CreateFixtureAsync([SingleRelease()]);
        var outputRoot = ExistingOutput("canceled-generation");
        var previousSnapshot = SnapshotFiles(outputRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GenerateAsync(fixture, outputRoot,
            cancellationToken: cancellation.Token));

        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    private async Task<Fixture> CreateFixtureAsync(IReadOnlyList<ReleaseInput> releases, Bridge? bridge = null)
    {
        var root = TestPathUtils.PathUnder(_root, "verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entries = new List<JsonObject>();
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            var tree = TestPathUtils.PathUnder(root, "releases", release.Version);
            Directory.CreateDirectory(tree);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var page in release.Pages)
            {
                if (!page.WriteArtifact) continue;
                AddFile(tree, files, page.ArtifactPath, page.Html);
            }
            foreach (var (path, content) in release.AdditionalFiles ?? new Dictionary<string, string>(StringComparer.Ordinal))
            {
                AddFile(tree, files, path, content);
            }
            if (release.IncludeRouteManifest)
            {
                var routeManifest = CreateRouteManifest(release.Pages, release.LegacyRouteManifest, release.HtmlUrlBase);
                AddFile(tree, files, RouteManifestName, Encoding.UTF8.GetString(routeManifest));
            }
            if (release.IncludeSidecar && release.Pages.Count > 0)
            {
                var sidecar = CreateSidecar(release.Pages);
                AddFile(tree, files, ConceptIndexName, Encoding.UTF8.GetString(sidecar));
            }

            var manifestBytes = CreateReleaseManifest(files);
            await File.WriteAllBytesAsync(TestPathUtils.PathUnder(tree, ReleaseManifestName), manifestBytes);
            var pin = Sha256(manifestBytes);
            pins.Add(release.Version, pin);
            entries.Add(new JsonObject
            {
                ["version"] = release.Version,
                ["exactTreePath"] = "releases/" + release.Version,
                ["releaseManifestSha256"] = pin,
                ["visibility"] = release.Visibility
            });
        }

        if (bridge is not null)
        {
            var mapBytes = CreateBridge(bridge, pins);
            var mapPath = TestPathUtils.PathUnder(root, "bridges", "concepts.json");
            Directory.CreateDirectory(Path.GetDirectoryName(mapPath)!);
            await File.WriteAllBytesAsync(mapPath, mapBytes);
            var owner = entries.Single(entry => entry["version"]!.GetValue<string>() == bridge.DescriptorVersion);
            owner["conceptMapPath"] = "bridges/concepts.json";
            owner["conceptMapSha256"] = Sha256(mapBytes);
        }

        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(root, "versions.json"), new JsonObject
        {
            ["versions"] = new JsonArray(entries.Select(entry => (JsonNode)entry).ToArray())
        }.ToJsonString(WebJson) is { } catalogText ? Encoding.UTF8.GetBytes(catalogText) : Array.Empty<byte>());
        return new Fixture(root, pins);
    }

    private string ExistingOutput(string name)
    {
        var path = TestPathUtils.PathUnder(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(TestPathUtils.PathUnder(path, "prior-output.txt"), "complete previous serving generation");
        return path;
    }

    private static ReleaseInput SingleRelease(string version = "1.0.0", string visibility = "Public") => new(version,
    [
        new PageInput("guides/start", "export/start.html", "en", "setup", Html: "<html><head></head><body><h1>Start</h1></body></html>")
    ], visibility);

    private static Task<DocsServingOutput> GenerateAsync(Fixture fixture, string outputRoot, string selectedVersion = "1.0.0",
        string routeRoot = "/docs", CancellationToken cancellationToken = default) =>
        ReleaseDocsStaticConceptPublication.GenerateAsync(fixture.Root, outputRoot, selectedVersion, routeRoot,
            4 * 1024 * 1024, new DocsStaticServingConfiguration("Default"), cancellationToken);

    private static async Task SetCatalogFieldAsync(Fixture fixture, string version, string field, JsonNode? value)
    {
        var catalogPath = TestPathUtils.PathUnder(fixture.Root, "versions.json");
        var catalog = JsonNode.Parse(await File.ReadAllBytesAsync(catalogPath))!.AsObject();
        var entry = catalog["versions"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["version"]!.GetValue<string>() == version);
        entry[field] = value;
        await File.WriteAllBytesAsync(catalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, WebJson));
    }

    private static async Task RewriteReleaseManifestAsync(Fixture fixture, string version, string modifiedPath, byte[] modifiedBytes)
    {
        var tree = TestPathUtils.PathUnder(fixture.Root, "releases", version);
        var path = TestPathUtils.PathUnder(tree, modifiedPath.Split('/'));
        await File.WriteAllBytesAsync(path, modifiedBytes);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tree, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == ReleaseManifestName) continue;
            files[relative] = await File.ReadAllBytesAsync(file);
        }
        var manifestBytes = CreateReleaseManifest(files);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(tree, ReleaseManifestName), manifestBytes);
        var catalogPath = TestPathUtils.PathUnder(fixture.Root, "versions.json");
        var catalog = JsonNode.Parse(await File.ReadAllBytesAsync(catalogPath))!.AsObject();
        var entry = catalog["versions"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["version"]!.GetValue<string>() == version);
        entry["releaseManifestSha256"] = Sha256(manifestBytes);
        await File.WriteAllBytesAsync(catalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, WebJson));
    }

    private static Task WriteCatalogAsync(Fixture fixture, string json) =>
        File.WriteAllTextAsync(TestPathUtils.PathUnder(fixture.Root, "versions.json"), json);

    private static async Task<AngleSharp.Dom.IDocument> ReadChoiceAsync(Fixture fixture, string outputRoot, string version, string route)
    {
        var identity = new DocConceptPageIdentity(version, fixture.ReleasePins[version], route, "en", LocaleIsKnown: true);
        var key = DocConceptIdentityIndex.CreateSourceKey("Default", identity);
        var path = TestPathUtils.PathUnder(outputRoot, "docs", "versions", "switch", key + ".html");
        return new HtmlParser().ParseDocument(await File.ReadAllTextAsync(path));
    }

    private static void AddFile(string tree, IDictionary<string, byte[]> files, string relative, string content)
    {
        var path = TestPathUtils.PathUnder(tree, relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);
        files.Add(relative, bytes);
    }

    private static byte[] CreateRouteManifest(IReadOnlyList<PageInput> pages, bool legacy = false, string? htmlUrlBase = "export-root")
    {
        var entries = new JsonArray();
        foreach (var page in pages)
        {
            var entry = new JsonObject
            {
                ["sourcePath"] = "guides/source.md",
                ["canonicalRoutePath"] = page.Route,
                ["recoveryAliases"] = new JsonArray(),
                ["declaredAliases"] = new JsonArray()
            };
            if (!legacy) entry["canonicalArtifactPath"] = page.ArtifactPath;
            entries.Add(entry);
        }
        var document = new JsonObject
        {
            ["schema"] = "appsurface-docs-route-manifest-v1",
            ["entries"] = entries
        };
        if (!legacy) document["htmlUrlBase"] = htmlUrlBase;
        return document.ToJsonString(WebJson) is { } text ? Encoding.UTF8.GetBytes(text) : Array.Empty<byte>();
    }

    private static byte[] CreateSidecar(IReadOnlyList<PageInput> pages)
    {
        var pageNodes = new JsonArray();
        foreach (var page in pages)
        {
            pageNodes.Add(new JsonObject
            {
                ["canonicalRoutePath"] = page.Route,
                ["locale"] = page.Locale,
                ["conceptId"] = page.ConceptId,
                ["title"] = page.Title,
                ["anchorIds"] = new JsonArray("overview"),
                ["sectionLanding"] = page.SectionLanding
            });
        }
        var coverage = pages.All(page => page.ConceptId is not null) ? "complete" : "partial";
        return new JsonObject
        {
            ["schema"] = "appsurface-docs-concepts-v1",
            ["identityCoverage"] = coverage,
            ["pages"] = pageNodes
        }.ToJsonString(WebJson) is { } text ? Encoding.UTF8.GetBytes(text) : Array.Empty<byte>();
    }

    private static byte[] CreateReleaseManifest(IReadOnlyDictionary<string, byte[]> files)
    {
        var manifestFiles = new JsonArray();
        foreach (var (path, bytes) in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            manifestFiles.Add(new JsonObject
            {
                ["path"] = path,
                ["length"] = bytes.LongLength,
                ["contentType"] = Path.GetExtension(path) == ".html" ? "text/html" : "application/json",
                ["hashAlgorithm"] = "sha256",
                ["sha256"] = Sha256(bytes)
            });
        }
        return new JsonObject
        {
            ["schema"] = "appsurface-docs-release-manifest-v1",
            ["files"] = manifestFiles
        }.ToJsonString(WebJson) is { } text ? Encoding.UTF8.GetBytes(text) : Array.Empty<byte>();
    }

    private static byte[] CreateBridge(Bridge bridge, IReadOnlyDictionary<string, string> pins)
    {
        var memberships = new JsonArray();
        foreach (var membership in bridge.Memberships)
        {
            var item = new JsonObject
            {
                ["version"] = membership.Version,
                ["releaseManifestSha256"] = pins[membership.Version],
                ["locale"] = "en",
                ["kind"] = membership.Kind
            };
            if (membership.CanonicalRoutePath is not null) item["canonicalRoutePath"] = membership.CanonicalRoutePath;
            if (membership.FallbackSectionRoute is not null) item["fallbackSectionRoute"] = membership.FallbackSectionRoute;
            memberships.Add(item);
        }
        return new JsonObject
        {
            ["schema"] = "appsurface-docs-concept-map-v1",
            ["concepts"] = new JsonArray(new JsonObject
            {
                ["conceptId"] = bridge.ConceptId,
                ["memberships"] = memberships
            })
        }.ToJsonString(WebJson) is { } text ? Encoding.UTF8.GetBytes(text) : Array.Empty<byte>();
    }

    private static Dictionary<string, string> SnapshotFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            path => Sha256(File.ReadAllBytes(TestPathUtils.PathUnder(root, Path.GetRelativePath(root, path)))), StringComparer.Ordinal);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record Fixture(string Root, IReadOnlyDictionary<string, string> ReleasePins);
    private sealed record ReleaseInput(string Version, IReadOnlyList<PageInput> Pages, string Visibility = "Public",
        bool IncludeRouteManifest = true, bool IncludeSidecar = true, bool IncludeSearchIndex = false,
        IReadOnlyDictionary<string, string>? AdditionalFiles = null, bool LegacyRouteManifest = false,
        string? HtmlUrlBase = "export-root");
    private sealed record PageInput(string Route, string ArtifactPath, string Locale, string? ConceptId,
        string Title = "Guide", bool SectionLanding = false, string Html = "<html><head></head><body><h1>Guide</h1></body></html>", bool WriteArtifact = true);
    private sealed record Bridge(string ConceptId, string DescriptorVersion, IReadOnlyList<Membership> Memberships);
    private sealed record Membership(string Version, string Kind, string? CanonicalRoutePath = null, string? FallbackSectionRoute = null);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
