using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;

namespace ForgeTrust.Runnable.Web.RazorDocs.ViewComponents;

/// <summary>
/// Shapes public-section snapshots into the grouped link structures used by section pages and the shared sidebar.
/// </summary>
internal static class DocSectionDisplayBuilder
{
    /// <summary>
    /// Builds grouped section links for one public section snapshot.
    /// </summary>
    /// <param name="snapshot">The public-section snapshot to group for display.</param>
    /// <param name="currentHref">
    /// The current docs href, if known. When provided, matching links are marked current for accessibility and styling.
    /// </param>
    /// <param name="namespacePrefixes">
    /// Optional namespace prefixes used to shorten API-reference labels and family headings. When omitted, API-reference
    /// groups derive prefixes from the visible pages in <paramref name="snapshot"/>.
    /// </param>
    /// <param name="docsRootPath">The app-relative docs root path used to build canonical links for the current surface.</param>
    /// <returns>The grouped link model for the supplied section snapshot.</returns>
    /// <remarks>
    /// Editorial sections stay flat and task-oriented, while <see cref="DocPublicSection.ApiReference"/> delegates to the
    /// namespace-aware grouping path so API reference content stays organized by family. API-reference groups intentionally
    /// omit generated type-anchor children from these global navigation models; readers reach type and member anchors
    /// from the namespace page's local outline, source links, or search instead of loading every symbol into the primary
    /// sidebar.
    /// </remarks>
    internal static IReadOnlyList<DocSectionGroupViewModel> BuildGroups(
        DocSectionSnapshot snapshot,
        string? currentHref = null,
        IReadOnlyList<string>? namespacePrefixes = null,
        string docsRootPath = "/docs")
    {
        return snapshot.Section == DocPublicSection.ApiReference
            ? BuildApiReferenceGroups(snapshot, currentHref, namespacePrefixes, docsRootPath)
            : BuildEditorialGroups(snapshot, currentHref, docsRootPath);
    }

    private static IReadOnlyList<DocSectionGroupViewModel> BuildEditorialGroups(
        DocSectionSnapshot snapshot,
        string? currentHref,
        string docsRootPath)
    {
        var rootItems = snapshot.VisiblePages
            .Where(doc => string.IsNullOrEmpty(doc.ParentPath) && !SidebarDisplayHelper.IsTypeAnchorNode(doc))
            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return
        [
            new DocSectionGroupViewModel
            {
                Links = rootItems
                    .Select(doc => CreateLink(doc, snapshot.VisiblePages, currentHref, docsRootPath))
                    .ToList()
            }
        ];
    }

    private static IReadOnlyList<DocSectionGroupViewModel> BuildApiReferenceGroups(
        DocSectionSnapshot snapshot,
        string? currentHref,
        IReadOnlyList<string>? configuredNamespacePrefixes,
        string docsRootPath)
    {
        var namespacePrefixes = configuredNamespacePrefixes is { Count: > 0 }
            ? configuredNamespacePrefixes
            : SidebarDisplayHelper.GetDerivedNamespacePrefixes(snapshot.VisiblePages);
        var rootItems = snapshot.VisiblePages
            .Where(doc => string.IsNullOrEmpty(doc.ParentPath) && !SidebarDisplayHelper.IsTypeAnchorNode(doc))
            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
            .ThenBy(doc => SidebarDisplayHelper.GetFullNamespaceName(doc), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<DocSectionGroupViewModel>();
        var namespaceRoot = rootItems.FirstOrDefault(
            doc => doc.Path.Trim(' ', '/').Equals("Namespaces", StringComparison.OrdinalIgnoreCase));
        if (namespaceRoot is not null)
        {
            groups.Add(
                new DocSectionGroupViewModel
                {
                    Links =
                    [
                        CreateLink(
                            namespaceRoot,
                            snapshot.VisiblePages,
                            currentHref,
                            docsRootPath,
                            includeTypeAnchorChildren: false)
                    ]
                });
        }

        var namespaceNodes = rootItems
            .Where(doc => doc.Path.Trim(' ', '/').StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        groups.AddRange(
            namespaceNodes
                .GroupBy(
                    doc => SidebarDisplayHelper.GetNamespaceFamily(
                        SidebarDisplayHelper.GetFullNamespaceName(doc),
                        namespacePrefixes))
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(
                    group => new DocSectionGroupViewModel
                    {
                        Title = group.Key,
                        Links = group
                            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
                            .ThenBy(doc => SidebarDisplayHelper.GetFullNamespaceName(doc), StringComparer.OrdinalIgnoreCase)
                            .Select(doc => CreateLink(
                                doc,
                                snapshot.VisiblePages,
                                currentHref,
                                docsRootPath,
                                namespacePrefixes,
                                includeTypeAnchorChildren: false))
                            .ToList()
                    }));

        return groups;
    }

    private static DocSectionLinkViewModel CreateLink(
        DocNode doc,
        IReadOnlyList<DocNode> sectionDocs,
        string? currentHref,
        string docsRootPath,
        IReadOnlyList<string>? namespacePrefixes = null,
        bool includeTypeAnchorChildren = true)
    {
        var normalizedDocPath = NormalizePath(doc.Path);
        var href = DocsUrlBuilder.BuildDocUrl(docsRootPath, GetCanonicalPath(doc));
        var badge = DocMetadataPresentation.ResolvePageTypeBadge(doc.Metadata?.PageType);
        IReadOnlyList<DocSectionLinkViewModel> children = includeTypeAnchorChildren
            ? BuildTypeAnchorChildren(sectionDocs, normalizedDocPath, currentHref, docsRootPath)
            : [];

        var title = namespacePrefixes is not null
            ? SidebarDisplayHelper.GetNamespaceDisplayName(
                SidebarDisplayHelper.GetFullNamespaceName(doc),
                namespacePrefixes)
            : doc.Title;

        return new DocSectionLinkViewModel
        {
            Title = title,
            Href = href,
            Summary = string.IsNullOrWhiteSpace(doc.Metadata?.Summary) ? null : doc.Metadata!.Summary!.Trim(),
            PageTypeBadge = badge,
            Children = children,
            UseAnchorNavigation = true,
            IsCurrent = IsCurrentLink(currentHref, href)
        };
    }

    private static IReadOnlyList<DocSectionLinkViewModel> BuildTypeAnchorChildren(
        IReadOnlyList<DocNode> sectionDocs,
        string? normalizedDocPath,
        string? currentHref,
        string docsRootPath)
    {
        return sectionDocs
            .Where(item => string.Equals(NormalizePath(item.ParentPath), normalizedDocPath, StringComparison.OrdinalIgnoreCase)
                && SidebarDisplayHelper.IsTypeAnchorNode(item))
            .OrderBy(item => item.Metadata?.Order ?? int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(
                item =>
                {
                    var childHref = DocsUrlBuilder.BuildDocUrl(docsRootPath, GetCanonicalPath(item));
                    return new DocSectionLinkViewModel
                    {
                        Title = item.Title,
                        Href = childHref,
                        UseAnchorNavigation = true,
                        IsCurrent = IsCurrentLink(currentHref, childHref)
                    };
                })
            .ToList();
    }

    private static bool IsCurrentLink(string? currentHref, string href)
    {
        return !string.IsNullOrWhiteSpace(currentHref)
               && string.Equals(currentHref, href, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCanonicalPath(DocNode doc)
    {
        return NormalizePath(doc.CanonicalPath ?? doc.Path) ?? string.Empty;
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path.Trim().Trim('/', '\\');
    }
}
