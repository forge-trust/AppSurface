using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Resolves authored reader-intent landing curation metadata into browser-facing RazorDocs featured-page groups.
/// </summary>
/// <remarks>
/// The resolver accepts the normalized <c>featured_page_groups</c> metadata on a landing document, matches each authored
/// destination against harvested docs by source or canonical path, skips destinations that are missing, hidden, blank, or
/// duplicated, and returns only groups that still contain at least one visible page. Logging is intentionally warning-level
/// because curation mistakes degrade first-run docs navigation without breaking application startup.
/// </remarks>
public sealed class DocFeaturedPageResolver
{
    private readonly DocsUrlBuilder _docsUrlBuilder;
    private readonly ILogger<DocFeaturedPageResolver> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DocFeaturedPageResolver"/> with the default live docs root.
    /// </summary>
    /// <remarks>
    /// This overload preserves the historical convenience API for ad hoc callers that only need the default
    /// <c>/docs</c> routing surface. Hosts that customize <see cref="RazorDocsRoutingOptions.DocsRootPath"/> should use
    /// <see cref="DocFeaturedPageResolver(ILogger{DocFeaturedPageResolver}, DocsUrlBuilder)"/> so resolved featured-page
    /// links match the configured live docs root.
    /// </remarks>
    /// <param name="logger">Logger used for authored curation diagnostics.</param>
    public DocFeaturedPageResolver(ILogger<DocFeaturedPageResolver> logger)
        : this(logger, new DocsUrlBuilder(new RazorDocsOptions()))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="DocFeaturedPageResolver"/> with the configured live docs root.
    /// </summary>
    /// <param name="logger">Logger used for authored curation diagnostics.</param>
    /// <param name="docsUrlBuilder">
    /// URL builder that supplies the current live docs root for browser-facing featured-page links.
    /// </param>
    public DocFeaturedPageResolver(ILogger<DocFeaturedPageResolver> logger, DocsUrlBuilder docsUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _docsUrlBuilder = docsUrlBuilder ?? throw new ArgumentNullException(nameof(docsUrlBuilder));
    }

    /// <summary>
    /// Resolves grouped featured-page metadata from <paramref name="landingDoc"/> against the harvested docs corpus.
    /// </summary>
    /// <param name="landingDoc">
    /// The root or section landing document that owns the curation metadata, or <c>null</c> when the caller has no
    /// landing page to resolve.
    /// </param>
    /// <param name="docs">The harvested docs corpus used for destination lookup.</param>
    /// <returns>
    /// A list of resolved featured-page groups ordered by authored group order, then authored position. The method returns
    /// an empty list when <paramref name="landingDoc"/> is <c>null</c>, when the landing doc has no
    /// <c>featured_page_groups</c>, or when every authored group is filtered out during resolution. Groups with no visible
    /// destinations after validation are omitted. Destinations that resolve to docs without a public
    /// <see cref="DocNode.CanonicalPath"/> are skipped with a warning because collision losers and reserved routes are
    /// intentionally not linkable. Duplicate destinations are suppressed across all groups, so a page resolved earlier in
    /// authored order will not appear again later in the landing. Browser-facing
    /// <see cref="DocLandingFeaturedPageViewModel.Href"/> values are rooted at the current live docs surface from
    /// <see cref="DocsUrlBuilder"/>, not hardcoded to <c>/docs</c>, and authored canonical input paths are matched
    /// against that same configured live docs root before the configured route-family root and legacy stable
    /// <c>/docs</c> prefix fallback are considered.
    /// </returns>
    public IReadOnlyList<DocLandingFeaturedPageGroupViewModel> ResolveGroups(
        DocNode? landingDoc,
        IReadOnlyList<DocNode> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);

        if (landingDoc?.Metadata?.FeaturedPageGroups is not { Count: > 0 } featuredGroups)
        {
            return [];
        }

        var pathResolver = DocPathResolver.Create(docs);
        var resolvedGroups = new List<DocLandingFeaturedPageGroupViewModel>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var authoredGroups = new List<(DocFeaturedPageGroupDefinition Group, int Index)>();
        for (var groupIndex = 0; groupIndex < featuredGroups.Count; groupIndex++)
        {
            var group = featuredGroups[groupIndex];
            if (group is null)
            {
                _logger.LogWarning(
                    "Skipping featured docs landing group on {LandingPath} at {FieldPath} because it is null.",
                    landingDoc.Path,
                    $"featured_page_groups[{groupIndex}]");
                continue;
            }

            authoredGroups.Add((group, groupIndex));
        }

        foreach (var (group, groupIndex) in authoredGroups
                     .OrderBy(item => item.Group.Order ?? int.MaxValue)
                     .ThenBy(item => item.Index))
        {
            var groupPath = group.SourceFieldPath ?? $"featured_page_groups[{groupIndex}]";
            var resolvedPages = new List<DocLandingFeaturedPageViewModel>();
            var authoredPages = group.Pages;
            if (authoredPages is null || authoredPages.Count == 0)
            {
                _logger.LogWarning(
                    "Skipping featured docs landing group on {LandingPath} at {FieldPath} because it has no pages.",
                    landingDoc.Path,
                    groupPath);
                continue;
            }

            var pageDefinitions = new List<(DocFeaturedPageDefinition Definition, int Index)>();
            for (var pageIndex = 0; pageIndex < authoredPages.Count; pageIndex++)
            {
                var definition = authoredPages[pageIndex];
                if (definition is null)
                {
                    _logger.LogWarning(
                        "Skipping featured docs landing entry on {LandingPath} at {FieldPath} because it is null.",
                        landingDoc.Path,
                        $"{groupPath}.pages[{pageIndex}]");
                    continue;
                }

                pageDefinitions.Add((definition, pageIndex));
            }

            foreach (var (definition, pageIndex) in pageDefinitions
                         .OrderBy(item => item.Definition.Order ?? int.MaxValue)
                         .ThenBy(item => item.Index))
            {
                var fieldPath = definition.SourceFieldPath ?? $"{groupPath}.pages[{pageIndex}]";
                var resolvedPage = ResolvePage(landingDoc, definition, fieldPath, pathResolver, seenPaths);
                if (resolvedPage is not null)
                {
                    resolvedPages.Add(resolvedPage);
                }
            }

            if (resolvedPages.Count == 0)
            {
                continue;
            }

            resolvedGroups.Add(
                new DocLandingFeaturedPageGroupViewModel
                {
                    Intent = NormalizeOptionalText(group.Intent) ?? string.Empty,
                    Label = NormalizeOptionalText(group.Label)
                            ?? NormalizeOptionalText(group.Intent)
                            ?? "Featured",
                    Summary = NormalizeOptionalText(group.Summary),
                    Pages = resolvedPages
                });
        }

        if (resolvedGroups.Count == 0)
        {
            _logger.LogWarning(
                "Skipping all featured docs landing groups on {LandingPath} because no visible destination pages resolved.",
                landingDoc.Path);
        }

        return resolvedGroups;
    }

    private DocLandingFeaturedPageViewModel? ResolvePage(
        DocNode landingDoc,
        DocFeaturedPageDefinition definition,
        string fieldPath,
        DocPathResolver pathResolver,
        HashSet<string> seenPaths)
    {
        if (string.IsNullOrWhiteSpace(definition.Path))
        {
            _logger.LogWarning(
                "Skipping featured docs landing entry on {LandingPath} at {FieldPath} because it has no destination path.",
                landingDoc.Path,
                $"{fieldPath}.path");
            return null;
        }

        var destination = pathResolver.Resolve(
            definition.Path!,
            _docsUrlBuilder.CurrentDocsRootPath,
            _docsUrlBuilder.RouteRootPath,
            DocsUrlBuilder.DocsEntryPath);
        if (destination is null)
        {
            _logger.LogWarning(
                "Skipping featured docs landing entry '{FeaturedPath}' on {LandingPath} at {FieldPath} because the destination page could not be resolved.",
                definition.Path,
                landingDoc.Path,
                $"{fieldPath}.path");
            return null;
        }

        if (destination.Metadata?.HideFromPublicNav == true)
        {
            _logger.LogWarning(
                "Skipping featured docs landing entry '{FeaturedPath}' on {LandingPath} at {FieldPath} because the destination page is hidden from public navigation.",
                definition.Path,
                landingDoc.Path,
                $"{fieldPath}.path");
            return null;
        }

        if (destination.CanonicalPath is null)
        {
            _logger.LogWarning(
                "Skipping featured docs landing entry '{FeaturedPath}' on {LandingPath} at {FieldPath} because the destination page has no public route.",
                definition.Path,
                landingDoc.Path,
                $"{fieldPath}.path");
            return null;
        }

        var destinationLinkPath = GetSnapshotCanonicalPath(destination);
        if (!seenPaths.Add(destinationLinkPath))
        {
            _logger.LogWarning(
                "Skipping duplicate featured docs landing entry '{FeaturedPath}' on {LandingPath} at {FieldPath} because its destination is already featured.",
                definition.Path,
                landingDoc.Path,
                $"{fieldPath}.path");
            return null;
        }

        var destinationTitle = ResolveDisplayTitle(destination);
        var question = string.IsNullOrWhiteSpace(definition.Question)
            ? destinationTitle
            : definition.Question.Trim();

        return new DocLandingFeaturedPageViewModel
        {
            Question = question,
            Title = destinationTitle,
            Href = _docsUrlBuilder.BuildDocUrl(destinationLinkPath),
            PageType = destination.Metadata?.PageType,
            PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(destination.Metadata?.PageType),
            SupportingText = GetSupportingText(definition, destination)
        };
    }

    private static string GetSnapshotCanonicalPath(DocNode doc)
    {
        return doc.CanonicalPath
               ?? throw new InvalidOperationException(
                   $"DocFeaturedPageResolver requires snapshot canonical paths. Doc '{doc.Path}' was missing CanonicalPath.");
    }

    private static string? GetSupportingText(DocFeaturedPageDefinition definition, DocNode destination)
    {
        if (!string.IsNullOrWhiteSpace(definition.SupportingCopy))
        {
            return definition.SupportingCopy.Trim();
        }

        return string.IsNullOrWhiteSpace(destination.Metadata?.Summary)
            ? null
            : destination.Metadata!.Summary!.Trim();
    }

    private static string ResolveDisplayTitle(DocNode doc)
    {
        return string.IsNullOrWhiteSpace(doc.Metadata?.Title)
            ? doc.Title
            : doc.Metadata!.Title!.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
