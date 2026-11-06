using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release;

/// <summary>Builds the separately inventoried, transformed static serving tree for exact-version docs.</summary>
/// <remarks>Original exact trees remain the sole inputs to archive verification and are never rewritten.</remarks>
internal static class ReleaseDocsStaticConceptPublication
{
    internal const string InventoryFileName = ".appsurface-docs-serving-inventory.json";
    internal const string InventorySchema = "appsurface-docs-serving-inventory-v1";
    internal const string TransformVersion = "docs-version-switch-static-v1";
    private const string ConceptIndexFileName = ".appsurface-docs-concepts.json";
    private const string ReleaseManifestFileName = ".appsurface-docs-release-manifest.json";
    private const string RouteManifestFileName = ".appsurface-docs-route-manifest.json";
    private const string RuntimeRelativePath = "versions/switch/runtime.js";
    private const string StyleRelativePath = "versions/switch/switch.css";

    internal static Task<DocsServingOutput> GenerateAsync(
        string verificationRoot,
        string servingRoot,
        string selectedVersion,
        string routeRoot,
        long maximumFileSizeBytes,
        CancellationToken cancellationToken) =>
        GenerateAsync(verificationRoot, servingRoot, selectedVersion, routeRoot, maximumFileSizeBytes,
            new DocsStaticServingConfiguration("Default"), cancellationToken);

    internal static async Task<DocsServingOutput> GenerateAsync(
        string verificationRoot,
        string servingRoot,
        string selectedVersion,
        string routeRoot,
        long maximumFileSizeBytes,
        DocsStaticServingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(servingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedVersion);
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateBudget(maximumFileSizeBytes);
        var limit = checked((int)maximumFileSizeBytes);
        var root = NormalizeRouteRoot(routeRoot);
        var sourceCatalogPath = Path.Combine(verificationRoot, "versions.json");
        if (!File.Exists(sourceCatalogPath))
        {
            return DocsServingOutput.Empty(servingRoot);
        }

        try
        {
            var catalogBytes = await File.ReadAllBytesAsync(sourceCatalogPath, cancellationToken);
            var catalog = JsonNode.Parse(catalogBytes)?.AsObject()
                ?? throw Invalid("The Docs version catalog is malformed.");
            var releases = await ReadReleasesAsync(verificationRoot, catalog, limit, selectedVersion, cancellationToken);
            if (releases.Count == 0)
            {
                return DocsServingOutput.Empty(servingRoot);
            }

            var maps = await ReadMapsAsync(verificationRoot, catalog, limit, cancellationToken);
            var built = DocConceptIdentityIndex.BuildCombined(releases.Select(release => release.Input).ToArray(), maps,
                DocConceptValidationMode.Strict, limit);
            if (!built.IsValid)
            {
                var first = built.Issues.FirstOrDefault();
                var code = first?.Failure == DocConceptValidationFailure.InvalidConceptId ? "docs-concept-id-invalid"
                    : first?.Failure == DocConceptValidationFailure.ArtifactTooLarge ? "docs-switch-artifact-overflow"
                    : first?.Artifact == "concept-index" ? "docs-concept-index-invalid" : "docs-concept-map-invalid";
                throw Failure(code, "Static Docs concept identity validation failed.",
                    first is null ? "Concept metadata could not be validated." : SafeIssue(first),
                    "Correct the source metadata or complete catalog-wide bridge, re-export and verify changed archives, update all matching pins, and rerun publication before uploads.");
            }

            var index = built.Index;
            var allPages = index.EnumeratePublicPages().ToArray();
            var privateMaps = catalog["versions"]!.AsArray().OfType<JsonObject>()
                .Select(entry => String(entry, "conceptMapPath", optional: true))
                .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool IsPrivateMap(string path, string relative)
            {
                if (privateMaps.Contains(relative)) return true;
                // A release export can also contain a root-serving copy of the same source member.
                // The pinned digest excludes that identical bridge content at every copy boundary.
                var length = new FileInfo(path).Length;
                var candidates = maps.Where(map => map.Bytes.Length == length).ToArray();
                if (candidates.Length == 0) return false;
                using var stream = File.OpenRead(path);
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                return candidates.Any(map => string.Equals(map.Sha256, digest, StringComparison.OrdinalIgnoreCase));
            }
            var servedReleases = releases.Where(item => item.IsPublic && item.IsAvailable).ToArray();
            var selected = servedReleases.SingleOrDefault(item => string.Equals(item.Version, selectedVersion, StringComparison.Ordinal));
            if (selected is null)
            {
                throw Invalid("Selected release is not present as an available public exact tree.");
            }

            var outputRoot = Path.GetFullPath(servingRoot);
            var stagingRoot = outputRoot + ".switch-staging";
            foreach (var boundary in new[] { verificationRoot, outputRoot, stagingRoot })
            {
                if ((File.Exists(boundary) || Directory.Exists(boundary))
                    && (File.GetAttributes(boundary) & FileAttributes.ReparsePoint) != 0)
                    throw Invalid("A serving input or output boundary is a reparse-point directory.");
            }
            EnsureDistinctRoots(Path.GetFullPath(verificationRoot), outputRoot, stagingRoot);
            ResetDirectory(stagingRoot);
            try
            {
                Dictionary<string, (long Length, string Sha256)>? priorFiles = null;
                foreach (var file in EnumerateFiles(verificationRoot, cancellationToken))
                {
                    var relative = Path.GetRelativePath(verificationRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (IsPrivateMap(file, relative)) continue;
                    if (relative.StartsWith("releases/", StringComparison.OrdinalIgnoreCase)
                        && !servedReleases.Any(release => relative.StartsWith(release.ExactTreePath + "/", StringComparison.Ordinal))) continue;
                    if (releases.Any(release => (!release.IsPublic || !release.IsAvailable)
                            && (relative.Equals(release.ExactTreePath, StringComparison.OrdinalIgnoreCase)
                                || relative.StartsWith(release.ExactTreePath + "/", StringComparison.OrdinalIgnoreCase)))) continue;
                    var oldMount = ServingMember(root, "v/");
                    var managed = ServingMember(root, "versions/switch/");
                    if (relative.StartsWith(oldMount, StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith(managed, StringComparison.OrdinalIgnoreCase))
                    {
                        priorFiles ??= await ReadPriorServingFilesAsync(verificationRoot, cancellationToken);
                        if (!priorFiles.TryGetValue(relative, out var prior))
                            throw Invalid("An unlisted output collides with a reserved Docs namespace.");
                        await using var stream = File.OpenRead(file);
                        if (stream.Length != prior.Length
                            || !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                                .Equals(prior.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw Invalid("A reserved Docs file does not match its prior serving inventory.");
                        continue;
                    }
                    var destination = Path.Combine(stagingRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                }

                var outputRecords = new List<DocsServingOutputFile>();
                if (allPages.Length > 0)
                {
                    foreach (var release in servedReleases)
                    {
                        var inputTree = SafeCombine(verificationRoot, release.ExactTreePath);
                        foreach (var file in EnumerateFiles(inputTree, cancellationToken))
                        {
                            var relative = Path.GetRelativePath(inputTree, file).Replace(Path.DirectorySeparatorChar, '/');
                            if (relative is ReleaseManifestFileName or RouteManifestFileName or ConceptIndexFileName) continue;
                            if (IsPrivateMap(file, release.ExactTreePath + "/" + relative)) continue;
                            var destination = SafeCombine(stagingRoot, ServingMember(root, "v/" + release.Version + "/" + relative));
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Copy(file, destination, overwrite: true);
                        }
                    }

                    foreach (var release in servedReleases)
                    {
                        var mount = Join(root, "v/" + Uri.EscapeDataString(release.Version));
                        var tree = SafeCombine(stagingRoot, ServingMember(root, "v/" + release.Version));
                        foreach (var file in EnumerateFiles(tree, cancellationToken).Where(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).ToArray())
                        {
                            var bytes = await ReadBoundedAsync(file, limit, cancellationToken);
                            var document = new HtmlParser().ParseDocument(new UTF8Encoding(false, true).GetString(bytes));
                            RebaseDocument(document, mount, release.UsesExportRoot);
                            var rendered = document.ToHtml();
                            await File.WriteAllTextAsync(file, rendered, cancellationToken);
                            var relative = Path.GetRelativePath(stagingRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                            outputRecords.Add(new DocsServingOutputFile(relative, "mounted-html", release.Version, release.ManifestSha256,
                                Encoding.UTF8.GetByteCount(rendered), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rendered))).ToLowerInvariant()));
                        }
                    }

                    foreach (var release in servedReleases)
                    {
                        var mount = Join(root, "v/" + Uri.EscapeDataString(release.Version));
                        var relative = ServingMember(root, "v/" + release.Version + "/search-index.json");
                        var path = SafeCombine(stagingRoot, relative);
                        if (!File.Exists(path)) continue;
                        var data = JsonNode.Parse(await ReadBoundedAsync(path, limit, cancellationToken));
                        RewriteSearchPaths(data, mount);
                        await WriteDerivedAsync(stagingRoot, relative, Encoding.UTF8.GetBytes(data!.ToJsonString()), "search-index",
                            release.Version, release.ManifestSha256, outputRecords, cancellationToken);
                    }

                    var runtime = DocConceptSwitchRuntime.ReadRuntimeBytes();
                    var stylesheet = DocConceptSwitchRuntime.ReadStylesheetBytesForCaller();
                    await WriteDerivedAsync(stagingRoot, ServingMember(root, RuntimeRelativePath), runtime, "packaged-runtime", null, null, outputRecords, cancellationToken);
                    await WriteDerivedAsync(stagingRoot, ServingMember(root, StyleRelativePath), stylesheet, "packaged-stylesheet", null, null, outputRecords, cancellationToken);

                    var sourceSet = allPages;
                    foreach (var (identity, page) in sourceSet)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var release = releases.Single(item => string.Equals(item.Version, identity.Version, StringComparison.Ordinal)
                            && string.Equals(item.ManifestSha256, identity.ReleaseManifestSha256, StringComparison.OrdinalIgnoreCase));
                        var route = CanonicalLinkPath(identity.RoutePath, page.ArtifactPath!);
                        var switchUrl = Join(root, "versions/switch/" + DocConceptIdentityIndex.CreateSourceKey(configuration.InstanceId, identity) + ".html");
                        var guideUrl = Join(Join(root, "v/" + Uri.EscapeDataString(identity.Version)), route);
                        var archiveUrl = Join(root, "versions");
                        var stylesUrl = Join(root, StyleRelativePath);
                        var views = CreateChoiceViews(index, allPages, releases, identity, page, root, configuration, limit);
                        var choices = DocConceptPresentation.Choices(Title(page), identity.Version, guideUrl, archiveUrl, stylesUrl,
                            views, limit, StaticPolicy());
                        await WriteDerivedAsync(stagingRoot, ServingMember(root, "versions/switch/" + DocConceptIdentityIndex.CreateSourceKey(configuration.InstanceId, identity) + ".html"),
                            Encoding.UTF8.GetBytes(choices), "choice-page", identity.Version, release.ManifestSha256, outputRecords, cancellationToken);

                        var memberPath = page.ArtifactPath!.Split('#')[0];
                        if (page.RoutePath.Equals("versions/switch", StringComparison.OrdinalIgnoreCase)
                            || page.RoutePath.StartsWith("versions/switch/", StringComparison.OrdinalIgnoreCase))
                            throw Invalid("A canonical route collides with the reserved switch namespace.");
                        var physicalPath = SafeCombine(verificationRoot, release.ExactTreePath + "/" + memberPath);
                        if (!File.Exists(physicalPath)) throw Invalid("A validated canonical page artifact is missing from the exact tree.");
                        var originalBytes = await ReadBoundedAsync(physicalPath, limit, cancellationToken);
                        var originalHtml = new UTF8Encoding(false, true).GetString(originalBytes);
                        var rewritten = RewriteGuide(originalHtml, page, identity, index, releases, switchUrl, archiveUrl,
                            root, configuration, limit);
                        await WriteDerivedAsync(stagingRoot,
                            ServingMember(root, "v/" + identity.Version + "/" + memberPath),
                            Encoding.UTF8.GetBytes(rewritten), "guide", identity.Version, release.ManifestSha256, outputRecords, cancellationToken);
                    }

                    var archivePath = ServingMember(root, "versions/index.html");
                    var archiveItems = servedReleases.Select(release =>
                    {
                        var page = index.PublicAvailableReleases.Single(item => item.Version == release.Version).Pages.FirstOrDefault(item => item.IsServeable);
                        var actions = page is null ? Array.Empty<DocConceptAction>() : new[] { new DocConceptAction("Browse documentation", VersionPageUrl(root, release.Version, page)) };
                        return new DocConceptChoiceView(release.Version, "", "Documentation " + release.Version, "Browse this exact release.", actions, Join(root, ""));
                    });
                    var archiveHtml = DocConceptPresentation.Choices("documentation", selectedVersion, Join(root, ""), Join(root, "versions"),
                        Join(root, StyleRelativePath), archiveItems, limit, StaticPolicy());
                    await WriteDerivedAsync(stagingRoot, archivePath, Encoding.UTF8.GetBytes(archiveHtml), "version-archive", null, null, outputRecords, cancellationToken);

                    // Inventory every changed serving member, including mounted search/redirect HTML. Original release
                    // manifests remain under releases/ and are not reused as hashes for the separately transformed v/ tree.
                    foreach (var release in servedReleases)
                    {
                        var prefix = ServingMember(root, "v/" + release.Version);
                        foreach (var path in EnumerateFiles(SafeCombine(stagingRoot, prefix), cancellationToken))
                        {
                            var relative = Path.GetRelativePath(stagingRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                            if (outputRecords.Any(record => record.Path == relative)) continue;
                            await using var stream = File.OpenRead(path);
                            var digest = await SHA256.HashDataAsync(stream, cancellationToken);
                            outputRecords.Add(new DocsServingOutputFile(relative, "mounted-member", release.Version, release.ManifestSha256,
                                stream.Length, Convert.ToHexString(digest).ToLowerInvariant()));
                        }
                    }

                    // Validate every switch-owned URL against the selected mount and known generated destinations before publishing.
                    ValidateGeneratedUrls(outputRecords, stagingRoot, root);
                }
                var publicCatalog = catalog.DeepClone().AsObject();
                publicCatalog["versions"] = new JsonArray(catalog["versions"]!.AsArray().OfType<JsonObject>()
                    .Where(IsPublicRelease)
                    .Select(entry =>
                    {
                        var descriptor = entry.DeepClone().AsObject();
                        descriptor.Remove("conceptMapPath");
                        descriptor.Remove("conceptMapSha256");
                        return (JsonNode)descriptor;
                    }).ToArray());
                if (publicCatalog["recommendedVersion"] is JsonValue recommendation
                    && recommendation.TryGetValue<string>(out var value)
                    && !releases.Any(release => release.IsPublic && release.Version == value)) publicCatalog["recommendedVersion"] = null;
                await File.WriteAllTextAsync(Path.Combine(stagingRoot, "versions.json"), publicCatalog.ToJsonString(ReleaseJson.Options), cancellationToken);

                var inventory = new DocsServingInventory(InventorySchema, TransformVersion,
                    releases.Where(item => item.IsPublic && item.IsAvailable)
                        .Select(item => new DocsServingReleasePin(item.Version, item.ManifestSha256, item.ExactTreePath)).ToArray(),
                    outputRecords.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
                var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                });
                await File.WriteAllBytesAsync(Path.Combine(stagingRoot, InventoryFileName), inventoryBytes, cancellationToken);

                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
                Directory.Move(stagingRoot, outputRoot);
                return new DocsServingOutput(outputRoot, Path.Combine(outputRoot, InventoryFileName), outputRecords.Count,
                    inventoryBytes.Length, releases.Where(item => item.IsPublic && item.IsAvailable).Select(item => item.Version).ToArray());
            }
            catch
            {
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReleaseToolException)
        {
            throw;
        }
        catch (DocConceptArtifactOverflowException overflow)
        {
            throw Failure("docs-switch-artifact-overflow", "Static Docs version switching exceeded the artifact budget.",
                $"A complete generated switch artifact exceeded {overflow.MaximumBytes} UTF-8 bytes (observed at least {overflow.ObservedBytes}).",
                "Reduce the source guide/metadata or deliberately raise --max-rewritten-file-size-bytes within its supported range, then rerun publication.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw Failure("docs-switch-processing-failed", "Static Docs version switching could not produce a complete serving tree.",
                "A validated source or destination could not be read or transformed. Failure type: " + ex.GetType().Name
                    + "; methods: " + string.Join(";", new System.Diagnostics.StackTrace(ex, false).GetFrames().Take(8)
                        .Select(frame => frame.GetMethod()).Select(method => method?.DeclaringType?.FullName + "." + method?.Name)),
                "Repair the exported exact trees and concept metadata, then rerun docs publication.");
        }
    }

    private static async Task<List<StaticRelease>> ReadReleasesAsync(string root, JsonObject catalog, int limit, string selectedVersion, CancellationToken token)
    {
        if (!catalog.TryGetPropertyValue("versions", out var versionsNode) || versionsNode is not JsonArray versions)
            throw Invalid("The Docs version catalog does not contain a versions array.");
        var result = new List<StaticRelease>();
        foreach (var entry in versions.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            var version = String(entry, "version");
            if (string.IsNullOrWhiteSpace(version)) continue;
            var exact = String(entry, "exactTreePath");
            var pin = String(entry, "releaseManifestSha256");
            var isPublic = IsPublicRelease(entry);
            if (exact is null || pin is null) continue;
            var tree = ResolveCatalogTree(root, exact);
            if (!Directory.Exists(tree))
            {
                var unavailable = new DocConceptReleaseInput(version, pin, isPublic, false, false, false, null, [], null);
                result.Add(new StaticRelease(version, pin, exact, isPublic, false, unavailable));
                continue;
            }
            if (!ReleaseDocsArchiveGate.TryVerifyArchiveForStaticPublication(tree, pin, out var verificationReason))
            {
                if (version == selectedVersion) throw Invalid(verificationReason ?? "The selected release archive failed verification.");
                var unavailable = new DocConceptReleaseInput(version, pin, isPublic, false, false, false, null, [], null);
                result.Add(new StaticRelease(version, pin, exact, isPublic, false, unavailable));
                continue;
            }
            var manifest = await File.ReadAllBytesAsync(Path.Combine(tree, ReleaseManifestFileName), token);
            var routes = ReadCanonicalRoutes(tree, manifest);
            var conceptPath = Path.Combine(tree, ConceptIndexFileName);
            byte[]? concept = null;
            if (File.Exists(conceptPath))
            {
                concept = await ReadBoundedAsync(conceptPath, limit, token);
                if (!ManifestPins(manifest, ConceptIndexFileName, concept)) throw Invalid("Concept sidecar is not pinned by the exact release manifest.");
            }
            var search = File.Exists(Path.Combine(tree, "search-index.json"));
            var input = new DocConceptReleaseInput(version, pin, isPublic, true, routes.Count > 0, search,
                routes.FirstOrDefault(route => route.RoutePath.Length == 0)?.RoutePath, routes, concept is null ? (ReadOnlyMemory<byte>?)null : concept.AsMemory());
            var usesExportRoot = false;
            if (File.Exists(Path.Combine(tree, RouteManifestFileName)))
            {
                using var frozen = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(tree, RouteManifestFileName), token));
                usesExportRoot = frozen.RootElement.TryGetProperty("htmlUrlBase", out var urlBase) && urlBase.GetString() == "export-root";
            }
            result.Add(new StaticRelease(version, pin, exact, isPublic, true, input, usesExportRoot));
        }
        return result;
    }

    private static async Task<List<DocConceptMapReference>> ReadMapsAsync(string root, JsonObject catalog, int limit, CancellationToken token)
    {
        var result = new List<DocConceptMapReference>();
        if (catalog["versions"] is not JsonArray versions) return result;
        foreach (var entry in versions.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            var path = String(entry, "conceptMapPath", optional: true);
            var pin = String(entry, "conceptMapSha256", optional: true);
            if (path is null && pin is null) continue;
            if (path is null || pin is null || pin.Length != 64 || !pin.All(Uri.IsHexDigit)) throw Invalid("A catalog concept-map path and SHA-256 pin must be supplied together.");
            var physical = ResolveCatalogTree(root, path);
            var bytes = await ReadBoundedAsync(physical, limit, token);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, pin, StringComparison.OrdinalIgnoreCase)) throw Invalid("A catalog concept-map digest does not match its bytes.");
            result.Add(new DocConceptMapReference(path, pin, bytes));
        }
        return result;
    }

    private static List<DocConceptCanonicalRouteFact> ReadCanonicalRoutes(string tree, byte[] releaseManifestBytes)
    {
        using var manifest = JsonDocument.Parse(releaseManifestBytes);
        var manifestFiles = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);
        var routeManifestPath = Path.Combine(tree, RouteManifestFileName);
        if (!manifestFiles.Contains(RouteManifestFileName) || !File.Exists(routeManifestPath)) return [];
        using var routesDocument = JsonDocument.Parse(File.ReadAllBytes(routeManifestPath));
        var result = new List<DocConceptCanonicalRouteFact>();
        var modern = routesDocument.RootElement.TryGetProperty("htmlUrlBase", out var mode);
        if (modern && mode.GetString() != "export-root") throw Invalid("The frozen route manifest uses an unsupported physical projection.");
        foreach (var item in routesDocument.RootElement.GetProperty("entries").EnumerateArray())
        {
            var route = item.GetProperty("canonicalRoutePath").GetString() ?? throw Invalid("A canonical identity is missing.");
            var parts = route.Split('#', 2);
            var part = parts[0].TrimStart('/');
            string? artifact = null;
            if (item.TryGetProperty("canonicalArtifactPath", out var artifactElement)) artifact = artifactElement.GetString();
            if (modern && string.IsNullOrEmpty(artifact)) throw Invalid("A modern canonical identity has no physical artifact mapping.");
            if (artifact is null)
            {
                var candidates = part.Length == 0 ? new[] { "index.html" }
                    : part.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? new[] { part }
                    : new[] { part + ".html", part + "/index.html" };
                artifact = candidates.SelectMany(candidate => new[] { candidate, "docs/" + candidate })
                    .FirstOrDefault(manifestFiles.Contains);
            }
            if (artifact is null) continue;
            var member = artifact.Split('#')[0];
            if (!manifestFiles.Contains(member) || !File.Exists(SafeCombine(tree, member))
                || !member.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                throw Invalid("A canonical physical artifact is not a verified ordinary HTML file.");
            var destination = member + (parts.Length == 2 ? "#" + parts[1] : string.Empty);
            result.Add(new DocConceptCanonicalRouteFact(part, destination, null, LocaleIsKnown: false));
        }
        return result;
    }

    private static IEnumerable<DocConceptChoiceView> CreateChoiceViews(DocConceptIdentityIndex index,
        IReadOnlyList<(DocConceptPageIdentity Identity, DocConceptCanonicalPage Page)> allPages,
        IReadOnlyList<StaticRelease> releases, DocConceptPageIdentity sourceIdentity,
        DocConceptCanonicalPage sourcePage, string root, DocsStaticServingConfiguration config, int limit)
    {
        var key = DocConceptIdentityIndex.CreateSourceKey(config.InstanceId, sourceIdentity);
        foreach (var target in releases.Where(release => release.IsPublic && release.Version != sourceIdentity.Version))
        {
            var resolution = index.Resolve(sourceIdentity, target.Version, target.ManifestSha256);
            var actions = new List<DocConceptAction>();
            if (resolution.TargetPage is { } targetPage)
            {
                var targetUrl = VersionPageUrl(root, target.Version, targetPage);
                actions.Add(new DocConceptAction("Open this guide", WithContext(targetUrl, key, sourcePage.RoutePath, targetPage.Anchors)));
            }
            else
            {
                if (resolution.FallbackSection is { } section)
                    actions.Add(new DocConceptAction("Browse this version's related section", VersionPageUrl(root, target.Version, section)));
                if (resolution.SearchAvailable)
                    actions.Add(new DocConceptAction("Search this version", Join(Join(root, "v/" + Uri.EscapeDataString(target.Version)), "search.html")));
                if (resolution.HomeRoutePath is { } home)
                {
                    var homePage = allPages.FirstOrDefault(page => page.Identity.Version == target.Version && string.Equals(page.Page.RoutePath, home, StringComparison.OrdinalIgnoreCase)).Page;
                    if (homePage is not null) actions.Add(new DocConceptAction("Open this version's documentation home", VersionPageUrl(root, target.Version, homePage)));
                }
                actions.Add(new DocConceptAction("Version archive", Join(root, "versions")));
            }
            if (!actions.Any(action => action.Label.Contains("Return", StringComparison.Ordinal)))
                actions.Add(new DocConceptAction("Return to original guide", Join(Join(root, "v/" + Uri.EscapeDataString(sourceIdentity.Version)), CanonicalLinkPath(sourcePage.RoutePath, sourcePage.ArtifactPath!))));
            var heading = resolution.Reason switch
            {
                DocConceptResolutionReason.SamePage => "This guide is available",
                DocConceptResolutionReason.MovedPage => "This guide moved",
                DocConceptResolutionReason.RenamedPage => "This guide has a different title",
                DocConceptResolutionReason.KnownAbsent => "No documented counterpart",
                DocConceptResolutionReason.TargetUnavailable => "This version is unavailable",
                DocConceptResolutionReason.NoSelectedLocaleDocs => "No documentation in this language",
                _ => "No verified counterpart"
            };
            var explanation = resolution.Reason switch
            {
                DocConceptResolutionReason.MovedPage => $"This guide has a different location in {target.Version}.",
                DocConceptResolutionReason.RenamedPage => $"This guide has a different title in {target.Version}.",
                DocConceptResolutionReason.KnownAbsent => $"There is no documented counterpart in {target.Version}. This does not establish whether the feature exists in that version.",
                DocConceptResolutionReason.SamePage => "The documented guide is available at the same location.",
                DocConceptResolutionReason.TargetUnavailable => "This version cannot currently be read. Return to the original guide or choose another version.",
                DocConceptResolutionReason.NoSelectedLocaleDocs => "This version has no verified documentation in the selected language. Your language has not been changed.",
                _ => $"No known counterpart in {target.Version}. Available identity metadata does not establish one."
            };
            yield return new DocConceptChoiceView(target.Version, LocaleLabel(sourceIdentity.Locale), heading, explanation, actions,
                Join(Join(root, "v/" + Uri.EscapeDataString(sourceIdentity.Version)), CanonicalLinkPath(sourcePage.RoutePath, sourcePage.ArtifactPath!)));
        }
    }

    private static string RewriteGuide(string html, DocConceptCanonicalPage sourcePage, DocConceptPageIdentity sourceIdentity,
        DocConceptIdentityIndex index, IReadOnlyList<StaticRelease> releases, string choiceUrl, string archiveUrl, string root,
        DocsStaticServingConfiguration config, int limit)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html);
        if (document.Head is null || document.QuerySelector("[data-docs-version-switch], [data-docs-version-data], [data-docs-switch-runtime]") is not null)
            throw Invalid("A canonical guide shell is unsupported or collides with the static switch namespace.");
        var h1 = document.QuerySelector("h1");
        if (h1?.ParentElement is null) throw Invalid("A canonical guide shell has no insertable heading.");
        var release = releases.Single(item => item.Version == sourceIdentity.Version && string.Equals(item.ManifestSha256, sourceIdentity.ReleaseManifestSha256, StringComparison.OrdinalIgnoreCase));
        RebaseDocument(document, Join(root, "v/" + Uri.EscapeDataString(release.Version)), release.UsesExportRoot);
        var sourceKey = DocConceptIdentityIndex.CreateSourceKey(config.InstanceId, sourceIdentity);
        var choiceLinks = new List<DocConceptChoiceLink>();
        foreach (var target in releases.Where(item => item.IsPublic && item.Version != sourceIdentity.Version))
        {
            var resolution = index.Resolve(sourceIdentity, target.Version, target.ManifestSha256);
            string href;
            IReadOnlyList<string>? anchors = null;
            if (resolution.TargetPage is { } page)
            {
                href = WithContext(VersionPageUrl(root, target.Version, page), sourceKey, sourcePage.RoutePath, page.Anchors);
                anchors = page.Anchors;
            }
            else href = choiceUrl + "#" + DocConceptPresentation.ChoiceAnchor(target.Version);
            choiceLinks.Add(new DocConceptChoiceLink(target.Version, target.Version, href, anchors));
        }

        var identity = new DocConceptDestination(config.InstanceId, sourceIdentity.Version, sourceIdentity.ReleaseManifestSha256,
            sourceIdentity.Locale, sourceIdentity.RoutePath);
        var incoming = index.GetIncomingIndexed(sourceIdentity, config.InstanceId)
            .Select(row => new DocConceptIncomingView(row.SourceKey, row.Reason.ToString(),
                TransitionNotice(row.Reason, sourceIdentity.Version),
                VersionPageUrl(root, row.SourceIdentity.Version, row.SourcePage), row.SourceIdentity.Version,
                Title(row.SourcePage) is { Length: > 0 } title ? title : row.SourcePage.RoutePath.Length > 0 ? row.SourcePage.RoutePath : "Documentation", identity));
        var guide = new DocConceptGuideView(identity,
            Title(sourcePage) is { Length: > 0 } pageTitle ? pageTitle : sourcePage.RoutePath.Length > 0 ? sourcePage.RoutePath : "Documentation",
            sourceKey, choiceUrl, archiveUrl, Join(root, RuntimeRelativePath), Join(root, StyleRelativePath), choiceLinks, incoming,
            VersionPageUrl(root, sourceIdentity.Version, sourcePage));
        var markup = DocConceptPresentation.Guide(guide, DocConceptSwitchRuntime.RuntimeSubresourceIntegrity, limit);
        if (document.QuerySelectorAll("meta[http-equiv]").Any(element =>
            string.Equals(element.GetAttribute("http-equiv"), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("The exported guide contains a CSP policy that cannot be safely combined with the static switch policy.");
        foreach (var element in document.QuerySelectorAll("[nonce]")) element.RemoveAttribute("nonce");
        var canonical = document.QuerySelector("link[rel=canonical]");
        if (canonical is not null)
        {
            var target = VersionPageUrl(root, sourceIdentity.Version, sourcePage);
            if (Uri.TryCreate(canonical.GetAttribute("href"), UriKind.Absolute, out var origin)
                && origin.Scheme is "http" or "https") target = origin.GetLeftPart(UriPartial.Authority) + target;
            canonical.SetAttribute("href", target);
        }
        var policy = StaticPolicy(document);
        var completeAddedBytes = Encoding.UTF8.GetByteCount(policy) + (long)markup.Utf8Bytes;
        if (completeAddedBytes > limit) throw new DocConceptArtifactOverflowException(completeAddedBytes, limit);
        var policyElement = parser.ParseFragment(policy, document.Head).OfType<IElement>().Single();
        InsertFragment(document, document.Head, markup.Head, before: null);
        document.Head.InsertBefore(policyElement, document.Head.FirstChild);
        InsertFragment(document, h1.ParentElement, markup.Body, h1);
        return document.ToHtml();
    }

    private static void InsertFragment(IDocument document, IElement parent, string html, IElement? before)
    {
        var parser = new HtmlParser();
        var nodes = parser.ParseFragment(html, parent).ToArray();
        foreach (var node in nodes)
        {
            parent.InsertBefore(node, before);
        }
    }

    private static void RewriteSearchPaths(JsonNode? node, string mount)
    {
        if (node is JsonObject item)
        {
            foreach (var pair in item.ToArray())
            {
                if (pair.Key == "path" && pair.Value is JsonValue value && value.TryGetValue<string>(out var path)
                    && path.StartsWith("/docs/", StringComparison.Ordinal)) item[pair.Key] = Join(mount, path[6..]);
                else RewriteSearchPaths(pair.Value, mount);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) RewriteSearchPaths(child, mount);
    }

    private static void RebaseDocument(IDocument document, string mount, bool usesExportRoot)
    {
        string Rebase(string value, string prefix)
        {
            if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)) return value;
            if (!usesExportRoot && (value == "/docs" || value.StartsWith("/docs/", StringComparison.Ordinal)))
                return Join(prefix, value.Length == 5 ? "" : value[6..]);
            return Join(prefix, value);
        }
        foreach (var name in new[] { "href", "src", "action" })
            foreach (var element in document.QuerySelectorAll("[" + name + "]")
                         .Where(element => element.LocalName != "link" || element.GetAttribute("rel") != "canonical"))
            {
                element.SetAttribute(name, Rebase(element.GetAttribute(name)!, mount));
            }
        foreach (var refresh in document.QuerySelectorAll("meta[http-equiv]")
                     .Where(refresh => string.Equals(refresh.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)))
        {
            var content = refresh.GetAttribute("content") ?? string.Empty;
            var position = content.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
            if (position >= 0) refresh.SetAttribute("content", content[..(position + 4)] + Rebase(content[(position + 4)..].Trim(), mount));
        }
    }

    /// <summary>The build-owned existing theme bootstrap hash; the Docs parity test binds it to the theme package.</summary>
    internal const string ThemeBootstrapHash = "sha256-+j2Lg3GeG7Bz0atNRFfABL82+IWJ7cDIZxWaD7ngdio=";

    private static string StaticPolicy(IDocument? document = null)
    {
        var sources = new List<string> { "'" + DocConceptSwitchRuntime.RuntimeCspHashSource + "'" };
        if (document?.QuerySelectorAll("script[data-as-theme-preference-bootstrap]")
                .Any(script => Hash(script.TextContent) == ThemeBootstrapHash) == true)
            sources.Add("'" + ThemeBootstrapHash + "'");
        var styles = document is null ? [] : document.QuerySelectorAll("style").Select(style => Hash(style.TextContent))
            .Concat(document.QuerySelectorAll("[style]").Select(element => Hash(element.GetAttribute("style")!)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var policy = "default-src 'self'; script-src " + string.Join(' ', sources)
            + "; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; worker-src 'none'; style-src 'self'"
            + (styles.Length == 0 ? string.Empty : " 'unsafe-hashes' " + string.Join(' ', styles.Select(hash => "'" + hash + "'")))
            + "; img-src 'self' data:; font-src 'self'";
        return "<meta http-equiv=\"Content-Security-Policy\" content=\"" + System.Net.WebUtility.HtmlEncode(policy) + "\">";
    }

    private static string Hash(string value) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool ManifestPins(byte[] manifestBytes, string path, byte[] bytes)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        foreach (var item in document.RootElement.GetProperty("files").EnumerateArray())
        {
            if (string.Equals(item.GetProperty("path").GetString(), path, StringComparison.Ordinal))
            {
                return item.GetProperty("length").GetInt64() == bytes.LongLength
                    && string.Equals(item.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.Length > limit) throw new DocConceptArtifactOverflowException(info.Length, limit);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(81920, limit + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > limit) throw new DocConceptArtifactOverflowException(output.Length + read, limit);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task WriteDerivedAsync(string stagingRoot, string relative, byte[] bytes, string kind,
        string? sourceVersion, string? sourcePin, ICollection<DocsServingOutputFile> records, CancellationToken token)
    {
        var target = SafeCombine(stagingRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes, token);
        var normalized = relative.Replace(Path.DirectorySeparatorChar, '/');
        if (records is List<DocsServingOutputFile> list) list.RemoveAll(record => record.Path == normalized);
        records.Add(new DocsServingOutputFile(normalized, kind, sourceVersion, sourcePin,
            bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
    }

    private static void ValidateGeneratedUrls(IEnumerable<DocsServingOutputFile> records, string stagingRoot, string routeRoot)
    {
        foreach (var record in records.Where(record => record.Kind is "guide" or "choice-page"))
        {
            var document = new HtmlParser().ParseDocument(File.ReadAllText(SafeCombine(stagingRoot, record.Path)));
            var scope = record.Kind == "choice-page" ? document.QuerySelector("main") : document.QuerySelector("[data-docs-version-switch]");
            foreach (var href in scope?.QuerySelectorAll("a[href]").AsEnumerable()
                         .Select(link => link.GetAttribute("href")!) ?? Enumerable.Empty<string>())
            {
                var path = href.Split('?', '#')[0];
                if (path == Join(routeRoot, "versions")) continue;
                if (!path.StartsWith(routeRoot.TrimEnd('/') + "/", StringComparison.Ordinal))
                    throw Invalid("A generated switch link escaped its configured Docs mount.");
                var relative = Uri.UnescapeDataString(path.TrimStart('/'));
                if (relative.Split('/').Any(segment => segment is "." or ".."))
                    throw Invalid("A generated switch link contains traversal.");
                var target = SafeCombine(stagingRoot, relative);
                if (!File.Exists(target) && !File.Exists(target + ".html") && !File.Exists(Path.Combine(target, "index.html")))
                    throw Invalid("A generated switch link has no staged destination.");
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("The serving input contains a reparse-point directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("The serving input contains a reparse-point member.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (Path.GetFileName(entry) != InventoryFileName) yield return entry;
            }
        }
    }

    private static void EnsureDistinctRoots(string source, string output, string staging)
    {
        if (SameOrNested(source, output) || SameOrNested(output, source) || SameOrNested(source, staging) || SameOrNested(staging, source))
            throw Invalid("Verification input and serving output roots must be distinct, non-overlapping directories.");
    }

    private static bool SameOrNested(string left, string right) => string.Equals(left, right, PathComparison)
        || left.StartsWith(Path.TrimEndingDirectorySeparator(right) + Path.DirectorySeparatorChar, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ResetDirectory(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Invalid("A serving staging directory is a reparse point.");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    /// <summary>Reads prior generated-file claims; callers verify each reserved member's bytes before replacing it.</summary>
    /// <remarks>These claims authorize replacement only. Original archive trust still requires its catalog-pinned manifest.</remarks>
    private static async Task<Dictionary<string, (long Length, string Sha256)>> ReadPriorServingFilesAsync(string root, CancellationToken token)
    {
        var path = SafeCombine(root, InventoryFileName);
        if (!File.Exists(path)) throw Invalid("An authored output collides with a reserved Docs namespace.");
        await using var stream = File.OpenRead(path);
        var inventory = await JsonNode.ParseAsync(stream, cancellationToken: token) as JsonObject;
        if (inventory is null || String(inventory, "schema") != InventorySchema
            || String(inventory, "transformVersion") != TransformVersion || inventory["files"] is not JsonArray files)
            throw Invalid("The prior serving inventory has an unsupported schema, transform, or file list.");
        var result = new Dictionary<string, (long Length, string Sha256)>(StringComparer.Ordinal);
        foreach (var node in files)
        {
            if (node is not JsonObject file) throw Invalid("A prior serving inventory file record is malformed.");
            var relative = String(file, "path")!;
            _ = SafeCombine(root, relative);
            var digest = String(file, "sha256")!;
            if (file["length"] is not JsonValue length || !length.TryGetValue<long>(out var count) || count < 0
                || digest.Length != 64 || !digest.All(Uri.IsHexDigit) || !result.TryAdd(relative, (count, digest)))
                throw Invalid("A prior serving inventory file length, digest, or unique path is invalid.");
        }
        return result;
    }

    private static string ResolveCatalogTree(string root, string relative) => SafeCombine(root, relative);

    private static string SafeCombine(string root, string relative)
    {
        var clean = relative.Replace('\\', '/');
        if (clean.StartsWith('/') || relative.Contains('\\') || clean.IndexOfAny([':', '?', '#', '%']) >= 0 || clean.Any(char.IsControl)
            || clean.Split('/').Any(segment => segment is "" or "." or ".."
                || segment.StartsWith(".", StringComparison.Ordinal) && segment is not (ConceptIndexFileName or RouteManifestFileName or ReleaseManifestFileName or InventoryFileName)))
            throw Invalid("A catalog or route mapping contains an unsafe relative path.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, clean.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, PathComparison)) throw Invalid("A derived path escaped its trusted root.");
        var current = fullRoot;
        foreach (var segment in clean.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("A derived path contains a reparse point.");
        }
        return full;
    }

    private static string NormalizeRouteRoot(string root)
    {
        root = string.IsNullOrWhiteSpace(root) ? "/docs" : root.Trim();
        if (!root.StartsWith('/') || root.StartsWith("//", StringComparison.Ordinal) || root.Any(char.IsControl) || root.Contains('\\') || root.Contains('?') || root.Contains('#'))
            throw Invalid("The static Docs route root is invalid.");
        var segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) throw Invalid("The static Docs route root is invalid.");
        return segments.Length == 0 ? string.Empty : "/" + string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static string CanonicalLinkPath(string route, string artifact)
    {
        var parts = artifact.Split('#', 2);
        return string.Join('/', parts[0].Split('/').Select(Uri.EscapeDataString))
            + (parts.Length == 2 ? "#" + Uri.EscapeDataString(parts[1]) : string.Empty);
    }

    private static string VersionPageUrl(string root, string version, DocConceptCanonicalPage page) =>
        Join(Join(root, "v/" + Uri.EscapeDataString(version)), CanonicalLinkPath(page.RoutePath, page.ArtifactPath!));

    private static string WithContext(string url, string sourceKey, string sourceRoute, IReadOnlyList<string> targetAnchors)
    {
        var parts = url.Split('#', 2);
        return parts[0] + "?docsFrom=" + sourceKey + (parts.Length == 2 ? "#" + parts[1] : string.Empty);
    }

    private static string ServingMember(string routeRoot, string relative) =>
        routeRoot.Trim('/') is { Length: > 0 } prefix ? prefix + "/" + relative : relative;

    private static string Join(string root, string relative) => root.TrimEnd('/') + "/" + relative.TrimStart('/');
    private static string? String(JsonObject item, string key, bool optional = false)
    {
        if (item[key] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (optional && item[key] is null) return null;
        throw Invalid("The Docs catalog has a missing or malformed string field.");
    }

    private static bool IsPublicRelease(JsonObject entry)
    {
        if (entry["visibility"] is null) return true;
        if (entry["visibility"] is JsonValue value)
        {
            if (value.TryGetValue<int>(out var numeric) && numeric is 0 or 1) return numeric == 0;
            if (value.TryGetValue<string>(out var text))
            {
                var visibility = text.Trim();
                if (string.Equals(visibility, "Public", StringComparison.OrdinalIgnoreCase) || visibility == "0") return true;
                if (string.Equals(visibility, "Hidden", StringComparison.OrdinalIgnoreCase) || visibility == "1") return false;
            }
        }
        throw Invalid("The Docs catalog has an unsupported visibility value.");
    }
    private static string Title(DocConceptCanonicalPage page) => page.Title.Length > 0 ? page.Title : page.RoutePath.Length > 0 ? page.RoutePath : "Documentation";
    private static string LocaleLabel(string? locale) => locale ?? "Default language";
    private static string TransitionNotice(DocConceptResolutionReason reason, string version) => reason switch
    {
        DocConceptResolutionReason.MovedPage => $"This guide has a different location in {version}.",
        DocConceptResolutionReason.RenamedPage => $"This guide has a different title in {version}.",
        _ => string.Empty
    };

    private static void ValidateBudget(long value)
    {
        if (value is >= ReleaseDocsPublication.MinMaxRewrittenFileSizeBytes and <= ReleaseDocsPublication.MaxMaxRewrittenFileSizeBytes) return;
        throw Failure("release-docs-publication-rewritten-file-size-invalid", "Docs publication received an invalid rewritten artifact size limit.",
            "The supplied byte limit is outside the supported range.", "Pass --max-rewritten-file-size-bytes between 1 and 33554432; the default is 4194304.");
    }

    private static string SafeIssue(DocConceptValidationIssue issue) => issue.Failure switch
    {
        DocConceptValidationFailure.InvalidConceptId => "A concept sidecar contains an invalid concept identifier.",
        DocConceptValidationFailure.ArtifactTooLarge => "A concept sidecar or bridge exceeds the configured byte budget.",
        DocConceptValidationFailure.MapInvalid => "A pinned concept bridge is malformed or conflicts with validated identity data.",
        DocConceptValidationFailure.CanonicalRouteInvalid => "A concept sidecar references a non-canonical or unservable route.",
        _ => "Concept identity data is invalid or ambiguous."
    };

    private static ReleaseToolException Invalid(string detail) => Failure("docs-switch-processing-failed",
        "Static Docs version switching could not produce a complete serving tree.", detail,
        "Repair the exported exact trees and concept metadata, then rerun docs publication.");

    private static ReleaseToolException Failure(string code, string summary, string detail, string fix) => new(ReleaseDiagnostic.Error(
        code, summary, detail, fix, "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));

    private sealed record StaticRelease(string Version, string ManifestSha256, string ExactTreePath, bool IsPublic, bool IsAvailable, DocConceptReleaseInput Input, bool UsesExportRoot = false);
}

internal sealed record DocsStaticServingConfiguration(string InstanceId);
internal sealed record DocsServingReleasePin(string Version, string ReleaseManifestSha256, string ExactTreePath);
internal sealed record DocsServingOutputFile(string Path, string Kind, string? SourceVersion, string? SourceReleaseManifestSha256, long Length, string Sha256);
internal sealed record DocsServingInventory(string Schema, string TransformVersion, IReadOnlyList<DocsServingReleasePin> SourceReleases, IReadOnlyList<DocsServingOutputFile> Files);
internal sealed record DocsServingOutput(string RootPath, string InventoryPath, int GeneratedFileCount, long InventoryBytes, IReadOnlyList<string> VerifiedVersions)
{
    internal static DocsServingOutput Empty(string root) => new(root, Path.Combine(root, ReleaseDocsStaticConceptPublication.InventoryFileName), 0, 0, []);
}
