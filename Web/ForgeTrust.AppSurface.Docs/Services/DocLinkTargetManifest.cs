using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Enumerates the documentation targets harvested into a RazorDocs snapshot so link rewriting can avoid guessing from
/// file extensions alone.
/// </summary>
/// <remarks>
/// The manifest stores both source paths, such as <c>guides/start.md</c>, and canonical browser paths, such as
/// <c>guides/start.md.html</c>. Query strings and fragments are intentionally ignored because anchors and query
/// parameters decorate a page target rather than defining a separate harvested document.
/// </remarks>
internal sealed class DocLinkTargetManifest
{
    private readonly HashSet<string> _targets;

    private DocLinkTargetManifest(HashSet<string> targets)
    {
        _targets = targets;
    }

    /// <summary>
    /// Creates a manifest from harvested documentation nodes.
    /// </summary>
    /// <param name="nodes">The harvested documentation nodes that may be linked through RazorDocs routes.</param>
    /// <returns>A manifest containing source and canonical target forms for the supplied nodes.</returns>
    internal static DocLinkTargetManifest FromNodes(IEnumerable<DocNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        return FromPaths(nodes.SelectMany(node => new[] { node.Path, node.CanonicalPath }));
    }

    /// <summary>
    /// Creates a manifest from source or canonical documentation paths.
    /// </summary>
    /// <param name="paths">The documentation paths to register as known link targets.</param>
    /// <returns>A manifest containing normalized source and canonical target forms.</returns>
    internal static DocLinkTargetManifest FromPaths(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            AddTargetVariants(targets, path);
        }

        return new DocLinkTargetManifest(targets);
    }

    /// <summary>
    /// Determines whether the supplied path resolves to a harvested documentation target.
    /// </summary>
    /// <param name="path">A source or canonical documentation path, optionally rooted, queried, or fragmented.</param>
    /// <returns><c>true</c> when the normalized target is in the manifest; otherwise <c>false</c>.</returns>
    internal bool Contains(string? path)
    {
        var normalizedPath = NormalizeTargetPath(path);
        return !string.IsNullOrEmpty(normalizedPath)
               && _targets.Contains(normalizedPath);
    }

    private static void AddTargetVariants(HashSet<string> targets, string? path)
    {
        var normalizedPath = NormalizeTargetPath(path);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return;
        }

        targets.Add(normalizedPath);
        targets.Add(DocRoutePath.BuildCanonicalPath(normalizedPath));
    }

    private static string NormalizeTargetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('\\', '/');
        var isRootedDocsRoute = normalized.StartsWith("/docs/", StringComparison.OrdinalIgnoreCase);
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        var queryIndex = normalized.IndexOf('?');
        if (queryIndex >= 0)
        {
            normalized = normalized[..queryIndex];
        }

        normalized = normalized.Trim('/');
        if (isRootedDocsRoute)
        {
            normalized = TrimKnownDocsRootPrefix(normalized);
        }

        return normalized;
    }

    private static string TrimKnownDocsRootPrefix(string normalizedPath)
    {
        const string docsRoot = "docs/";
        if (!normalizedPath.StartsWith(docsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        var remaining = normalizedPath[docsRoot.Length..];
        if (remaining.StartsWith("next/", StringComparison.OrdinalIgnoreCase))
        {
            return remaining["next/".Length..];
        }

        if (remaining.StartsWith("v/", StringComparison.OrdinalIgnoreCase))
        {
            var versionSeparatorIndex = remaining.IndexOf('/', "v/".Length);
            return versionSeparatorIndex >= 0
                ? remaining[(versionSeparatorIndex + 1)..]
                : string.Empty;
        }

        return remaining;
    }
}
