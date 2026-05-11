namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Declares the published RazorDocs versions that should be exposed through the version archive and static-tree mounting.
/// </summary>
/// <remarks>
/// The catalog is the release-level source of truth for version routing and archive presentation. It does not describe
/// individual documentation pages; instead it points at already-exported exact-version trees and records release state
/// such as support posture, visibility, and advisory severity. The runtime validates each published tree
/// independently so one broken version does not take down the whole docs host.
/// </remarks>
public sealed class RazorDocsVersionCatalog
{
    /// <summary>
    /// Gets or sets the exact version that should also be exposed through the configured route-family root alias.
    /// </summary>
    public string? RecommendedVersion { get; set; }

    /// <summary>
    /// Gets or sets the published versions known to the catalog.
    /// </summary>
    public List<RazorDocsPublishedVersion> Versions { get; set; } = [];
}

/// <summary>
/// Describes one published RazorDocs release tree.
/// </summary>
public sealed class RazorDocsPublishedVersion
{
    /// <summary>
    /// Gets or sets the exact published version identifier, such as <c>0.4.0</c> or <c>1.2.3-rc.1</c>.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short reader-facing label for the release.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets optional summary copy shown in the version archive.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Gets or sets the path to the exported exact-version docs subtree.
    /// </summary>
    /// <remarks>
    /// The directory should contain the static files exported from the stable docs surface for one exact release, such as
    /// <c>index.html</c>, <c>search-index.json</c>, section routes, and detail pages. RazorDocs can then mount that same
    /// artifact at either <c>RouteRootPath</c> or <c>{RouteRootPath}/v/{version}</c> by rebasing stable-root links at
    /// response time. Relative paths resolve from the directory containing the version catalog file.
    /// </remarks>
    public string? ExactTreePath { get; set; }

    /// <summary>
    /// Gets or sets the support-state badge surfaced in the version archive.
    /// </summary>
    public RazorDocsVersionSupportState SupportState { get; set; } = RazorDocsVersionSupportState.Current;

    /// <summary>
    /// Gets or sets the archive visibility behavior for the release.
    /// </summary>
    public RazorDocsVersionVisibility Visibility { get; set; } = RazorDocsVersionVisibility.Public;

    /// <summary>
    /// Gets or sets the release-level advisory state surfaced beside the version.
    /// </summary>
    public RazorDocsVersionAdvisoryState AdvisoryState { get; set; } = RazorDocsVersionAdvisoryState.None;
}

/// <summary>
/// Describes the support posture for one published docs release.
/// </summary>
/// <remarks>
/// Numeric values are explicit and stable because catalog payloads and downstream consumers may serialize or persist
/// these states outside the current process.
/// </remarks>
public enum RazorDocsVersionSupportState
{
    /// <summary>
    /// The release is the actively recommended line.
    /// </summary>
    Current = 0,

    /// <summary>
    /// The release remains supported but is no longer the recommended line.
    /// </summary>
    Maintained = 1,

    /// <summary>
    /// The release is visible for migration or auditing, but new work should move away from it.
    /// </summary>
    Deprecated = 2,

    /// <summary>
    /// The release is kept only as historical record.
    /// </summary>
    Archived = 3
}

/// <summary>
/// Controls whether a published version appears in the public archive and is mounted for static serving.
/// </summary>
/// <remarks>
/// Numeric values are explicit and stable because catalog payloads and downstream consumers may serialize or persist
/// these states outside the current process.
/// </remarks>
public enum RazorDocsVersionVisibility
{
    /// <summary>
    /// The version is visible in the archive and eligible for mounting.
    /// </summary>
    Public = 0,

    /// <summary>
    /// The version stays hidden from the public archive.
    /// </summary>
    Hidden = 1
}

/// <summary>
/// Describes release-level advisory severity shown in the archive.
/// </summary>
/// <remarks>
/// Numeric values are explicit and stable because catalog payloads and downstream consumers may serialize or persist
/// these states outside the current process.
/// </remarks>
public enum RazorDocsVersionAdvisoryState
{
    /// <summary>
    /// No special advisory is attached to the release.
    /// </summary>
    None = 0,

    /// <summary>
    /// The release is known to contain a vulnerability that readers should see before adopting it.
    /// </summary>
    Vulnerable = 1,

    /// <summary>
    /// The release has a more severe security warning that should be emphasized in archive UI.
    /// </summary>
    SecurityRisk = 2
}
