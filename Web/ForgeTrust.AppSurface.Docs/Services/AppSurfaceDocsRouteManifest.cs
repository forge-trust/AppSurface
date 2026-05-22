using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Publishes the public route identity graph for one AppSurface Docs snapshot.
/// </summary>
/// <remarks>
/// The manifest is an internal read model, not a second router. It contains only public canonical winners plus redirect
/// aliases that should recover to those winners. Collision losers, reserved routes, and invalid routes stay represented by
/// diagnostics so export and future consumers do not guess a destination for ambiguous paths.
/// </remarks>
internal sealed record AppSurfaceDocsRouteManifest(
    IReadOnlyList<AppSurfaceDocsRouteManifestEntry> Entries,
    IReadOnlyList<DocHarvestDiagnostic> Diagnostics);

/// <summary>
/// Describes one public AppSurface Docs page and the aliases that should redirect to it.
/// </summary>
/// <param name="SourcePath">Harvested repository-relative source path for the canonical winner.</param>
/// <param name="CanonicalRoutePath">Docs-root-relative canonical route path.</param>
/// <param name="CanonicalLiveUrl">App-relative live URL for the canonical route.</param>
/// <param name="RecoveryAliases">Implicit source-shaped Markdown aliases for paste recovery.</param>
/// <param name="DeclaredAliases">Author-declared redirect aliases from metadata.</param>
/// <param name="SourcePathIsMarkdown">Whether <paramref name="SourcePath"/> is a Markdown document.</param>
internal sealed record AppSurfaceDocsRouteManifestEntry(
    string SourcePath,
    string CanonicalRoutePath,
    string CanonicalLiveUrl,
    IReadOnlyList<AppSurfaceDocsRouteAlias> RecoveryAliases,
    IReadOnlyList<AppSurfaceDocsRouteAlias> DeclaredAliases,
    bool SourcePathIsMarkdown);

/// <summary>
/// Describes one route alias that redirects to a canonical AppSurface Docs page.
/// </summary>
/// <param name="RoutePath">Docs-root-relative alias route path.</param>
/// <param name="LiveUrl">App-relative live URL for the alias.</param>
/// <param name="Kind">The alias source category.</param>
internal sealed record AppSurfaceDocsRouteAlias(
    string RoutePath,
    string LiveUrl,
    AppSurfaceDocsRouteAliasKind Kind);

/// <summary>
/// Identifies why an AppSurface Docs route alias exists.
/// </summary>
internal enum AppSurfaceDocsRouteAliasKind
{
    /// <summary>A harvested Markdown source path such as <c>guides/start.md</c>.</summary>
    MarkdownSource = 0,

    /// <summary>A legacy HTML-shaped Markdown source path such as <c>guides/start.md.html</c>.</summary>
    MarkdownSourceHtml = 1,

    /// <summary>An author-declared redirect alias from documentation metadata.</summary>
    DeclaredRedirect = 2
}
