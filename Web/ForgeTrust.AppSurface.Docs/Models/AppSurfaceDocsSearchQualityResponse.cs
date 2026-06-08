namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Maintainer-facing aggregate search-quality snapshot for AppSurface Docs.
/// </summary>
/// <param name="GeneratedUtc">UTC timestamp when the snapshot was generated.</param>
/// <param name="TotalAcceptedEvents">Total accepted docs metrics events represented by the process-local read model.</param>
/// <param name="WindowCapacity">Maximum number of recent accepted events retained by the process-local read model.</param>
/// <param name="SubmittedSearches">Count of accepted search submission events.</param>
/// <param name="ZeroResultSearches">Count of accepted zero-result search events.</param>
/// <param name="ResultSelections">Count of accepted search result selection events.</param>
/// <param name="RecoverySelections">Count of accepted recovery-link selection events.</param>
/// <param name="FilterChanges">Count of accepted filter-change events.</param>
/// <param name="FeedbackSubmissions">Count of accepted search-friction feedback events.</param>
/// <param name="Mode">Resolved metrics mode summary for maintainers.</param>
/// <param name="FrictionBuckets">Low-cardinality search friction buckets with suggested docs actions.</param>
/// <param name="FeedbackBuckets">Search-friction feedback buckets.</param>
public sealed record AppSurfaceDocsSearchQualityResponse(
    DateTimeOffset GeneratedUtc,
    int TotalAcceptedEvents,
    int WindowCapacity,
    int SubmittedSearches,
    int ZeroResultSearches,
    int ResultSelections,
    int RecoverySelections,
    int FilterChanges,
    int FeedbackSubmissions,
    AppSurfaceDocsSearchQualityMode Mode,
    IReadOnlyList<AppSurfaceDocsSearchQualityBucket> FrictionBuckets,
    IReadOnlyList<AppSurfaceDocsSearchQualityBucket> FeedbackBuckets);

/// <summary>
/// Resolved AppSurface Docs metrics mode shown on the hosted diagnostics surface.
/// </summary>
/// <param name="BrowserCollectorEnabled">Whether browser collector forwarding is enabled.</param>
/// <param name="HostedCollectionEnabled">Whether hosted event ingestion is enabled.</param>
/// <param name="HostedReviewEnabled">Whether hosted search-quality review is enabled.</param>
/// <param name="RawQueriesDisabled">Whether raw search query capture is disabled by contract.</param>
public sealed record AppSurfaceDocsSearchQualityMode(
    bool BrowserCollectorEnabled,
    bool HostedCollectionEnabled,
    bool HostedReviewEnabled,
    bool RawQueriesDisabled);

/// <summary>
/// One low-cardinality search-quality bucket.
/// </summary>
/// <param name="Name">Bucket name.</param>
/// <param name="Count">Accepted event count in the bucket.</param>
/// <param name="SuggestedAction">Maintainer action suggested by the bucket.</param>
public sealed record AppSurfaceDocsSearchQualityBucket(
    string Name,
    int Count,
    string SuggestedAction);
