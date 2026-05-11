using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.ViewComponents;

internal static class SidebarDisplayHelper
{
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

    internal static string GetGroupName(string path)
    {
        var normalizedPath = path.Trim().Trim('/');
        if (normalizedPath.Equals("Namespaces", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase))
        {
            return "Namespaces";
        }

        var normalizedPathForOs = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(normalizedPathForOs);
        return string.IsNullOrWhiteSpace(directory) ? "General" : directory.Replace('\\', '/');
    }

    internal static string? NormalizeGroupName(string? groupName)
    {
        return string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
    }

    internal static bool IsTypeAnchorNode(DocNode node)
    {
        return !string.IsNullOrWhiteSpace(node.ParentPath)
               && string.IsNullOrWhiteSpace(node.Content)
               && node.Path.Contains('#');
    }

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

    internal static string GetNamespaceFamily(string fullNamespace, IReadOnlyList<string> namespacePrefixes)
    {
        var simplified = SimplifyNamespace(fullNamespace, namespacePrefixes);
        var separatorIndex = simplified.IndexOf('.');
        return separatorIndex > 0 ? simplified[..separatorIndex] : simplified;
    }

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

    private static string GetLastNamespaceSegment(string namespaceValue)
    {
        var separatorIndex = namespaceValue.LastIndexOf('.');
        return separatorIndex >= 0 ? namespaceValue[(separatorIndex + 1)..] : namespaceValue;
    }
}
