using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Loads, validates, and writes the frozen route manifest stored inside exported exact-version docs trees.
/// </summary>
/// <remarks>
/// Frozen manifests are archive-local read models. They preserve the route identity that existed when an exact release
/// tree was exported, but they do not replace <see cref="DocRouteIdentityCatalog" /> for live source-backed docs.
/// New exports also bind every logical canonical identity to an ordinary HTML artifact beneath the complete export
/// root and declare <c>htmlUrlBase: export-root</c>. Legacy v1 documents without these optional fields retain logical
/// redirects and docs-relative HTML rewriting. Partial mappings, unsafe paths, unfinished exporter staging, and route
/// namespace collisions disable the entire new manifest; verified archive loading rejects them before activation.
/// </remarks>
internal sealed class AppSurfaceDocsFrozenRouteManifest
{
    /// <summary>Filename used at the root of each exported exact-version docs tree.</summary>
    internal const string FileName = ".appsurface-docs-route-manifest.json";

    private const string Schema = "appsurface-docs-route-manifest-v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly AppSurfaceDocsFrozenRouteManifest EmptyManifest = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, string> _canonicalRoutePathByAlias;
    private readonly IReadOnlyDictionary<string, string> _artifactPathByCanonicalRoute;

    /// <summary>Gets canonical, source-shaped recovery and declared routes captured from verified bytes.</summary>
    /// <remarks>The snapshot owns a read-only copy; consumers cannot change the routes shared by mounted aliases.</remarks>
    internal IReadOnlyList<string> PublicRoutePaths { get; private set; } = [];

    private AppSurfaceDocsFrozenRouteManifest(
        IReadOnlyDictionary<string, string> canonicalRoutePathByAlias,
        IReadOnlyDictionary<string, string>? artifactPathByCanonicalRoute = null,
        bool htmlUsesExportRootUrls = false)
    {
        _canonicalRoutePathByAlias = canonicalRoutePathByAlias;
        _artifactPathByCanonicalRoute = artifactPathByCanonicalRoute ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HtmlUsesExportRootUrls = htmlUsesExportRootUrls;
    }

    /// <summary>Whether HTML URLs are relative to the complete export root rather than the legacy logical docs root.</summary>
    internal bool HtmlUsesExportRootUrls { get; }

    /// <summary>Gets the ordinary HTML members that release verification must bind to this same archive.</summary>
    internal IReadOnlyCollection<string> CanonicalArtifactPaths => Array.AsReadOnly(
        _artifactPathByCanonicalRoute.Values.Select(GetRoutePathPart).ToArray());

    /// <summary>
    /// Gets the empty manifest used when an archive has no frozen route identity or when parsing failed.
    /// </summary>
    internal static AppSurfaceDocsFrozenRouteManifest Empty => EmptyManifest;

    /// <summary>
    /// Writes the frozen route manifest artifact to an export output directory.
    /// </summary>
    /// <param name="outputPath">Export output root that represents one exact-version tree.</param>
    /// <param name="routeManifest">Live route manifest captured from the source-backed docs snapshot.</param>
    /// <param name="cancellationToken">Token observed while writing the artifact.</param>
    /// <returns>A task that completes once the manifest has been written.</returns>
    internal static async Task WriteAsync(
        string outputPath,
        AppSurfaceDocsRouteManifest routeManifest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(routeManifest);

        var contents = Serialize(routeManifest);
        var manifestPath = BuildManifestPath(outputPath, FileName);
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(manifestPath, contents, cancellationToken);
    }

    /// <summary>
    /// Serializes a frozen route manifest document only after strict validation succeeds.
    /// </summary>
    /// <remarks>
    /// This writer-facing API validates with the same strict rules used for exact archive pins. It throws instead of
    /// dropping unsafe entries when a canonical route is missing or unsafe, when aliases are unsafe, self-referential, or
    /// collide with canonical routes, or when duplicate aliases point at different canonical routes. Use it when creating
    /// a new frozen manifest from trusted source-backed route metadata. Use the tolerant loading paths when reading an
    /// existing archive where an invalid manifest should degrade to an empty alias map instead of failing the caller.
    /// </remarks>
    /// <param name="routeManifest">Live route manifest captured from the source-backed docs snapshot.</param>
    /// <returns>The exact JSON payload used for the frozen route manifest artifact.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routeManifest" /> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The route manifest contains unsafe, duplicate, colliding, or ambiguous canonical routes or aliases.
    /// </exception>
    internal static string Serialize(AppSurfaceDocsRouteManifest routeManifest)
    {
        ArgumentNullException.ThrowIfNull(routeManifest);

        var document = CreateDocument(routeManifest);
        ValidateDocument(document, strict: true, out _);
        return JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine;
    }

    /// <summary>Stages live canonical URLs for exporter finalization before release-manifest hashing.</summary>
    /// <param name="routeManifest">The source-backed snapshot whose canonical pages the exporter seeds.</param>
    /// <returns>Strictly validated route JSON containing private <c>exportCanonicalLiveUrl</c> members.</returns>
    /// <remarks>
    /// This is an exporter handoff, not a consumable archive. The exporter must replace every staging member with
    /// the successful ordinary HTML outcome's whole-root <c>canonicalArtifactPath</c> and add
    /// <c>htmlUrlBase: export-root</c> before integrity hashing. Final archive loading rejects leftover staging data.
    /// Use <see cref="Serialize"/> for legacy logical-route fixtures that do not undergo this finalization.
    /// </remarks>
    internal static string SerializeForExport(AppSurfaceDocsRouteManifest routeManifest)
    {
        ArgumentNullException.ThrowIfNull(routeManifest);
        var document = CreateDocument(routeManifest);
        ValidateDocument(document, strict: true, out _);
        document = document with
        {
            Entries = document.Entries.Select((entry, index) => entry with
            {
                ExportCanonicalLiveUrl = JsonSerializer.SerializeToElement(routeManifest.Entries[index].CanonicalLiveUrl)
            }).ToArray()
        };
        return JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine;
    }

    /// <summary>
    /// Builds the on-disk path for a frozen route manifest filename beneath an exact-version export root.
    /// </summary>
    /// <param name="outputPath">Export output root that represents one exact-version tree.</param>
    /// <param name="fileName">Manifest filename to place beneath <paramref name="outputPath" />.</param>
    /// <returns>The manifest path under the export output root.</returns>
    internal static string BuildManifestPath(string outputPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (Path.IsPathRooted(fileName))
        {
            throw new InvalidOperationException("Frozen AppSurface Docs route manifest filename must be relative.");
        }

        return Path.Join(outputPath, fileName);
    }

    /// <summary>
    /// Loads a frozen manifest from a mounted exact-version tree provider.
    /// </summary>
    /// <param name="fileProvider">File provider rooted at the exact-version tree.</param>
    /// <param name="logger">Logger used for malformed-manifest diagnostics.</param>
    /// <param name="sourceDescription">Human-readable tree identity for logs.</param>
    /// <returns>The parsed manifest, or <see cref="Empty" /> when the manifest is missing or unusable.</returns>
    internal static AppSurfaceDocsFrozenRouteManifest Load(
        IFileProvider fileProvider,
        ILogger logger,
        string sourceDescription)
    {
        ArgumentNullException.ThrowIfNull(fileProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var fileInfo = fileProvider.GetFileInfo(FileName);
        if (!fileInfo.Exists)
        {
            return Empty;
        }

        try
        {
            using var stream = fileInfo.CreateReadStream();
            var document = JsonSerializer.Deserialize<FrozenRouteManifestDocument>(stream, SerializerOptions);
            if (document is null || !string.Equals(document.Schema, Schema, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "AppSurface Docs frozen route manifest {ManifestFile} for {SourceDescription} has an unsupported schema.",
                    FileName,
                    sourceDescription);
                return Empty;
            }

            var modern = HasArtifactProjection(document);
            var aliasMap = ValidateDocument(document, strict: modern, out var ignoredRouteCount);
            var artifactMap = ValidateArtifactProjection(document);
            if (ignoredRouteCount > 0)
            {
                logger.LogWarning(
                    "AppSurface Docs frozen route manifest {ManifestFile} for {SourceDescription} ignored {IgnoredRouteCount} unsafe or ambiguous route(s).",
                    FileName,
                    sourceDescription,
                    ignoredRouteCount);
            }

            return CreateSnapshot(document, aliasMap, artifactMap, modern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException)
        {
            logger.LogWarning(
                ex,
                "AppSurface Docs frozen route manifest {ManifestFile} for {SourceDescription} could not be loaded. Archive alias recovery is disabled for this tree.",
                FileName,
                sourceDescription);
            return Empty;
        }
    }

    /// <summary>
    /// Parses verified frozen route manifest bytes that have already matched a catalog-pinned release archive manifest.
    /// </summary>
    /// <param name="utf8Bytes">The exact UTF-8 bytes covered by the verified release archive manifest.</param>
    /// <param name="manifest">The immutable route manifest when parsing succeeds.</param>
    /// <param name="issue">A human-readable parse or validation issue when parsing fails.</param>
    /// <returns><c>true</c> when the verified bytes describe a supported frozen route manifest.</returns>
    internal static bool TryLoadVerified(
        byte[] utf8Bytes,
        out AppSurfaceDocsFrozenRouteManifest manifest,
        out string issue)
    {
        ArgumentNullException.ThrowIfNull(utf8Bytes);

        manifest = Empty;
        issue = string.Empty;

        try
        {
            var document = JsonSerializer.Deserialize<FrozenRouteManifestDocument>(utf8Bytes, SerializerOptions);
            if (document is null || !string.Equals(document.Schema, Schema, StringComparison.Ordinal))
            {
                issue = "unsupported schema";
                return false;
            }

            var aliasMap = ValidateDocument(document, strict: true, out _);
            var artifactMap = ValidateArtifactProjection(document);
            manifest = CreateSnapshot(document, aliasMap, artifactMap, HasArtifactProjection(document));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            issue = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Attempts to resolve a docs-root-relative alias route to its frozen canonical route path.
    /// </summary>
    /// <param name="aliasRoutePath">Docs-root-relative request path.</param>
    /// <param name="canonicalRoutePath">Frozen canonical route path when an alias matches.</param>
    /// <returns><c>true</c> when the alias is known by the frozen manifest; otherwise <c>false</c>.</returns>
    internal bool TryResolveAlias(string aliasRoutePath, out string canonicalRoutePath)
    {
        canonicalRoutePath = string.Empty;
        var normalizedAlias = NormalizeRoutePath(aliasRoutePath);
        if (string.IsNullOrWhiteSpace(normalizedAlias)
            || !_canonicalRoutePathByAlias.TryGetValue(normalizedAlias, out var resolvedCanonicalRoutePath))
        {
            return false;
        }

        canonicalRoutePath = resolvedCanonicalRoutePath;
        return true;
    }

    /// <summary>Resolves a logical canonical identity to its finalized archive HTML member and canonical fragment.</summary>
    /// <param name="canonicalRoutePath">The logical identity, including the empty docs-root identity.</param>
    /// <param name="artifactPath">Whole-export-root relative HTML member, with the frozen canonical fragment.</param>
    /// <returns>Whether this archive supplies a finalized mapping for the identity.</returns>
    internal bool TryResolveCanonicalArtifactPath(string canonicalRoutePath, out string artifactPath)
    {
        return _artifactPathByCanonicalRoute.TryGetValue(
            GetRoutePathPart(NormalizeRoutePath(canonicalRoutePath)), out artifactPath!);
    }

    internal static string NormalizeRoutePath(string? routePath)
    {
        if (string.IsNullOrWhiteSpace(routePath))
        {
            return string.Empty;
        }

        return routePath.Trim().TrimStart('/');
    }

    /// <summary>
    /// Checks whether a docs-root-relative route path is safe to use as a frozen alias or redirect target.
    /// </summary>
    /// <param name="routePath">The docs-root-relative route path, optionally including a canonical fragment.</param>
    /// <returns>
    /// <c>true</c> when the route stays inside the docs archive namespace; otherwise <c>false</c>.
    /// </returns>
    internal static bool IsSafeRoutePath(string? routePath)
    {
        var normalizedRoutePath = NormalizeRoutePath(routePath);
        var pathPart = GetRoutePathPart(normalizedRoutePath);

        if (pathPart.Length == 0)
        {
            return true;
        }

        if (pathPart.Contains('\\', StringComparison.Ordinal)
            || pathPart.Contains('?', StringComparison.Ordinal)
            || pathPart.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return pathPart.Split('/')
            .All(segment => !string.IsNullOrWhiteSpace(segment)
                            && !segment.StartsWith(".", StringComparison.Ordinal));
    }

    private static FrozenRouteManifestDocument CreateDocument(AppSurfaceDocsRouteManifest routeManifest)
    {
        var entries = routeManifest.Entries
            .Select(
                entry => new FrozenRouteManifestEntry(
                    entry.SourcePath,
                    NormalizeRoutePath(entry.CanonicalRoutePath),
                    entry.RecoveryAliases.Select(alias => NormalizeRoutePath(alias.RoutePath)).Where(path => path.Length > 0).ToArray(),
                    entry.DeclaredAliases.Select(alias => NormalizeRoutePath(alias.RoutePath)).Where(path => path.Length > 0).ToArray()))
            .ToArray();

        return new FrozenRouteManifestDocument(Schema, entries);
    }

    private static IReadOnlyDictionary<string, string> ValidateDocument(
        FrozenRouteManifestDocument document,
        bool strict,
        out int ignoredRouteCount)
    {
        ignoredRouteCount = 0;
        var hasArtifactProjection = HasArtifactProjection(document);
        var canonicalRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsafeCanonicalRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries ?? [])
        {
            if (entry is null || entry.CanonicalRoutePath is null)
            {
                throw new InvalidOperationException("Frozen AppSurface Docs route manifest entries require canonicalRoutePath values.");
            }

            if (hasArtifactProjection
                && (entry.CanonicalRoutePath.Any(char.IsControl)
                    || (entry.RecoveryAliases ?? []).Concat(entry.DeclaredAliases ?? [])
                        .Any(alias => alias?.Any(char.IsControl) == true)))
            {
                throw new InvalidOperationException("Frozen AppSurface Docs artifact route identities cannot contain control characters.");
            }

            var canonicalRoutePath = NormalizeRoutePath(entry.CanonicalRoutePath);
            var canonicalRoutePathPart = GetRoutePathPart(canonicalRoutePath);
            if (!IsSafeRoutePath(canonicalRoutePath))
            {
                if (strict)
                {
                    throw new InvalidOperationException($"Frozen AppSurface Docs route manifest contains unsafe canonical route '{canonicalRoutePath}'.");
                }

                ignoredRouteCount++;
                unsafeCanonicalRoutes.Add(canonicalRoutePath);
                continue;
            }

            if (!canonicalRoutes.Add(canonicalRoutePathPart) && strict)
            {
                throw new InvalidOperationException($"Frozen AppSurface Docs route manifest contains duplicate canonical route '{canonicalRoutePath}'.");
            }
        }

        var canonicalRouteByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries ?? [])
        {
            if (entry.CanonicalRoutePath is null)
            {
                continue;
            }

            var canonicalRoutePath = NormalizeRoutePath(entry.CanonicalRoutePath);
            var canonicalRoutePathPart = GetRoutePathPart(canonicalRoutePath);
            if (unsafeCanonicalRoutes.Contains(canonicalRoutePath))
            {
                continue;
            }

            foreach (var aliasRoutePath in (entry.RecoveryAliases ?? [])
                         .Concat(entry.DeclaredAliases ?? [])
                         .Select(NormalizeRoutePath))
            {
                if (string.IsNullOrWhiteSpace(aliasRoutePath))
                {
                    continue;
                }

                if (!IsSafeRoutePath(aliasRoutePath))
                {
                    if (strict)
                    {
                        throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' is unsafe.");
                    }

                    ignoredRouteCount++;
                    continue;
                }

                var aliasRoutePathPart = GetRoutePathPart(aliasRoutePath);
                if (string.Equals(aliasRoutePathPart, canonicalRoutePathPart, StringComparison.OrdinalIgnoreCase))
                {
                    if (strict)
                    {
                        throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' matches its canonical route.");
                    }

                    ambiguousAliases.Add(aliasRoutePath);
                    ignoredRouteCount++;
                    continue;
                }

                if (canonicalRoutes.Contains(aliasRoutePathPart))
                {
                    if (strict)
                    {
                        throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' collides with a canonical route.");
                    }

                    ambiguousAliases.Add(aliasRoutePath);
                    ignoredRouteCount++;
                    continue;
                }

                if (canonicalRouteByAlias.TryGetValue(aliasRoutePath, out var existingCanonicalRoute)
                    && strict)
                {
                    var issue = string.Equals(existingCanonicalRoute, canonicalRoutePath, StringComparison.OrdinalIgnoreCase)
                        ? "is duplicated"
                        : "points at multiple canonical routes";
                    throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' {issue}.");
                }

                if (canonicalRouteByAlias.TryGetValue(aliasRoutePath, out existingCanonicalRoute)
                    && !string.Equals(existingCanonicalRoute, canonicalRoutePath, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguousAliases.Add(aliasRoutePath);
                    ignoredRouteCount++;
                    continue;
                }

                canonicalRouteByAlias[aliasRoutePath] = canonicalRoutePath;
            }
        }

        foreach (var ambiguousAlias in ambiguousAliases)
        {
            canonicalRouteByAlias.Remove(ambiguousAlias);
        }

        return canonicalRouteByAlias;
    }

    /// <summary>Keeps complete route identities even for a valid manifest with no redirects.</summary>
    private static AppSurfaceDocsFrozenRouteManifest CreateSnapshot(FrozenRouteManifestDocument document,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> artifacts,
        bool htmlUsesExportRootUrls)
    {
        return new AppSurfaceDocsFrozenRouteManifest(aliases, artifacts, htmlUsesExportRootUrls)
        {
            PublicRoutePaths = Array.AsReadOnly((document.Entries ?? []).SelectMany(entry =>
                new[] { entry.CanonicalRoutePath, entry.SourcePath }.Concat(entry.RecoveryAliases ?? []).Concat(entry.DeclaredAliases ?? []))
                .Where(path => path is not null && IsSafeRoutePath(NormalizeRoutePath(path)))
                .Select(path => NormalizeRoutePath(path!)).ToArray())
        };
    }

    private static string GetRoutePathPart(string routePath)
    {
        var fragmentIndex = routePath.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex < 0
            ? routePath
            : routePath[..fragmentIndex];
    }

    private static bool HasArtifactProjection(FrozenRouteManifestDocument document)
    {
        return document.HtmlUrlBase is not null
               || (document.Entries ?? []).Any(entry => entry?.CanonicalArtifactPath is not null);
    }

    private static IReadOnlyDictionary<string, string> ValidateArtifactProjection(FrozenRouteManifestDocument document)
    {
        var entries = document.Entries ?? [];
        if (entries.Any(entry => entry.ExportCanonicalLiveUrl.ValueKind != JsonValueKind.Undefined))
        {
            throw new InvalidOperationException("Frozen AppSurface Docs route manifest contains unfinished exportCanonicalLiveUrl staging data.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!HasArtifactProjection(document))
        {
            return result;
        }

        if (!string.Equals(document.HtmlUrlBase, "export-root", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Frozen AppSurface Docs artifact mappings require htmlUrlBase 'export-root'.");
        }

        var canonicalRoutes = entries.Select(entry => GetRoutePathPart(NormalizeRoutePath(entry.CanonicalRoutePath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliasPaths = entries.SelectMany(entry => (entry.RecoveryAliases ?? []).Concat(entry.DeclaredAliases ?? []))
            .Select(alias => GetRoutePathPart(NormalizeRoutePath(alias))).ToArray();
        var aliases = aliasPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (aliasPaths.Any(string.IsNullOrWhiteSpace) || aliases.Count != aliasPaths.Length)
        {
            throw new InvalidOperationException("Frozen AppSurface Docs artifact aliases require distinct nonempty route paths, independent of fragments.");
        }

        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var artifact = entry.CanonicalArtifactPath;
            if (!IsSafeArtifactPath(artifact))
            {
                throw new InvalidOperationException("Frozen AppSurface Docs canonicalArtifactPath must be a safe whole-root relative HTML file.");
            }

            var canonical = NormalizeRoutePath(entry.CanonicalRoutePath);
            var canonicalPath = GetRoutePathPart(canonical);
            if (!artifacts.Add(artifact!)
                || aliases.Contains(artifact!)
                || (canonicalRoutes.Contains(artifact!) && !string.Equals(artifact, canonicalPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Frozen AppSurface Docs canonicalArtifactPath '{artifact}' collides with another route, alias, or artifact.");
            }

            result.Add(canonicalPath, artifact + canonical[canonicalPath.Length..]);
        }

        return result;
    }

    private static bool IsSafeArtifactPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && string.Equals(path, path.Trim(), StringComparison.Ordinal)
               && !path.StartsWith("/", StringComparison.Ordinal)
               && !path.Any(char.IsControl)
               && path.IndexOfAny(['\\', ':', '?', '#', '%']) < 0
               && !path.Contains("//", StringComparison.Ordinal)
               && path.Split('/').All(segment => !string.IsNullOrWhiteSpace(segment) && !segment.StartsWith(".", StringComparison.Ordinal))
               && Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record FrozenRouteManifestDocument(
        string Schema,
        IReadOnlyList<FrozenRouteManifestEntry> Entries,
        string? HtmlUrlBase = null);

    private sealed record FrozenRouteManifestEntry(
        string SourcePath,
        string CanonicalRoutePath,
        IReadOnlyList<string> RecoveryAliases,
        IReadOnlyList<string> DeclaredAliases,
        string? CanonicalArtifactPath = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement ExportCanonicalLiveUrl = default);
}

/// <summary>
/// Caches the frozen route manifest for one mounted exact-version tree.
/// </summary>
internal sealed class AppSurfaceDocsFrozenRouteManifestCache
{
    private readonly IFileProvider? _fileProvider;
    private readonly string _sourceDescription;
    private readonly object _gate = new();
    private AppSurfaceDocsFrozenRouteManifest? _manifest;

    /// <summary>
    /// Initializes a new instance of <see cref="AppSurfaceDocsFrozenRouteManifestCache" />.
    /// </summary>
    /// <param name="fileProvider">File provider rooted at the exact-version tree.</param>
    /// <param name="sourceDescription">Human-readable tree identity used for diagnostics.</param>
    internal AppSurfaceDocsFrozenRouteManifestCache(IFileProvider fileProvider, string sourceDescription)
    {
        _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        _sourceDescription = string.IsNullOrWhiteSpace(sourceDescription) ? "published tree" : sourceDescription;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="AppSurfaceDocsFrozenRouteManifestCache" /> with already-verified manifest data.
    /// </summary>
    /// <param name="manifest">Manifest parsed from bytes covered by the verified release archive manifest.</param>
    /// <param name="sourceDescription">Human-readable tree identity used for diagnostics.</param>
    internal AppSurfaceDocsFrozenRouteManifestCache(AppSurfaceDocsFrozenRouteManifest manifest, string sourceDescription)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _sourceDescription = string.IsNullOrWhiteSpace(sourceDescription) ? "verified published tree" : sourceDescription;
    }

    /// <summary>
    /// Gets a value indicating whether this cache is backed by manifest data parsed during release archive verification.
    /// </summary>
    internal bool UsesVerifiedSnapshot => _fileProvider is null;

    /// <summary>
    /// Returns the cached manifest, loading it from the tree on first use.
    /// </summary>
    /// <param name="logger">Logger used when a present manifest cannot be loaded.</param>
    /// <returns>The cached frozen route manifest, or an empty manifest when unavailable.</returns>
    internal AppSurfaceDocsFrozenRouteManifest GetManifest(ILogger logger)
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        lock (_gate)
        {
            _manifest ??= AppSurfaceDocsFrozenRouteManifest.Load(_fileProvider!, logger, _sourceDescription);
            return _manifest;
        }
    }
}
