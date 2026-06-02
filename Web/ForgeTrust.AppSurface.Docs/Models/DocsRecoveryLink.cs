using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Describes one route-contract-backed recovery link for browser-facing docs recovery surfaces.
/// </summary>
/// <remarks>
/// These links are intentionally harvest-free: callers can render them before a docs snapshot exists, during search
/// index failure, or from a standalone browser status page. The href is produced from <see cref="DocsUrlBuilder" />
/// rather than from the missing request path, so stale or hostile paths never influence the recovery destination.
/// Search-specific callers should keep using <see cref="SearchPageFallbackLink" /> for search workspace behavior and
/// adapt these neutral links only when they need static entry points.
/// </remarks>
/// <param name="Title">Short visible label for the recovery action.</param>
/// <param name="Href">App-relative, route-contract-backed destination.</param>
/// <param name="Description">Short explanation of why the destination helps the reader recover.</param>
/// <param name="Kind">Presentation hint used to distinguish the primary action from secondary browse links.</param>
/// <param name="IgnoreDuringStaticExport">
/// A value indicating whether static exporters may ignore this href when validating or crawling the containing page.
/// Use this for optional recovery links that are helpful at runtime but should not force sparse exports to include
/// additional pages.
/// </param>
internal sealed record DocsRecoveryLink(
    string Title,
    string Href,
    string Description,
    DocsRecoveryLinkKind Kind = DocsRecoveryLinkKind.Secondary,
    bool IgnoreDuringStaticExport = false);

/// <summary>
/// Identifies how a neutral docs recovery link should be visually emphasized.
/// </summary>
/// <remarks>
/// The enum is deliberately small because it is not a navigation taxonomy. Recovery surfaces should use
/// <see cref="Primary" /> for the one safest next step and <see cref="Secondary" /> for the small set of route-safe
/// browse alternatives.
/// </remarks>
internal enum DocsRecoveryLinkKind
{
    /// <summary>
    /// The main recovery action, normally the current docs search page.
    /// </summary>
    Primary,

    /// <summary>
    /// A supporting browse or home action.
    /// </summary>
    Secondary
}
