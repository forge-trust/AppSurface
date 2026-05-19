namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Harvest policy settings for RazorDocs source-backed documentation.
/// </summary>
/// <remarks>
/// The default policy is tolerant so public runtime hosts can continue serving even when source harvesting has a
/// transient problem. Enable <see cref="FailOnFailure"/> in CI or export hosts that should fail closed when every
/// configured harvester fails, times out, or cancels. Use <see cref="Paths"/>, <see cref="Markdown"/>, and
/// <see cref="CSharp"/> to define the repository-relative public documentation boundary shared by runtime hosts,
/// export flows, and hygiene checks.
/// </remarks>
public sealed class RazorDocsHarvestOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether host startup should fail when the aggregate harvest health is
    /// <see cref="ForgeTrust.AppSurface.Docs.Models.DocHarvestHealthStatus.Failed"/>.
    /// </summary>
    /// <remarks>
    /// Strict mode treats only the aggregate failed state as fatal. Empty docs and degraded partial harvests remain
    /// non-fatal in this slice because they can represent intentional empty repositories or still-usable partial docs.
    /// </remarks>
    public bool FailOnFailure { get; set; }

    /// <summary>
    /// Gets health-surface settings for the operator-facing RazorDocs harvest health routes and sidebar chrome.
    /// </summary>
    public RazorDocsHarvestHealthOptions Health { get; set; } = new();

    /// <summary>
    /// Gets global repository-relative path policy settings shared by every built-in harvester.
    /// </summary>
    /// <remarks>
    /// Global include globs are a repository-wide boundary. When configured, every built-in source kind must match
    /// one global include before harvester-specific includes are considered. Global excludes win over includes and
    /// default-exclusion allows.
    /// </remarks>
    public RazorDocsHarvestPathOptions Paths { get; set; } = new();

    /// <summary>
    /// Gets Markdown-specific path policy settings that refine the global path policy.
    /// </summary>
    public RazorDocsMarkdownHarvestOptions Markdown { get; set; } = new();

    /// <summary>
    /// Gets C# API-reference path policy settings that refine the global path policy.
    /// </summary>
    public RazorDocsCSharpHarvestOptions CSharp { get; set; } = new();
}
