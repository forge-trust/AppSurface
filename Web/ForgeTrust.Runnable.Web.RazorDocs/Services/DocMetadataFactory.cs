using ForgeTrust.Runnable.Web.RazorDocs.Models;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Builds normalized RazorDocs metadata defaults and fallbacks for harvested documentation nodes.
/// </summary>
internal static class DocMetadataFactory
{
    private const string RunnableNamespacePrefix = "ForgeTrust.Runnable.";

    /// <summary>
    /// Creates normalized metadata for a Markdown documentation node without emitting normalization warnings.
    /// </summary>
    /// <param name="path">The source path used for default section, page-type, and audience inference.</param>
    /// <param name="resolvedTitle">The resolved display title for the Markdown node.</param>
    /// <param name="explicitMetadata">Optional authored metadata that should override inferred defaults.</param>
    /// <param name="derivedSummary">Optional summary text derived from the document body.</param>
    /// <returns>The merged metadata with inferred defaults, normalized nav-group handling, and fallback breadcrumbs.</returns>
    internal static DocMetadata CreateMarkdownMetadata(
        string path,
        string resolvedTitle,
        DocMetadata? explicitMetadata,
        string? derivedSummary)
    {
        return CreateMarkdownMetadata(path, resolvedTitle, explicitMetadata, derivedSummary, logger: null);
    }

    /// <summary>
    /// Creates normalized metadata for a Markdown documentation node and optionally logs authored nav-group fallback warnings.
    /// </summary>
    /// <param name="path">The source path used for default section, page-type, and audience inference.</param>
    /// <param name="resolvedTitle">The resolved display title for the Markdown node.</param>
    /// <param name="explicitMetadata">Optional authored metadata that should override inferred defaults.</param>
    /// <param name="derivedSummary">Optional summary text derived from the document body.</param>
    /// <param name="logger">
    /// An optional logger that receives warnings when authored <c>nav_group</c> values do not resolve to a built-in public
    /// section and RazorDocs falls back to the derived section assignment.
    /// </param>
    /// <returns>The merged metadata with normalized section labels, fallback breadcrumbs, and derived-field flags.</returns>
    /// <remarks>
    /// This shared internal entry point normalizes explicit public-section selection, preserves authored metadata where valid,
    /// derives title/summary fallback semantics, and rebuilds default breadcrumbs when authors do not supply them explicitly.
    /// </remarks>
    internal static DocMetadata CreateMarkdownMetadata(
        string path,
        string resolvedTitle,
        DocMetadata? explicitMetadata,
        string? derivedSummary,
        ILogger? logger)
    {
        var defaultSection = GetDefaultMarkdownSection(path);
        var defaultNavGroup = DocPublicSectionCatalog.GetLabel(defaultSection);
        var isInternalPath = IsInternalPath(path);
        var defaults = new DocMetadata
        {
            Title = resolvedTitle,
            Summary = derivedSummary,
            SummaryIsDerived = string.IsNullOrWhiteSpace(derivedSummary) ? null : true,
            PageType = GetDefaultMarkdownPageType(path),
            PageTypeIsDerived = true,
            Audience = GetDefaultAudience(path),
            AudienceIsDerived = true,
            Component = DeriveComponentFromPath(path),
            ComponentIsDerived = true,
            NavGroup = defaultNavGroup,
            NavGroupIsDerived = string.IsNullOrWhiteSpace(defaultNavGroup) ? null : true,
            HideFromPublicNav = isInternalPath ? true : null,
            HideFromSearch = null
        };

        var merged = DocMetadata.Merge(explicitMetadata, defaults) ?? new DocMetadata();
        var normalizedExplicitNavGroup = NormalizeExplicitNavGroup(path, explicitMetadata?.NavGroup, defaultSection, logger);
        var normalizedNavGroup = normalizedExplicitNavGroup ?? defaultNavGroup;
        bool? summaryIsDerived = string.IsNullOrWhiteSpace(merged.Summary)
            ? null
            : string.IsNullOrWhiteSpace(explicitMetadata?.Summary);
        var authoredBreadcrumbs = explicitMetadata?.Breadcrumbs?
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .ToArray();
        var authoredBreadcrumbCount = authoredBreadcrumbs?.Length ?? 0;
        var breadcrumbs = authoredBreadcrumbs is { Length: > 0 }
            ? authoredBreadcrumbs
            : BuildDefaultBreadcrumbs(normalizedNavGroup, resolvedTitle);
        var breadcrumbTargetCount = GetMarkdownBreadcrumbTargetCount(path);
        var firstAuthoredBreadcrumb = authoredBreadcrumbs?.FirstOrDefault();
        var authoredNavGroupParent = normalizedExplicitNavGroup ?? explicitMetadata?.NavGroup?.Trim();
        var authoredBreadcrumbsMatchPathTargets = authoredBreadcrumbCount == breadcrumbTargetCount;
        var authoredBreadcrumbsIncludeNavGroupParent = !string.IsNullOrWhiteSpace(authoredNavGroupParent)
                                                       && authoredBreadcrumbCount == breadcrumbTargetCount + 1
                                                       && string.Equals(
                                                           firstAuthoredBreadcrumb,
                                                           authoredNavGroupParent,
                                                           StringComparison.OrdinalIgnoreCase);
        bool? breadcrumbsMatchPathTargets = authoredBreadcrumbCount > 0
                                            && (authoredBreadcrumbsMatchPathTargets
                                                || authoredBreadcrumbsIncludeNavGroupParent)
            ? true
            : null;

        return merged with
        {
            NavGroup = normalizedNavGroup,
            NavGroupIsDerived = normalizedExplicitNavGroup is not null ? false : true,
            SummaryIsDerived = summaryIsDerived,
            Breadcrumbs = breadcrumbs,
            BreadcrumbsMatchPathTargets = breadcrumbsMatchPathTargets
        };
    }

    /// <summary>
    /// Creates canonical metadata for an API-reference documentation node.
    /// </summary>
    /// <param name="title">The display title for the API node.</param>
    /// <param name="namespaceName">The owning namespace used for component inference and breadcrumb generation.</param>
    /// <returns>Metadata configured for API-reference navigation, contributor visibility, and namespace breadcrumbs.</returns>
    internal static DocMetadata CreateApiReferenceMetadata(string title, string namespaceName)
    {
        var isInternalNamespace = IsInternalNamespace(namespaceName);
        var isRunnableNamespace = IsRunnableNamespace(namespaceName);
        var hideFromPublicNav = isInternalNamespace || !isRunnableNamespace;
        return new DocMetadata
        {
            Title = title,
            PageType = "api-reference",
            PageTypeIsDerived = false,
            Audience = "developer",
            AudienceIsDerived = false,
            Component = DeriveComponentFromNamespace(namespaceName),
            ComponentIsDerived = false,
            NavGroup = DocPublicSectionCatalog.GetLabel(DocPublicSection.ApiReference),
            NavGroupIsDerived = false,
            HideFromPublicNav = hideFromPublicNav ? true : null,
            HideFromSearch = null,
            Breadcrumbs = BuildApiReferenceBreadcrumbs(title, namespaceName),
            BreadcrumbsMatchPathTargets = true
        };
    }

    private static string? GetDefaultMarkdownPageType(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Contains("examples/", StringComparison.OrdinalIgnoreCase))
        {
            return "example";
        }

        if (IsInternalPath(normalizedPath))
        {
            return "internals";
        }

        return "guide";
    }

    private static string? GetDefaultAudience(string path)
    {
        return IsInternalPath(NormalizePath(path)) ? "contributor" : "implementer";
    }

    private static DocPublicSection GetDefaultMarkdownSection(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (normalizedPath.Equals("README.md", StringComparison.OrdinalIgnoreCase))
        {
            return DocPublicSection.StartHere;
        }

        if (normalizedPath.Contains("examples/", StringComparison.OrdinalIgnoreCase))
        {
            return DocPublicSection.Examples;
        }

        if (IsInternalPath(normalizedPath))
        {
            return DocPublicSection.Internals;
        }

        if (IsReleasePath(normalizedPath))
        {
            return DocPublicSection.Releases;
        }

        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        if (IsStartHereLikeName(fileName))
        {
            return DocPublicSection.StartHere;
        }

        if (ContainsAny(normalizedPath, "concept", "architecture", "explanation", "glossary"))
        {
            return DocPublicSection.Concepts;
        }

        if (ContainsAny(normalizedPath, "troubleshoot", "faq", "debug", "failure", "error"))
        {
            return DocPublicSection.Troubleshooting;
        }

        return DocPublicSection.HowToGuides;
    }

    /// <summary>
    /// Derives the owning Runnable component name from a documentation path when possible.
    /// </summary>
    /// <param name="path">The documentation path whose segments should be inspected.</param>
    /// <returns>The inferred component name, or <see langword="null"/> when no component hint can be derived.</returns>
    internal static string? DeriveComponentFromPath(string path)
    {
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith(RunnableNamespacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return DeriveComponentFromNamespace(segment);
            }
        }

        return path.Contains("Runnable", StringComparison.OrdinalIgnoreCase) ? "Runnable" : null;
    }

    /// <summary>
    /// Derives the owning Runnable component name from a namespace.
    /// </summary>
    /// <param name="namespaceName">The namespace to inspect.</param>
    /// <returns>The inferred component name, or <see langword="null"/> when the namespace is blank.</returns>
    internal static string? DeriveComponentFromNamespace(string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return null;
        }

        if (namespaceName.StartsWith(RunnableNamespacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = namespaceName[RunnableNamespacePrefix.Length..];
            var parts = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "Runnable" : GetRunnableComponentName(parts);
        }

        var fallbackParts = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return fallbackParts.Length == 0 ? namespaceName : fallbackParts[0];
    }

    private static bool IsInternalPath(string path)
    {
        var normalizedPath = NormalizePath(path);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var segmentCount = Path.HasExtension(segments[^1]) ? segments.Length - 1 : segments.Length;
        for (var i = 0; i < segmentCount; i++)
        {
            if (IsInternalSegment(segments[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInternalNamespace(string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return false;
        }

        var segments = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(IsInternalSegment)
               || namespaceName.Contains("Benchmark", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRunnableNamespace(string namespaceName)
    {
        return namespaceName.Equals("ForgeTrust.Runnable", StringComparison.OrdinalIgnoreCase)
               || namespaceName.StartsWith(RunnableNamespacePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRunnableComponentName(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0];
        }

        return parts[0] switch
        {
            "Web" or "Dependency" => parts[1],
            _ => parts[0]
        };
    }

    private static bool IsInternalSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        return segment.Equals("Tests", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("Test", StringComparison.OrdinalIgnoreCase)
               || segment.Equals("benchmarks", StringComparison.OrdinalIgnoreCase)
               || segment.Contains(".Tests", StringComparison.OrdinalIgnoreCase)
               || segment.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
               || segment.Contains("Benchmark", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReleasePath(string path)
    {
        var normalizedPath = NormalizePath(path);

        return normalizedPath.StartsWith("releases/", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeMetadataValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeExplicitNavGroup(
        string path,
        string? explicitNavGroup,
        DocPublicSection defaultSection,
        ILogger? logger)
    {
        var normalized = NormalizeMetadataValue(explicitNavGroup);
        if (normalized is null)
        {
            return null;
        }

        if (DocPublicSectionCatalog.TryResolve(normalized, out var section))
        {
            return DocPublicSectionCatalog.GetLabel(section);
        }

        logger?.LogWarning(
            "Ignoring invalid nav_group value '{NavGroup}' on {DocPath}. Falling back to derived public section '{SectionLabel}'.",
            explicitNavGroup,
            path,
            DocPublicSectionCatalog.GetLabel(defaultSection));

        return null;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStartHereLikeName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.Equals("quickstart", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("getting-started", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("getting_started", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("start-here", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("start_here", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("intro", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("introduction", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("overview", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string>? BuildDefaultBreadcrumbs(string? navGroup, string resolvedTitle)
    {
        if (string.IsNullOrWhiteSpace(navGroup))
        {
            return null;
        }

        return string.Equals(navGroup, resolvedTitle, StringComparison.OrdinalIgnoreCase)
            ? [navGroup]
            : [navGroup, resolvedTitle];
    }

    private static int GetMarkdownBreadcrumbTargetCount(string path)
    {
        var segments = NormalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (segments.Count > 1
            && segments[^1].Equals("README.md", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return segments.Count;
    }

    private static IReadOnlyList<string> BuildApiReferenceBreadcrumbs(string title, string namespaceName)
    {
        var breadcrumbs = new List<string> { "Namespaces" };
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            if (!string.Equals(title, "Namespaces", StringComparison.OrdinalIgnoreCase))
            {
                breadcrumbs.Add(title);
            }

            return breadcrumbs;
        }

        breadcrumbs.AddRange(namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries));

        var finalLabel = breadcrumbs[^1];
        if (!string.Equals(title, finalLabel, StringComparison.OrdinalIgnoreCase))
        {
            breadcrumbs.Add(title);
        }

        return breadcrumbs;
    }
}
