namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// View model for the public RazorDocs version archive and degraded entry surface.
/// </summary>
/// <remarks>
/// The same model drives both the dedicated archive page and the degraded route-root recovery surface when no healthy
/// recommended release can be mounted at the stable entry alias.
/// <see cref="PreviewHref"/> always points at the current source-backed preview surface, while
/// <see cref="VersionsHref"/> stays on the stable archive URL. <see cref="Versions"/> preserves the authored
/// catalog order and should be treated as a read-only projection of the resolved catalog state.
/// </remarks>
public sealed record RazorDocsVersionArchiveViewModel
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
    public IReadOnlyList<RazorDocsVersionArchiveEntryViewModel> Versions { get; init; } = [];
}

/// <summary>
/// Represents one release entry in the public RazorDocs version archive.
/// </summary>
/// <remarks>
/// Entries may describe either a healthy exact-version tree with an <see cref="Href"/> target or an unavailable
/// release that should remain visible in the archive with an explanatory availability message.
/// <see cref="IsRecommended"/> identifies the exact release currently mirrored at the stable route-root alias,
/// while <see cref="IsAvailable"/> controls whether the entry can link directly to that exact version.
/// Advisory and support labels are independent: an unavailable or deprecated release may still surface an advisory,
/// and a recommended release may still carry a warning label when the catalog says readers should see one.
/// </remarks>
public sealed record RazorDocsVersionArchiveEntryViewModel
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
