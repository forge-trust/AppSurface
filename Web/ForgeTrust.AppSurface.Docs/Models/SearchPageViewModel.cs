namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Describes the server-rendered shell for the dedicated docs search workspace.
/// </summary>
/// <remarks>
/// This model is rendered before the client-side search index is available so the page can show stable loading,
/// starter, browse-recovery, and failure states even when the search payload is slow or unavailable. All members are required and
/// should be supplied as non-<see langword="null" /> values. The list properties render in the order provided.
/// Use <see cref="SearchPageViewModel" /> for the server-rendered shell contract and recovery guidance, not for live
/// search results or client-side facet state. Callers should prefer empty lists over <see langword="null" /> for
/// <paramref name="SuggestedQueries" /> and <paramref name="FailureFallbackLinks" />.
/// Suggested queries render as real search-state anchors before JavaScript and may be enhanced by the client runtime.
/// Fallback links render as the shared server-derived browse navigation used both before the index loads and after an
/// index-load failure, not as server-side search results. Both lists are displayed in the supplied order, so place the
/// highest-signal actions first. Avoid relying on client-side mutation of this model after render, and avoid using
/// external absolute URLs in <see cref="SearchPageFallbackLink.Href" /> because the shell assumes app-relative
/// navigation semantics.
/// </remarks>
/// <param name="Title">The primary page heading shown above the workspace controls.</param>
/// <param name="Orientation">A short orientation sentence that explains what users can discover from the workspace.</param>
/// <param name="StarterHint">Helper copy shown in the starter state before a query or filter is applied.</param>
/// <param name="SearchPlaceholder">Placeholder text for the advanced search input.</param>
/// <param name="SuggestedQueries">Starter-state suggestions rendered as query-string anchors and enhanced by JavaScript when the search runtime is available. Empty lists are allowed, but the shell is designed around a curated set of useful first queries.</param>
/// <param name="FailureFallbackLinks">Ordered server-derived browse links shown before the search runtime loads and preserved when the runtime or index cannot be loaded. Include at least one non-search path that still helps users continue through the docs set.</param>
public sealed record SearchPageViewModel(
    string Title,
    string Orientation,
    string StarterHint,
    string SearchPlaceholder,
    IReadOnlyList<string> SuggestedQueries,
    IReadOnlyList<SearchPageFallbackLink> FailureFallbackLinks);

/// <summary>
/// Represents one server-rendered browse recovery action for the dedicated docs search workspace.
/// </summary>
/// <remarks>
/// These links are rendered before the client search payload is available and remain useful after index-fetch failure,
/// so each entry should be complete enough to stand on its own. They are navigation aids, not server-side search
/// results. <see cref="UsesDocsFrame" /> determines whether the destination should continue navigating inside the docs
/// content frame or escalate to a top-level page navigation.
/// Keep <see cref="Href" /> app-relative and already URL-safe, and assume the UI will HTML-escape the visible text
/// in <see cref="Title" /> and <see cref="Description" />. Prefer concise copy that fits comfortably in a compact
/// recovery card, and do not depend on client search indexing to make the destination discoverable.
/// </remarks>
/// <param name="Title">Short, action-oriented label used as the visible link title.</param>
/// <param name="Href">The app-relative destination URL to open when the user follows the recovery action.</param>
/// <param name="Description">Supporting context that explains what the destination contains and why it helps recover from the failed search flow.</param>
/// <param name="UsesDocsFrame">
/// A value indicating whether the destination should stay inside the docs content frame. Set this to
/// <see langword="true" /> only when the destination is known to be another docs page rather than a top-level
/// recovery surface such as the docs home or version archive.
/// </param>
public sealed record SearchPageFallbackLink(
    string Title,
    string Href,
    string Description,
    bool UsesDocsFrame = false);
