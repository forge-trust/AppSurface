namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Declares the published AppSurface Docs versions that should be exposed through the version archive and static-tree mounting.
/// </summary>
/// <remarks>
/// The catalog is the release-level source of truth for version routing and archive presentation. It does not describe
/// individual documentation pages; instead it points at already-exported exact-version trees and records release state
/// such as support posture, visibility, and advisory severity. The runtime validates each published tree
/// independently so one broken version does not take down the whole docs host.
/// </remarks>
public sealed class AppSurfaceDocsVersionCatalog
{
    /// <summary>
    /// Gets or sets the exact version that should also be exposed through the configured route-family root alias.
    /// </summary>
    public string? RecommendedVersion { get; set; }

    /// <summary>
    /// Gets or sets the published versions known to the catalog.
    /// </summary>
    public List<AppSurfaceDocsPublishedVersion> Versions { get; set; } = [];

    /// <summary>
    /// Gets or sets named public routes that resolve to exact entries from <see cref="Versions"/>.
    /// </summary>
    /// <remarks>
    /// Alias names are normalized route labels under <c>{RouteRootPath}/a/{name}</c>. They do not select a release
    /// automatically: each entry names one exact version, and only a verified public version can be mounted. See the
    /// <see href="https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#version-aliases">version alias guide</see>
    /// for the configuration, visibility, and failure contract.
    /// </remarks>
    public List<AppSurfaceDocsVersionAlias> Aliases { get; set; } = [];
}

/// <summary>
/// Describes a named documentation route that points to one exact published version.
/// </summary>
/// <remarks>
/// Names are trimmed and lowercased invariantly, then validated against the safe alias grammar before route creation.
/// A label has no built-in meaning; maintainers control its exact target and may change that target on host restart.
/// </remarks>
public sealed class AppSurfaceDocsVersionAlias
{
    /// <summary>Gets or sets the URL-safe alias name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact version identifier from the catalog's <c>versions</c> collection.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the reader-facing label; a blank value defaults to the normalized name.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets optional reader-facing summary text.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets whether the alias is public or hidden; omitted values default to public.</summary>
    public AppSurfaceDocsVersionVisibility Visibility { get; set; } = AppSurfaceDocsVersionVisibility.Public;
}

/// <summary>
/// Describes one published AppSurface Docs release tree.
/// </summary>
public sealed class AppSurfaceDocsPublishedVersion
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
    /// Gets or sets the trusted-release-root-relative path to the exported exact-version docs subtree.
    /// </summary>
    /// <remarks>
    /// The directory should contain the static files exported from the stable docs surface for one exact release, such as
    /// <c>index.html</c>, <c>search-index.json</c>, section routes, and detail pages. AppSurface Docs can then mount that same
    /// artifact at either <c>RouteRootPath</c> or <c>{RouteRootPath}/v/{version}</c> by rebasing stable-root links at
    /// response time. Relative paths resolve from
    /// <see cref="AppSurfaceDocsVersioningOptions.TrustedReleaseRootPath"/>. When that option is blank, the trusted
    /// release root defaults to the directory containing the version catalog file. Values such as
    /// <c>./releases/1.2.3</c> are valid after normalization, but rooted paths and paths containing <c>..</c> are
    /// unavailable and are never mounted.
    /// </remarks>
    public string? ExactTreePath { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 digest of the release archive manifest that this catalog entry expects.
    /// </summary>
    /// <remarks>
    /// The value pins the bytes of <c>.appsurface-docs-release-manifest.json</c> in trusted host configuration. AppSurface
    /// Docs verifies the manifest, required handler-servable file coverage, and every covered file before mounting the
    /// exact tree. Public catalog entries without this pin are unavailable because mounted archive HTML, scripts,
    /// stylesheets, SVG, and search payloads must be proven against a catalog trust anchor.
    /// </remarks>
    public string? ReleaseManifestSha256 { get; set; }

    /// <summary>
    /// Gets or sets the support-state badge surfaced in the version archive.
    /// </summary>
    public AppSurfaceDocsVersionSupportState SupportState { get; set; } = AppSurfaceDocsVersionSupportState.Current;

    /// <summary>
    /// Gets or sets the archive visibility behavior for the release.
    /// </summary>
    public AppSurfaceDocsVersionVisibility Visibility { get; set; } = AppSurfaceDocsVersionVisibility.Public;

    /// <summary>
    /// Gets or sets the release-level advisory state surfaced beside the version.
    /// </summary>
    public AppSurfaceDocsVersionAdvisoryState AdvisoryState { get; set; } = AppSurfaceDocsVersionAdvisoryState.None;
}

/// <summary>
/// Describes the support posture for one published docs release.
/// </summary>
/// <remarks>
/// Numeric values are explicit and stable because catalog payloads and downstream consumers may serialize or persist
/// these states outside the current process.
/// </remarks>
public enum AppSurfaceDocsVersionSupportState
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
public enum AppSurfaceDocsVersionVisibility
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
public enum AppSurfaceDocsVersionAdvisoryState
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
