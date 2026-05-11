using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.ViewComponents;

/// <summary>
/// A view component that renders the sidebar navigation for public documentation sections.
/// </summary>
/// <remarks>
/// The component returns a <see cref="DocSidebarViewModel"/> whose <see cref="DocSidebarViewModel.Sections"/> contain
/// normalized public section links and whose <see cref="DocSidebarViewModel.HarvestHealth"/> is populated only when
/// harvest health chrome is visible for the configured options and host environment.
/// </remarks>
public class SidebarViewComponent : ViewComponent
{
    private readonly DocAggregator _aggregator;
    private readonly RazorDocsOptions _options;
    private readonly DocsUrlBuilder _docsUrlBuilder;
    private readonly IWebHostEnvironment _environment;
    private readonly string[] _namespacePrefixes;

    /// <summary>
    /// Initializes a new instance of the <see cref="SidebarViewComponent"/> class.
    /// </summary>
    /// <remarks>
    /// This is the convenience overload for callers that only have <see cref="RazorDocsOptions" /> available. It
    /// creates a fresh <see cref="DocsUrlBuilder"/> from those options and a fallback <see cref="IWebHostEnvironment"/>
    /// initialized to <see cref="Environments.Production"/>. This is suitable for direct construction in tests or ad
    /// hoc usage but does not reuse any shared builder instance or inherit any development chrome or local defaults
    /// already registered in dependency injection. Pitfall: tests or ad hoc hosts that expect development behavior must
    /// supply their own <see cref="IWebHostEnvironment"/> through the other constructor overload.
    /// </remarks>
    /// <param name="aggregator">The documentation aggregator used to retrieve document nodes.</param>
    /// <param name="options">Typed RazorDocs options used for optional namespace prefix simplification settings.</param>
    public SidebarViewComponent(DocAggregator aggregator, RazorDocsOptions options)
        : this(aggregator, options, new DocsUrlBuilder(options), new DefaultWebHostEnvironment())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SidebarViewComponent"/> class.
    /// </summary>
    /// <remarks>
    /// This is the dependency-injection-preferred overload. The shared <paramref name="docsUrlBuilder"/> should stay
    /// aligned with <paramref name="options"/> so route detection, search-path checks, and generated sidebar links all
    /// describe the same docs surface. Prefer this overload whenever a host already registered a shared or
    /// preconfigured <see cref="DocsUrlBuilder"/> instance.
    /// </remarks>
    /// <param name="aggregator">The documentation aggregator used to retrieve document nodes.</param>
    /// <param name="options">Typed RazorDocs options used for optional namespace prefix simplification settings.</param>
    /// <param name="docsUrlBuilder">Shared URL builder for the live source-backed docs surface.</param>
    /// <param name="environment">Host environment used for development-default health chrome visibility.</param>
    [ActivatorUtilitiesConstructor]
    public SidebarViewComponent(
        DocAggregator aggregator,
        RazorDocsOptions options,
        DocsUrlBuilder docsUrlBuilder,
        IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Sidebar);
        ArgumentNullException.ThrowIfNull(options.Sidebar.NamespacePrefixes);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);
        ArgumentNullException.ThrowIfNull(environment);

        _aggregator = aggregator;
        _options = options;
        _docsUrlBuilder = docsUrlBuilder;
        _environment = environment;
        _namespacePrefixes = options.Sidebar.NamespacePrefixes
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .ToArray();
    }

    /// <summary>
    /// Retrieves the normalized public sections and optional harvest health chrome, then shapes them into the sidebar
    /// display model.
    /// </summary>
    /// <remarks>
    /// <see cref="InvokeAsync"/> returns a <see cref="DocSidebarViewModel"/> whose sections come from normalized public
    /// section snapshots and whose <see cref="DocSidebarViewModel.HarvestHealth"/> value is resolved by
    /// <see cref="ResolveHarvestHealthAsync"/>. Harvest health is controlled by
    /// <see cref="RazorDocsHarvestHealthVisibility"/> and may be <c>null</c> when chrome is hidden. When present, it
    /// contains a <see cref="DocSidebarHarvestHealthViewModel.Status"/>,
    /// <see cref="DocSidebarHarvestHealthViewModel.Ok"/>, and an optional
    /// <see cref="DocSidebarHarvestHealthViewModel.Href"/>. The href is omitted when chrome is visible but health
    /// routes are not exposed. Health resolution uses <see cref="DocAggregator.GetHarvestHealthAsync(CancellationToken)"/>,
    /// links with <see cref="DocsUrlBuilder.BuildHealthUrl"/>, and observes the current request's aborted token.
    /// </remarks>
    /// <returns>A view result containing the section-first sidebar view model and optional harvest health chrome.</returns>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var sections = await _aggregator.GetPublicSectionsAsync();
        var currentContext = await ResolveCurrentContextAsync();
        var harvestHealth = await ResolveHarvestHealthAsync();
        var sidebarSections = sections
            .Select(
                snapshot => new DocSidebarSectionViewModel
                {
                    Section = snapshot.Section,
                    Label = snapshot.Label,
                    Slug = snapshot.Slug,
                    Href = _docsUrlBuilder.BuildSectionUrl(snapshot.Section),
                    IsActive = currentContext.Section == snapshot.Section,
                    IsExpanded = currentContext.Section == snapshot.Section,
                    Groups = DocSectionDisplayBuilder.BuildGroups(
                        snapshot,
                        currentContext.CurrentHref,
                        _namespacePrefixes,
                        _docsUrlBuilder.CurrentDocsRootPath)
                })
            .ToList();

        return View(new DocSidebarViewModel { Sections = sidebarSections, HarvestHealth = harvestHealth });
    }

    /// <summary>
    /// Resolves the optional harvest health chrome view model for the current sidebar request.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> when <see cref="RazorDocsHarvestHealthVisibility.ShouldShowChrome(RazorDocsOptions, IHostEnvironment)"/>
    /// hides chrome. Otherwise it reads the current harvest snapshot through
    /// <see cref="DocAggregator.GetHarvestHealthAsync(CancellationToken)"/>, maps the status and verification result,
    /// and supplies an href from <see cref="DocsUrlBuilder.BuildHealthUrl"/> only when
    /// <see cref="RazorDocsHarvestHealthVisibility.AreRoutesExposed(RazorDocsOptions, IHostEnvironment)"/> exposes the
    /// operator route. The aggregation wait respects <see cref="HttpContext.RequestAborted"/> when a view context is
    /// available.
    /// </remarks>
    /// <returns>The sidebar harvest health view model, or <c>null</c> when chrome is hidden.</returns>
    private async Task<DocSidebarHarvestHealthViewModel?> ResolveHarvestHealthAsync()
    {
        if (!RazorDocsHarvestHealthVisibility.ShouldShowChrome(_options, _environment))
        {
            return null;
        }

        var requestAborted = ViewContext?.HttpContext?.RequestAborted ?? CancellationToken.None;
        var health = await _aggregator.GetHarvestHealthAsync(requestAborted);
        var href = RazorDocsHarvestHealthVisibility.AreRoutesExposed(_options, _environment)
            ? _docsUrlBuilder.BuildHealthUrl()
            : null;

        return new DocSidebarHarvestHealthViewModel
        {
            Status = health.Status.ToString(),
            Ok = RazorDocsHarvestHealthResponse.IsOk(health.Status),
            Href = href
        };
    }

    private async Task<(DocPublicSection? Section, string? CurrentHref)> ResolveCurrentContextAsync()
    {
        var requestPath = ViewContext?.HttpContext?.Request?.Path.Value;
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return (null, null);
        }

        requestPath = NormalizeRequestPath(requestPath);

        var isRootMounted = string.Equals(_docsUrlBuilder.CurrentDocsRootPath, "/", StringComparison.Ordinal);

        if (string.Equals(requestPath, _docsUrlBuilder.CurrentDocsRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return (DocPublicSection.StartHere, requestPath);
        }

        var sectionPrefix = isRootMounted
            ? "/sections/"
            : _docsUrlBuilder.CurrentDocsRootPath + "/sections/";
        if (requestPath.StartsWith(sectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var slug = requestPath[sectionPrefix.Length..].Trim('/');
            if (DocPublicSectionCatalog.TryResolveSlug(slug, out var section))
            {
                return (section, requestPath);
            }
        }

        if (!_docsUrlBuilder.IsCurrentDocsPath(requestPath)
            || string.Equals(requestPath, _docsUrlBuilder.BuildSearchUrl(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestPath, _docsUrlBuilder.BuildSearchIndexUrl(), StringComparison.OrdinalIgnoreCase))
        {
            if (!isRootMounted
                || string.Equals(requestPath, _docsUrlBuilder.BuildSearchUrl(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestPath, _docsUrlBuilder.BuildSearchIndexUrl(), StringComparison.OrdinalIgnoreCase)
                || !requestPath.StartsWith("/", StringComparison.Ordinal))
            {
                return (null, null);
            }
        }

        var docPath = isRootMounted
            ? requestPath.TrimStart('/')
            : requestPath[(_docsUrlBuilder.CurrentDocsRootPath.Length + 1)..];
        var doc = await _aggregator.GetDocByPathAsync(docPath);
        if (doc is not null && DocPublicSectionCatalog.TryResolve(doc.Metadata?.NavGroup, out var sectionForDoc))
        {
            return (sectionForDoc, requestPath);
        }

        return (null, requestPath);
    }

    private static string NormalizeRequestPath(string requestPath)
    {
        return requestPath.Length > 1
            ? requestPath.TrimEnd('/')
            : requestPath;
    }

    private sealed class DefaultWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(SidebarViewComponent).Assembly.GetName().Name ?? "RazorDocs";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
