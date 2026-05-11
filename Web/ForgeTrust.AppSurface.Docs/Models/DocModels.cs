namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Structured metadata that can drive navigation, breadcrumbs, related links, and search without re-parsing source content.
/// </summary>
public sealed record DocMetadata
{
    /// <summary>
    /// Gets the resolved display title for the documentation node.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets a short summary describing the documentation node.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Summary"/> was derived from page content instead of authored explicitly.
    /// </summary>
    public bool? SummaryIsDerived { get; init; }

    /// <summary>
    /// Gets the page type, such as guide, example, api-reference, or troubleshooting.
    /// </summary>
    public string? PageType { get; init; }

    /// <summary>
    /// Gets the intended audience for the page.
    /// </summary>
    public string? Audience { get; init; }

    /// <summary>
    /// Gets the product component associated with the page.
    /// </summary>
    public string? Component { get; init; }

    /// <summary>
    /// Gets alternate terms that should resolve to this page in search.
    /// </summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>
    /// Gets alternate route aliases that should redirect to the canonical page when redirect support is enabled.
    /// </summary>
    public IReadOnlyList<string>? RedirectAliases { get; init; }

    /// <summary>
    /// Gets search keywords associated with the page.
    /// </summary>
    public IReadOnlyList<string>? Keywords { get; init; }

    /// <summary>
    /// Gets the content lifecycle status for the page.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets the navigation group used by public docs navigation.
    /// </summary>
    public string? NavGroup { get; init; }

    /// <summary>
    /// Gets the relative ordering value within a navigation group.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// Gets the explicit sequence identifier used to connect pages into one proof path.
    /// </summary>
    /// <remarks>
    /// RazorDocs does not infer sequence membership from folders or filenames in this slice. Pages participate in
    /// next/previous wayfinding only when authors opt them into the same <see cref="SequenceKey"/> and assign
    /// comparable <see cref="Order"/> values.
    /// </remarks>
    public string? SequenceKey { get; init; }

    /// <summary>
    /// Gets a value indicating whether the page is the authored landing doc for its public section.
    /// </summary>
    public bool? SectionLanding { get; init; }

    /// <summary>
    /// Gets a value indicating whether the page should be hidden from public navigation.
    /// </summary>
    public bool? HideFromPublicNav { get; init; }

    /// <summary>
    /// Gets a value indicating whether the page should be hidden from search.
    /// </summary>
    public bool? HideFromSearch { get; init; }

    /// <summary>
    /// Gets related page identifiers or titles.
    /// </summary>
    public IReadOnlyList<string>? RelatedPages { get; init; }

    /// <summary>
    /// Gets the preferred canonical slug for the page.
    /// </summary>
    public string? CanonicalSlug { get; init; }

    /// <summary>
    /// Gets optional human-readable breadcrumb labels for the page.
    /// </summary>
    public IReadOnlyList<string>? Breadcrumbs { get; init; }

    /// <summary>
    /// Gets optional landing-page curation groups authored with the documentation page.
    /// </summary>
    /// <remarks>
    /// RazorDocs parses this metadata on any page so the contract stays page-agnostic. Authors can supply groups either
    /// inline in Markdown front matter or through a paired sidecar such as <c>README.md.yml</c>. The built-in docs
    /// landing consumes the repository-root <c>README.md</c> groups, and section landing docs consume their own groups
    /// for reader-intent next steps.
    /// </remarks>
    public IReadOnlyList<DocFeaturedPageGroupDefinition>? FeaturedPageGroups { get; init; }

    /// <summary>
    /// Gets optional trust and provenance metadata rendered near the top of the page.
    /// </summary>
    /// <remarks>
    /// This nested object is designed for release notes, upgrade policies, changelogs, and similar pages that need to
    /// communicate current status, adoption safety, and archival provenance without custom view logic.
    /// </remarks>
    public DocTrustMetadata? Trust { get; init; }

    /// <summary>
    /// Gets optional contributor provenance metadata for page-level source, edit, and freshness control.
    /// </summary>
    public DocContributorMetadata? Contributor { get; init; }

    internal bool? PageTypeIsDerived { get; init; }

    internal bool? AudienceIsDerived { get; init; }

    internal bool? ComponentIsDerived { get; init; }

    internal bool? NavGroupIsDerived { get; init; }

    /// <summary>
    /// Gets a value indicating whether authored breadcrumb labels align with the path-derived breadcrumb targets that
    /// RazorDocs can safely reuse for rendering.
    /// </summary>
    internal bool? BreadcrumbsMatchPathTargets { get; init; }

    internal static DocMetadata? Merge(DocMetadata? primary, DocMetadata? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        if (fallback is null)
        {
            return primary;
        }

        var (summary, summaryIsDerived) = MergeTextWithFlag(
            primary.Summary,
            primary.SummaryIsDerived,
            fallback.Summary,
            fallback.SummaryIsDerived);
        var (pageType, pageTypeIsDerived) = MergeTextWithFlag(
            primary.PageType,
            primary.PageTypeIsDerived,
            fallback.PageType,
            fallback.PageTypeIsDerived);
        var (audience, audienceIsDerived) = MergeTextWithFlag(
            primary.Audience,
            primary.AudienceIsDerived,
            fallback.Audience,
            fallback.AudienceIsDerived);
        var (component, componentIsDerived) = MergeTextWithFlag(
            primary.Component,
            primary.ComponentIsDerived,
            fallback.Component,
            fallback.ComponentIsDerived);
        var (navGroup, navGroupIsDerived) = MergeTextWithFlag(
            primary.NavGroup,
            primary.NavGroupIsDerived,
            fallback.NavGroup,
            fallback.NavGroupIsDerived);
        var (breadcrumbs, breadcrumbsMatchPathTargets) = MergeListWithFlag(
            primary.Breadcrumbs,
            primary.BreadcrumbsMatchPathTargets,
            fallback.Breadcrumbs,
            fallback.BreadcrumbsMatchPathTargets);

        return new DocMetadata
        {
            Title = DocTrustMergeHelpers.PreferNonBlank(primary.Title, fallback.Title),
            Summary = summary,
            SummaryIsDerived = summaryIsDerived,
            PageType = pageType,
            PageTypeIsDerived = pageTypeIsDerived,
            Audience = audience,
            AudienceIsDerived = audienceIsDerived,
            Component = component,
            ComponentIsDerived = componentIsDerived,
            Aliases = MergeLists(primary.Aliases, fallback.Aliases),
            RedirectAliases = MergeLists(primary.RedirectAliases, fallback.RedirectAliases),
            Keywords = MergeLists(primary.Keywords, fallback.Keywords),
            Status = DocTrustMergeHelpers.PreferNonBlank(primary.Status, fallback.Status),
            NavGroup = navGroup,
            NavGroupIsDerived = navGroupIsDerived,
            Order = primary.Order ?? fallback.Order,
            SequenceKey = DocTrustMergeHelpers.PreferNonBlank(primary.SequenceKey, fallback.SequenceKey),
            SectionLanding = primary.SectionLanding ?? fallback.SectionLanding,
            HideFromPublicNav = primary.HideFromPublicNav ?? fallback.HideFromPublicNav,
            HideFromSearch = primary.HideFromSearch ?? fallback.HideFromSearch,
            RelatedPages = MergeLists(primary.RelatedPages, fallback.RelatedPages),
            CanonicalSlug = DocTrustMergeHelpers.PreferNonBlank(primary.CanonicalSlug, fallback.CanonicalSlug),
            Breadcrumbs = breadcrumbs,
            BreadcrumbsMatchPathTargets = breadcrumbsMatchPathTargets,
            FeaturedPageGroups = MergeLists(primary.FeaturedPageGroups, fallback.FeaturedPageGroups),
            Trust = DocTrustMetadata.Merge(primary.Trust, fallback.Trust),
            Contributor = DocContributorMetadata.Merge(primary.Contributor, fallback.Contributor)
        };
    }

    internal static IReadOnlyList<T>? MergeLists<T>(
        IReadOnlyList<T>? primary,
        IReadOnlyList<T>? fallback)
    {
        if (primary is not null)
        {
            return primary;
        }

        return fallback;
    }

    private static (string? Value, bool? Flag) MergeTextWithFlag(
        string? primaryValue,
        bool? primaryFlag,
        string? fallbackValue,
        bool? fallbackFlag)
    {
        if (!string.IsNullOrWhiteSpace(primaryValue))
        {
            return (primaryValue.Trim(), primaryFlag);
        }

        return !string.IsNullOrWhiteSpace(fallbackValue)
            ? (fallbackValue.Trim(), fallbackFlag)
            : (null, null);
    }

    private static (IReadOnlyList<string>? Value, bool? Flag) MergeListWithFlag(
        IReadOnlyList<string>? primaryValue,
        bool? primaryFlag,
        IReadOnlyList<string>? fallbackValue,
        bool? fallbackFlag)
    {
        if (primaryValue is not null)
        {
            return (primaryValue, primaryFlag);
        }

        return fallbackValue is not null
            ? (fallbackValue, fallbackFlag)
            : (null, null);
    }
}

/// <summary>
/// Represents one navigable heading captured while harvesting a documentation page.
/// </summary>
public sealed record DocOutlineItem
{
    /// <summary>
    /// Gets the heading text shown in the page-local outline and search metadata.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the HTML fragment identifier that anchors this outline item within the page.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the normalized heading level for this entry.
    /// </summary>
    public int Level { get; init; }
}

/// <summary>
/// Structured trust and provenance metadata for a documentation page.
/// </summary>
public sealed record DocTrustMetadata
{
    /// <summary>
    /// Gets the compact top-level state shown in the trust bar, such as <c>Unreleased</c> or <c>Pre-1.0 policy</c>.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets the short trust statement that explains what the current status means for readers.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the freshness statement that explains how current or provisional the page is.
    /// </summary>
    public string? Freshness { get; init; }

    /// <summary>
    /// Gets the statement describing which product surfaces or artifacts this page covers.
    /// </summary>
    public string? ChangeScope { get; init; }

    /// <summary>
    /// Gets an optional link to migration or upgrade guidance.
    /// </summary>
    public DocTrustLink? Migration { get; init; }

    /// <summary>
    /// Gets the archival or long-term home statement for the page contents.
    /// </summary>
    public string? Archive { get; init; }

    /// <summary>
    /// Gets optional provenance notes or upstream sources that support the page.
    /// </summary>
    public IReadOnlyList<string>? Sources { get; init; }

    internal static DocTrustMetadata? Merge(DocTrustMetadata? primary, DocTrustMetadata? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        if (fallback is null)
        {
            return primary;
        }

        return new DocTrustMetadata
        {
            Status = DocTrustMergeHelpers.PreferNonBlank(primary.Status, fallback.Status),
            Summary = DocTrustMergeHelpers.PreferNonBlank(primary.Summary, fallback.Summary),
            Freshness = DocTrustMergeHelpers.PreferNonBlank(primary.Freshness, fallback.Freshness),
            ChangeScope = DocTrustMergeHelpers.PreferNonBlank(primary.ChangeScope, fallback.ChangeScope),
            Migration = DocTrustLink.Merge(primary.Migration, fallback.Migration),
            Archive = DocTrustMergeHelpers.PreferNonBlank(primary.Archive, fallback.Archive),
            Sources = DocMetadata.MergeLists(primary.Sources, fallback.Sources)
        };
    }
}

/// <summary>
/// Link metadata used by trust-bar actions such as migration guidance.
/// </summary>
public sealed record DocTrustLink
{
    /// <summary>
    /// Gets the reader-facing link label.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets the browser-facing destination URL.
    /// </summary>
    public string? Href { get; init; }

    internal static DocTrustLink? Merge(DocTrustLink? primary, DocTrustLink? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        if (fallback is null)
        {
            return primary;
        }

        return new DocTrustLink
        {
            Label = DocTrustMergeHelpers.PreferNonBlank(primary.Label, fallback.Label),
            Href = DocTrustMergeHelpers.PreferNonBlank(primary.Href, fallback.Href)
        };
    }
}

/// <summary>
/// Page-level contributor provenance metadata used to override or suppress source, edit, and freshness evidence.
/// </summary>
public sealed record DocContributorMetadata
{
    /// <summary>
    /// Gets a value indicating whether contributor provenance should be hidden for the page even when automatic evidence exists.
    /// </summary>
    public bool? HideContributorInfo { get; init; }

    /// <summary>
    /// Gets an optional repository-relative source-path override used for source links, edit links, and git freshness resolution.
    /// Rooted paths and traversal segments are rejected.
    /// </summary>
    public string? SourcePathOverride { get; init; }

    /// <summary>
    /// Gets an optional explicit source URL override.
    /// Only absolute HTTP(S) URLs and root-relative paths are accepted.
    /// </summary>
    public string? SourceUrlOverride { get; init; }

    /// <summary>
    /// Gets an optional explicit edit URL override.
    /// Only absolute HTTP(S) URLs and root-relative paths are accepted.
    /// </summary>
    public string? EditUrlOverride { get; init; }

    /// <summary>
    /// Gets an optional exact timestamp override for contributor freshness.
    /// </summary>
    public DateTimeOffset? LastUpdatedOverride { get; init; }

    /// <summary>
    /// Merges contributor metadata by preferring authored primary values and filling missing values from fallback metadata.
    /// </summary>
    /// <remarks>
    /// Precedence rules:
    /// <list type="bullet">
    /// <item><description><see cref="HideContributorInfo"/> uses nullable-boolean precedence, so explicit <see langword="false"/> is preserved.</description></item>
    /// <item><description><see cref="SourcePathOverride"/>, <see cref="SourceUrlOverride"/>, and <see cref="EditUrlOverride"/> prefer the first non-blank string; whitespace-only values are treated as missing.</description></item>
    /// <item><description><see cref="LastUpdatedOverride"/> uses null coalescing and therefore keeps the primary timestamp when present.</description></item>
    /// </list>
    /// Pitfalls:
    /// <list type="bullet">
    /// <item><description>Setting a string override to the empty string does not clear a fallback value; it falls back instead.</description></item>
    /// <item><description>Callers that need to suppress inherited contributor rendering should use <see cref="HideContributorInfo"/> instead of relying on blank string overrides.</description></item>
    /// </list>
    /// </remarks>
    internal static DocContributorMetadata? Merge(DocContributorMetadata? primary, DocContributorMetadata? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        if (fallback is null)
        {
            return primary;
        }

        return new DocContributorMetadata
        {
            HideContributorInfo = primary.HideContributorInfo ?? fallback.HideContributorInfo,
            SourcePathOverride = DocTrustMergeHelpers.PreferNonBlank(primary.SourcePathOverride, fallback.SourcePathOverride),
            SourceUrlOverride = DocTrustMergeHelpers.PreferNonBlank(primary.SourceUrlOverride, fallback.SourceUrlOverride),
            EditUrlOverride = DocTrustMergeHelpers.PreferNonBlank(primary.EditUrlOverride, fallback.EditUrlOverride),
            LastUpdatedOverride = primary.LastUpdatedOverride ?? fallback.LastUpdatedOverride
        };
    }
}

file static class DocTrustMergeHelpers
{
    internal static string? PreferNonBlank(string? preferred, string? fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return string.IsNullOrWhiteSpace(fallbackValue) ? null : fallbackValue.Trim();
    }
}

/// <summary>
/// Identifies the source declaration that produced one rendered C# API documentation symbol.
/// </summary>
public sealed record DocSymbolSourceProvenance
{
    /// <summary>
    /// Gets the rendered HTML anchor ID for the generated API symbol.
    /// </summary>
    public string AnchorId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the repository-relative source file path that contains the documented declaration.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the 1-based source declaration line.
    /// </summary>
    public int StartLine { get; init; }
}

/// <summary>
/// Represents a documentation node within the repository.
/// </summary>
/// <param name="Title">The display title of the document.</param>
/// <param name="Path">The relative path to the documentation source.</param>
/// <param name="Content">The rendered HTML content of the documentation.</param>
/// <param name="ParentPath">The optional parent path for hierarchical organization.</param>
/// <param name="IsDirectory">Indicates if this node represents a directory container.</param>
/// <param name="CanonicalPath">The browser-facing docs route path used for linking and lookup.</param>
/// <param name="Metadata">Structured metadata associated with the documentation node.</param>
/// <param name="Outline">Structured in-page outline entries captured during harvesting.</param>
/// <param name="SymbolSourceProvenance">Optional source declarations keyed by rendered C# API symbol anchor IDs.</param>
public record DocNode(
    string Title,
    string Path,
    string Content,
    string? ParentPath = null,
    bool IsDirectory = false,
    string? CanonicalPath = null,
    DocMetadata? Metadata = null,
    IReadOnlyList<DocOutlineItem>? Outline = null,
    IReadOnlyList<DocSymbolSourceProvenance>? SymbolSourceProvenance = null);

/// <summary>
/// Describes the overall health of the latest RazorDocs harvest snapshot.
/// </summary>
/// <remarks>
/// Numeric values are a stable public compatibility contract for persisted and serialized representations. Do not
/// remove, reorder, or renumber existing members.
/// </remarks>
public enum DocHarvestHealthStatus
{
    /// <summary>
    /// At least one configured harvester returned documentation and no harvester failed.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// Harvesting completed without failed harvesters, but no documentation nodes were produced.
    /// </summary>
    Empty = 1,

    /// <summary>
    /// At least one harvester completed successfully while at least one other harvester failed, timed out, or canceled.
    /// </summary>
    Degraded = 2,

    /// <summary>
    /// Every configured harvester failed, timed out, or canceled.
    /// </summary>
    Failed = 3
}

/// <summary>
/// Describes one configured harvester's contribution to a RazorDocs harvest snapshot.
/// </summary>
/// <remarks>
/// Numeric values are a stable public compatibility contract for persisted and serialized representations. Do not
/// remove, reorder, or renumber existing members.
/// </remarks>
public enum DocHarvesterHealthStatus
{
    /// <summary>
    /// The harvester completed and returned one or more documentation nodes.
    /// </summary>
    Succeeded = 0,

    /// <summary>
    /// The harvester completed without error and returned no documentation nodes.
    /// </summary>
    ReturnedEmpty = 1,

    /// <summary>
    /// The harvester threw an exception while scanning documentation.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The harvester exceeded RazorDocs' per-harvester timeout budget.
    /// </summary>
    TimedOut = 3,

    /// <summary>
    /// The harvester observed cancellation that was not caused by RazorDocs' timeout budget.
    /// </summary>
    Canceled = 4
}

/// <summary>
/// Describes the severity of a structured RazorDocs harvest diagnostic.
/// </summary>
/// <remarks>
/// Numeric values are a stable public compatibility contract for persisted and serialized representations. Do not
/// remove, reorder, or renumber existing members.
/// </remarks>
public enum DocHarvestDiagnosticSeverity
{
    /// <summary>
    /// Informational state that does not indicate a failed harvest.
    /// </summary>
    Information = 0,

    /// <summary>
    /// Non-fatal problem that may reduce the harvested docs corpus.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Harvester-level failure that prevented that harvester from contributing documentation.
    /// </summary>
    Error = 2,

    /// <summary>
    /// Aggregate failure that means RazorDocs could not harvest any configured source successfully.
    /// </summary>
    Critical = 3
}

/// <summary>
/// Captures the structured health of one RazorDocs harvest snapshot.
/// </summary>
/// <param name="Status">Overall health rollup for the snapshot.</param>
/// <param name="GeneratedUtc">UTC timestamp when the snapshot was generated.</param>
/// <param name="RepositoryRoot">
/// Repository root passed to configured harvesters. Treat this as server-only operational data because it can contain
/// sensitive or environment-specific filesystem paths; redact or omit it before sending snapshots to clients.
/// </param>
/// <param name="TotalHarvesters">Number of harvesters configured for the snapshot.</param>
/// <param name="SuccessfulHarvesters">Number of harvesters that completed with either docs or a valid empty result.</param>
/// <param name="FailedHarvesters">Number of harvesters that failed, timed out, or canceled.</param>
/// <param name="TotalDocs">Number of documentation nodes published by the final cached docs snapshot.</param>
/// <param name="Harvesters">Per-harvester health entries. Never <see langword="null" /> in RazorDocs-created snapshots.</param>
/// <param name="Diagnostics">Structured diagnostics for failed, degraded, or noteworthy states. Never <see langword="null" /> in RazorDocs-created snapshots.</param>
/// <remarks>
/// RazorDocs-created snapshots retain non-null <see cref="Harvesters"/> and <see cref="Diagnostics"/> collections for
/// safe server-side inspection, but callers that serialize this record into client-visible payloads must sanitize
/// <see cref="RepositoryRoot"/> first.
/// </remarks>
public sealed record DocHarvestHealthSnapshot(
    DocHarvestHealthStatus Status,
    DateTimeOffset GeneratedUtc,
    string RepositoryRoot,
    int TotalHarvesters,
    int SuccessfulHarvesters,
    int FailedHarvesters,
    int TotalDocs,
    IReadOnlyList<DocHarvesterHealth> Harvesters,
    IReadOnlyList<DocHarvestDiagnostic> Diagnostics);

/// <summary>
/// Captures one configured harvester's status inside a RazorDocs harvest snapshot.
/// </summary>
/// <param name="HarvesterType">Concrete harvester type name used in logs and diagnostics.</param>
/// <param name="Status">Harvester-level health status.</param>
/// <param name="DocCount">Number of documentation nodes returned by the harvester before RazorDocs post-processing.</param>
/// <param name="Diagnostic">Diagnostic explaining a failed, timed-out, or canceled harvester; usually <see langword="null" /> for non-failure outcomes.</param>
public sealed record DocHarvesterHealth(
    string HarvesterType,
    DocHarvesterHealthStatus Status,
    int DocCount,
    DocHarvestDiagnostic? Diagnostic);

/// <summary>
/// Describes one structured RazorDocs harvest health diagnostic.
/// </summary>
/// <param name="Code">Stable diagnostic code suitable for tests, logs, documentation, and host UI branching.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="HarvesterType">Concrete harvester type when the diagnostic belongs to one harvester, or <see langword="null" /> for aggregate diagnostics.</param>
/// <param name="Problem">Operator-facing summary of what went wrong.</param>
/// <param name="Cause">Explanation of why RazorDocs could not safely treat the harvest as fully healthy.</param>
/// <param name="Fix">Suggested operator or docs-author action that resolves the problem.</param>
public sealed record DocHarvestDiagnostic(
    string Code,
    DocHarvestDiagnosticSeverity Severity,
    string? HarvesterType,
    string Problem,
    string Cause,
    string Fix);

/// <summary>
/// Defines the stable diagnostic codes emitted by RazorDocs harvest health snapshots.
/// </summary>
/// <remarks>
/// Use these constants when testing or branching on <see cref="DocHarvestDiagnostic.Code"/> values. The string values are
/// public compatibility contracts and must not be changed once released.
/// </remarks>
public static class DocHarvestDiagnosticCodes
{
    /// <summary>
    /// A harvester exceeded RazorDocs' per-harvester timeout budget.
    /// </summary>
    public const string HarvesterTimedOut = "razordocs.harvest.harvester_timed_out";

    /// <summary>
    /// A harvester observed cancellation outside RazorDocs' timeout budget.
    /// </summary>
    public const string HarvesterCanceled = "razordocs.harvest.harvester_canceled";

    /// <summary>
    /// A harvester threw while scanning the documentation source.
    /// </summary>
    public const string HarvesterFailed = "razordocs.harvest.harvester_failed";

    /// <summary>
    /// No harvesters were registered for the RazorDocs host.
    /// </summary>
    public const string NoHarvesters = "razordocs.harvest.no_harvesters";

    /// <summary>
    /// Every configured harvester failed, timed out, or canceled for the snapshot.
    /// </summary>
    public const string AllFailed = "razordocs.harvest.all_failed";
}

/// <summary>
/// Enumerates the built-in public documentation sections used by RazorDocs.
/// </summary>
/// <remarks>
/// Numeric values are a stable public compatibility contract for persisted and serialized representations. Do not
/// remove, reorder, or renumber existing members. Presentation order is defined by
/// <c>DocPublicSectionCatalog.OrderedSections</c>, so renderers should not infer UI ordering from enum ordinals.
/// </remarks>
public enum DocPublicSection
{
    /// <summary>
    /// A first-read routing surface for evaluators who need to understand what the product is for before going deeper.
    /// </summary>
    StartHere = 0,

    /// <summary>
    /// Explanatory material that builds conceptual understanding before implementation details.
    /// </summary>
    Concepts = 1,

    /// <summary>
    /// Task-oriented guides that show a reader how to accomplish something concrete.
    /// </summary>
    HowToGuides = 2,

    /// <summary>
    /// Concrete examples and proof artifacts that demonstrate the system working in practice.
    /// </summary>
    Examples = 3,

    /// <summary>
    /// API and namespace reference material intended for readers who already know what they are looking for.
    /// </summary>
    ApiReference = 4,

    /// <summary>
    /// Recovery-oriented material for failures, debugging, and operational honesty.
    /// </summary>
    Troubleshooting = 5,

    /// <summary>
    /// Contributor-oriented or otherwise internal material that should only appear when explicitly made public.
    /// </summary>
    Internals = 6,

    /// <summary>
    /// Release notes, changelogs, upgrade policies, and other version-facing project history.
    /// </summary>
    Releases = 7
}

/// <summary>
/// Represents one normalized public-section snapshot derived from the harvested docs corpus.
/// </summary>
public sealed record DocSectionSnapshot
{
    /// <summary>
    /// Gets the typed public section identifier.
    /// </summary>
    public DocPublicSection Section { get; init; }

    /// <summary>
    /// Gets the canonical display label for the section.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stable route slug for the section.
    /// </summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional authored landing doc that represents the section.
    /// </summary>
    public DocNode? LandingDoc { get; init; }

    /// <summary>
    /// Gets the public pages that belong to the section, ordered for display.
    /// </summary>
    public IReadOnlyList<DocNode> VisiblePages { get; init; } = [];
}

/// <summary>
/// Defines one authored reader-intent group for a docs landing surface.
/// </summary>
public sealed record DocFeaturedPageGroupDefinition
{
    /// <summary>
    /// Gets the stable reader-intent identifier for the group.
    /// </summary>
    /// <remarks>
    /// Authors may omit this value when <see cref="Label"/> is present. RazorDocs derives a normalized intent from the
    /// label during metadata parsing so downstream resolvers can still identify the group consistently.
    /// </remarks>
    public string? Intent { get; init; }

    /// <summary>
    /// Gets the reader-facing group heading.
    /// </summary>
    /// <remarks>
    /// Authors may omit this value when <see cref="Intent"/> is present. RazorDocs converts the intent into a
    /// title-cased label during metadata parsing so the landing can still render a useful heading.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>
    /// Gets optional copy that explains when a reader should choose the group.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the relative display order for the group.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// Gets the featured destination pages in this group.
    /// </summary>
    public IReadOnlyList<DocFeaturedPageDefinition> Pages { get; init; } = [];

    /// <summary>
    /// Gets the parser-populated metadata field path for group-level diagnostics.
    /// </summary>
    /// <remarks>
    /// This internal value is for diagnostics and source attribution only. Authored metadata and consumers should not
    /// depend on it as stable content because the parser path format may change.
    /// </remarks>
    internal string? SourceFieldPath { get; init; }
}

/// <summary>
/// Defines one authored featured-page entry for a docs landing surface.
/// </summary>
public sealed record DocFeaturedPageDefinition
{
    /// <summary>
    /// Gets the reader-facing evaluator question or label for the card.
    /// </summary>
    /// <remarks>
    /// When this value is omitted on the built-in docs landing, RazorDocs falls back to the resolved destination
    /// page title so the card still renders with a sensible label.
    /// </remarks>
    public string? Question { get; init; }

    /// <summary>
    /// Gets the source or canonical path of the destination page to feature.
    /// </summary>
    /// <remarks>
    /// RazorDocs matches both source paths and canonical browser paths. Path separators are normalized during
    /// resolution so authored forward-slash and backslash forms point to the same destination.
    /// </remarks>
    public string? Path { get; init; }

    /// <summary>
    /// Gets optional landing-only supporting copy shown instead of destination summary text.
    /// </summary>
    public string? SupportingCopy { get; init; }

    /// <summary>
    /// Gets the relative display order for the featured entry.
    /// </summary>
    public int? Order { get; init; }

    /// <summary>
    /// Gets the parser-populated metadata field path for page-level diagnostics.
    /// </summary>
    /// <remarks>
    /// This internal value is for diagnostics and source attribution only. Authored metadata and consumers should not
    /// depend on it as stable content because the parser path format may change.
    /// </remarks>
    internal string? SourceFieldPath { get; init; }
}

/// <summary>
/// View model for the docs landing page.
/// </summary>
public sealed record DocLandingViewModel
{
    /// <summary>
    /// Gets the hero heading shown on the docs landing.
    /// </summary>
    public string Heading { get; init; } = "Documentation";

    /// <summary>
    /// Gets the supporting description shown under the hero heading.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the repository-root landing document when one was harvested.
    /// </summary>
    public DocNode? LandingDoc { get; init; }

    /// <summary>
    /// Gets the href for the section-level <c>Start Here</c> route when that section exists in the current public docs corpus.
    /// </summary>
    public string? StartHereHref { get; init; }

    /// <summary>
    /// Gets the visible documentation nodes used by the neutral fallback landing state.
    /// </summary>
    public IReadOnlyList<DocNode> VisibleDocs { get; init; } = [];

    /// <summary>
    /// Gets the resolved proof-path groups for the landing experience.
    /// </summary>
    public IReadOnlyList<DocLandingFeaturedPageGroupViewModel> FeaturedPageGroups { get; init; } = [];

    /// <summary>
    /// Gets the secondary section summaries shown under the primary <c>Start Here</c> route.
    /// </summary>
    public IReadOnlyList<DocHomeSectionViewModel> SecondarySections { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the landing should render a proof-path lead section.
    /// </summary>
    public bool HasFeaturedPages => FeaturedPageGroups.Any(group => group.Pages.Count > 0);
}

/// <summary>
/// View model for one resolved reader-intent group on a docs landing page.
/// </summary>
/// <remarks>
/// <see cref="Intent"/> and <see cref="Label"/> are normalized by the featured-page resolver before rendering: authored
/// whitespace is trimmed, missing labels fall back to the resolved intent, and both values are non-null. <see cref="Summary"/>
/// contains optional group copy and may be <c>null</c>. <see cref="Pages"/> contains the resolved
/// <see cref="DocLandingFeaturedPageViewModel"/> rows produced by the resolver after it matches authored destinations to
/// visible docs. Empty <see cref="Pages"/> lists are treated as no featured pages and are suppressed by
/// <see cref="DocLandingViewModel.HasFeaturedPages"/>, <see cref="DocDetailsViewModel.HasFeaturedPages"/>, and the
/// RazorDocs views.
/// Pitfalls: callers should not rely on an empty <see cref="Pages"/> list being rendered, and should expect
/// <see cref="Intent"/>, <see cref="Label"/>, <see cref="Summary"/>, and <see cref="Pages"/> to reflect resolver output
/// rather than raw authored front matter.
/// </remarks>
public sealed record DocLandingFeaturedPageGroupViewModel
{
    /// <summary>
    /// Gets the stable reader-intent identifier for the group.
    /// </summary>
    public string Intent { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reader-facing group label.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional copy that explains when to choose this group.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the resolved featured-page rows in this group.
    /// </summary>
    public IReadOnlyList<DocLandingFeaturedPageViewModel> Pages { get; init; } = [];
}

/// <summary>
/// View model for one resolved featured card on the docs landing page.
/// </summary>
public sealed record DocLandingFeaturedPageViewModel
{
    /// <summary>
    /// Gets the evaluator question or label shown on the card.
    /// </summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>
    /// Gets the destination page title shown on the card.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the browser-facing link to the destination page.
    /// </summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>
    /// Gets the destination page type, such as guide, example, or api-reference.
    /// </summary>
    public string? PageType { get; init; }

    /// <summary>
    /// Gets the normalized badge presentation for <see cref="PageType"/> when RazorDocs can render one.
    /// </summary>
    public DocPageTypeBadgePresentation? PageTypeBadge { get; init; }

    /// <summary>
    /// Gets the supporting body copy shown on the card.
    /// </summary>
    public string? SupportingText { get; init; }
}

/// <summary>
/// View model describing one secondary public-section summary on the docs home.
/// </summary>
public sealed record DocHomeSectionViewModel
{
    /// <summary>
    /// Gets the typed public section represented by the summary.
    /// </summary>
    public DocPublicSection Section { get; init; }

    /// <summary>
    /// Gets the section label shown to the reader.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stable route slug for the section.
    /// </summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>
    /// Gets the route that enters the section.
    /// </summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>
    /// Gets the one-sentence utility copy that explains what the reader can do in the section.
    /// </summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>
    /// Gets the key routes surfaced for the section on the docs home.
    /// </summary>
    public IReadOnlyList<DocSectionLinkViewModel> KeyRoutes { get; init; } = [];
}

/// <summary>
/// View model for a section-scoped or doc-scoped breadcrumb item.
/// </summary>
public sealed record DocBreadcrumbViewModel
{
    /// <summary>
    /// Gets the breadcrumb label shown to the reader.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the optional target href for the breadcrumb.
    /// </summary>
    public string? Href { get; init; }
}

/// <summary>
/// View model for one section list or sidebar link.
/// </summary>
public sealed record DocSectionLinkViewModel
{
    /// <summary>
    /// Gets the displayed link title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the destination href.
    /// </summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional utility copy shown with the link.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets optional short eyebrow text shown above the link title.
    /// </summary>
    public string? Eyebrow { get; init; }

    /// <summary>
    /// Gets the normalized page-type badge for the destination when one is available.
    /// </summary>
    public DocPageTypeBadgePresentation? PageTypeBadge { get; init; }

    /// <summary>
    /// Gets nested child links shown under the current link.
    /// </summary>
    public IReadOnlyList<DocSectionLinkViewModel> Children { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the link should use docs anchor navigation semantics.
    /// </summary>
    public bool UseAnchorNavigation { get; init; }

    /// <summary>
    /// Gets a value indicating whether this link represents the current page.
    /// </summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// View model for one grouped set of section links.
/// </summary>
public sealed record DocSectionGroupViewModel
{
    /// <summary>
    /// Gets the optional group heading shown above the link list.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the links that belong to the group.
    /// </summary>
    public IReadOnlyList<DocSectionLinkViewModel> Links { get; init; } = [];
}

/// <summary>
/// View model for one resolved documentation link shown in related or sequence wayfinding.
/// </summary>
public sealed record DocPageLinkViewModel
{
    /// <summary>
    /// Gets the destination page title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the browser-facing destination URL.
    /// </summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional supporting text for the destination.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the normalized page type badge metadata for the destination when available.
    /// </summary>
    public DocPageTypeBadgePresentation? PageTypeBadge { get; init; }
}

/// <summary>
/// View model for the sidebar navigation shell.
/// </summary>
public sealed record DocSidebarViewModel
{
    /// <summary>
    /// Gets the sections shown in the sidebar.
    /// </summary>
    public IReadOnlyList<DocSidebarSectionViewModel> Sections { get; init; } = [];
}

/// <summary>
/// View model for one public section in the sidebar.
/// </summary>
public sealed record DocSidebarSectionViewModel
{
    /// <summary>
    /// Gets the typed section represented by the sidebar entry.
    /// </summary>
    public DocPublicSection Section { get; init; }

    /// <summary>
    /// Gets the section label shown in the sidebar.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stable section slug.
    /// </summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>
    /// Gets the section route href.
    /// </summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the section owns the current page context.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Gets a value indicating whether the section should render expanded by default.
    /// </summary>
    public bool IsExpanded { get; init; }

    /// <summary>
    /// Gets the grouped links rendered when the section is expanded.
    /// </summary>
    public IReadOnlyList<DocSectionGroupViewModel> Groups { get; init; } = [];
}

/// <summary>
/// View model for the grouped-section fallback and unavailable section surfaces.
/// </summary>
public sealed record DocSectionPageViewModel
{
    /// <summary>
    /// Gets the typed section when the route resolved to a known built-in section.
    /// </summary>
    public DocPublicSection? Section { get; init; }

    /// <summary>
    /// Gets the section label or unavailable-page heading.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the primary explanatory copy for the page.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the docs home route.
    /// </summary>
    public string DocsHomeHref { get; init; } = "/docs";

    /// <summary>
    /// Gets the href for the section-level <c>Start Here</c> route when that section exists in the current public docs corpus.
    /// </summary>
    public string? StartHereHref { get; init; }

    /// <summary>
    /// Gets a value indicating whether the route resolved to an unavailable section surface.
    /// </summary>
    public bool IsUnavailable { get; init; }

    /// <summary>
    /// Gets the explanatory copy shown when the section is unavailable.
    /// </summary>
    public string? AvailabilityMessage { get; init; }

    /// <summary>
    /// Gets a value indicating whether the fallback section is intentionally sparse.
    /// </summary>
    public bool IsSparse { get; init; }

    /// <summary>
    /// Gets the key routes surfaced for a sparse section fallback.
    /// </summary>
    public IReadOnlyList<DocSectionLinkViewModel> KeyRoutes { get; init; } = [];

    /// <summary>
    /// Gets the grouped page lists shown for the section.
    /// </summary>
    public IReadOnlyList<DocSectionGroupViewModel> Groups { get; init; } = [];
}

/// <summary>
/// View model for a rendered documentation details page.
/// </summary>
public sealed record DocDetailsViewModel
{
    /// <summary>
    /// Gets the underlying documentation node.
    /// </summary>
    public DocNode Document { get; init; } = new(string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Gets the in-page outline entries for the current document.
    /// </summary>
    public IReadOnlyList<DocOutlineItem> Outline { get; init; } = [];

    /// <summary>
    /// Gets the previous page within the current authored sequence, when one exists.
    /// </summary>
    public DocPageLinkViewModel? PreviousPage { get; init; }

    /// <summary>
    /// Gets the next page within the current authored sequence, when one exists.
    /// </summary>
    public DocPageLinkViewModel? NextPage { get; init; }

    /// <summary>
    /// Gets the authored related pages that resolved successfully.
    /// </summary>
    public IReadOnlyList<DocPageLinkViewModel> RelatedPages { get; init; } = [];

    /// <summary>
    /// Gets the contributor provenance evidence resolved for the current page.
    /// </summary>
    public DocContributorProvenanceViewModel? ContributorProvenance { get; init; }

    /// <summary>
    /// Gets the resolved display title for the page.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the authored summary that should be rendered under the title when available.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Summary"/> should be rendered.
    /// </summary>
    public bool ShowSummary { get; init; }

    /// <summary>
    /// Gets a value indicating whether the page is a C# API reference document.
    /// </summary>
    public bool IsCSharpApiDoc { get; init; }

    /// <summary>
    /// Gets a value indicating whether the page should use the API reference reading surface.
    /// </summary>
    /// <remarks>
    /// This covers non-Markdown generated documents and Markdown documents explicitly marked with API-oriented metadata,
    /// while <see cref="IsCSharpApiDoc" /> remains scoped to C# generated page chrome decisions.
    /// </remarks>
    public bool IsApiSurfaceDoc { get; init; }

    /// <summary>
    /// Gets the normalized page-type badge presentation for the current page when available.
    /// </summary>
    public DocPageTypeBadgePresentation? PageTypeBadge { get; init; }

    /// <summary>
    /// Gets the explicit component metadata shown with the page when available.
    /// </summary>
    public string? Component { get; init; }

    /// <summary>
    /// Gets the explicit audience metadata shown with the page when available.
    /// </summary>
    public string? Audience { get; init; }

    /// <summary>
    /// Gets the breadcrumb trail used by the page.
    /// </summary>
    public IReadOnlyList<DocBreadcrumbViewModel> Breadcrumbs { get; init; } = [];

    /// <summary>
    /// Gets the current public section when the page belongs to a public docs section.
    /// </summary>
    public DocPublicSection? PublicSection { get; init; }

    /// <summary>
    /// Gets the current public-section label when one exists.
    /// </summary>
    public string? PublicSectionLabel { get; init; }

    /// <summary>
    /// Gets the current public-section route href when one exists.
    /// </summary>
    public string? PublicSectionHref { get; init; }

    /// <summary>
    /// Gets the current public-section utility sentence when one exists.
    /// </summary>
    public string? PublicSectionPurpose { get; init; }

    /// <summary>
    /// Gets a value indicating whether the trust-bar migration link should stay inside the docs content frame.
    /// </summary>
    /// <remarks>
    /// This is resolved from the harvested docs corpus rather than inferred from the raw href alone so root-mounted
    /// docs surfaces can still treat canonical plain <c>.html</c> docs routes as docs-local without misclassifying
    /// unrelated site pages.
    /// </remarks>
    public bool TrustMigrationUsesTurbo { get; init; }

    /// <summary>
    /// Gets a value indicating whether the contributor source link should stay inside the docs content frame.
    /// </summary>
    /// <remarks>
    /// This is resolved from the harvested docs corpus rather than inferred from the raw href alone so mounted or
    /// root-hosted docs surfaces can keep local provenance links inside the docs shell without trapping unrelated app
    /// routes.
    /// </remarks>
    public bool ContributorSourceUsesTurbo { get; init; }

    /// <summary>
    /// Gets a value indicating whether the contributor edit link should stay inside the docs content frame.
    /// </summary>
    /// <remarks>
    /// This follows the same docs-local resolution contract as <see cref="ContributorSourceUsesTurbo" /> so preview,
    /// versioned, and root-mounted docs surfaces all make the same frame-targeting decision.
    /// </remarks>
    public bool ContributorEditUsesTurbo { get; init; }

    /// <summary>
    /// Gets a value indicating whether the current document is a section landing doc.
    /// </summary>
    public bool IsSectionLanding { get; init; }

    /// <summary>
    /// Gets the curated next-step groups shown by a section landing doc.
    /// </summary>
    public IReadOnlyList<DocLandingFeaturedPageGroupViewModel> FeaturedPageGroups { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether any curated section-landing group has visible next-step pages.
    /// </summary>
    public bool HasFeaturedPages => FeaturedPageGroups.Any(group => group.Pages.Count > 0);

    /// <summary>
    /// Gets the grouped <c>In this section</c> lists shown by a section landing doc.
    /// </summary>
    public IReadOnlyList<DocSectionGroupViewModel> SectionGroups { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the page has an in-page outline to render.
    /// </summary>
    public bool HasOutline => Outline.Count > 0;

    /// <summary>
    /// Gets a value indicating whether the page has any sequence or related-page wayfinding links to render.
    /// </summary>
    public bool HasWayfinding => PreviousPage is not null || NextPage is not null || RelatedPages.Count > 0;
}

/// <summary>
/// View model describing the contributor provenance evidence rendered near the top of a details page.
/// </summary>
public sealed record DocContributorProvenanceViewModel
{
    /// <summary>
    /// Gets the reader-facing provenance strip label.
    /// </summary>
    public string Label { get; init; } = "Source of truth";

    /// <summary>
    /// Gets the browser-facing source URL when one exists.
    /// </summary>
    public string? SourceHref { get; init; }

    /// <summary>
    /// Gets the browser-facing edit URL when one exists.
    /// </summary>
    public string? EditHref { get; init; }

    /// <summary>
    /// Gets the exact UTC timestamp used for contributor freshness when one exists.
    /// </summary>
    public DateTimeOffset? LastUpdatedUtc { get; init; }
}

/// <summary>
/// Interface for harvesting documentation from various sources.
/// </summary>
public interface IDocHarvester
{
    /// <summary>
    /// Asynchronously scans the specified root path and returns a collection of documentation nodes harvested from sources under that path.
    /// </summary>
    /// <param name="rootPath">The filesystem root path to scan for documentation sources.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>A collection of <see cref="DocNode"/> representing the harvested documentation.</returns>
    Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default);
}
