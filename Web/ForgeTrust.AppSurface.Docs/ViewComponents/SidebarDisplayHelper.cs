using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.ViewComponents;

/// <summary>
/// Normalizes RazorDocs sidebar labels, grouping, and namespace display names.
/// </summary>
/// <remarks>
/// <see cref="SidebarDisplayHelper"/> treats the authored <c>Namespaces</c> route family specially so API reference
/// pages stay grouped together. Path inputs are trimmed, slash-normalized, and allowed to be empty only where the
/// method explicitly documents the fallback.
/// </remarks>
internal static class SidebarDisplayHelper
{
    /// <summary>
    /// Gets the sidebar group for a documentation node, honoring explicit metadata except for namespace pages.
    /// </summary>
    /// <param name="node">The documentation node to classify.</param>
    /// <returns>The normalized group name, <c>Namespaces</c> for namespace pages, or a path-derived fallback.</returns>
    internal static string GetGroupName(DocNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var pathGroup = GetGroupName(node.Path);
        if (string.Equals(pathGroup, "Namespaces", StringComparison.Ordinal))
        {
            return pathGroup;
        }

        return NormalizeGroupName(node.Metadata?.NavGroup) ?? pathGroup;
    }

    /// <summary>
    /// Gets the sidebar group implied by a source path.
    /// </summary>
    /// <param name="path">A source or docs path. Directory separators and leading or trailing slashes are normalized.</param>
    /// <returns><c>Namespaces</c> for namespace paths, <c>General</c> for root files, or the containing directory.</returns>
    internal static string GetGroupName(string path)
    {
        var normalizedPath = path.Trim()
            .Replace('\\', '/')
            .Trim('/');
        if (normalizedPath.Equals("Namespaces", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase))
        {
            return "Namespaces";
        }

        var normalizedPathForOs = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(normalizedPathForOs);
        return string.IsNullOrWhiteSpace(directory) ? "General" : directory.Replace('\\', '/');
    }

    /// <summary>
    /// Trims an authored group name and converts blank values to <see langword="null"/>.
    /// </summary>
    /// <param name="groupName">The authored group name.</param>
    /// <returns>A trimmed group name, or <see langword="null"/> when no meaningful group was authored.</returns>
    internal static string? NormalizeGroupName(string? groupName)
    {
        return string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
    }

    /// <summary>
    /// Determines whether a node represents a generated type-anchor entry below a parent API page.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <returns><see langword="true"/> when the node has a parent, no body content, and a fragment path.</returns>
    internal static bool IsTypeAnchorNode(DocNode node)
    {
        return !string.IsNullOrWhiteSpace(node.ParentPath)
               && string.IsNullOrWhiteSpace(node.Content)
               && node.Path.Contains('#');
    }

    /// <summary>
    /// Gets the full namespace represented by a namespace documentation node.
    /// </summary>
    /// <param name="node">A node from the <c>Namespaces</c> route family or a fallback titled node.</param>
    /// <returns>The namespace path after <c>Namespaces/</c>, an empty string for the root, or the node title fallback.</returns>
    internal static string GetFullNamespaceName(DocNode node)
    {
        var normalizedPath = node.Path.Trim().Trim('/');
        if (normalizedPath.Equals("Namespaces", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalizedPath.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase)
            ? normalizedPath["Namespaces/".Length..]
            : node.Title;
    }

    /// <summary>
    /// Gets the top-level namespace family after configured prefix simplification.
    /// </summary>
    /// <param name="fullNamespace">The full namespace to simplify.</param>
    /// <param name="namespacePrefixes">Prefixes that may be removed before family extraction.</param>
    /// <returns>The first simplified namespace segment, or the whole simplified value when it has no dot.</returns>
    internal static string GetNamespaceFamily(string fullNamespace, IReadOnlyList<string> namespacePrefixes)
    {
        var simplified = SimplifyNamespace(fullNamespace, namespacePrefixes);
        var separatorIndex = simplified.IndexOf('.');
        return separatorIndex > 0 ? simplified[..separatorIndex] : simplified;
    }

    /// <summary>
    /// Gets the display name for a namespace after removing its family segment.
    /// </summary>
    /// <param name="fullNamespace">The full namespace to simplify.</param>
    /// <param name="namespacePrefixes">Prefixes that may be removed before display extraction.</param>
    /// <returns>The namespace remainder after the family segment, or the simplified namespace when no remainder exists.</returns>
    internal static string GetNamespaceDisplayName(string fullNamespace, IReadOnlyList<string> namespacePrefixes)
    {
        var simplified = SimplifyNamespace(fullNamespace, namespacePrefixes);
        var separatorIndex = simplified.IndexOf('.');
        if (separatorIndex < 0)
        {
            return simplified;
        }

        var remainder = simplified[(separatorIndex + 1)..];
        return string.IsNullOrWhiteSpace(remainder) ? simplified : remainder;
    }

    /// <summary>
    /// Removes the first matching configured namespace prefix from a namespace.
    /// </summary>
    /// <param name="fullNamespace">The namespace to simplify. Blank values become <c>Namespaces</c>.</param>
    /// <param name="namespacePrefixes">Candidate prefixes, with optional trailing dots.</param>
    /// <returns>The simplified namespace, the last prefix segment for exact prefix matches, or the original namespace.</returns>
    internal static string SimplifyNamespace(string fullNamespace, IReadOnlyList<string> namespacePrefixes)
    {
        if (string.IsNullOrWhiteSpace(fullNamespace))
        {
            return "Namespaces";
        }

        foreach (var prefix in namespacePrefixes)
        {
            var normalizedPrefix = prefix.Trim();
            if (string.IsNullOrWhiteSpace(normalizedPrefix))
            {
                continue;
            }

            var trimmedPrefix = normalizedPrefix.TrimEnd('.');
            if (string.Equals(fullNamespace, trimmedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return GetLastNamespaceSegment(trimmedPrefix);
            }

            var dottedPrefix = trimmedPrefix + ".";
            if (fullNamespace.StartsWith(dottedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = fullNamespace[dottedPrefix.Length..];
                return string.IsNullOrWhiteSpace(remainder)
                    ? GetLastNamespaceSegment(trimmedPrefix)
                    : remainder;
            }
        }

        return fullNamespace;
    }

    /// <summary>
    /// Derives shared namespace prefixes from the root namespace pages in a docs set.
    /// </summary>
    /// <param name="docs">The harvested documentation nodes.</param>
    /// <returns>The shared prefix with and without trailing dot, or an empty array when no common prefix exists.</returns>
    internal static string[] GetDerivedNamespacePrefixes(IEnumerable<DocNode> docs)
    {
        var namespaces = docs
            .Where(d => string.IsNullOrEmpty(d.ParentPath))
            .Select(d => d.Path.Trim().Trim('/'))
            .Where(path => path.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase))
            .Select(path => path["Namespaces/".Length..])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (namespaces.Count == 0)
        {
            return [];
        }

        var sharedSegments = namespaces[0].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var sharedLength = sharedSegments.Length;

        foreach (var namespaceName in namespaces.Skip(1))
        {
            var parts = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            sharedLength = Math.Min(sharedLength, parts.Length);
            for (var i = 0; i < sharedLength; i++)
            {
                if (!string.Equals(sharedSegments[i], parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    sharedLength = i;
                    break;
                }
            }
        }

        if (sharedLength == 0)
        {
            return [];
        }

        var sharedPrefix = string.Join(".", sharedSegments.Take(sharedLength));
        return [sharedPrefix + ".", sharedPrefix];
    }

    /// <summary>
    /// Gets the last segment of a namespace value.
    /// </summary>
    /// <param name="namespaceValue">A dot-delimited namespace value.</param>
    /// <returns>The segment after the last dot, or the original value when no dot exists.</returns>
    private static string GetLastNamespaceSegment(string namespaceValue)
    {
        var separatorIndex = namespaceValue.LastIndexOf('.');
        return separatorIndex >= 0 ? namespaceValue[(separatorIndex + 1)..] : namespaceValue;
    }
}
