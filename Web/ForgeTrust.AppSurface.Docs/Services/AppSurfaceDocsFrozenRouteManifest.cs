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

    private AppSurfaceDocsFrozenRouteManifest(IReadOnlyDictionary<string, string> canonicalRoutePathByAlias)
    {
        _canonicalRoutePathByAlias = canonicalRoutePathByAlias;
    }

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

        var document = CreateDocument(routeManifest);
        ValidateDocument(document, strict: true, out _);

        Directory.CreateDirectory(outputPath);
        var manifestPath = BuildManifestPath(outputPath, FileName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine,
            cancellationToken);
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

            var aliasMap = ValidateDocument(document, strict: false, out var ignoredRouteCount);
            if (ignoredRouteCount > 0)
            {
                logger.LogWarning(
                    "AppSurface Docs frozen route manifest {ManifestFile} for {SourceDescription} ignored {IgnoredRouteCount} unsafe or ambiguous route(s).",
                    FileName,
                    sourceDescription,
                    ignoredRouteCount);
            }

            return aliasMap.Count == 0 ? Empty : new AppSurfaceDocsFrozenRouteManifest(aliasMap);
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
        var fragmentIndex = normalizedRoutePath.IndexOf('#', StringComparison.Ordinal);
        var pathPart = fragmentIndex < 0
            ? normalizedRoutePath
            : normalizedRoutePath[..fragmentIndex];

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
        var canonicalRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsafeCanonicalRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries ?? [])
        {
            if (entry.CanonicalRoutePath is null)
            {
                throw new InvalidOperationException("Frozen AppSurface Docs route manifest entries require canonicalRoutePath values.");
            }

            var canonicalRoutePath = NormalizeRoutePath(entry.CanonicalRoutePath);
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

            if (!canonicalRoutes.Add(canonicalRoutePath) && strict)
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

                if (string.Equals(aliasRoutePath, canonicalRoutePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (strict)
                    {
                        throw new InvalidOperationException($"Frozen AppSurface Docs route manifest alias '{aliasRoutePath}' matches its canonical route.");
                    }

                    ambiguousAliases.Add(aliasRoutePath);
                    ignoredRouteCount++;
                    continue;
                }

                if (canonicalRoutes.Contains(aliasRoutePath))
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

    private sealed record FrozenRouteManifestDocument(
        string Schema,
        IReadOnlyList<FrozenRouteManifestEntry> Entries);

    private sealed record FrozenRouteManifestEntry(
        string SourcePath,
        string CanonicalRoutePath,
        IReadOnlyList<string> RecoveryAliases,
        IReadOnlyList<string> DeclaredAliases);
}

/// <summary>
/// Caches the frozen route manifest for one mounted exact-version tree.
/// </summary>
internal sealed class AppSurfaceDocsFrozenRouteManifestCache
{
    private readonly IFileProvider _fileProvider;
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
            _manifest ??= AppSurfaceDocsFrozenRouteManifest.Load(_fileProvider, logger, _sourceDescription);
            return _manifest;
        }
    }
}
