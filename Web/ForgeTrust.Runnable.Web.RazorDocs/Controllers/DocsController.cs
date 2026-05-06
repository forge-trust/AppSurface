using System.Text.Json;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using ForgeTrust.Runnable.Web.RazorDocs.ViewComponents;
using ForgeTrust.Runnable.Web.RazorWire.Bridge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.Runnable.Web.RazorDocs.Controllers;

/// <summary>
/// Controller for serving documentation pages.
/// </summary>
public class DocsController : Controller
{
    private const string RootLandingSourcePath = "README.md";
    private const string NeutralLandingHeading = "Documentation";
    private const string NeutralLandingDescription = "Start with the strongest proof path, then branch into guides, examples, and reference once you know where you want to go deeper.";
    private const string CuratedLandingDescription = "Start with the proof path that answers the first evaluator questions, then move into the sections that fit your next decision.";
    private const string SectionUnavailableHeading = "Section unavailable";
    private static readonly string[] DefaultProofPathStageLabels = ["Understand", "See Proof", "Adopt Next"];
    private static readonly TimeSpan SearchShellFallbackBudget = TimeSpan.FromMilliseconds(500);

    private readonly DocAggregator _aggregator;
    private readonly DocsUrlBuilder _docsUrlBuilder;
    private readonly RazorDocsVersionCatalogService _versionCatalogService;
    private readonly DocFeaturedPageResolver _featuredPageResolver;
    private readonly ILogger<DocsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DocsController"/> for ad hoc callers that only supply the doc aggregator and logger.
    /// </summary>
    /// <remarks>
    /// This convenience overload does <em>not</em> use the host-configured RazorDocs routing or version catalog.
    /// Instead it constructs <see cref="DocsUrlBuilder" /> from <c>new RazorDocsOptions()</c> and creates a fallback
    /// version catalog via <see cref="CreateDefaultVersionCatalogService()" />. Callers that need the configured live
    /// docs root, preview/versioned routing surface, or published-release catalog should use the
    /// <see cref="DocsController(DocAggregator, DocsUrlBuilder, RazorDocsVersionCatalogService, ILogger{DocsController})" />
    /// overload instead.
    /// </remarks>
    /// <param name="aggregator">Service used to retrieve documentation items.</param>
    /// <param name="logger">Logger used for search index diagnostics.</param>
    public DocsController(DocAggregator aggregator, ILogger<DocsController> logger)
        : this(
            aggregator,
            new DocsUrlBuilder(new RazorDocsOptions()),
            CreateDefaultVersionCatalogService(),
            new DocFeaturedPageResolver(
                NullLogger<DocFeaturedPageResolver>.Instance,
                new DocsUrlBuilder(new RazorDocsOptions())),
            logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="DocsController"/> for callers that want grouped featured-page resolution but
    /// still rely on the default docs routing and version catalog services.
    /// </summary>
    /// <remarks>
    /// This overload preserves the convenience surface introduced for landing-curation callers while still constructing
    /// <see cref="DocsUrlBuilder" /> from <c>new RazorDocsOptions()</c> and the fallback version catalog from
    /// <see cref="CreateDefaultVersionCatalogService()" />. Use the overload that accepts
    /// <see cref="DocsUrlBuilder" /> and <see cref="RazorDocsVersionCatalogService" /> when the configured live docs root,
    /// path-base-aware versioning routes, or published catalog state must match the host.
    /// </remarks>
    /// <param name="aggregator">Service used to retrieve documentation items.</param>
    /// <param name="featuredPageResolver">Service used to resolve grouped landing curation metadata.</param>
    /// <param name="logger">Logger used for search index diagnostics.</param>
    public DocsController(
        DocAggregator aggregator,
        DocFeaturedPageResolver featuredPageResolver,
        ILogger<DocsController> logger)
        : this(
            aggregator,
            new DocsUrlBuilder(new RazorDocsOptions()),
            CreateDefaultVersionCatalogService(),
            featuredPageResolver,
            logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="DocsController"/> with the specified documentation aggregator.
    /// </summary>
    /// <param name="aggregator">Service used to retrieve documentation items.</param>
    /// <param name="docsUrlBuilder">Shared URL builder for the live source-backed docs surface.</param>
    /// <param name="versionCatalogService">Resolved catalog service for published docs versions.</param>
    /// <param name="logger">Logger used for search index diagnostics.</param>
    public DocsController(
        DocAggregator aggregator,
        DocsUrlBuilder docsUrlBuilder,
        RazorDocsVersionCatalogService versionCatalogService,
        ILogger<DocsController> logger)
        : this(
            aggregator,
            docsUrlBuilder,
            versionCatalogService,
            new DocFeaturedPageResolver(NullLogger<DocFeaturedPageResolver>.Instance, docsUrlBuilder),
            logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="DocsController"/> with the specified documentation services.
    /// </summary>
    /// <param name="aggregator">Service used to retrieve documentation items.</param>
    /// <param name="docsUrlBuilder">Shared URL builder for the live source-backed docs surface.</param>
    /// <param name="versionCatalogService">Resolved catalog service for published docs versions.</param>
    /// <param name="featuredPageResolver">Service used to resolve grouped landing curation metadata.</param>
    /// <param name="logger">Logger used for search index diagnostics.</param>
    [ActivatorUtilitiesConstructor]
    public DocsController(
        DocAggregator aggregator,
        DocsUrlBuilder docsUrlBuilder,
        RazorDocsVersionCatalogService versionCatalogService,
        DocFeaturedPageResolver featuredPageResolver,
        ILogger<DocsController> logger)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _docsUrlBuilder = docsUrlBuilder ?? throw new ArgumentNullException(nameof(docsUrlBuilder));
        _versionCatalogService = versionCatalogService ?? throw new ArgumentNullException(nameof(versionCatalogService));
        _featuredPageResolver = featuredPageResolver ?? throw new ArgumentNullException(nameof(featuredPageResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Displays the documentation index view containing either curated proof paths from the repository-root landing doc metadata or the neutral docs landing fallback.
    /// </summary>
    /// <returns>
    /// A view result whose model is a <see cref="DocLandingViewModel"/>. The model includes curated featured cards when the
    /// repository-root <c>README.md</c> metadata authors <c>featured_page_groups</c> through inline front matter or a paired
    /// sidecar such as <c>README.md.yml</c>; otherwise it includes the neutral fallback landing data.
    /// </returns>
    public async Task<IActionResult> Index()
    {
        var docs = await _aggregator.GetDocsAsync(HttpContext.RequestAborted);
        var sections = await _aggregator.GetPublicSectionsAsync(HttpContext.RequestAborted);
        var viewModel = BuildLandingViewModel(docs, sections);

        return View(viewModel);
    }

    /// <summary>
    /// Displays the stable docs entry fallback when versioning is enabled but the recommended released tree is not mounted.
    /// </summary>
    /// <returns>
    /// A view result describing the available released versions plus the current preview surface, or a redirect to the
    /// live docs home when versioning is disabled.
    /// </returns>
    public IActionResult VersionEntry()
    {
        if (!_docsUrlBuilder.VersioningEnabled)
        {
            return Redirect(PathBaseAware(_docsUrlBuilder.BuildHomeUrl()));
        }

        return View("Versions", BuildVersionArchiveViewModel(entryFallback: true));
    }

    /// <summary>
    /// Displays the public docs version archive.
    /// </summary>
    /// <returns>
    /// A view result that lists the published versions described by the configured version catalog, or a redirect to
    /// the live docs home when versioning is disabled.
    /// </returns>
    public IActionResult Versions()
    {
        if (!_docsUrlBuilder.VersioningEnabled)
        {
            return Redirect(PathBaseAware(_docsUrlBuilder.BuildHomeUrl()));
        }

        return View(BuildVersionArchiveViewModel(entryFallback: false));
    }

    /// <summary>
    /// Enters one normalized public documentation section.
    /// </summary>
    /// <param name="sectionSlug">The stable slug for the public section.</param>
    /// <returns>
    /// A redirect to the authored landing doc when one exists, otherwise a grouped section fallback or unavailable view.
    /// </returns>
    public async Task<IActionResult> Section(string sectionSlug)
    {
        var sections = await _aggregator.GetPublicSectionsAsync(HttpContext.RequestAborted);
        var startHereHref = ResolveStartHereHref(sections);

        if (!DocPublicSectionCatalog.TryResolveSlug(sectionSlug, out var section))
        {
            if (DocPublicSectionCatalog.TryResolve(sectionSlug, out var aliasSection))
            {
                return Redirect(PathBaseAware(_docsUrlBuilder.BuildSectionUrl(aliasSection)));
            }

            return View("Section", BuildUnavailableSectionViewModel(null, startHereHref));
        }

        var snapshot = sections.FirstOrDefault(item => item.Section == section);
        if (snapshot is null)
        {
            return View("Section", BuildUnavailableSectionViewModel(section, startHereHref));
        }

        if (snapshot.LandingDoc is not null)
        {
            return Redirect(PathBaseAware(_docsUrlBuilder.BuildDocUrl(GetSnapshotCanonicalPath(snapshot.LandingDoc))));
        }

        return View("Section", BuildSectionPageViewModel(snapshot, startHereHref));
    }

    /// <summary>
    /// Displays the full or partial details view for a documentation item identified by the given path.
    /// </summary>
    /// <param name="path">
    /// The source path, canonical docs path, or <c>.partial.html</c> resource identifier of the documentation item to
    /// retrieve.
    /// </param>
    /// <returns>
    /// An <see cref="IActionResult"/> rendering the details view or the <c>doc-content</c> RazorWire frame; returns
    /// <see cref="NotFoundResult"/> when the path is invalid or no document is found after fallback resolution.
    /// </returns>
    /// <remarks>
    /// Partial requests ending in <c>.partial.html</c> are resolved through the same
    /// <see cref="DocAggregator.GetDocDetailsAsync(string, CancellationToken)"/> flow as full-page requests. When a
    /// partial path resolves to an <c>/index</c> resource, such as <c>/index.partial.html</c>, the action transparently
    /// retries the parent document before returning <see cref="NotFoundResult"/>. Successful requests load the complete
    /// docs corpus and public-section snapshots with <see cref="DocAggregator.GetDocsAsync(CancellationToken)"/> and
    /// <see cref="DocAggregator.GetPublicSectionsAsync(CancellationToken)"/>, then build the response model with
    /// <c>BuildDetailsViewModel</c>. All aggregator calls observe <see cref="HttpContext.RequestAborted"/>. Visible
    /// caller side effects are limited to returning either the full details view or a <c>doc-content</c> frame for
    /// partial navigation.
    /// </remarks>
    public async Task<IActionResult> Details(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NotFound();
        }

        var servesPartial = path.EndsWith(".partial.html", StringComparison.OrdinalIgnoreCase);
        var resolvedPath = servesPartial
            ? path[..^".partial.html".Length]
            : path;
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return NotFound();
        }

        var docDetails = await _aggregator.GetDocDetailsAsync(resolvedPath, HttpContext.RequestAborted);
        if (docDetails == null
            && servesPartial
            && resolvedPath.EndsWith("/index", StringComparison.OrdinalIgnoreCase))
        {
            var fallbackPath = resolvedPath[..^"/index".Length];
            if (!string.IsNullOrWhiteSpace(fallbackPath))
            {
                docDetails = await _aggregator.GetDocDetailsAsync(fallbackPath, HttpContext.RequestAborted);
            }
        }

        if (docDetails == null)
        {
            return NotFound();
        }

        var docs = await _aggregator.GetDocsAsync(HttpContext.RequestAborted);
        var sections = await _aggregator.GetPublicSectionsAsync(HttpContext.RequestAborted);
        var viewModel = BuildDetailsViewModel(docDetails, docs, sections);

        if (servesPartial)
        {
            return RazorWireBridge.Frame(this, "doc-content", "DetailsFrame", viewModel);
        }

        return View(viewModel);
    }

    /// <summary>
    /// Displays the dedicated docs search workspace shell.
    /// </summary>
    /// <remarks>
    /// The action returns a <see cref="SearchPageViewModel"/> immediately so the workspace can render starter,
    /// loading, and retry UI before the client downloads the search index. Fallback link generation shares a linked
    /// cancellation token with the current request and is capped by <see cref="SearchShellFallbackBudget"/> so slow
    /// aggregation does not block the shell from rendering. If aggregation times out or throws, the view still
    /// renders with default recovery links.
    /// </remarks>
    /// <returns>
    /// A <see cref="ViewResult"/> whose model is a <see cref="SearchPageViewModel"/> describing the search shell and
    /// its server-rendered recovery paths.
    /// </returns>
    public async Task<IActionResult> Search()
    {
        ViewData["Title"] = "Search";
        IReadOnlyList<DocNode> docs = [];

        try
        {
            using var fallbackBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            fallbackBudgetCts.CancelAfter(SearchShellFallbackBudget);
            docs = await _aggregator.GetDocsAsync(fallbackBudgetCts.Token);
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Docs search shell fallback link generation exceeded the {BudgetMs}ms budget. Rendering the shell with default recovery links.",
                SearchShellFallbackBudget.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Docs search shell fallback link generation failed. Rendering the shell with default recovery links.");
        }

        var model = BuildSearchPageViewModel(docs);
        return View(model);
    }

    /// <summary>
    /// Returns docs search index data for live-hosted docs.
    /// </summary>
    /// <returns>
    /// A JSON result containing searchable document metadata, including normalized page-type badge fields that keep search
    /// result rendering consistent with the built-in landing and details experiences. When
    /// <see cref="HttpRequest.PathBase" /> is non-empty, only <c>documents[*].path</c> values that already start with
    /// <c>/</c> are rebased onto that path base before serialization so a mounted app returns links like
    /// <c>/some-base/docs/guide.html</c> instead of <c>/docs/guide.html</c>.
    /// </returns>
    /// <remarks>
    /// The path-base rewrite is intentionally narrow. Only rooted <c>documents[*].path</c> values are prefixed; blank,
    /// missing, or already non-rooted values such as <c>guide.html</c> remain unchanged. The rewrite trims trailing
    /// slashes from the request path base before concatenation, so <c>/some-base/</c> and <c>/some-base</c> produce the
    /// same output. For example, a typed payload that contains <c>documents[0].path = "/docs/guide.html"</c> becomes
    /// <c>/some-base/docs/guide.html</c> when the request path base is <c>/some-base</c>. This action always receives the
    /// typed <see cref="DocsSearchIndexPayload" /> produced by <see cref="DocAggregator.GetSearchIndexPayloadAsync(System.Threading.CancellationToken)" />;
    /// raw JSON payloads without a top-level <c>documents</c> array are outside this method's contract and must be
    /// handled before this action is invoked. The rewrite is idempotent when <see cref="HttpRequest.PathBase" /> is
    /// null, empty, or <c>/</c>.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> SearchIndex()
    {
        // Manual invalidation hook for operators: /docs/search-index.json?refresh=1|true
        if (ShouldRefreshCache(Request.Query))
        {
            if (CanRefreshCache())
            {
                _aggregator.InvalidateCache();
                _logger.LogInformation("Search index cache generation bumped by an authenticated user.");
            }
            else
            {
                _logger.LogWarning("Ignoring unauthenticated search index refresh attempt.");
            }
        }

        // Keep response caching private by default; docs may be served behind auth.
        Response.Headers.CacheControl = $"private,max-age={(int)_aggregator.SnapshotCacheDuration.TotalSeconds}";

        var payload = await _aggregator.GetSearchIndexPayloadAsync(HttpContext.RequestAborted);
        var pathBaseAwarePayload = PrefixSearchIndexPathsForPathBase(payload, Request.PathBase.Value);

        return Json(pathBaseAwarePayload);
    }

    /// <summary>
    /// Determines whether the search index cache should be refreshed based on the presence of a "refresh" query parameter.
    /// </summary>
    /// <param name="query">The collection of query parameters from the HTTP request.</param>
    /// <returns><c>true</c> if the cache should be refreshed; otherwise, <c>false</c>.</returns>
    private static bool ShouldRefreshCache(IQueryCollection query)
    {
        if (!query.TryGetValue("refresh", out StringValues refreshValues))
        {
            return false;
        }

        var refresh = refreshValues.ToString();
        return refresh == "1"
               || string.Equals(refresh, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether the current user has permission to initiate a cache refresh.
    /// </summary>
    /// <returns><c>true</c> if the user is authenticated; otherwise, <c>false</c>.</returns>
    internal bool CanRefreshCache()
    {
        return User?.Identity?.IsAuthenticated == true;
    }

    /// <summary>
    /// Prefixes rooted search-document paths in a cached search-index payload for the active request path base.
    /// </summary>
    /// <param name="payload">The cached search-index payload whose document paths may need rebasing.</param>
    /// <param name="requestPathBase">The current request path base that should be prepended when it is non-empty and not <c>/</c>.</param>
    /// <returns>
    /// The original payload when no rewrite is needed; otherwise a cloned payload whose rooted
    /// <see cref="DocsSearchIndexDocument.Path" /> values are prefixed with the normalized path base.
    /// </returns>
    /// <remarks>
    /// This helper operates on the typed <see cref="DocsSearchIndexPayload" /> contract, which always exposes a top-level
    /// <see cref="DocsSearchIndexPayload.Documents" /> list. It does not inspect or reshape arbitrary JSON payloads, so
    /// callers that hold raw JSON without a top-level <c>documents</c> array must deserialize or otherwise handle that
    /// mismatch before calling this method. Within the typed payload, only rooted
    /// <see cref="DocsSearchIndexDocument.Path" /> values are rewritten. Non-rooted, blank, or otherwise unchanged values
    /// such as <c>guide.html</c> are returned as-is, so callers should supply leading-slash browser paths for docs-local
    /// navigation when rebasing is expected. For example, rebasing a payload that contains
    /// <c>documents[0].path = "/docs/guide.html"</c> with <c>/some-base/</c> produces
    /// <c>/some-base/docs/guide.html</c>. The supplied path base is trimmed of trailing slashes before concatenation, and
    /// the method is idempotent when <paramref name="requestPathBase" /> is null, empty, or <c>/</c>.
    /// </remarks>
    internal static DocsSearchIndexPayload PrefixSearchIndexPathsForPathBase(
        DocsSearchIndexPayload payload,
        string? requestPathBase)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(requestPathBase)
            || string.Equals(requestPathBase, "/", StringComparison.Ordinal))
        {
            return payload;
        }

        var normalizedPathBase = requestPathBase.TrimEnd('/');
        var changed = false;
        var documents = new DocsSearchIndexDocument[payload.Documents.Count];

        for (var index = 0; index < payload.Documents.Count; index++)
        {
            var document = payload.Documents[index];
            if (string.IsNullOrWhiteSpace(document.Path)
                || !document.Path.StartsWith("/", StringComparison.Ordinal))
            {
                documents[index] = document;
                continue;
            }

            changed = true;
            documents[index] = document with
            {
                Path = normalizedPathBase + document.Path
            };
        }

        return changed ? payload with { Documents = documents } : payload;
    }

    private DocLandingViewModel BuildLandingViewModel(
        IReadOnlyList<DocNode> docs,
        IReadOnlyList<DocSectionSnapshot> sections)
    {
        var visibleDocs = docs
            .Where(d => d.Metadata?.HideFromPublicNav != true)
            .ToList();
        var landingDoc = docs.FirstOrDefault(
            d => string.Equals(d.Path, RootLandingSourcePath, StringComparison.OrdinalIgnoreCase));
        var startHereSection = sections.FirstOrDefault(section => section.Section == DocPublicSection.StartHere);
        var featuredPageGroups = BuildProofPathGroups(landingDoc, docs, startHereSection);

        return new DocLandingViewModel
        {
            Heading = landingDoc is not null ? GetCuratedHeading(landingDoc) : NeutralLandingHeading,
            Description = landingDoc is not null ? GetCuratedDescription(landingDoc) : NeutralLandingDescription,
            LandingDoc = landingDoc,
            StartHereHref = startHereSection is null ? null : _docsUrlBuilder.BuildSectionUrl(DocPublicSection.StartHere),
            VisibleDocs = visibleDocs,
            FeaturedPageGroups = featuredPageGroups,
            SecondarySections = BuildSecondarySections(sections, docs)
        };
    }

    private IReadOnlyList<DocLandingFeaturedPageGroupViewModel> BuildProofPathGroups(
        DocNode? landingDoc,
        IReadOnlyList<DocNode> docs,
        DocSectionSnapshot? startHereSection)
    {
        var curatedGroups = _featuredPageResolver.ResolveGroups(landingDoc, docs);
        if (curatedGroups.Count > 0)
        {
            return curatedGroups;
        }

        if (startHereSection is null)
        {
            return [];
        }

        var candidates = startHereSection.VisiblePages
            .Where(doc => !string.Equals(doc.Path, RootLandingSourcePath, StringComparison.OrdinalIgnoreCase))
            .Where(doc => !string.Equals(doc.Path, startHereSection.LandingDoc?.Path, StringComparison.OrdinalIgnoreCase))
            .Where(doc => !SidebarDisplayHelper.IsTypeAnchorNode(doc))
            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .Take(DefaultProofPathStageLabels.Length)
            .ToList();

        var pages = candidates
            .Select(
                (doc, index) =>
                {
                    var metadata = doc.Metadata;
                    var summary = metadata?.Summary;
                    return new DocLandingFeaturedPageViewModel
                    {
                        Question = DefaultProofPathStageLabels[Math.Min(index, DefaultProofPathStageLabels.Length - 1)],
                        Title = ResolveDisplayTitle(doc),
                        Href = _docsUrlBuilder.BuildDocUrl(GetSnapshotCanonicalPath(doc)),
                        PageType = metadata?.PageType,
                        PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(metadata?.PageType),
                        SupportingText = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim()
                    };
                })
            .ToList();

        return pages.Count == 0
            ? []
            :
            [
                new DocLandingFeaturedPageGroupViewModel
                {
                    Intent = "start-here",
                    Label = "Start Here",
                    Summary = DocPublicSectionCatalog.GetPurpose(DocPublicSection.StartHere),
                    Pages = pages
                }
            ];
    }

    private IReadOnlyList<DocHomeSectionViewModel> BuildSecondarySections(
        IReadOnlyList<DocSectionSnapshot> sections,
        IReadOnlyList<DocNode> docs)
    {
        return sections
            .Where(section => section.Section != DocPublicSection.StartHere)
            .Select(
                section => new DocHomeSectionViewModel
                {
                    Section = section.Section,
                    Label = section.Label,
                    Slug = section.Slug,
                    Href = _docsUrlBuilder.BuildSectionUrl(section.Section),
                    Purpose = DocPublicSectionCatalog.GetPurpose(section.Section),
                    KeyRoutes = BuildSectionKeyRoutes(section, docs, maxRoutes: 2)
                })
            .ToList();
    }

    private IReadOnlyList<DocSectionLinkViewModel> BuildSectionKeyRoutes(
        DocSectionSnapshot snapshot,
        IReadOnlyList<DocNode> docs,
        int maxRoutes)
    {
        if (snapshot.LandingDoc is not null)
        {
            var curatedRoutes = _featuredPageResolver.ResolveGroups(snapshot.LandingDoc, docs)
                .SelectMany(group => group.Pages.Select(page => (Group: group, Page: page)))
                .Take(maxRoutes)
                .Select(
                    item => new DocSectionLinkViewModel
                    {
                        Title = item.Page.Title,
                        Href = item.Page.Href,
                        Summary = item.Page.SupportingText,
                        Eyebrow = string.Equals(item.Group.Label, "Featured", StringComparison.OrdinalIgnoreCase)
                            ? item.Page.Question
                            : item.Group.Label,
                        PageTypeBadge = item.Page.PageTypeBadge
                    })
                .ToList();

            if (curatedRoutes.Count > 0)
            {
                return curatedRoutes;
            }
        }

        var candidates = snapshot.VisiblePages
            .Where(doc => !string.Equals(doc.Path, snapshot.LandingDoc?.Path, StringComparison.OrdinalIgnoreCase))
            .Where(doc => !SidebarDisplayHelper.IsTypeAnchorNode(doc))
            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxRoutes)
            .ToList();

        if (candidates.Count == 0 && snapshot.LandingDoc is not null)
        {
            candidates = [snapshot.LandingDoc];
        }

        return candidates.Select(CreateSectionLink).ToList();
    }

    private DocSectionPageViewModel BuildSectionPageViewModel(
        DocSectionSnapshot snapshot,
        string? startHereHref)
    {
        var currentHref = _docsUrlBuilder.BuildSectionUrl(snapshot.Section);
        var sparseRoutes = snapshot.VisiblePages.Count <= 1
            ? BuildSparseSectionRoutes(snapshot)
            : [];

        return new DocSectionPageViewModel
        {
            Section = snapshot.Section,
            Heading = snapshot.Label,
            Description = DocPublicSectionCatalog.GetPurpose(snapshot.Section),
            IsSparse = snapshot.VisiblePages.Count <= 1,
            KeyRoutes = sparseRoutes,
            Groups = DocSectionDisplayBuilder.BuildGroups(snapshot, currentHref, docsRootPath: _docsUrlBuilder.CurrentDocsRootPath),
            DocsHomeHref = _docsUrlBuilder.BuildHomeUrl(),
            StartHereHref = startHereHref
        };
    }

    private IReadOnlyList<DocSectionLinkViewModel> BuildSparseSectionRoutes(DocSectionSnapshot snapshot)
    {
        return snapshot.VisiblePages
            .Where(doc => !SidebarDisplayHelper.IsTypeAnchorNode(doc))
            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .Take(1)
            .Select(CreateSectionLink)
            .ToList();
    }

    private DocSectionPageViewModel BuildUnavailableSectionViewModel(
        DocPublicSection? section,
        string? startHereHref)
    {
        var heading = section is null
            ? SectionUnavailableHeading
            : DocPublicSectionCatalog.GetLabel(section.Value);
        var description = section is null
            ? "This docs section is not available in the current public experience."
            : DocPublicSectionCatalog.GetPurpose(section.Value);

        return new DocSectionPageViewModel
        {
            Section = section,
            Heading = heading,
            Description = description,
            IsUnavailable = true,
            AvailabilityMessage = "This section may be hidden from the public shell, moved to a different route, or not have any visible pages yet.",
            DocsHomeHref = _docsUrlBuilder.BuildHomeUrl(),
            StartHereHref = startHereHref
        };
    }

    private string? ResolveStartHereHref(IReadOnlyList<DocSectionSnapshot> sections)
    {
        return sections.Any(snapshot => snapshot.Section == DocPublicSection.StartHere)
            ? _docsUrlBuilder.BuildSectionUrl(DocPublicSection.StartHere)
            : null;
    }

    private DocDetailsViewModel BuildDetailsViewModel(
        DocDetailsViewModel details,
        IReadOnlyList<DocNode> docs,
        IReadOnlyList<DocSectionSnapshot> sections)
    {
        var doc = details.Document;
        var pathResolver = DocPathResolver.Create(docs);
        var pathBaseAwareDoc = doc with
        {
            Content = DocContentLinkRewriter.PrefixPathBaseForDocsUrls(
                doc.Content,
                _docsUrlBuilder.CurrentDocsRootPath,
                HttpContext.Request.PathBase.Value)
        };
        var metadata = doc.Metadata;
        var resolvedTitle = ResolveDisplayTitle(doc);
        var summary = metadata?.Summary;
        var showSummary = !string.IsNullOrWhiteSpace(summary) && metadata?.SummaryIsDerived != true;
        var pageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(metadata?.PageType);
        var component = metadata?.ComponentIsDerived == true || string.IsNullOrWhiteSpace(metadata?.Component)
            ? null
            : metadata!.Component!.Trim();
        var audience = metadata?.AudienceIsDerived == true || string.IsNullOrWhiteSpace(metadata?.Audience)
            ? null
            : metadata!.Audience!.Trim();
        var currentSectionSnapshot = TryResolvePublicSection(metadata?.NavGroup, sections, out var publicSection)
            ? sections.First(section => section.Section == publicSection)
            : null;
        var currentHref = _docsUrlBuilder.BuildDocUrl(GetSnapshotCanonicalPath(doc));
        var isSectionLanding = currentSectionSnapshot?.LandingDoc is not null
                               && string.Equals(currentSectionSnapshot.LandingDoc.Path, doc.Path, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<DocLandingFeaturedPageGroupViewModel> featuredPageGroups = [];
        IReadOnlyList<DocSectionGroupViewModel> sectionGroups = [];
        if (isSectionLanding && currentSectionSnapshot is not null)
        {
            featuredPageGroups = _featuredPageResolver.ResolveGroups(doc, docs);
            var remainingPages = currentSectionSnapshot.VisiblePages
                .Where(item => !string.Equals(item.Path, doc.Path, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (remainingPages.Count > 0)
            {
                var sectionSnapshot = currentSectionSnapshot with
                {
                    LandingDoc = null,
                    VisiblePages = remainingPages
                };
                sectionGroups = DocSectionDisplayBuilder.BuildGroups(
                    sectionSnapshot,
                    currentHref,
                    docsRootPath: _docsUrlBuilder.CurrentDocsRootPath);
            }
        }

        return details with
        {
            Document = pathBaseAwareDoc,
            Title = resolvedTitle,
            Summary = summary,
            ShowSummary = showSummary,
            IsCSharpApiDoc = doc.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
            IsApiSurfaceDoc = IsApiSurfaceDoc(doc),
            PageTypeBadge = pageTypeBadge,
            Component = component,
            Audience = audience,
            Breadcrumbs = BuildBreadcrumbs(doc, currentSectionSnapshot, resolvedTitle, docs),
            PublicSection = currentSectionSnapshot?.Section,
            PublicSectionLabel = currentSectionSnapshot?.Label,
            PublicSectionHref = currentSectionSnapshot is null ? null : _docsUrlBuilder.BuildSectionUrl(currentSectionSnapshot.Section),
            PublicSectionPurpose = currentSectionSnapshot is null ? null : DocPublicSectionCatalog.GetPurpose(currentSectionSnapshot.Section),
            ContributorSourceUsesTurbo = ShouldUseDocsFrame(details.ContributorProvenance?.SourceHref, pathResolver),
            ContributorEditUsesTurbo = ShouldUseDocsFrame(details.ContributorProvenance?.EditHref, pathResolver),
            TrustMigrationUsesTurbo = ShouldUseDocsFrame(metadata?.Trust?.Migration?.Href, pathResolver),
            IsSectionLanding = isSectionLanding,
            FeaturedPageGroups = featuredPageGroups,
            SectionGroups = sectionGroups
        };
    }

    private string PathBaseAware(string appRelativeUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRelativeUrl);
        var pathBase = HttpContext?.Request.PathBase.Value;
        if (string.IsNullOrWhiteSpace(pathBase) || string.Equals(pathBase, "/", StringComparison.Ordinal))
        {
            return appRelativeUrl;
        }

        var normalizedPathBase = pathBase.TrimEnd('/');
        return string.Equals(appRelativeUrl, "/", StringComparison.Ordinal)
            ? normalizedPathBase + "/"
            : normalizedPathBase + appRelativeUrl;
    }

    private IReadOnlyList<DocBreadcrumbViewModel> BuildBreadcrumbs(
        DocNode doc,
        DocSectionSnapshot? currentSectionSnapshot,
        string resolvedTitle,
        IReadOnlyList<DocNode> docs)
    {
        var publishedDocHrefs = docs
            .Where(item => !string.IsNullOrWhiteSpace(item.CanonicalPath))
            .Select(item => _docsUrlBuilder.BuildDocUrl(GetSnapshotCanonicalPath(item)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedPath = doc.Path.Trim().Trim('/');
        var isNamespacePath = normalizedPath.Equals("Namespaces", StringComparison.OrdinalIgnoreCase)
                              || normalizedPath.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase);
        var metadataBreadcrumbs = doc.Metadata?.Breadcrumbs?
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .ToList();
        var parsedBreadcrumbs = new List<DocBreadcrumbViewModel>();

        if (isNamespacePath)
        {
            parsedBreadcrumbs.Add(
                new DocBreadcrumbViewModel
                {
                    Label = "Namespaces",
                    Href = _docsUrlBuilder.BuildDocUrl("Namespaces.html")
                });

            if (normalizedPath.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase))
            {
                var fullNamespace = normalizedPath["Namespaces/".Length..];
                var parts = fullNamespace.Split('.', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    var prefix = string.Join(".", parts.Take(i + 1));
                    var isLast = i == parts.Length - 1;
                    parsedBreadcrumbs.Add(
                        new DocBreadcrumbViewModel
                        {
                            Label = parts[i],
                            Href = isLast ? null : _docsUrlBuilder.BuildDocUrl($"Namespaces/{prefix}.html")
                        });
                }
            }
        }
        else
        {
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (segments.Count > 1
                && string.Equals(segments[^1], "README.md", StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(segments.Count - 1);
            }

            var current = string.Empty;
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                current = string.IsNullOrEmpty(current) ? segment : $"{current}/{segment}";
                var isLast = i == segments.Count - 1;
                parsedBreadcrumbs.Add(
                    new DocBreadcrumbViewModel
                    {
                        Label = segment,
                        Href = isLast ? null : _docsUrlBuilder.BuildDocUrl($"{current}.html")
                    });
            }
        }

        var metadataBreadcrumbCount = metadataBreadcrumbs?.Count ?? 0;
        var canUseMetadataBreadcrumbs = MetadataBreadcrumbsMatchPathTargets(
            doc.Metadata,
            metadataBreadcrumbs,
            parsedBreadcrumbs.Count);
        if (!canUseMetadataBreadcrumbs)
        {
            if (currentSectionSnapshot is not null && currentSectionSnapshot.Section != DocPublicSection.ApiReference)
            {
                var sectionHref = _docsUrlBuilder.BuildSectionUrl(currentSectionSnapshot.Section);
                return string.Equals(currentSectionSnapshot.Label, resolvedTitle, StringComparison.OrdinalIgnoreCase)
                    ? [new DocBreadcrumbViewModel { Label = currentSectionSnapshot.Label }]
                    :
                    [
                        new DocBreadcrumbViewModel
                        {
                            Label = currentSectionSnapshot.Label,
                            Href = sectionHref
                        },
                        new DocBreadcrumbViewModel
                        {
                            Label = resolvedTitle
                        }
                    ];
            }

            return ResolveBreadcrumbHrefs(parsedBreadcrumbs, currentSectionSnapshot, publishedDocHrefs);
        }

        return metadataBreadcrumbs!
            .Select(
                (label, index) => new DocBreadcrumbViewModel
                {
                    Label = label,
                    Href = ResolveBreadcrumbHref(
                        label,
                        index == metadataBreadcrumbCount - 1,
                        index - (metadataBreadcrumbCount - parsedBreadcrumbs.Count) >= 0
                            ? parsedBreadcrumbs[index - (metadataBreadcrumbCount - parsedBreadcrumbs.Count)].Href
                            : null,
                        currentSectionSnapshot,
                        publishedDocHrefs)
                })
            .ToList();
    }

    private IReadOnlyList<DocBreadcrumbViewModel> ResolveBreadcrumbHrefs(
        IEnumerable<DocBreadcrumbViewModel> breadcrumbs,
        DocSectionSnapshot? currentSectionSnapshot,
        IReadOnlySet<string> publishedDocHrefs)
    {
        var items = breadcrumbs.ToArray();
        return items
            .Select(
                (breadcrumb, index) => breadcrumb with
                {
                    Href = ResolveBreadcrumbHref(
                        breadcrumb.Label,
                        index == items.Length - 1,
                        breadcrumb.Href,
                        currentSectionSnapshot,
                        publishedDocHrefs)
                })
            .ToArray();
    }

    private string? ResolveBreadcrumbHref(
        string label,
        bool isLast,
        string? candidateHref,
        DocSectionSnapshot? currentSectionSnapshot,
        IReadOnlySet<string> publishedDocHrefs)
    {
        if (isLast)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(candidateHref)
            && publishedDocHrefs.Contains(candidateHref))
        {
            return candidateHref;
        }

        return currentSectionSnapshot is not null
               && DocPublicSectionCatalog.TryResolve(label, out var section)
               && section == currentSectionSnapshot.Section
            ? _docsUrlBuilder.BuildSectionUrl(currentSectionSnapshot.Section)
            : null;
    }

    private static bool MetadataBreadcrumbsMatchPathTargets(
        DocMetadata? metadata,
        IReadOnlyList<string>? metadataBreadcrumbs,
        int parsedBreadcrumbCount)
    {
        var metadataBreadcrumbCount = metadataBreadcrumbs?.Count ?? 0;
        if (metadataBreadcrumbCount == 0 || metadata?.BreadcrumbsMatchPathTargets != true)
        {
            return false;
        }

        if (metadataBreadcrumbCount == parsedBreadcrumbCount)
        {
            return true;
        }

        var navGroupParent = metadata.NavGroup?.Trim();
        return metadataBreadcrumbCount == parsedBreadcrumbCount + 1
               && !string.IsNullOrWhiteSpace(navGroupParent)
               && string.Equals(metadataBreadcrumbs![0], navGroupParent, StringComparison.OrdinalIgnoreCase);
    }

    private DocSectionLinkViewModel CreateSectionLink(DocNode doc)
    {
        var metadata = doc.Metadata;
        var summary = metadata?.Summary;
        return new DocSectionLinkViewModel
        {
            Title = ResolveDisplayTitle(doc),
            Href = _docsUrlBuilder.BuildDocUrl(GetSnapshotCanonicalPath(doc)),
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
            PageTypeBadge = DocMetadataPresentation.ResolvePageTypeBadge(metadata?.PageType)
        };
    }

    private static string ResolveDisplayTitle(DocNode doc)
    {
        return string.IsNullOrWhiteSpace(doc.Metadata?.Title)
            ? doc.Title
            : doc.Metadata!.Title!.Trim();
    }

    private static bool TryResolvePublicSection(
        string? navGroup,
        IReadOnlyList<DocSectionSnapshot> sections,
        out DocPublicSection section)
    {
        if (DocPublicSectionCatalog.TryResolve(navGroup, out var resolvedSection)
            && sections.Any(item => item.Section == resolvedSection))
        {
            section = resolvedSection;
            return true;
        }

        section = default;
        return false;
    }

    private SearchPageViewModel BuildSearchPageViewModel(IReadOnlyList<DocNode> docs)
    {
        return new SearchPageViewModel(
            Title: "Search Documentation",
            Orientation: "Search across guides, examples, and API reference, or browse by filter when you are not sure what the corpus contains yet.",
            StarterHint: "Try a starter query, or open filters to browse by page type, component, audience, or status before typing.",
            SearchPlaceholder: "Search by topic, component, or page type",
            SuggestedQueries: ["getting started", "example", "api reference"],
            FailureFallbackLinks: BuildSearchFallbackLinks(docs));
    }

    private RazorDocsVersionArchiveViewModel BuildVersionArchiveViewModel(bool entryFallback)
    {
        var catalog = _versionCatalogService.GetCatalog();
        var recommendedVersion = catalog.RecommendedVersion;
        var versions = catalog.PublicVersions
            .Select(
                version => new RazorDocsVersionArchiveEntryViewModel
                {
                    Version = version.Version,
                    Label = version.Label,
                    Summary = version.Summary,
                    Href = version.IsAvailable ? version.ExactRootUrl : null,
                    IsRecommended = string.Equals(
                        version.Version,
                        recommendedVersion?.Version,
                        StringComparison.OrdinalIgnoreCase),
                    IsAvailable = version.IsAvailable,
                    SupportStateLabel = GetSupportStateLabel(version.SupportState),
                    AdvisoryLabel = GetAdvisoryLabel(version.AdvisoryState),
                    AvailabilityMessage = version.AvailabilityIssue
                })
            .ToList();

        return new RazorDocsVersionArchiveViewModel
        {
            Heading = entryFallback ? "Published documentation versions" : "Documentation versions",
            Description = entryFallback
                ? "The stable docs entry is waiting for a healthy recommended release tree. You can keep reading the preview surface or open an exact published version below."
                : "Choose the exact release you want to read, or keep using the current preview surface for unreleased work.",
            AvailabilityMessage = entryFallback
                ? $"No healthy recommended release tree is currently mounted at {PathBaseAware(DocsUrlBuilder.DocsEntryPath)}."
                : null,
            PreviewHref = _docsUrlBuilder.BuildHomeUrl(),
            VersionsHref = _docsUrlBuilder.BuildVersionsUrl(),
            Versions = versions
        };
    }

    private IReadOnlyList<SearchPageFallbackLink> BuildSearchFallbackLinks(IReadOnlyList<DocNode> docs)
    {
        var pathResolver = DocPathResolver.Create(docs);
        var links = new List<SearchPageFallbackLink>();

        TryAddFallbackLink(
            links,
            pathResolver,
            SelectFallbackDoc(
                docs,
                doc => HasPageType(doc, "guide", "concept", "tutorial", "troubleshooting")
                       || doc.Path.StartsWith("guides/", StringComparison.OrdinalIgnoreCase)),
            "Browse guides",
            "Open a high-signal guide while search is unavailable.");

        TryAddFallbackLink(
            links,
            pathResolver,
            SelectFallbackDoc(
                docs,
                doc => HasPageType(doc, "example")
                       || doc.Path.StartsWith("examples/", StringComparison.OrdinalIgnoreCase)),
            "Open an example",
            "Jump into a working example to keep moving.");

        TryAddFallbackLink(
            links,
            pathResolver,
            SelectFallbackDoc(
                docs,
                doc => HasPageType(doc, "api-reference", "api")
                       || doc.Path.StartsWith("Namespaces/", StringComparison.OrdinalIgnoreCase)),
            "Explore API reference",
            "Browse the reference surface directly while search recovers.");

        if (links.Count < 3)
        {
            var namespacesRoot = SelectFallbackDoc(
                docs,
                doc => string.Equals(doc.Path, "Namespaces", StringComparison.OrdinalIgnoreCase));
            TryAddFallbackLink(
                links,
                pathResolver,
                namespacesRoot,
                "Browse namespaces",
                "Use the namespace index when you need the reference map.");
        }

        if (links.Count < 3)
        {
            links.Add(
                new SearchPageFallbackLink(
                    "Documentation index",
                    _docsUrlBuilder.BuildHomeUrl(),
                    "Return to the docs index and keep exploring from there.",
                    UsesDocsFrame: false));
        }

        return links;
    }

    private void TryAddFallbackLink(
        ICollection<SearchPageFallbackLink> links,
        DocPathResolver pathResolver,
        DocNode? doc,
        string title,
        string description)
    {
        if (doc is null)
        {
            return;
        }

        var href = DocAggregator.BuildSearchDocUrl(_docsUrlBuilder.CurrentDocsRootPath, doc.Path);
        if (links.Any(link => string.Equals(link.Href, href, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        links.Add(
            new SearchPageFallbackLink(
                title,
                href,
                description,
                UsesDocsFrame: ShouldUseDocsFrame(href, pathResolver)));
    }

    private bool ShouldUseDocsFrame(string? href, DocPathResolver pathResolver)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var trimmedHref = href.Trim();
        var hrefPath = ExtractHrefPath(trimmedHref);
        if (string.Equals(hrefPath, _docsUrlBuilder.BuildHomeUrl(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_docsUrlBuilder.IsCurrentDocsPath(hrefPath))
        {
            return true;
        }

        if (!string.Equals(_docsUrlBuilder.CurrentDocsRootPath, "/", StringComparison.Ordinal)
            || !hrefPath.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        return pathResolver.Resolve(hrefPath.TrimStart('/')) is not null;
    }

    private static DocNode? SelectFallbackDoc(
        IEnumerable<DocNode> docs,
        Func<DocNode, bool> predicate)
    {
        return docs
            .Where(
                doc => !doc.IsDirectory
                       && doc.Metadata?.HideFromPublicNav != true
                       && doc.Metadata?.HideFromSearch != true
                       && predicate(doc))
            .OrderBy(doc => doc.Metadata?.Order ?? int.MaxValue)
            .ThenBy(doc => doc.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(doc => doc.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string GetSupportStateLabel(RazorDocsVersionSupportState supportState)
    {
        return supportState switch
        {
            RazorDocsVersionSupportState.Current => "Current",
            RazorDocsVersionSupportState.Maintained => "Maintained",
            RazorDocsVersionSupportState.Deprecated => "Deprecated",
            RazorDocsVersionSupportState.Archived => "Archived",
            _ => supportState.ToString()
        };
    }

    private static string? GetAdvisoryLabel(RazorDocsVersionAdvisoryState advisoryState)
    {
        return advisoryState switch
        {
            RazorDocsVersionAdvisoryState.None => null,
            RazorDocsVersionAdvisoryState.Vulnerable => "Vulnerable",
            RazorDocsVersionAdvisoryState.SecurityRisk => "Security risk",
            _ => advisoryState.ToString()
        };
    }

    private static string GetSnapshotCanonicalPath(DocNode doc)
    {
        return doc.CanonicalPath
               ?? throw new InvalidOperationException(
                   $"DocsController requires snapshot canonical paths. Doc '{doc.Path}' was missing CanonicalPath.");
    }

    private static string ExtractHrefPath(string href)
    {
        var fragmentIndex = href.IndexOf('#');
        var withoutFragment = fragmentIndex >= 0 ? href[..fragmentIndex] : href;
        var queryIndex = withoutFragment.IndexOf('?');
        return queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;
    }
    private static string GetCuratedHeading(DocNode landingDoc)
    {
        var title = string.IsNullOrWhiteSpace(landingDoc.Metadata?.Title)
            ? landingDoc.Title
            : landingDoc.Metadata!.Title;
        if (string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), "Home", StringComparison.OrdinalIgnoreCase))
        {
            return NeutralLandingHeading;
        }

        return title.Trim();
    }

    private static string GetCuratedDescription(DocNode landingDoc)
    {
        var summary = landingDoc.Metadata?.Summary;
        return string.IsNullOrWhiteSpace(summary)
            ? CuratedLandingDescription
            : summary.Trim();
    }

    private static bool HasPageType(DocNode doc, params string[] expectedTypes)
    {
        var pageType = doc.Metadata?.PageType;
        if (string.IsNullOrWhiteSpace(pageType))
        {
            return false;
        }

        return expectedTypes.Any(expected => string.Equals(pageType, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsApiSurfaceDoc(DocNode doc)
    {
        return !IsMarkdownDoc(doc.Path)
               || IsApiSurfacePageType(doc.Metadata?.PageType);
    }

    private static bool IsApiSurfacePageType(string? pageType)
    {
        var normalizedPageType = DocMetadataPresentation.NormalizeToken(pageType);

        return normalizedPageType is "api" or "api-reference";
    }

    private static bool IsMarkdownDoc(string path)
    {
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static RazorDocsVersionCatalogService CreateDefaultVersionCatalogService()
    {
        return new RazorDocsVersionCatalogService(
            new RazorDocsOptions(),
            new DefaultWebHostEnvironment(),
            NullLogger<RazorDocsVersionCatalogService>.Instance);
    }

    private sealed class DefaultWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(DocsController).Assembly.GetName().Name ?? "RazorDocs";

        public IFileProvider WebRootFileProvider { get; set; } = null!;

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
