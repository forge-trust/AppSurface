using System.Collections.ObjectModel;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>Describes how much trusted concept identity data is available for one exact release.</summary>
internal enum DocConceptIdentityState
{
    /// <summary>The release predates the concept sidecar, so its identity coverage is unknown.</summary>
    LegacyUnknown = 0,

    /// <summary>The sidecar is valid, but at least one canonical page has no authored concept identifier.</summary>
    ValidPartial = 1,

    /// <summary>The sidecar is valid and every canonical page has an authored concept identifier.</summary>
    ValidComplete = 2,

    /// <summary>Optional identity data was rejected; it cannot prove a match or absence.</summary>
    Quarantined = 3
}

/// <summary>Selects whether invalid optional concept data is rejected or retained as quarantined startup data.</summary>
internal enum DocConceptValidationMode
{
    /// <summary>Publication mode: any invalid sidecar, map, or conflict makes the build invalid.</summary>
    Strict = 0,

    /// <summary>Startup mode: preserve usable releases while disabling only untrusted identity groups.</summary>
    Quarantine = 1
}

/// <summary>Stable, transport-neutral validation causes returned by the concept core.</summary>
internal enum DocConceptValidationFailure
{
    /// <summary>An index has invalid UTF-8, JSON, schema, fields, or coverage.</summary>
    IndexInvalid = 0,

    /// <summary>A bridge has invalid UTF-8, JSON, schema, fields, or membership shape.</summary>
    MapInvalid = 1,

    /// <summary>A concept identifier does not satisfy the approved grammar.</summary>
    InvalidConceptId = 2,

    /// <summary>An exact release version or manifest pin does not resolve to a validated release.</summary>
    ReleasePinInvalid = 3,

    /// <summary>A route is not an exact public canonical winner in its pinned release and locale.</summary>
    CanonicalRouteInvalid = 4,

    /// <summary>A route or page identity is duplicated or assigned incompatibly.</summary>
    IdentityConflict = 5,

    /// <summary>An artifact exceeds the configured per-artifact UTF-8 byte budget.</summary>
    ArtifactTooLarge = 6,

    /// <summary>A byte-budget configuration is outside the supported range.</summary>
    ArtifactBudgetInvalid = 7
}

/// <summary>Describes the reader-facing disposition of a source guide in one exact target release.</summary>
internal enum DocConceptResolutionReason
{
    /// <summary>The canonical route and title are unchanged.</summary>
    SamePage = 0,

    /// <summary>The documented concept has a different canonical route in the target.</summary>
    MovedPage = 1,

    /// <summary>The documented concept keeps its route but has a different title.</summary>
    RenamedPage = 2,

    /// <summary>Trusted evidence establishes that no documented counterpart exists in the selected locale.</summary>
    KnownAbsent = 3,

    /// <summary>Available evidence is incomplete and cannot establish a counterpart or absence.</summary>
    Unknown = 4,

    /// <summary>Conflicting identity evidence disables resolution for the affected concept group.</summary>
    Ambiguous = 5,

    /// <summary>The selected exact target release is unavailable.</summary>
    TargetUnavailable = 6,

    /// <summary>The selected target has no page in the source locale; language is never switched implicitly.</summary>
    NoSelectedLocaleDocs = 7
}

/// <summary>Reports a safe core validation cause and artifact category without transport-specific diagnostics.</summary>
/// <param name="Failure">Stable validation cause for the caller to map to its existing diagnostic channel.</param>
/// <param name="Artifact">Artifact category or caller-provided opaque artifact reference.</param>
/// <param name="Detail">Short non-secret explanation suitable for operator diagnostics.</param>
internal sealed record DocConceptValidationIssue(
    DocConceptValidationFailure Failure,
    string Artifact,
    string Detail);

/// <summary>Identifies a canonical route's already-verified physical serving artifact and locale facts.</summary>
/// <param name="RoutePath">Docs-root-relative canonical route exactly as stored by the frozen route manifest.</param>
/// <param name="ArtifactPath">Whole-export-root relative artifact path from the verified route-to-artifact mapping.</param>
/// <param name="Locale">Normalized locale, or <see langword="null" /> for a known non-localized surface.</param>
/// <param name="LocaleIsKnown">Whether <paramref name="Locale" /> was established by verified route facts.</param>
/// <param name="IsServeable">Whether the caller verified an ordinary readable artifact for this canonical winner.</param>
internal sealed record DocConceptCanonicalRouteFact(
    string RoutePath,
    string? ArtifactPath,
    string? Locale,
    bool LocaleIsKnown,
    bool IsServeable = true);

/// <summary>Immutable metadata for one verified public canonical page in an exact release.</summary>
internal sealed class DocConceptCanonicalPage
{
    /// <summary>Creates an immutable page snapshot and detaches its anchor collection.</summary>
    /// <param name="routePath">Docs-root-relative canonical route, retaining the winning route's authored casing.</param>
    /// <param name="artifactPath">Verified whole-tree relative artifact path, if one is available.</param>
    /// <param name="locale">Normalized locale, or <see langword="null" /> for an explicitly non-localized page.</param>
    /// <param name="localeIsKnown">Whether locale identity is established; false represents unverifiable legacy locale data.</param>
    /// <param name="title">Title recorded by the release concept index.</param>
    /// <param name="anchors">Exact emitted anchor identifiers recorded by the release concept index.</param>
    /// <param name="landing">Whether this page is an eligible resolved public-section landing page.</param>
    /// <param name="conceptId">Validated ordinal concept identifier, or <see langword="null" /> when unspecified.</param>
    /// <param name="isServeable">Whether a verified ordinary physical artifact is available for this page.</param>
    public DocConceptCanonicalPage(
        string routePath,
        string? artifactPath,
        string? locale,
        bool localeIsKnown,
        string title,
        IReadOnlyList<string> anchors,
        bool landing,
        string? conceptId,
        bool isServeable = true)
    {
        RoutePath = routePath ?? throw new ArgumentNullException(nameof(routePath));
        ArtifactPath = artifactPath;
        Locale = locale;
        LocaleIsKnown = localeIsKnown;
        Title = title ?? throw new ArgumentNullException(nameof(title));
        ArgumentNullException.ThrowIfNull(anchors);
        Anchors = new ReadOnlyCollection<string>(anchors.ToArray());
        Landing = landing;
        ConceptId = conceptId;
        IsServeable = isServeable;
    }

    /// <summary>Gets the canonical route with the target winner's original spelling.</summary>
    public string RoutePath { get; }

    /// <summary>Gets the verified physical artifact path without deriving it from the route.</summary>
    public string? ArtifactPath { get; }

    /// <summary>Gets the normalized locale or <see langword="null" /> for a known non-localized surface.</summary>
    public string? Locale { get; }

    /// <summary>Gets whether the locale value is trusted rather than unknown legacy evidence.</summary>
    public bool LocaleIsKnown { get; }

    /// <summary>Gets the exact title recorded for this release page.</summary>
    public string Title { get; }

    /// <summary>Gets an immutable copy of the exact emitted anchor identifiers.</summary>
    public IReadOnlyList<string> Anchors { get; }

    /// <summary>Gets whether this verified canonical winner can be used as section recovery.</summary>
    public bool Landing { get; }

    /// <summary>Gets the validated ordinal concept identifier, or <see langword="null" />.</summary>
    public string? ConceptId { get; }

    /// <summary>Gets whether the caller verified an ordinary readable physical artifact.</summary>
    public bool IsServeable { get; }
}

/// <summary>One exact public-version descriptor's verified archive facts passed to the combined loader.</summary>
internal sealed class DocConceptReleaseInput
{
    /// <summary>Creates a detached archive snapshot input for the pure combined loader.</summary>
    /// <param name="version">Exact version identifier from the catalog.</param>
    /// <param name="releaseManifestSha256">SHA-256 pin of the verified release manifest.</param>
    /// <param name="isPublic">Whether this release is eligible for reader-facing version selection.</param>
    /// <param name="isAvailable">Whether the exact release archive passed existing verification.</param>
    /// <param name="hasDocumentation">Whether this release contains documentation pages.</param>
    /// <param name="hasSearchIndex">Whether search is verified and available for this release.</param>
    /// <param name="homeRoutePath">Verified canonical home route for this release, if available.</param>
    /// <param name="canonicalRoutes">Only public canonical winners and their verified physical mappings.</param>
    /// <param name="conceptIndexBytes">Exact sidecar bytes covered by the verified archive manifest, or null when absent.</param>
    public DocConceptReleaseInput(
        string version,
        string releaseManifestSha256,
        bool isPublic,
        bool isAvailable,
        bool hasDocumentation,
        bool hasSearchIndex,
        string? homeRoutePath,
        IReadOnlyList<DocConceptCanonicalRouteFact> canonicalRoutes,
        ReadOnlyMemory<byte>? conceptIndexBytes)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        ReleaseManifestSha256 = releaseManifestSha256 ?? throw new ArgumentNullException(nameof(releaseManifestSha256));
        IsPublic = isPublic;
        IsAvailable = isAvailable;
        HasDocumentation = hasDocumentation;
        HasSearchIndex = hasSearchIndex;
        HomeRoutePath = homeRoutePath;
        ArgumentNullException.ThrowIfNull(canonicalRoutes);
        CanonicalRoutes = new ReadOnlyCollection<DocConceptCanonicalRouteFact>(canonicalRoutes.ToArray());
        ConceptIndexBytes = conceptIndexBytes.HasValue
            ? new ReadOnlyMemory<byte>(conceptIndexBytes.Value.ToArray())
            : (ReadOnlyMemory<byte>?)null;
    }

    /// <summary>Gets the exact catalog version identifier.</summary>
    public string Version { get; }

    /// <summary>Gets the verified lowercase or uppercase hexadecimal release-manifest digest.</summary>
    public string ReleaseManifestSha256 { get; }

    /// <summary>Gets whether this release is public in the pinned catalog snapshot.</summary>
    public bool IsPublic { get; }

    /// <summary>Gets whether archive verification made this exact release readable.</summary>
    public bool IsAvailable { get; }

    /// <summary>Gets whether any documentation page exists in the exact release.</summary>
    public bool HasDocumentation { get; }

    /// <summary>Gets whether the exact release exposes a verified search index.</summary>
    public bool HasSearchIndex { get; }

    /// <summary>Gets the verified canonical documentation home route, or null when unavailable.</summary>
    public string? HomeRoutePath { get; }

    /// <summary>Gets a detached inventory of public canonical route winners.</summary>
    public IReadOnlyList<DocConceptCanonicalRouteFact> CanonicalRoutes { get; }

    /// <summary>Gets a detached copy of the exact, already-verified sidecar bytes, or null if absent.</summary>
    public ReadOnlyMemory<byte>? ConceptIndexBytes { get; }
}

/// <summary>An optional bridge artifact reference whose path was resolved under the caller's trusted release root.</summary>
internal sealed class DocConceptMapReference
{
    /// <summary>Creates a bridge reference and copies its exact artifact bytes.</summary>
    /// <param name="path">Descriptor-relative bridge path retained for exact-reference deduplication.</param>
    /// <param name="sha256">Descriptor-pinned SHA-256 digest.</param>
    /// <param name="bytes">Exact bytes read by the caller after trusted-root resolution.</param>
    public DocConceptMapReference(string path, string sha256, ReadOnlyMemory<byte> bytes)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        Bytes = new ReadOnlyMemory<byte>(bytes.ToArray());
    }

    /// <summary>Gets the descriptor-relative path used to deduplicate identical catalog references.</summary>
    public string Path { get; }

    /// <summary>Gets the exact descriptor digest pin expected for <see cref="Bytes" />.</summary>
    public string Sha256 { get; }

    /// <summary>Gets a detached immutable-memory copy of the exact bridge bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }
}

/// <summary>Identifies a public canonical source page inside an exact release snapshot.</summary>
/// <param name="Version">Exact source version.</param>
/// <param name="ReleaseManifestSha256">Exact source release-manifest pin.</param>
/// <param name="RoutePath">Source page's canonical route, retaining its verified spelling.</param>
/// <param name="Locale">Normalized source locale or null for a known non-localized page.</param>
/// <param name="LocaleIsKnown">Whether the source locale was established by verified facts.</param>
internal sealed record DocConceptPageIdentity(
    string Version,
    string ReleaseManifestSha256,
    string RoutePath,
    string? Locale,
    bool LocaleIsKnown);

/// <summary>Immutable runtime view of one exact release and its validated canonical pages.</summary>
internal sealed class DocConceptReleaseSnapshot
{
    internal DocConceptReleaseSnapshot(
        string version,
        string releaseManifestSha256,
        bool isPublic,
        bool isAvailable,
        bool hasDocumentation,
        bool hasSearchIndex,
        string? homeRoutePath,
        DocConceptIdentityState identityState,
        IReadOnlyList<DocConceptCanonicalPage> pages)
    {
        Version = version;
        ReleaseManifestSha256 = releaseManifestSha256;
        IsPublic = isPublic;
        IsAvailable = isAvailable;
        HasDocumentation = hasDocumentation;
        HasSearchIndex = hasSearchIndex;
        HomeRoutePath = homeRoutePath;
        IdentityState = identityState;
        Pages = new ReadOnlyCollection<DocConceptCanonicalPage>(pages.ToArray());
    }

    /// <summary>Gets the exact catalog version identifier.</summary>
    public string Version { get; }

    /// <summary>Gets the exact verified release-manifest SHA-256 pin.</summary>
    public string ReleaseManifestSha256 { get; }

    /// <summary>Gets whether the exact version is public to the reader.</summary>
    public bool IsPublic { get; }

    /// <summary>Gets whether this exact version archive is available.</summary>
    public bool IsAvailable { get; }

    /// <summary>Gets whether any documentation pages exist in this release.</summary>
    public bool HasDocumentation { get; }

    /// <summary>Gets whether a verified release-local search index exists.</summary>
    public bool HasSearchIndex { get; }

    /// <summary>Gets the verified canonical home route, if any.</summary>
    public string? HomeRoutePath { get; }

    /// <summary>Gets the validated coverage state for this release's embedded concept sidecar.</summary>
    public DocConceptIdentityState IdentityState { get; }

    /// <summary>Gets an immutable detached snapshot of public canonical winners only.</summary>
    public IReadOnlyList<DocConceptCanonicalPage> Pages { get; }
}

/// <summary>The complete immutable result of resolving one source guide against one selected exact release.</summary>
internal sealed record DocConceptResolution(
    DocConceptResolutionReason Reason,
    DocConceptPageIdentity SourceIdentity,
    DocConceptCanonicalPage? SourcePage,
    DocConceptReleaseSnapshot? TargetRelease,
    DocConceptCanonicalPage? TargetPage,
    DocConceptCanonicalPage? FallbackSection,
    bool SearchAvailable,
    string? HomeRoutePath)
{
    /// <summary>Gets whether this result names a documented counterpart rather than browsing recovery.</summary>
    public bool HasCounterpart => TargetPage is not null;
}

/// <summary>One validated incoming source whose resolver outcome actually lands on a destination page.</summary>
/// <param name="SourceKey">Stable public lookup key derived from the source identity tuple.</param>
/// <param name="SourceIdentity">Verified source page identity used to build the key.</param>
/// <param name="SourcePage">Safe source title and route information from the immutable catalog snapshot.</param>
/// <param name="Reason">Exact counterpart outcome; browsing recovery is never projected as a counterpart.</param>
internal sealed record DocConceptIncomingSource(
    string SourceKey,
    DocConceptPageIdentity SourceIdentity,
    DocConceptCanonicalPage SourcePage,
    DocConceptResolutionReason Reason);

/// <summary>Returns the combined identity snapshot and all strict-validation or startup-quarantine issues.</summary>
internal sealed class DocConceptIndexBuildResult
{
    internal DocConceptIndexBuildResult(
        DocConceptIdentityIndex index,
        IReadOnlyList<DocConceptValidationIssue> issues,
        bool isValid)
    {
        Index = index;
        Issues = new ReadOnlyCollection<DocConceptValidationIssue>(issues.ToArray());
        IsValid = isValid;
    }

    /// <summary>Gets the immutable combined index, including quarantined groups where applicable.</summary>
    public DocConceptIdentityIndex Index { get; }

    /// <summary>Gets detached validation causes for caller-owned operator diagnostics.</summary>
    public IReadOnlyList<DocConceptValidationIssue> Issues { get; }

    /// <summary>Gets whether strict publication validation succeeded without rejected input or conflicts.</summary>
    public bool IsValid { get; }
}

/// <summary>Contains the default and accepted range for the coupled UTF-8 artifact byte budget.</summary>
internal static class DocConceptArtifactBudget
{
    /// <summary>Gets the default per-artifact limit, shared with existing Docs HTML/search-input limits.</summary>
    public const int DefaultMaximumBytes = 4 * 1024 * 1024;

    /// <summary>Gets the minimum supported non-zero per-artifact byte limit.</summary>
    public const int MinimumBytes = 1;

    /// <summary>Gets the maximum supported per-artifact byte limit.</summary>
    public const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>Checks that a configured per-artifact byte limit uses the existing supported range.</summary>
    /// <param name="maximumBytes">Configured maximum bytes.</param>
    /// <returns>Whether the configured value is within the supported inclusive range.</returns>
    public static bool IsValidLimit(int maximumBytes) => maximumBytes is >= MinimumBytes and <= MaximumBytes;

    /// <summary>Counts strict UTF-8 bytes for a complete generated artifact and rejects the whole artifact above the limit.</summary>
    /// <param name="artifact">Complete generated text; no truncation is performed.</param>
    /// <param name="maximumBytes">Effective byte limit shared with existing rewritten-file behavior.</param>
    /// <param name="utf8ByteCount">Exact UTF-8 byte count when the limit is valid.</param>
    /// <returns>Whether the complete artifact is within the configured byte budget.</returns>
    public static bool TryMeasure(string artifact, int maximumBytes, out int utf8ByteCount)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        utf8ByteCount = 0;
        if (!IsValidLimit(maximumBytes))
        {
            return false;
        }

        utf8ByteCount = System.Text.Encoding.UTF8.GetByteCount(artifact);
        return utf8ByteCount <= maximumBytes;
    }

    /// <summary>Checks exact artifact bytes after the caller has performed a bounded read.</summary>
    /// <param name="artifactBytes">Exact complete bytes; this method never accepts a truncated prefix.</param>
    /// <param name="maximumBytes">Effective per-artifact byte limit.</param>
    /// <param name="byteCount">Exact byte length when the limit is valid.</param>
    /// <returns>Whether the complete artifact is within the configured byte budget.</returns>
    public static bool TryMeasure(ReadOnlySpan<byte> artifactBytes, int maximumBytes, out int byteCount)
    {
        byteCount = artifactBytes.Length;
        if (!IsValidLimit(maximumBytes))
        {
            byteCount = 0;
            return false;
        }

        return artifactBytes.Length <= maximumBytes;
    }
}
