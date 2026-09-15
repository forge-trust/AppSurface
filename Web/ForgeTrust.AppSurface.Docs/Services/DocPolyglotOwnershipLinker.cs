using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Connects accepted Python module pages with their explicitly declared C# host types.
/// </summary>
/// <remarks>
/// Both markers are emitted only by built-in static harvesters. The linker intentionally waits until every harvester
/// has succeeded, then requires one accepted Python module and one documented C# owner for each module path. Ambiguous
/// or unmatched declarations disappear from rendered HTML instead of creating a best-effort or potentially broken
/// relationship. The marker values are percent-encoded repository-relative paths so source text never becomes markup.
/// </remarks>
internal static partial class DocPolyglotOwnershipLinker
{
    private const string PythonModuleMarkerAttribute = "data-appsurfacedocs-python-module";
    private const string PythonOwnerMarkerAttribute = "data-appsurfacedocs-python-owner";
    private const string PythonOwnerAnchorAttribute = "data-appsurfacedocs-python-owner-anchor";
    private const string PythonOwnerLabelAttribute = "data-appsurfacedocs-python-owner-label";

    /// <summary>
    /// Replaces internal ownership markers with reciprocal, docs-local links.
    /// </summary>
    /// <param name="nodes">Generated documentation nodes from the current aggregation pass, before sanitization removes internal markers.</param>
    /// <param name="docsRootPath">Current live docs root used to build route-base-aware links.</param>
    /// <returns>Nodes whose ownership markers have been replaced or removed.</returns>
    internal static IReadOnlyList<DocNode> Link(IReadOnlyList<DocNode> nodes, string docsRootPath)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentException.ThrowIfNullOrWhiteSpace(docsRootPath);

        var pythonModules = nodes
            .Where(IsPythonModulePage)
            .SelectMany(node => PythonModuleMarkerRegex().Matches(node.Content).Select(match => new PythonModule(node, DecodePath(match.Groups["path"].Value))))
            .Where(static module => module.RelativePath is not null)
            .GroupBy(static module => module.RelativePath!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var csharpOwners = nodes
            .Where(IsCSharpApiPage)
            .SelectMany(node => PythonOwnerMarkerRegex().Matches(node.Content).Select(match => new CSharpOwner(
                node,
                DecodePath(match.Groups["path"].Value),
                DecodeAnchor(match.Groups["anchor"].Value),
                DecodeDisplayName(match.Groups["label"].Value))))
            .Where(static owner => owner.RelativePath is not null && owner.AnchorId is not null && owner.DisplayName is not null)
            .GroupBy(static owner => owner.RelativePath!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        if (pythonModules.Count == 0 && csharpOwners.Count == 0)
        {
            return nodes;
        }

        var linksByPythonNode = new Dictionary<DocNode, OwnershipLink>(ReferenceEqualityComparer.Instance);
        var linksByCSharpNodeAndAnchor = new Dictionary<(DocNode Node, string Anchor), OwnershipLink>(new OwnerLinkKeyComparer());
        foreach (var (relativePath, modules) in pythonModules)
        {
            if (modules.Length != 1
                || !csharpOwners.TryGetValue(relativePath, out var owners)
                || owners.Length != 1)
            {
                continue;
            }

            var module = modules[0];
            var owner = owners[0];
            var link = new OwnershipLink(module.Node, owner.Node, owner.AnchorId!, owner.DisplayName!, relativePath);
            linksByPythonNode.Add(module.Node, link);
            linksByCSharpNodeAndAnchor.Add((owner.Node, owner.AnchorId!), link);
        }

        return nodes
            .Select(node => ReplaceMarkers(node, docsRootPath, linksByPythonNode, linksByCSharpNodeAndAnchor))
            .ToArray();
    }

    /// <summary>
    /// Creates a marker for an accepted Python module source path.
    /// </summary>
    internal static string CreatePythonModuleMarker(string relativePath)
    {
        return $"<span {PythonModuleMarkerAttribute}=\"{WebUtility.HtmlEncode(Uri.EscapeDataString(relativePath))}\"></span>";
    }

    /// <summary>
    /// Creates a marker for a documented C# type that explicitly owns a Python module.
    /// </summary>
    internal static string CreateCSharpOwnerMarker(string relativePath, string anchorId, string displayName)
    {
        return $"<span {PythonOwnerMarkerAttribute}=\"{WebUtility.HtmlEncode(Uri.EscapeDataString(relativePath))}\" {PythonOwnerAnchorAttribute}=\"{WebUtility.HtmlEncode(Uri.EscapeDataString(anchorId))}\" {PythonOwnerLabelAttribute}=\"{WebUtility.HtmlEncode(Uri.EscapeDataString(displayName))}\"></span>";
    }

    private static DocNode ReplaceMarkers(
        DocNode node,
        string docsRootPath,
        IReadOnlyDictionary<DocNode, OwnershipLink> linksByPythonNode,
        IReadOnlyDictionary<(DocNode Node, string Anchor), OwnershipLink> linksByCSharpNodeAndAnchor)
    {
        if (!node.Content.Contains(PythonModuleMarkerAttribute, StringComparison.Ordinal)
            && !node.Content.Contains(PythonOwnerMarkerAttribute, StringComparison.Ordinal))
        {
            return node;
        }

        var content = PythonModuleMarkerRegex().Replace(
            node.Content,
            match =>
            {
                if (!linksByPythonNode.TryGetValue(node, out var link)
                    || !string.Equals(DecodePath(match.Groups["path"].Value), link.RelativePath, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                var ownerHref = DocsUrlBuilder.BuildDocUrl(docsRootPath, $"{link.CSharpNode.Path}#{link.CSharpAnchorId}");
                return $"<p class=\"doc-polyglot-link\">C# host: <a href=\"{WebUtility.HtmlEncode(ownerHref)}\">{WebUtility.HtmlEncode(link.CSharpDisplayName)}</a></p>";
            });
        content = PythonOwnerMarkerRegex().Replace(
            content,
            match =>
            {
                var path = DecodePath(match.Groups["path"].Value);
                var anchor = DecodeAnchor(match.Groups["anchor"].Value);
                if (path is null
                    || anchor is null
                    || !linksByCSharpNodeAndAnchor.TryGetValue((node, anchor), out var link)
                    || !string.Equals(path, link.RelativePath, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                var moduleHref = DocsUrlBuilder.BuildDocUrl(docsRootPath, link.PythonNode.Path);
                return $"<span class=\"doc-polyglot-link\">Python module: <a href=\"{WebUtility.HtmlEncode(moduleHref)}\">{WebUtility.HtmlEncode(link.RelativePath)}</a></span>";
            });
        return content == node.Content ? node : node with { Content = content };
    }

    private static bool IsPythonModulePage(DocNode node)
    {
        return string.Equals(node.Metadata?.CodeLanguage, "python", StringComparison.Ordinal)
               && string.Equals(node.Metadata?.PageType, "python-module", StringComparison.Ordinal)
               && node.Path.StartsWith("api/python/", StringComparison.Ordinal);
    }

    private static bool IsCSharpApiPage(DocNode node)
    {
        return string.Equals(node.Metadata?.CodeLanguage, "csharp", StringComparison.Ordinal)
               && string.Equals(node.Metadata?.PageType, "api-reference", StringComparison.Ordinal)
               && node.Path.StartsWith("Namespaces/", StringComparison.Ordinal);
    }

    private static string? DecodePath(string encodedPath)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(encodedPath);
            return IsValidPath(decoded) ? decoded : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? DecodeAnchor(string encodedAnchor)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(encodedAnchor);
            return AnchorRegex().IsMatch(decoded) ? decoded : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? DecodeDisplayName(string encodedDisplayName)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(encodedDisplayName);
            return !string.IsNullOrWhiteSpace(decoded) && decoded.Length <= 512 ? decoded : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool IsValidPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
               && path.IndexOf('\\') < 0
               && !path.StartsWith("/", StringComparison.Ordinal)
               && path.Split('/').All(static segment => !string.IsNullOrWhiteSpace(segment) && segment is not "." and not "..");
    }

    [GeneratedRegex("<span\\s+data-appsurfacedocs-python-module=\\\"(?<path>[A-Za-z0-9%._~-]+)\\\"\\s*></span>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PythonModuleMarkerRegex();

    [GeneratedRegex("<span\\s+data-appsurfacedocs-python-owner=\\\"(?<path>[A-Za-z0-9%._~-]+)\\\"\\s+data-appsurfacedocs-python-owner-anchor=\\\"(?<anchor>[A-Za-z0-9%._~-]+)\\\"\\s+data-appsurfacedocs-python-owner-label=\\\"(?<label>[A-Za-z0-9%._~-]+)\\\"\\s*></span>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PythonOwnerMarkerRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AnchorRegex();

    private sealed record PythonModule(DocNode Node, string? RelativePath);

    private sealed record CSharpOwner(DocNode Node, string? RelativePath, string? AnchorId, string? DisplayName);

    private sealed record OwnershipLink(DocNode PythonNode, DocNode CSharpNode, string CSharpAnchorId, string CSharpDisplayName, string RelativePath);

    private sealed class OwnerLinkKeyComparer : IEqualityComparer<(DocNode Node, string Anchor)>
    {
        /// <inheritdoc />
        public bool Equals((DocNode Node, string Anchor) x, (DocNode Node, string Anchor) y)
        {
            return ReferenceEquals(x.Node, y.Node) && string.Equals(x.Anchor, y.Anchor, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public int GetHashCode((DocNode Node, string Anchor) value)
        {
            return HashCode.Combine(RuntimeHelpers.GetHashCode(value.Node), StringComparer.Ordinal.GetHashCode(value.Anchor));
        }
    }
}
