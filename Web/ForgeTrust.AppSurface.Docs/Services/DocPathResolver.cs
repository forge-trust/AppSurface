using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Resolves authored, source, and canonical RazorDocs paths against a harvested documentation corpus.
/// </summary>
/// <remarks>
/// RazorDocs accepts paths from several authoring surfaces: browser routes, source-relative Markdown metadata, generated
/// canonical paths, and route-prefixed links. This resolver is the shared source of truth for trimming route separators,
/// ignoring lookup fragments when selecting candidate buckets, preserving exact fragment matches when available, and
/// ranking fallback candidates when a fragment-specific page is not present.
/// </remarks>
internal sealed class DocPathResolver
{
    private readonly Dictionary<string, DocLookupBucket> _lookup;

    private DocPathResolver(Dictionary<string, DocLookupBucket> lookup)
    {
        _lookup = lookup;
    }

    /// <summary>
    /// Builds a resolver for a docs snapshot.
    /// </summary>
    /// <param name="docs">The harvested docs whose source and canonical paths should be resolvable.</param>
    /// <returns>A resolver that can match source paths, canonical route paths, and route-relative variants.</returns>
    internal static DocPathResolver Create(IEnumerable<DocNode> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);

        var lookup = new Dictionary<string, DocLookupBucket>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in docs)
        {
            var sourcePath = NormalizeLookupPath(doc.Path);
            AddLookupEntry(lookup, sourcePath, doc);

            if (!string.IsNullOrWhiteSpace(doc.CanonicalPath))
            {
                AddLookupEntry(lookup, NormalizeLookupPath(doc.CanonicalPath), doc);
            }
        }

        return new DocPathResolver(lookup);
    }

    /// <summary>
    /// Resolves a path exactly as authored, using RazorDocs source and canonical matching rules.
    /// </summary>
    /// <param name="path">The authored source or canonical path to resolve.</param>
    /// <returns>The best matching doc node, or <c>null</c> when no harvested doc matches.</returns>
    internal DocNode? Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return ResolveNormalizedPath(path);
    }

    /// <summary>
    /// Resolves a path by preserving authored source-relative matches before stripping known docs route roots from
    /// browser-facing inputs.
    /// </summary>
    /// <param name="path">The authored or browser-facing path to resolve.</param>
    /// <param name="routeRootPaths">Route roots, such as the configured live docs root and the stable <c>/docs</c> root.</param>
    /// <returns>The best matching doc node, or <c>null</c> when neither route-relative variants nor the original path match.</returns>
    internal DocNode? Resolve(string path, params string[] routeRootPaths)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(routeRootPaths);

        var isRootedBrowserPath = IsRootedBrowserPath(path);
        if (!isRootedBrowserPath)
        {
            var authoredMatch = ResolveNormalizedPath(path);
            if (authoredMatch is not null)
            {
                return authoredMatch;
            }
        }

        var seenRouteRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var routeRootPath in routeRootPaths)
        {
            if (!TryStripRouteRoot(path, routeRootPath, out var routeRelativePath)
                || !seenRouteRelativePaths.Add(routeRelativePath))
            {
                continue;
            }

            var resolved = ResolveNormalizedPath(routeRelativePath);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return isRootedBrowserPath ? ResolveNormalizedPath(path) : null;
    }

    /// <summary>
    /// Normalizes a documentation path for lookup by trimming route separators and removing fragment anchors.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized lookup path.</returns>
    internal static string NormalizeLookupPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sanitized = path.Trim().Replace('\\', '/').Trim('/');
        var hashIndex = sanitized.IndexOf('#');
        if (hashIndex >= 0)
        {
            sanitized = sanitized[..hashIndex];
        }

        return sanitized;
    }

    /// <summary>
    /// Normalizes a documentation path for canonical comparison by trimming route separators while preserving fragments.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized canonical path.</returns>
    internal static string NormalizeCanonicalPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Trim().Replace('\\', '/').Trim('/');
    }

    /// <summary>
    /// Extracts a fragment from a documentation path after canonical normalization.
    /// </summary>
    /// <param name="path">The path that may contain a fragment anchor.</param>
    /// <returns>The fragment without the leading <c>#</c>, or <c>null</c> when no non-empty fragment exists.</returns>
    internal static string? GetFragment(string path)
    {
        var canonical = NormalizeCanonicalPath(path);
        var hashIndex = canonical.IndexOf('#');
        if (hashIndex < 0 || hashIndex == canonical.Length - 1)
        {
            return null;
        }

        return canonical[(hashIndex + 1)..];
    }

    private static void AddLookupEntry(Dictionary<string, DocLookupBucket> lookup, string key, DocNode doc)
    {
        if (!lookup.TryGetValue(key, out var bucket))
        {
            bucket = new DocLookupBucket();
            lookup[key] = bucket;
        }

        if (bucket.SeenDocs.Add(doc))
        {
            bucket.OrderedDocs.Add(doc);
        }
    }

    private DocNode? ResolveNormalizedPath(string path)
    {
        var lookupPath = NormalizeLookupPath(path);
        var lookupCanonicalPath = NormalizeCanonicalPath(path);

        if (!_lookup.TryGetValue(lookupPath, out var bucket) || bucket.OrderedDocs.Count == 0)
        {
            return null;
        }

        var candidates = bucket.OrderedDocs;
        var exactCanonicalMatch = candidates.FirstOrDefault(
            doc => (!string.IsNullOrWhiteSpace(doc.CanonicalPath)
                    && string.Equals(
                        NormalizeCanonicalPath(doc.CanonicalPath),
                        lookupCanonicalPath,
                        StringComparison.OrdinalIgnoreCase))
                   || string.Equals(
                       NormalizeCanonicalPath(doc.Path),
                       lookupCanonicalPath,
                       StringComparison.OrdinalIgnoreCase));
        if (exactCanonicalMatch is not null)
        {
            return exactCanonicalMatch;
        }

        return candidates
            .OrderBy(doc => string.IsNullOrWhiteSpace(GetFragment(doc.CanonicalPath ?? doc.Path)) ? 0 : 1)
            .ThenBy(doc => string.IsNullOrWhiteSpace(doc.Content) ? 1 : 0)
            .ThenBy(doc => doc.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsRootedBrowserPath(string path)
    {
        var trimmedPath = path.TrimStart();
        return trimmedPath.StartsWith("/", StringComparison.Ordinal)
               || trimmedPath.StartsWith("\\", StringComparison.Ordinal);
    }

    private static bool TryStripRouteRoot(string path, string routeRootPath, out string routeRelativePath)
    {
        routeRelativePath = path;

        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(routeRootPath))
        {
            return false;
        }

        var normalizedPath = path.Trim().Replace('\\', '/').TrimStart('/');
        var normalizedRouteRoot = routeRootPath.Trim().Replace('\\', '/').Trim('/');
        if (normalizedRouteRoot.Length == 0)
        {
            return false;
        }

        if (string.Equals(normalizedPath, normalizedRouteRoot, StringComparison.OrdinalIgnoreCase))
        {
            routeRelativePath = string.Empty;
            return true;
        }

        var routePrefix = normalizedRouteRoot + "/";
        if (normalizedPath.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
        {
            routeRelativePath = normalizedPath[routePrefix.Length..];
            return true;
        }

        return false;
    }

    private sealed class DocLookupBucket
    {
        internal List<DocNode> OrderedDocs { get; } = [];

        internal HashSet<DocNode> SeenDocs { get; } = new(ReferenceEqualityComparer.Instance);
    }
}
