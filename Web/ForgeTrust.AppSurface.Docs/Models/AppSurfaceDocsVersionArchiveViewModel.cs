namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// View model for the public AppSurface Docs version archive and degraded entry surface.
/// </summary>
/// <remarks>
/// The same model drives both the dedicated archive page and the degraded route-root recovery surface when no healthy
/// recommended release can be mounted at the stable entry alias.
/// <see cref="PreviewHref"/> always points at the current source-backed preview surface, while
/// <see cref="VersionsHref"/> stays on the stable archive URL. <see cref="Versions"/> preserves the authored
/// catalog order and should be treated as a read-only projection of the resolved catalog state.
/// </remarks>
public sealed record AppSurfaceDocsVersionArchiveViewModel
{
    /// <summary>
    /// Gets the page heading.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the orientation copy shown above the archive list.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional explanatory copy shown when the stable route-root alias cannot mount a recommended released tree.
    /// </summary>
    /// <remarks>
    /// This is typically <see langword="null"/> on the dedicated archive page and populated only for the degraded
    /// route-root recovery experience.
    /// </remarks>
    public string? AvailabilityMessage { get; init; }

    /// <summary>
    /// Gets the live preview docs URL.
    /// </summary>
    /// <remarks>
    /// This points at the configured source-backed preview surface, such as <c>/docs/next</c> for the default route
    /// family or <c>/foo/bar/next</c> for a custom one.
    /// </remarks>
    public string PreviewHref { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stable archive URL.
    /// </summary>
    public string VersionsHref { get; init; } = string.Empty;

    /// <summary>
    /// Gets the available published versions shown in the archive.
    /// </summary>
    public IReadOnlyList<AppSurfaceDocsVersionArchiveEntryViewModel> Versions { get; init; } = [];

    /// <summary>
    /// Gets the safely projected moving labels shown before the exact-release archive.
    /// </summary>
    /// <remarks>
    /// This collection is independent of the operator-facing resolved alias records. It contains only reader-safe
    /// fields and never carries configured targets for hidden or invalid entries.
    /// </remarks>
    public IReadOnlyList<AppSurfaceDocsVersionAliasArchiveEntryViewModel> Aliases { get; init; } = [];
}

/// <summary>
/// Represents one reader-safe moving label in the public AppSurface Docs version archive.
/// </summary>
/// <remarks>
/// A moving label may link to its selected exact release while that target is healthy, or remain informational when
/// its definition or target cannot be served. This is a deliberate safe projection: it has no configured-target
/// property and must never be populated from an operator-only configured target when the target is hidden or unknown.
/// Invalid and conflicting definitions use only <see cref="Name"/> and <see cref="AvailabilityMessage"/>.
/// </remarks>
public sealed record AppSurfaceDocsVersionAliasArchiveEntryViewModel
{
    /// <summary>
    /// Gets the normalized public label name used in the alias URL.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reader-facing label for a valid public definition.
    /// </summary>
    /// <remarks>
    /// This is empty for invalid or conflicting rows, whose only display identity is <see cref="Name"/>.
    /// </remarks>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional summary copy for a valid public definition.
    /// </summary>
    /// <remarks>Invalid and conflicting definitions never expose recovered summary text.</remarks>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the exact public target identifier when that target is itself known and public.
    /// </summary>
    /// <remarks>Unknown and hidden targets are represented as <see langword="null"/>.</remarks>
    public string? TargetVersion { get; init; }

    /// <summary>
    /// Gets the target release support label when a public target is known.
    /// </summary>
    public string? SupportStateLabel { get; init; }

    /// <summary>
    /// Gets the target release advisory label when a public target has an advisory.
    /// </summary>
    public string? AdvisoryLabel { get; init; }

    /// <summary>
    /// Gets the alias URL when the exact public target is available.
    /// </summary>
    /// <remarks>
    /// Unavailable, invalid, conflicting, hidden-target, and unknown-target entries have no link.
    /// </remarks>
    public string? Href { get; init; }

    /// <summary>
    /// Gets a reader-safe explanation when the entry cannot link to an available exact release.
    /// </summary>
    /// <remarks>
    /// This may contain only the approved generic explanation for invalid/conflicting definitions or a sanitized
    /// availability explanation for a valid public alias; it must not disclose a hidden target or filesystem path.
    /// </remarks>
    public string? AvailabilityMessage { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry links to its selected target.
    /// </summary>
    public bool IsAvailable { get; init; }
}

/// <summary>
/// Represents one release entry in the public AppSurface Docs version archive.
/// </summary>
/// <remarks>
/// Entries may describe either a healthy exact-version tree with an <see cref="Href"/> target or an unavailable
/// release that should remain visible in the archive with an explanatory availability message.
/// <see cref="IsRecommended"/> identifies the exact release currently mirrored at the stable route-root alias,
/// while <see cref="IsAvailable"/> controls whether the entry can link directly to that exact version.
/// Advisory and support labels are independent: an unavailable or deprecated release may still surface an advisory,
/// and a recommended release may still carry a warning label when the catalog says readers should see one.
/// </remarks>
public sealed record AppSurfaceDocsVersionArchiveEntryViewModel
{
    /// <summary>
    /// Gets the exact published version identifier.
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reader-facing label.
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Gets optional summary copy for the release.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Gets the exact-version URL when the release is available.
    /// </summary>
    /// <remarks>
    /// This is <see langword="null"/> when <see cref="IsAvailable"/> is <see langword="false"/> and the archive
    /// should render the release as informational-only.
    /// </remarks>
    public string? Href { get; init; }

    /// <summary>
    /// Gets a value indicating whether this version is the recommended stable alias target.
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// Gets a value indicating whether the exact-version tree is currently available.
    /// </summary>
    public bool IsAvailable { get; init; }

    /// <summary>
    /// Gets the human-readable support-state label.
    /// </summary>
    public string SupportStateLabel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the human-readable advisory label when a release warning should be surfaced.
    /// </summary>
    /// <remarks>
    /// This is <see langword="null"/> when no release-level warning badge should be rendered.
    /// </remarks>
    public string? AdvisoryLabel { get; init; }

    /// <summary>
    /// Gets the availability explanation when the release tree is unavailable.
    /// </summary>
    /// <remarks>
    /// This is usually populated only when <see cref="IsAvailable"/> is <see langword="false"/> and explains why
    /// the exact-version tree could not be mounted or linked.
    /// </remarks>
    public string? AvailabilityMessage { get; init; }
}
