using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseDocsConceptPublicationTests : IDisposable
{
    private const string ReleaseManifestName = ".appsurface-docs-release-manifest.json";
    private const string RouteManifestName = ".appsurface-docs-route-manifest.json";
    private const string ConceptIndexName = ".appsurface-docs-concepts.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "release-docs-concepts-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateAsync_PublishesPinnedPublicTreesAndAllVersionSwitchArtifacts()
    {
        var fixture = await CreateFixtureAsync();
        var sourceSnapshot = SnapshotFiles(fixture.VerificationRoot);
        var outputRoot = Path.Combine(_root, "serving");

        var result = await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        Assert.Equal(new[] { "1.0.0", "2.0.0" }, result.VerifiedVersions);
        Assert.True(File.Exists(Path.Combine(outputRoot, "docs", "versions", "index.html")));
        var archive = await File.ReadAllTextAsync(Path.Combine(outputRoot, "docs", "versions", "index.html"));
        Assert.Contains("1.0.0", archive, StringComparison.Ordinal);
        Assert.Contains("2.0.0", archive, StringComparison.Ordinal);
        Assert.DoesNotContain("9.0.0", archive, StringComparison.Ordinal);

        var oldIdentity = Identity(fixture, "1.0.0", "guides/start");
        var newIdentity = Identity(fixture, "2.0.0", "guides/quick-start");
        var oldChoicePath = Path.Combine(outputRoot, "docs", "versions", "switch", DocConceptIdentityIndex.CreateSourceKey("Default", oldIdentity) + ".html");
        var newChoicePath = Path.Combine(outputRoot, "docs", "versions", "switch", DocConceptIdentityIndex.CreateSourceKey("Default", newIdentity) + ".html");
        Assert.True(File.Exists(oldChoicePath));
        Assert.True(File.Exists(newChoicePath));
        var oldChoice = await File.ReadAllTextAsync(oldChoicePath);
        var newChoice = await File.ReadAllTextAsync(newChoicePath);

        var oldGuidePath = Path.Combine(outputRoot, "docs", "v", "1.0.0", "export", "start.html");
        var newGuidePath = Path.Combine(outputRoot, "docs", "v", "2.0.0", "export", "quick-start.html");
        var oldGuide = await File.ReadAllTextAsync(oldGuidePath);
        var newGuide = await File.ReadAllTextAsync(newGuidePath);
        var oldKey = DocConceptIdentityIndex.CreateSourceKey("Default", oldIdentity);
        var newKey = DocConceptIdentityIndex.CreateSourceKey("Default", newIdentity);
        Assert.Contains("/docs/v/2.0.0/export/quick-start.html?docsFrom=" + oldKey, oldGuide, StringComparison.Ordinal);
        Assert.Contains("/docs/v/2.0.0/export/quick-start.html?docsFrom=" + oldKey, oldChoice, StringComparison.Ordinal);
        Assert.Contains("/docs/v/1.0.0/export/start.html?docsFrom=" + newKey, newChoice, StringComparison.Ordinal);
        Assert.Contains("This guide has a different location in 2.0.0.", newGuide, StringComparison.Ordinal);
        Assert.Contains("/docs/v/1.0.0/export/start.html", newGuide, StringComparison.Ordinal);
        Assert.Contains("/docs/versions/switch/", oldGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("private-release-content", oldGuide + newGuide, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(outputRoot, "releases", "9.0.0")));
        Assert.False(File.Exists(Path.Combine(outputRoot, "docs", "v", "9.0.0", "export", "secret.html")));
        using (var publicCatalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputRoot, "versions.json"))))
        {
            Assert.Equal(new[] { "1.0.0", "2.0.0" }, publicCatalog.RootElement.GetProperty("versions").EnumerateArray()
                .Select(entry => entry.GetProperty("version").GetString()).ToArray());
        }
        Assert.DoesNotContain("private-release-content", string.Join("\n", Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetExtension(path) is ".html" or ".json" ? File.ReadAllText(path) : string.Empty)), StringComparison.Ordinal);
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.VerificationRoot));

        using (var searchIndex = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputRoot, "docs", "v", "2.0.0", "search-index.json"))))
        {
            var entries = searchIndex.RootElement.GetProperty("documents").EnumerateArray().ToArray();
            Assert.Equal("/docs/v/2.0.0/guides/quick-start.html", entries[0].GetProperty("path").GetString());
            Assert.Equal("/docs/guides/quick-start.html", entries[0].GetProperty("canonical").GetString());
            Assert.Equal("/other/path", entries[1].GetProperty("path").GetString());
            Assert.Equal("/docs/v/2.0.0/assets/related.html", entries[1].GetProperty("nested")[0].GetProperty("path").GetString());
            Assert.Equal("relative/path", entries[1].GetProperty("nested")[1].GetProperty("path").GetString());
            Assert.Equal(7, entries[2].GetProperty("path").GetInt32());
        }

        using var inventory = JsonDocument.Parse(await File.ReadAllBytesAsync(result.InventoryPath));
        var inventoryRoot = inventory.RootElement;
        Assert.Equal(ReleaseDocsStaticConceptPublication.InventorySchema, inventoryRoot.GetProperty("schema").GetString());
        Assert.Equal(ReleaseDocsStaticConceptPublication.TransformVersion, inventoryRoot.GetProperty("transformVersion").GetString());
        var sourcePins = inventoryRoot.GetProperty("sourceReleases").EnumerateArray().ToArray();
        Assert.Equal(new[] { "1.0.0", "2.0.0" }, sourcePins.Select(item => item.GetProperty("version").GetString()).ToArray());
        Assert.Equal(fixture.Releases["1.0.0"].Pin, sourcePins[0].GetProperty("releaseManifestSha256").GetString());
        Assert.Equal(fixture.Releases["2.0.0"].Pin, sourcePins[1].GetProperty("releaseManifestSha256").GetString());

        var files = inventoryRoot.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(result.GeneratedFileCount, files.Length);
        Assert.Contains(files, file => file.GetProperty("kind").GetString() == "guide"
            && file.GetProperty("sourceVersion").GetString() == "1.0.0"
            && file.GetProperty("sourceReleaseManifestSha256").GetString() == fixture.Releases["1.0.0"].Pin);
        Assert.Contains(files, file => file.GetProperty("kind").GetString() == "guide"
            && file.GetProperty("sourceVersion").GetString() == "2.0.0"
            && file.GetProperty("sourceReleaseManifestSha256").GetString() == fixture.Releases["2.0.0"].Pin);
        foreach (var file in files)
        {
            var path = file.GetProperty("path").GetString()!;
            var bytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(outputRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(file.GetProperty("length").GetInt64(), bytes.LongLength);
            Assert.Equal(file.GetProperty("sha256").GetString(), Sha256(bytes));
        }
    }

    [Fact]
    public async Task GenerateAsync_ReplacesPreviousInventoryAndRemovesHiddenAndStaleVersionTrees()
    {
        var fixture = await CreateFixtureAsync();
        var outputRoot = Path.Combine(_root, "replace-previous-serving");
        var first = await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);
        var previouslyPublicGuide = Path.Combine(outputRoot, "docs", "v", "2.0.0", "export", "quick-start.html");
        Assert.True(File.Exists(previouslyPublicGuide));
        using (var previousInventory = JsonDocument.Parse(await File.ReadAllBytesAsync(first.InventoryPath)))
        {
            Assert.Contains(previousInventory.RootElement.GetProperty("files").EnumerateArray(), file =>
                file.GetProperty("path").GetString() == "docs/v/2.0.0/export/quick-start.html");
        }

        var stalePrivatePage = Path.Combine(outputRoot, "docs", "v", "9.0.0", "export", "secret.html");
        var staleRemovedPage = Path.Combine(outputRoot, "docs", "v", "2.0.0", "removed-from-archive.html");
        Directory.CreateDirectory(Path.GetDirectoryName(stalePrivatePage)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staleRemovedPage)!);
        await File.WriteAllTextAsync(stalePrivatePage, "stale private page from a previous publication");
        await File.WriteAllTextAsync(staleRemovedPage, "stale page from a previous publication");

        var catalogPath = Path.Combine(fixture.VerificationRoot, "versions.json");
        var catalog = JsonNode.Parse(await File.ReadAllBytesAsync(catalogPath))!.AsObject();
        var target = catalog["versions"]!.AsArray().OfType<JsonObject>()
            .Single(version => version["version"]!.GetValue<string>() == "2.0.0");
        target["visibility"] = "Hidden";
        var source = catalog["versions"]!.AsArray().OfType<JsonObject>()
            .Single(version => version["version"]!.GetValue<string>() == "1.0.0");
        source.Remove("conceptMapPath");
        source.Remove("conceptMapSha256");
        await File.WriteAllBytesAsync(catalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, WebJson));
        File.Delete(Path.Combine(fixture.VerificationRoot, "bridges", "concepts.json"));

        var second = await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        Assert.False(File.Exists(previouslyPublicGuide));
        Assert.False(File.Exists(stalePrivatePage));
        Assert.False(File.Exists(staleRemovedPage));
        Assert.Equal(new[] { "1.0.0" }, second.VerifiedVersions);
        var publicCatalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputRoot, "versions.json")));
        Assert.Equal(new[] { "1.0.0" }, publicCatalog.RootElement.GetProperty("versions").EnumerateArray()
            .Select(version => version.GetProperty("version").GetString()).ToArray());
        using var inventory = JsonDocument.Parse(await File.ReadAllBytesAsync(second.InventoryPath));
        Assert.Equal(new[] { "1.0.0" }, inventory.RootElement.GetProperty("sourceReleases").EnumerateArray()
            .Select(release => release.GetProperty("version").GetString()).ToArray());
        Assert.DoesNotContain(inventory.RootElement.GetProperty("files").EnumerateArray(), file =>
            file.GetProperty("path").GetString()!.StartsWith("docs/v/2.0.0/", StringComparison.Ordinal)
            || file.GetProperty("path").GetString()!.StartsWith("docs/v/9.0.0/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sidecar", "docs-concept-id-invalid")]
    [InlineData("sidecar-schema", "docs-concept-index-invalid")]
    [InlineData("bridge", "docs-concept-map-invalid")]
    [InlineData("conflict", "docs-concept-map-invalid")]
    [InlineData("bridge-path", "docs-switch-processing-failed")]
    [InlineData("bridge-digest", "docs-switch-processing-failed")]
    public async Task GenerateAsync_StrictlyRejectsInvalidConceptInputsWithoutReplacingPreviousOutput(string invalidInput, string expectedCode)
    {
        var fixture = await CreateFixtureAsync(invalidInput);
        var sourceSnapshot = SnapshotFiles(fixture.VerificationRoot);
        var outputRoot = Path.Combine(_root, "existing-serving-" + invalidInput);
        Directory.CreateDirectory(outputRoot);
        var sentinel = Path.Combine(outputRoot, "previous-output.txt");
        await File.WriteAllTextAsync(sentinel, "previous complete serving output");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024));

        Assert.Equal(expectedCode, exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.VerificationRoot));
    }

    [Fact]
    public async Task GenerateAsync_RestoresPreviousServingTreeWhenStagingMoveFails()
    {
        var fixture = await CreateFixtureAsync();
        var outputRoot = Path.Combine(_root, "serving-move-failure");
        Directory.CreateDirectory(outputRoot);
        var sentinel = Path.Combine(outputRoot, "previous-output.txt");
        await File.WriteAllTextAsync(sentinel, "previous complete serving output");
        var previousSnapshot = SnapshotFiles(outputRoot);
        var failStagingMove = true;
        using var moveFailure = ReleaseDocsStaticConceptPublication.UseDirectoryMoveFailureForTesting((source, destination) =>
        {
            if (!failStagingMove
                || !source.EndsWith(".switch-staging", StringComparison.Ordinal)
                || !string.Equals(destination, outputRoot, StringComparison.Ordinal))
            {
                return false;
            }

            failStagingMove = false;
            return true;
        });

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.False(Directory.Exists(outputRoot + ".switch-staging"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(outputRoot)!),
            path => path.StartsWith(outputRoot + ".switch-previous", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("2")]
    [InlineData("\"Private\"")]
    [InlineData("\"\"")]
    public async Task GenerateAsync_RejectsUnsupportedVisibilityValuesWithoutReplacingPreviousOutput(string visibilityJson)
    {
        var fixture = await CreateFixtureAsync();
        await SetCatalogFieldAsync(fixture, "2.0.0", "visibility", JsonNode.Parse(visibilityJson));
        var sourceSnapshot = SnapshotFiles(fixture.VerificationRoot);
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visibilityJson)))[..8];
        var outputRoot = Path.Combine(_root, "invalid-visibility-serving-" + suffix);
        Directory.CreateDirectory(outputRoot);
        var sentinel = Path.Combine(outputRoot, "previous-output.txt");
        await File.WriteAllTextAsync(sentinel, "previous complete serving output");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.VerificationRoot));
    }

    [Fact]
    public async Task GenerateAsync_AcceptsNumericHiddenVisibilityWithoutServingTheRelease()
    {
        var fixture = await CreateFixtureAsync();
        await SetCatalogFieldAsync(fixture, "2.0.0", "visibility", JsonValue.Create(1));
        await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapPath", null);
        await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapSha256", null);
        File.Delete(Path.Combine(fixture.VerificationRoot, "bridges", "concepts.json"));
        var outputRoot = Path.Combine(_root, "numeric-hidden-serving");

        var result = await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        using var publicCatalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputRoot, "versions.json")));
        Assert.Equal(new[] { "1.0.0" }, publicCatalog.RootElement.GetProperty("versions").EnumerateArray()
            .Select(version => version.GetProperty("version").GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Combine(outputRoot, "docs", "v", "2.0.0")));
        Assert.DoesNotContain("2.0.0", await File.ReadAllTextAsync(Path.Combine(outputRoot, "docs", "versions", "index.html")), StringComparison.Ordinal);
        using var inventory = JsonDocument.Parse(await File.ReadAllBytesAsync(result.InventoryPath));
        Assert.DoesNotContain(inventory.RootElement.GetProperty("sourceReleases").EnumerateArray(), release =>
            release.GetProperty("version").GetString() == "2.0.0");
    }

    [Fact]
    public async Task GenerateAsync_RejectsNonStringOptionalBridgePathWithoutReplacingPreviousOutput()
    {
        var fixture = await CreateFixtureAsync();
        await SetCatalogFieldAsync(fixture, "1.0.0", "conceptMapPath", JsonValue.Create(true));
        var sourceSnapshot = SnapshotFiles(fixture.VerificationRoot);
        var outputRoot = Path.Combine(_root, "boolean-bridge-path-serving");
        Directory.CreateDirectory(outputRoot);
        var sentinel = Path.Combine(outputRoot, "previous-output.txt");
        await File.WriteAllTextAsync(sentinel, "previous complete serving output");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.VerificationRoot));
    }

    [Fact]
    public async Task GenerateAsync_ExcludesPinnedBridgeBytesDuplicatedInsidePublicReleaseTree()
    {
        var fixture = await CreateFixtureAsync();
        var bridgeBytes = await PinBridgeInsidePublicReleaseAsync(fixture);
        var sourceSnapshot = SnapshotFiles(fixture.VerificationRoot);
        var outputRoot = Path.Combine(_root, "private-bridge-copy-serving");

        await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.VerificationRoot));
        Assert.False(File.Exists(Path.Combine(outputRoot, "releases", "1.0.0", "bridges", "concepts.json")));
        Assert.False(File.Exists(Path.Combine(outputRoot, "docs", "concepts-copy.json")));
        foreach (var file in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories))
        {
            Assert.False(ContainsBytes(await File.ReadAllBytesAsync(file), bridgeBytes),
                $"Serving output leaked pinned bridge bytes in {Path.GetRelativePath(outputRoot, file)}.");
        }
        using var publicCatalog = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputRoot, "versions.json")));
        Assert.DoesNotContain("conceptMapPath", publicCatalog.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("concepts.json", publicCatalog.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_UsesConfiguredRouteRootAndKeepsUnavailableVersionsOutOfPhysicalLinks()
    {
        var fixture = await CreateFixtureAsync(includeUnavailable: true);
        var outputRoot = Path.Combine(_root, "custom-root-serving");

        await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024, routeRoot: "/manual/docs");

        Assert.True(File.Exists(Path.Combine(outputRoot, "manual", "docs", "v", "1.0.0", "export", "start.html")));
        var identity = Identity(fixture, "1.0.0", "guides/start");
        var choicePath = Path.Combine(outputRoot, "manual", "docs", "versions", "switch",
            DocConceptIdentityIndex.CreateSourceKey("Default", identity) + ".html");
        var choice = await File.ReadAllTextAsync(choicePath);
        Assert.Contains("/manual/docs/v/2.0.0/export/quick-start.html", choice, StringComparison.Ordinal);
        Assert.Contains("3.0.0", choice, StringComparison.Ordinal);
        Assert.Contains("This version is unavailable", choice, StringComparison.Ordinal);
        Assert.DoesNotContain("/manual/docs/v/3.0.0/", choice, StringComparison.Ordinal);
        Assert.DoesNotContain("9.0.0", choice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_UsesAuthoredFallbackThenVersionLocalSearchAndHomeActions()
    {
        var fixture = await CreateFixtureAsync(includeRecoveryPages: true);
        var outputRoot = Path.Combine(_root, "recovery-ladder-serving");

        await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        var choicePath = Path.Combine(outputRoot, "docs", "versions", "switch",
            DocConceptIdentityIndex.CreateSourceKey("Default", Identity(fixture, "1.0.0", "guides/start")) + ".html");
        var document = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(choicePath));
        var targetChoice = Assert.Single(document.QuerySelectorAll("main section"),
            element => element.TextContent.Contains("2.0.0", StringComparison.Ordinal));
        var links = targetChoice.QuerySelectorAll("a[href]").ToArray();
        Assert.Contains(links, link => link.TextContent.Contains("related section", StringComparison.Ordinal)
            && link.GetAttribute("href") == "/docs/v/2.0.0/export/guides/section.html");
        Assert.Contains(links, link => link.TextContent.Contains("Search this version", StringComparison.Ordinal)
            && link.GetAttribute("href") == "/docs/v/2.0.0/search.html");
        Assert.Contains(links, link => link.TextContent.Contains("documentation home", StringComparison.Ordinal)
            && link.GetAttribute("href") == "/docs/v/2.0.0/export/index.html");
        Assert.Contains(links, link => link.TextContent.Contains("Return to original guide", StringComparison.Ordinal)
            && link.GetAttribute("href") == "/docs/v/1.0.0/export/start.html");
    }

    [Fact]
    public async Task GenerateAsync_DoesNotSubstituteAnotherLocaleOrOfferTargetLocalRecovery()
    {
        var fixture = await CreateFixtureAsync(noSelectedLocale: true);
        var outputRoot = Path.Combine(_root, "locale-isolation-serving");

        await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        var choicePath = Path.Combine(outputRoot, "docs", "versions", "switch",
            DocConceptIdentityIndex.CreateSourceKey("Default", Identity(fixture, "1.0.0", "guides/start")) + ".html");
        var document = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(choicePath));
        var targetChoice = Assert.Single(document.QuerySelectorAll("main section"),
            element => element.TextContent.Contains("2.0.0", StringComparison.Ordinal));
        Assert.Contains("No documentation in this language", targetChoice.TextContent, StringComparison.Ordinal);
        Assert.Contains("Your language has not been changed", targetChoice.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(targetChoice.QuerySelectorAll("a[href]"), link =>
            link.GetAttribute("href")!.StartsWith("/docs/v/2.0.0/", StringComparison.Ordinal));
        Assert.Contains(targetChoice.QuerySelectorAll("a[href]"), link => link.GetAttribute("href") == "/docs/versions");
    }

    [Fact]
    public async Task GenerateAsync_ProjectsLegacyFrozenRoutesWithoutModernArtifactMappings()
    {
        var fixture = await CreateFixtureAsync(legacyRouteManifest: true);
        var outputRoot = Path.Combine(_root, "legacy-route-serving");

        var result = await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        var legacyGuide = Path.Combine(outputRoot, "docs", "v", "2.0.0", "guides", "quick-start.html");
        Assert.True(File.Exists(legacyGuide));
        var legacyHtml = await File.ReadAllTextAsync(legacyGuide);
        Assert.Contains("/docs/v/1.0.0/export/start.html", legacyHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/v/2.0.0/theme.css\"", legacyHtml, StringComparison.Ordinal);
        Assert.Contains("src=\"/docs/v/2.0.0/images/logo.svg\"", legacyHtml, StringComparison.Ordinal);
        Assert.Contains("action=\"/docs/v/2.0.0/guides/quick-start.html\"", legacyHtml, StringComparison.Ordinal);
        Assert.Contains("url=/docs/v/2.0.0/guides/quick-start.html", legacyHtml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outputRoot, "docs", "v", "2.0.0", "theme.css")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "docs", "v", "2.0.0", "images", "logo.svg")));
        using var inventory = JsonDocument.Parse(await File.ReadAllBytesAsync(result.InventoryPath));
        Assert.Contains(inventory.RootElement.GetProperty("files").EnumerateArray(), file =>
            file.GetProperty("kind").GetString() == "guide"
            && file.GetProperty("sourceVersion").GetString() == "2.0.0"
            && file.GetProperty("path").GetString() == "docs/v/2.0.0/guides/quick-start.html");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33554433)]
    public async Task GenerateAsync_RejectsArtifactBudgetOutsideSupportedBounds(long maximumBytes)
    {
        var fixture = await CreateFixtureAsync();

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            GenerateAsync(fixture, Path.Combine(_root, "invalid-budget-serving"), maximumBytes));

        Assert.Equal("release-docs-publication-rewritten-file-size-invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task GenerateAsync_RejectsAuthoredSwitchNamespaceCollisionWithoutReplacingPreviousOutput()
    {
        var fixture = await CreateFixtureAsync();
        var authored = Path.Combine(fixture.VerificationRoot, "docs", "versions", "switch", "authored.html");
        Directory.CreateDirectory(Path.GetDirectoryName(authored)!);
        await File.WriteAllTextAsync(authored, "authored output cannot own the generated switch namespace");
        var outputRoot = Path.Combine(_root, "reserved-namespace-serving");
        Directory.CreateDirectory(outputRoot);
        var sentinel = Path.Combine(outputRoot, "previous.txt");
        await File.WriteAllTextAsync(sentinel, "previous output");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_RejectsReparsePointsInsidePinnedArchives()
    {
        var fixture = await CreateFixtureAsync();
        var exactTree = Path.Combine(fixture.VerificationRoot, "releases", "1.0.0");
        var linkedDirectory = Path.Combine(exactTree, "linked-content");
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, fixture.VerificationRoot);
        }
        catch (Exception symlinkException) when (symlinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This host does not allow the directory symlink needed for the archive safety check.");
        }

        var outputRoot = Path.Combine(_root, "reparse-point-serving");
        Directory.CreateDirectory(outputRoot);
        var sentinel = Path.Combine(outputRoot, "previous.txt");
        await File.WriteAllTextAsync(sentinel, "previous output");
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024));

        Assert.Equal("docs-switch-processing-failed", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_ReturnsEmptyOutputWhenCatalogIsMissing()
    {
        var verificationRoot = Path.Combine(_root, "catalog-free");
        Directory.CreateDirectory(verificationRoot);
        var outputRoot = Path.Combine(_root, "catalog-free-serving");

        var result = await ReleaseDocsStaticConceptPublication.GenerateAsync(verificationRoot, outputRoot, "1.0.0", "/docs",
            4 * 1024 * 1024, new DocsStaticServingConfiguration("Default"), CancellationToken.None);

        Assert.Equal(outputRoot, result.RootPath);
        Assert.Equal(TestPathUtils.PathUnder(outputRoot, ReleaseDocsStaticConceptPublication.InventoryFileName), result.InventoryPath);
        Assert.Equal(0, result.GeneratedFileCount);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task GenerateAsync_AcceptsExactUtf8BudgetBoundaryAndPreservesBothTreesOnOverflow()
    {
        var fixture = await CreateFixtureAsync();
        var sourceSnapshot = SnapshotFiles(fixture.VerificationRoot);
        var exactLimit = await FindMaximumAcceptedBudgetAsync(fixture, 4 * 1024 * 1024);
        Assert.InRange(exactLimit, 2, ReleaseDocsPublication.MaxMaxRewrittenFileSizeBytes);

        var outputRoot = Path.Combine(_root, "exact-serving");
        var exact = await GenerateAsync(fixture, outputRoot, exactLimit);
        using var exactInventory = JsonDocument.Parse(await File.ReadAllBytesAsync(exact.InventoryPath));
        var exactArtifacts = exactInventory.RootElement.GetProperty("files").EnumerateArray()
            .Where(file => file.GetProperty("kind").GetString() is "choice-page" or "version-archive")
            .ToArray();
        Assert.All(exactArtifacts, file => Assert.InRange(file.GetProperty("length").GetInt64(), 0, exactLimit));
        var previousSnapshot = SnapshotFiles(outputRoot);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => GenerateAsync(fixture, outputRoot, exactLimit - 1));

        Assert.Equal("docs-switch-artifact-overflow", exception.Diagnostic.Code);
        Assert.Equal(previousSnapshot, SnapshotFiles(outputRoot));
        Assert.Equal(sourceSnapshot, SnapshotFiles(fixture.VerificationRoot));
    }

    private async Task<long> FindMaximumAcceptedBudgetAsync(Fixture fixture, long knownSuccessfulBudget)
    {
        long knownFailingBudget = 0;
        while (knownSuccessfulBudget - knownFailingBudget > 1)
        {
            var candidate = knownFailingBudget + ((knownSuccessfulBudget - knownFailingBudget) / 2);
            var outputRoot = Path.Combine(_root, "budget-probe-" + candidate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                await GenerateAsync(fixture, outputRoot, candidate);
                knownSuccessfulBudget = candidate;
            }
            catch (ReleaseToolException exception) when (exception.Diagnostic.Code == "docs-switch-artifact-overflow")
            {
                knownFailingBudget = candidate;
            }
        }

        return knownSuccessfulBudget;
    }

    [Fact]
    public async Task GenerateAsync_EmitsEarlyMetaPolicyWithExactRuntimeHashAndSubresourceIntegrity()
    {
        var fixture = await CreateFixtureAsync();
        var outputRoot = Path.Combine(_root, "policy-serving");
        await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);
        var guidePath = Path.Combine(outputRoot, "docs", "v", "1.0.0", "export", "start.html");
        var document = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(guidePath));
        var firstHeadElement = document.Head!.FirstElementChild;
        Assert.NotNull(firstHeadElement);
        Assert.Equal("meta", firstHeadElement!.LocalName);
        Assert.Equal("Content-Security-Policy", firstHeadElement.GetAttribute("http-equiv"));

        var policy = firstHeadElement.GetAttribute("content")!;
        Assert.Contains("script-src '" + DocConceptSwitchRuntime.RuntimeCspHashSource + "'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("sandbox", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);

        var runtime = document.QuerySelector("script[data-docs-switch-runtime]");
        Assert.NotNull(runtime);
        Assert.Equal(DocConceptSwitchRuntime.RuntimeSubresourceIntegrity, runtime!.GetAttribute("integrity"));
        Assert.Equal("anonymous", runtime.GetAttribute("crossorigin"));
        Assert.Equal("/docs/versions/switch/runtime.js", runtime.GetAttribute("src"));
        Assert.Equal("/docs/versions/switch/switch.css", document.QuerySelector("link[data-docs-version-style]")?.GetAttribute("href"));
    }

    [Fact]
    public async Task GenerateAsync_PreservesVerifiedThemeBootstrapAndHashesInlineStylesInStaticPolicy()
    {
        var fixture = await CreateFixtureAsync(includePolicyMarkup: true);
        var outputRoot = Path.Combine(_root, "theme-policy-serving");
        await GenerateAsync(fixture, outputRoot, 4 * 1024 * 1024);

        var guidePath = Path.Combine(outputRoot, "docs", "v", "1.0.0", "export", "start.html");
        var document = new HtmlParser().ParseDocument(await File.ReadAllTextAsync(guidePath));
        var policy = document.Head!.FirstElementChild!.GetAttribute("content")!;
        var bootstrap = document.QuerySelector("script[data-as-theme-preference-bootstrap]");
        Assert.NotNull(bootstrap);
        Assert.Null(bootstrap!.GetAttribute("nonce"));
        var bootstrapHash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ReadThemeBootstrapScript())));
        Assert.Equal(ReleaseDocsStaticConceptPublication.ThemeBootstrapHash, bootstrapHash);
        Assert.Equal(bootstrapHash, CspHash(bootstrap.TextContent));
        Assert.Contains("script-src '" + DocConceptSwitchRuntime.RuntimeCspHashSource + "' '" + bootstrapHash + "'", policy, StringComparison.Ordinal);
        Assert.Contains("style-src 'self' 'unsafe-hashes'", policy, StringComparison.Ordinal);
        Assert.Contains("'" + CspHash("html { color: navy; }") + "'", policy, StringComparison.Ordinal);
        Assert.Contains("'" + CspHash("color: red") + "'", policy, StringComparison.Ordinal);
        Assert.Null(document.QuerySelector("style")?.GetAttribute("nonce"));
        Assert.Null(document.QuerySelector("h1")?.GetAttribute("nonce"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Task<DocsServingOutput> GenerateAsync(Fixture fixture, string outputRoot, long maximumBytes, string routeRoot = "/docs") =>
        ReleaseDocsStaticConceptPublication.GenerateAsync(fixture.VerificationRoot, outputRoot, "1.0.0", routeRoot, maximumBytes,
            new DocsStaticServingConfiguration("Default"), CancellationToken.None);

    private async Task<Fixture> CreateFixtureAsync(string? invalidInput = null, bool includeUnavailable = false,
        bool legacyRouteManifest = false, bool includePolicyMarkup = false, bool includeRecoveryPages = false,
        bool noSelectedLocale = false)
    {
        var verificationRoot = Path.Combine(_root, "verification" + (invalidInput is null ? string.Empty : "-" + invalidInput));
        Directory.CreateDirectory(verificationRoot);
        Directory.CreateDirectory(Path.Combine(verificationRoot, "bridges"));
        var releases = new Dictionary<string, ReleaseEntry>(StringComparer.Ordinal);
        releases.Add("1.0.0", await CreateReleaseAsync(verificationRoot, "1.0.0", "guides/start", "export/start.html",
            "Start guide", includeSidecar: true, invalidSidecar: invalidInput is "sidecar" or "sidecar-schema", privateContent: false,
            invalidSidecarSchema: invalidInput == "sidecar-schema", includePolicyMarkup: includePolicyMarkup));
        releases.Add("2.0.0", await CreateReleaseAsync(verificationRoot, "2.0.0", "guides/quick-start", "export/quick-start.html",
            "Quick start", includeSidecar: includeRecoveryPages || noSelectedLocale, invalidSidecar: false, privateContent: false,
            legacyRouteManifest: legacyRouteManifest, includeSearchIndex: true,
            includeRecoveryPages: includeRecoveryPages, noSelectedLocale: noSelectedLocale));
        releases.Add("9.0.0", await CreateReleaseAsync(verificationRoot, "9.0.0", "secret", "export/secret.html",
            "Private release", includeSidecar: false, invalidSidecar: false, privateContent: true));

        var mapBytes = invalidInput == "bridge"
            ? Encoding.UTF8.GetBytes("{\"schema\":\"unsupported-bridge-v99\",\"concepts\":[]}")
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "appsurface-docs-concept-map-v1",
                concepts = new[]
                {
                    new
                    {
                        conceptId = invalidInput == "conflict" ? "different" : "setup",
                        memberships = new[]
                        {
                            new { version = "1.0.0", releaseManifestSha256 = releases["1.0.0"].Pin, locale = "en", kind = "page", canonicalRoutePath = "guides/start" },
                            RecoveryMembership(releases["2.0.0"], includeRecoveryPages)
                        }
                    }
                }
            }, WebJson);
        var mapPath = Path.Combine(verificationRoot, "bridges", "concepts.json");
        if (!noSelectedLocale) await File.WriteAllBytesAsync(mapPath, mapBytes);
        var mapPin = invalidInput == "bridge-digest" ? new string('0', 64) : Sha256(mapBytes);

        var catalog = JsonSerializer.SerializeToUtf8Bytes(new
        {
            versions = new[]
            {
                CatalogRelease(releases["1.0.0"], "Public",
                    noSelectedLocale ? null : invalidInput == "bridge-path" ? "../bridges/concepts.json" : "bridges/concepts.json",
                    noSelectedLocale ? null : mapPin),
                CatalogRelease(releases["2.0.0"], "Public", null, null),
                CatalogRelease(releases["9.0.0"], "Hidden", null, null)
            }
        }, WebJson);
        if (includeUnavailable)
        {
            var catalogNode = JsonNode.Parse(catalog)!.AsObject();
            catalogNode["versions"]!.AsArray().Add(JsonSerializer.SerializeToNode(
                CatalogRelease(new ReleaseEntry("3.0.0", "releases/3.0.0", new string('3', 64)), "Public", null, null), WebJson));
            catalog = JsonSerializer.SerializeToUtf8Bytes(catalogNode, WebJson);
        }
        await File.WriteAllBytesAsync(Path.Combine(verificationRoot, "versions.json"), catalog);
        return new Fixture(verificationRoot, releases);
    }

    private static async Task<ReleaseEntry> CreateReleaseAsync(string root, string version, string route, string artifact,
        string title, bool includeSidecar, bool invalidSidecar, bool privateContent, bool invalidSidecarSchema = false,
        bool legacyRouteManifest = false, bool includeSearchIndex = false, bool includePolicyMarkup = false,
        bool includeRecoveryPages = false, bool noSelectedLocale = false)
    {
        if (legacyRouteManifest) artifact = "guides/quick-start.html";
        var exactTreePath = Path.Combine(root, "releases", version);
        Directory.CreateDirectory(Path.Combine(exactTreePath, Path.GetDirectoryName(artifact)!));
        var htmlContent = privateContent
            ? "private-release-content"
            : "Readable exact-version guide with a UTF-8 detail: café.";
        var legacyAssets = legacyRouteManifest
            ? "<link rel=\"stylesheet\" href=\"/docs/theme.css\"><meta http-equiv=\"refresh\" content=\"0; url=/docs/guides/quick-start.html\">"
            : string.Empty;
        var legacyAssetBody = legacyRouteManifest
            ? "<img src=\"/docs/images/logo.svg\"><form action=\"/docs/guides/quick-start.html\"><button>Continue</button></form>"
            : string.Empty;
        var policyMarkup = includePolicyMarkup
            ? "<script data-as-theme-preference-bootstrap nonce=\"bootstrap-nonce\">" + ReadThemeBootstrapScript()
                + "</script><style nonce=\"style-nonce\">html { color: navy; }</style>"
            : string.Empty;
        var html = "<!doctype html><html lang=\"en\"><head><title>" + title + "</title>"
            + "<link rel=\"canonical\" href=\"https://docs.example/" + route + "\">" + legacyAssets + policyMarkup + "</head>"
            + "<body><main><h1" + (includePolicyMarkup ? " style=\"color: red\" nonce=\"heading-nonce\"" : string.Empty) + ">" + title
            + "</h1><p>" + htmlContent + "</p>" + legacyAssetBody + "</main></body></html>";
        await File.WriteAllTextAsync(Path.Combine(exactTreePath, artifact), html, new UTF8Encoding(false));
        if (legacyRouteManifest)
        {
            await File.WriteAllTextAsync(Path.Combine(exactTreePath, "theme.css"), "main { color: black; }");
            var imagePath = Path.Combine(exactTreePath, "images", "logo.svg");
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            await File.WriteAllTextAsync(imagePath, "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
        }
        if (includeSearchIndex)
        {
            var searchIndex = """{"documents":[{"path":"/docs/guides/quick-start.html","canonical":"/docs/guides/quick-start.html"},{"path":"/other/path","nested":[{"path":"/docs/assets/related.html"},{"path":"relative/path"}]},{"path":7}]}""";
            await File.WriteAllTextAsync(Path.Combine(exactTreePath, "search-index.json"), searchIndex, new UTF8Encoding(false));
            var searchPage = "<!doctype html><html><head><title>Search</title></head><body><h1>Search</h1></body></html>";
            await File.WriteAllTextAsync(Path.Combine(exactTreePath, "search.html"), searchPage, new UTF8Encoding(false));
        }
        if (includeRecoveryPages)
        {
            foreach (var (extraArtifact, content) in new[]
            {
                ("export/index.html", "<html><head><title>Home</title></head><body><h1>Home</h1></body></html>"),
                ("export/guides/section.html", "<html><head><title>Section</title></head><body><h1>Section</h1></body></html>")
            })
            {
                var path = Path.Combine(exactTreePath, extraArtifact.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
            }
        }

        var routeManifest = legacyRouteManifest
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "appsurface-docs-route-manifest-v1",
                entries = new[]
                {
                    new
                    {
                        sourcePath = "guides/guide.md",
                        canonicalRoutePath = route,
                        recoveryAliases = Array.Empty<string>(),
                        declaredAliases = Array.Empty<string>()
                    }
                }
            }, WebJson)
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "appsurface-docs-route-manifest-v1",
                htmlUrlBase = "export-root",
                entries = includeRecoveryPages
                    ? new object[]
                    {
                        RouteEntry(string.Empty, "export/index.html"),
                        RouteEntry("guides/section", "export/guides/section.html"),
                        RouteEntry(route, artifact)
                    }
                    : new object[]
                    {
                        RouteEntry(route, artifact)
                    }
            }, WebJson);
        await File.WriteAllBytesAsync(Path.Combine(exactTreePath, RouteManifestName), routeManifest);

        if (includeSidecar)
        {
            var sidecar = invalidSidecar
                ? invalidSidecarSchema
                    ? Encoding.UTF8.GetBytes("{\"schema\":\"unsupported-docs-concepts-v99\",\"identityCoverage\":\"complete\",\"pages\":[]}")
                    : Encoding.UTF8.GetBytes("{\"schema\":\"appsurface-docs-concepts-v1\",\"identityCoverage\":\"complete\",\"pages\":[{\"canonicalRoutePath\":\"guides/start\",\"locale\":\"en\",\"conceptId\":\"Invalid Concept\",\"title\":\"Start guide\",\"anchorIds\":[]}]}")
                : JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "appsurface-docs-concepts-v1",
                    identityCoverage = noSelectedLocale || includeRecoveryPages ? "partial" : "complete",
                    pages = includeRecoveryPages
                        ? new object[]
                        {
                            new { canonicalRoutePath = "", locale = "en", conceptId = (string?)null, title = "Home", anchorIds = Array.Empty<string>(), sectionLanding = false },
                            new { canonicalRoutePath = "guides/section", locale = "en", conceptId = (string?)null, title = "Section", anchorIds = Array.Empty<string>(), sectionLanding = true },
                            new { canonicalRoutePath = route, locale = "en", conceptId = (string?)null, title, anchorIds = new[] { "overview" }, sectionLanding = false }
                        }
                        : noSelectedLocale
                            ? new object[] { new { canonicalRoutePath = route, locale = "fr", conceptId = (string?)null, title, anchorIds = new[] { "overview" }, sectionLanding = false } }
                            : new object[] { new { canonicalRoutePath = route, locale = "en", conceptId = (string?)"setup", title, anchorIds = new[] { "overview" }, sectionLanding = false } }
                }, WebJson);
            await File.WriteAllBytesAsync(Path.Combine(exactTreePath, ConceptIndexName), sidecar);
        }

        var files = Directory.EnumerateFiles(exactTreePath, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new
                {
                    path = Path.GetRelativePath(exactTreePath, path).Replace(Path.DirectorySeparatorChar, '/'),
                    length = bytes.LongLength,
                    contentType = Path.GetExtension(path) == ".html" ? "text/html" : "application/json",
                    hashAlgorithm = "sha256",
                    sha256 = Sha256(bytes)
                };
            })
            .OrderBy(item => item.path, StringComparer.Ordinal)
            .ToArray();
        var releaseManifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "appsurface-docs-release-manifest-v1",
            files
        }, WebJson);
        await File.WriteAllBytesAsync(Path.Combine(exactTreePath, ReleaseManifestName), releaseManifest);
        return new ReleaseEntry(version, $"releases/{version}", Sha256(releaseManifest));
    }

    private static object RecoveryMembership(ReleaseEntry release, bool recovery) => recovery
        ? new { version = release.Version, releaseManifestSha256 = release.Pin, locale = "en", kind = "absent", fallbackSectionRoute = "guides/section" }
        : new { version = release.Version, releaseManifestSha256 = release.Pin, locale = "en", kind = "page", canonicalRoutePath = "guides/quick-start" };

    private static object RouteEntry(string route, string artifact) => new
    {
        sourcePath = "guides/guide.md",
        canonicalRoutePath = route,
        canonicalArtifactPath = artifact,
        recoveryAliases = Array.Empty<string>(),
        declaredAliases = Array.Empty<string>()
    };

    private static object CatalogRelease(ReleaseEntry release, string visibility, string? conceptMapPath, string? conceptMapSha256) => new
    {
        version = release.Version,
        exactTreePath = release.ExactTreePath,
        releaseManifestSha256 = release.Pin,
        visibility,
        conceptMapPath,
        conceptMapSha256
    };

    private static async Task SetCatalogFieldAsync(Fixture fixture, string version, string field, JsonNode? value)
    {
        var path = Path.Combine(fixture.VerificationRoot, "versions.json");
        var catalog = JsonNode.Parse(await File.ReadAllBytesAsync(path))!.AsObject();
        var release = catalog["versions"]!.AsArray().OfType<JsonObject>()
            .Single(entry => entry["version"]!.GetValue<string>() == version);
        release[field] = value;
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(catalog, WebJson));
    }

    private static async Task<byte[]> PinBridgeInsidePublicReleaseAsync(Fixture fixture)
    {
        var sourceTree = TestPathUtils.PathUnder(fixture.VerificationRoot, fixture.Releases["1.0.0"].ExactTreePath);
        var bridgeBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "appsurface-docs-concept-map-v1",
            concepts = new[]
            {
                new
                {
                    conceptId = "setup",
                    memberships = new[]
                    {
                        new
                        {
                            version = "2.0.0",
                            releaseManifestSha256 = fixture.Releases["2.0.0"].Pin,
                            locale = "en",
                            kind = "page",
                            canonicalRoutePath = "guides/quick-start"
                        }
                    }
                }
            }
        }, WebJson);
        var inTreeBridgePath = Path.Combine(sourceTree, "bridges", "concepts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(inTreeBridgePath)!);
        await File.WriteAllBytesAsync(inTreeBridgePath, bridgeBytes);

        var releaseManifestPath = Path.Combine(sourceTree, ReleaseManifestName);
        var releaseManifest = JsonNode.Parse(await File.ReadAllBytesAsync(releaseManifestPath))!.AsObject();
        var manifestFiles = releaseManifest["files"]!.AsArray();
        manifestFiles.Add(JsonSerializer.SerializeToNode(new
        {
            path = "bridges/concepts.json",
            length = bridgeBytes.LongLength,
            contentType = "application/json",
            hashAlgorithm = "sha256",
            sha256 = Sha256(bridgeBytes)
        }, WebJson));
        var sortedFiles = new JsonArray();
        foreach (var entry in manifestFiles.OrderBy(file => file!["path"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            sortedFiles.Add(entry!.DeepClone());
        }
        releaseManifest["files"] = sortedFiles;
        var releaseManifestBytes = JsonSerializer.SerializeToUtf8Bytes(releaseManifest, WebJson);
        await File.WriteAllBytesAsync(releaseManifestPath, releaseManifestBytes);
        var releasePin = Sha256(releaseManifestBytes);
        if (fixture.Releases is Dictionary<string, ReleaseEntry> releases)
        {
            releases["1.0.0"] = releases["1.0.0"] with { Pin = releasePin };
        }

        var rootBridgeCopy = Path.Combine(fixture.VerificationRoot, "bridges", "concepts.json");
        await File.WriteAllBytesAsync(rootBridgeCopy, bridgeBytes);
        var servingCopy = Path.Combine(fixture.VerificationRoot, "docs", "concepts-copy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(servingCopy)!);
        await File.WriteAllBytesAsync(servingCopy, bridgeBytes);

        var catalogPath = Path.Combine(fixture.VerificationRoot, "versions.json");
        var catalog = JsonNode.Parse(await File.ReadAllBytesAsync(catalogPath))!.AsObject();
        var sourceEntry = catalog["versions"]!.AsArray().OfType<JsonObject>()
            .Single(entry => entry["version"]!.GetValue<string>() == "1.0.0");
        sourceEntry["releaseManifestSha256"] = releasePin;
        var exactMapPath = "releases/1.0.0/bridges/concepts.json";
        var caseVariant = Path.Combine(sourceTree, "BRIDGES", "CONCEPTS.JSON");
        if (File.Exists(caseVariant)) exactMapPath = "releases/1.0.0/BRIDGES/CONCEPTS.JSON";
        sourceEntry["conceptMapPath"] = exactMapPath;
        sourceEntry["conceptMapSha256"] = Sha256(bridgeBytes);
        await File.WriteAllBytesAsync(catalogPath, JsonSerializer.SerializeToUtf8Bytes(catalog, WebJson));
        return bridgeBytes;
    }

    private static DocConceptPageIdentity Identity(Fixture fixture, string version, string route)
    {
        var release = fixture.Releases[version];
        return new DocConceptPageIdentity(version, release.Pin, route, "en", LocaleIsKnown: true);
    }

    private static Dictionary<string, string> SnapshotFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), path => Sha256(File.ReadAllBytes(path)), StringComparer.Ordinal);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static string CspHash(string value) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ReadThemeBootstrapScript()
    {
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project is not null && !File.Exists(Path.Combine(project.FullName,
            "Web", "ForgeTrust.AppSurface.Web", "Theming", "AppSurfaceThemePreferenceBootstrap.cs")))
            project = project.Parent;
        if (project is null) throw new InvalidOperationException("Could not locate the checked-in theme bootstrap source.");

        var source = File.ReadAllText(Path.Combine(project.FullName,
            "Web", "ForgeTrust.AppSurface.Web", "Theming", "AppSurfaceThemePreferenceBootstrap.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string opening = "internal const string Script = \"\"\"\n";
        const string closing = "\n        \"\"\";";
        var start = source.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Could not locate the theme bootstrap raw string.");
        start += opening.Length;
        var end = source.IndexOf(closing, start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("Could not find the end of the theme bootstrap raw string.");
        return string.Join('\n', source[start..end].Split('\n').Select(line => line.StartsWith("        ", StringComparison.Ordinal) ? line[8..] : line));
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record Fixture(string VerificationRoot, IReadOnlyDictionary<string, ReleaseEntry> Releases);
    private sealed record ReleaseEntry(string Version, string ExactTreePath, string Pin);
}
