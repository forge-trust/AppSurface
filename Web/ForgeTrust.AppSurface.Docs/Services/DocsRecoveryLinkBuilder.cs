using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Builds the small static recovery-link set shared by browser docs recovery surfaces.
/// </summary>
/// <remarks>
/// This builder only uses <see cref="DocsUrlBuilder" /> and built-in public-section route contracts. It must not
/// inspect harvested docs, search snapshots, or the missing request path. That constraint keeps standalone 404 pages
/// deterministic during cold starts and prevents stale or hostile request paths from shaping recovery navigation.
/// Callers that need search-result or representative-document fallback behavior should keep that logic in the search
/// surface and adapt these links only as static route-safe entries.
/// </remarks>
internal sealed class DocsRecoveryLinkBuilder
{
    private readonly DocsUrlBuilder _docsUrlBuilder;

    /// <summary>
    /// Initializes a new instance of <see cref="DocsRecoveryLinkBuilder" />.
    /// </summary>
    /// <param name="docsUrlBuilder">URL builder for the current live docs root.</param>
    public DocsRecoveryLinkBuilder(DocsUrlBuilder docsUrlBuilder)
    {
        _docsUrlBuilder = docsUrlBuilder ?? throw new ArgumentNullException(nameof(docsUrlBuilder));
    }

    /// <summary>
    /// Builds the route-safe docs recovery links for the current live docs surface.
    /// </summary>
    /// <returns>
    /// An ordered link set containing the primary search action, route-contract-backed Start Here and Packages section
    /// links, and the docs home link. The section and home links are marked export-ignorable because they are optional
    /// recovery affordances on sparse static exports; search remains crawlable so exported <c>404.html</c> pages keep a
    /// useful primary action when the search page is exported.
    /// </returns>
    public IReadOnlyList<DocsRecoveryLink> BuildRecoveryLinks()
    {
        return
        [
            new(
                "Search documentation",
                _docsUrlBuilder.BuildSearchUrl(),
                "Search guides, package docs, API reference, and release notes.",
                DocsRecoveryLinkKind.Primary),
            new(
                "Start Here",
                _docsUrlBuilder.BuildSectionUrl(DocPublicSection.StartHere),
                "Orient quickly and follow the strongest first-read path.",
                IgnoreDuringStaticExport: true),
            new(
                "Packages",
                _docsUrlBuilder.BuildSectionUrl(DocPublicSection.Packages),
                "Review package entry points and installation-facing docs.",
                IgnoreDuringStaticExport: true),
            new(
                "Docs home",
                _docsUrlBuilder.BuildHomeUrl(),
                "Return to the docs home and continue from the main index.",
                IgnoreDuringStaticExport: true)
        ];
    }
}
