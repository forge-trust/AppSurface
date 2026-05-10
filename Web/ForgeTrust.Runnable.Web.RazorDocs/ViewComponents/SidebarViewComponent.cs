using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorDocs.ViewComponents;

/// <summary>
/// A view component that renders the sidebar navigation for public documentation sections.
/// </summary>
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
    /// creates a fresh <see cref="DocsUrlBuilder"/> from those options, which is suitable for direct construction in
    /// tests or ad hoc usage but does not reuse any shared builder instance already registered in dependency
    /// injection.
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
    /// Retrieves the normalized public sections and shapes them into the sidebar display model.
    /// </summary>
    /// <returns>A view result containing the section-first sidebar view model.</returns>
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

    private async Task<DocSidebarHarvestHealthViewModel?> ResolveHarvestHealthAsync()
    {
        if (!RazorDocsHarvestHealthVisibility.ShouldShowChrome(_options, _environment))
        {
            return null;
        }

        var health = await _aggregator.GetHarvestHealthAsync();
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

        public IFileProvider WebRootFileProvider { get; set; } = null!;

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Development;

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
