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
/// normalized public section links and whose <see cref="DocSidebarViewModel.Diagnostics"/> is populated only when
/// maintainer diagnostics chrome is visible for the configured options and host environment.
/// </remarks>
public class SidebarViewComponent : ViewComponent
{
    private readonly DocAggregator _aggregator;
    private readonly AppSurfaceDocsOptions _options;
    private readonly DocsUrlBuilder _docsUrlBuilder;
    private readonly IWebHostEnvironment _environment;
    private readonly Func<CancellationToken, Task<DocHarvestHealthSnapshot>> _getHarvestHealthAsync;
    private readonly string[] _namespacePrefixes;

    /// <summary>
    /// Initializes a new instance of the <see cref="SidebarViewComponent"/> class.
    /// </summary>
    /// <remarks>
    /// This is the convenience overload for callers that only have <see cref="AppSurfaceDocsOptions" /> available. It
    /// creates a fresh <see cref="DocsUrlBuilder"/> from those options and a fallback <see cref="IWebHostEnvironment"/>
    /// initialized to <see cref="Environments.Production"/>. This is suitable for direct construction in tests or ad
    /// hoc usage but does not reuse any shared builder instance or inherit any development chrome or local defaults
    /// already registered in dependency injection. Pitfall: tests or ad hoc hosts that expect development behavior must
    /// supply their own <see cref="IWebHostEnvironment"/> through the other constructor overload.
    /// </remarks>
    /// <param name="aggregator">The documentation aggregator used to retrieve document nodes.</param>
    /// <param name="options">Typed AppSurface Docs options used for optional namespace prefix simplification settings.</param>
    public SidebarViewComponent(DocAggregator aggregator, AppSurfaceDocsOptions options)
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
    /// <param name="options">Typed AppSurface Docs options used for optional namespace prefix simplification settings.</param>
    /// <param name="docsUrlBuilder">Shared URL builder for the live source-backed docs surface.</param>
    /// <param name="environment">Host environment used for development-default health chrome visibility.</param>
    [ActivatorUtilitiesConstructor]
    public SidebarViewComponent(
        DocAggregator aggregator,
        AppSurfaceDocsOptions options,
        DocsUrlBuilder docsUrlBuilder,
        IWebHostEnvironment environment)
        : this(aggregator, options, docsUrlBuilder, environment, CreateHarvestHealthLookup(aggregator))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SidebarViewComponent"/> class with an explicit harvest health lookup
    /// delegate.
    /// </summary>
    /// <remarks>
    /// This overload exists as a focused test seam for diagnostics fallback behavior. Production hosts should use the
    /// dependency-injection constructor so health status comes directly from <see cref="DocAggregator"/>. The delegate
    /// must preserve the same cancellation semantics as <see cref="DocAggregator.GetHarvestHealthAsync(CancellationToken)"/>
    /// when a test needs to verify request-aborted behavior.
    /// </remarks>
    /// <param name="aggregator">The documentation aggregator used to retrieve document nodes.</param>
    /// <param name="options">Typed AppSurface Docs options used for optional namespace prefix simplification settings.</param>
    /// <param name="docsUrlBuilder">Shared URL builder for the live source-backed docs surface.</param>
    /// <param name="environment">Host environment used for development-default health chrome visibility.</param>
    /// <param name="getHarvestHealthAsync">Delegate used to resolve diagnostics health status.</param>
    internal SidebarViewComponent(
        DocAggregator aggregator,
        AppSurfaceDocsOptions options,
        DocsUrlBuilder docsUrlBuilder,
        IWebHostEnvironment environment,
        Func<CancellationToken, Task<DocHarvestHealthSnapshot>> getHarvestHealthAsync)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Sidebar);
        ArgumentNullException.ThrowIfNull(options.Sidebar.NamespacePrefixes);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(getHarvestHealthAsync);

        _aggregator = aggregator;
        _options = options;
        _docsUrlBuilder = docsUrlBuilder;
        _environment = environment;
        _getHarvestHealthAsync = getHarvestHealthAsync;
        _namespacePrefixes = options.Sidebar.NamespacePrefixes
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .ToArray();
    }

    private static Func<CancellationToken, Task<DocHarvestHealthSnapshot>> CreateHarvestHealthLookup(DocAggregator aggregator)
    {
        ArgumentNullException.ThrowIfNull(aggregator);

        return aggregator.GetHarvestHealthAsync;
    }

    /// <summary>
    /// Retrieves the normalized public sections and optional diagnostics chrome, then shapes them into the sidebar
    /// display model.
    /// </summary>
    /// <remarks>
    /// <see cref="InvokeAsync"/> returns a <see cref="DocSidebarViewModel"/> whose sections come from normalized public
    /// section snapshots and whose <see cref="DocSidebarViewModel.Diagnostics"/> value is resolved by
    /// <see cref="ResolveDiagnosticsAsync"/>. Diagnostics chrome is controlled separately from route responses: chrome
    /// exposure decides whether the sidebar may advertise a tool, while route exposure decides whether the row receives
    /// an href. Health resolution uses <see cref="DocAggregator.GetHarvestHealthAsync(CancellationToken)"/> only when
    /// health chrome is visible; route-inspector links remain independent of health lookup success.
    /// </remarks>
    /// <returns>A view result containing the section-first sidebar view model and optional diagnostics chrome.</returns>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var sections = await _aggregator.GetPublicSectionsAsync();
        var currentContext = await ResolveCurrentContextAsync();
        var diagnostics = await ResolveDiagnosticsAsync();
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

        return View(
            new DocSidebarViewModel
            {
                Sections = sidebarSections,
                Diagnostics = diagnostics.Model,
                HarvestHealth = diagnostics.HarvestHealth
            });
    }

    /// <summary>
    /// Resolves optional diagnostics chrome for the current sidebar request.
    /// </summary>
    /// <remarks>
    /// Returns an empty resolution when neither health nor route-inspector chrome is visible. When one source is visible,
    /// the returned model includes only the rows whose chrome policy and route policy allow them. A health row can be
    /// status-only when health chrome is visible but routes are hidden.
    /// </remarks>
    /// <returns>The diagnostics sidebar resolution for the current request.</returns>
    private async Task<SidebarDiagnosticsResolution> ResolveDiagnosticsAsync()
    {
        var showHealthChrome = AppSurfaceDocsHarvestHealthVisibility.ShouldShowChrome(_options, _environment);
        var exposeHealthRoutes = AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(_options, _environment);
        var showDiagnosticsChrome = AppSurfaceDocsDiagnosticsVisibility.ShouldShowChrome(_options, _environment);
        var exposeRouteInspector = AppSurfaceDocsDiagnosticsVisibility.IsRouteInspectorExposed(_options, _environment);
        DocSidebarHarvestHealthViewModel? legacyHealth = null;
        DocSidebarDiagnosticsStatusViewModel? status = null;
        List<DocSidebarDiagnosticsToolViewModel> tools = [];

        if (showHealthChrome)
        {
            var requestAborted = ResolveRequestAborted();
            try
            {
                var health = await _getHarvestHealthAsync(requestAborted);
                var healthHref = exposeHealthRoutes ? _docsUrlBuilder.BuildHealthUrl() : null;
                var healthJsonHref = exposeHealthRoutes ? _docsUrlBuilder.BuildHealthJsonUrl() : null;
                var healthOk = AppSurfaceDocsHarvestHealthResponse.IsOk(health.Status);

                legacyHealth = new DocSidebarHarvestHealthViewModel
                {
                    Status = health.Status.ToString(),
                    Ok = healthOk,
                    Href = healthHref
                };
                status = new DocSidebarDiagnosticsStatusViewModel
                {
                    Label = health.Status.ToString(),
                    Ok = healthOk
                };
                tools.Add(
                    new DocSidebarDiagnosticsToolViewModel
                    {
                        Label = "Harvest health",
                        Href = healthHref,
                        Summary = $"Harvest {health.Status}",
                        JsonAction = string.IsNullOrWhiteSpace(healthJsonHref)
                            ? null
                            : new DocSidebarDiagnosticsActionViewModel
                            {
                                Label = "Health JSON",
                                Href = healthJsonHref
                            }
                    });
            }
            catch (Exception) when (!requestAborted.IsCancellationRequested)
            {
                status = new DocSidebarDiagnosticsStatusViewModel { Label = "Health unavailable", Ok = false };
                tools.Add(
                    new DocSidebarDiagnosticsToolViewModel
                    {
                        Label = "Harvest health",
                        Summary = "Health unavailable"
                    });
            }
        }

        if (showDiagnosticsChrome && exposeRouteInspector)
        {
            tools.Add(
                new DocSidebarDiagnosticsToolViewModel
                {
                    Label = "Route inspector",
                    Href = _docsUrlBuilder.BuildRouteInspectorUrl(),
                    Summary = "Route manifest",
                    JsonAction = new DocSidebarDiagnosticsActionViewModel
                    {
                        Label = "Routes JSON",
                        Href = _docsUrlBuilder.BuildRouteInspectorJsonUrl()
                    }
                });
        }

        if (status is null && tools.Count == 0)
        {
            return new SidebarDiagnosticsResolution(null, legacyHealth);
        }

        return new SidebarDiagnosticsResolution(
            new DocSidebarDiagnosticsViewModel
            {
                Status = status,
                Tools = tools
            },
            legacyHealth);
    }

    private CancellationToken ResolveRequestAborted()
    {
        if (ViewContext.HttpContext is not { } httpContext)
        {
            return CancellationToken.None;
        }

        return httpContext.RequestAborted;
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
        public string ApplicationName { get; set; } = typeof(SidebarViewComponent).Assembly.GetName().Name ?? "AppSurface Docs";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record SidebarDiagnosticsResolution(
        DocSidebarDiagnosticsViewModel? Model,
        DocSidebarHarvestHealthViewModel? HarvestHealth);
}
