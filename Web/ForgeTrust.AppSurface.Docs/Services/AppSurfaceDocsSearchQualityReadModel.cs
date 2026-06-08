using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Intelligence;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Process-local, bounded aggregate read model for hosted AppSurface Docs search-quality review.
/// </summary>
/// <remarks>
/// This service accepts only validated, sanitized product-intelligence events. It stores no raw event payloads, raw search
/// text, URLs, identity, cookies, request bodies, or free-form comments. The rolling window exists only to bound process
/// memory while preserving enough recent aggregate signal for maintainer diagnostics.
/// </remarks>
public sealed class AppSurfaceDocsSearchQualityReadModel
{
    /// <summary>
    /// Maximum number of accepted events represented by the process-local window.
    /// </summary>
    public const int DefaultWindowCapacity = 512;

    private readonly object _gate = new();
    private readonly Queue<AppSurfaceDocsSearchQualityContribution> _events = new();
    private AppSurfaceDocsSearchQualityCounts _counts;

    /// <summary>
    /// Records one sanitized docs product-intelligence event into the bounded aggregate model.
    /// </summary>
    /// <param name="contract">The event contract that validated the event.</param>
    /// <param name="properties">Sanitized string properties returned by the registry.</param>
    public void Record(AppSurfaceProductEventContract contract, IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(properties);

        var contribution = AppSurfaceDocsSearchQualityContribution.From(contract.Name, properties);
        if (contribution is null)
        {
            return;
        }

        lock (_gate)
        {
            _events.Enqueue(contribution);
            _counts = _counts.Add(contribution);

            while (_events.Count > DefaultWindowCapacity)
            {
                _counts = _counts.Subtract(_events.Dequeue());
            }
        }
    }

    /// <summary>
    /// Creates a maintainer-facing snapshot of the current aggregate model.
    /// </summary>
    /// <param name="options">Current docs options used to report the resolved metrics mode.</param>
    /// <returns>A defensive aggregate snapshot.</returns>
    public AppSurfaceDocsSearchQualityResponse GetSnapshot(AppSurfaceDocsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AppSurfaceDocsSearchQualityCounts counts;
        int total;
        lock (_gate)
        {
            counts = _counts;
            total = _events.Count;
        }

        var metrics = options.Metrics ?? new AppSurfaceDocsMetricsOptions();
        return new AppSurfaceDocsSearchQualityResponse(
            DateTimeOffset.UtcNow,
            total,
            DefaultWindowCapacity,
            counts.SubmittedSearches,
            counts.ZeroResultSearches,
            counts.ResultSelections,
            counts.RecoverySelections,
            counts.FilterChanges,
            counts.FeedbackSubmissions,
            new AppSurfaceDocsSearchQualityMode(
                metrics.Enabled && metrics.BrowserCollector?.Enabled == true,
                metrics.Enabled && metrics.HostedCollection?.Enabled == true,
                metrics.Enabled && metrics.HostedReview?.Enabled == true,
                RawQueriesDisabled: true),
            BuildFrictionBuckets(counts),
            BuildFeedbackBuckets(counts));
    }

    private static IReadOnlyList<AppSurfaceDocsSearchQualityBucket> BuildFrictionBuckets(
        AppSurfaceDocsSearchQualityCounts counts)
    {
        return
        [
            new(
                "Zero-result searches",
                counts.ZeroResultSearches,
                "Add aliases, improve titles, or create a troubleshooting entry for missing topics."),
            new(
                "Recovery link selections",
                counts.RecoverySelections,
                "Inspect recovery routes and ensure starter pages answer the reader's next question."),
            new(
                "Filter changes",
                counts.FilterChanges,
                "Tune facet labels and summaries when readers narrow repeatedly before selecting a result."),
            new(
                "Result selections",
                counts.ResultSelections,
                "Compare selected result kinds with intended landing paths and adjust summaries or ranking inputs.")
        ];
    }

    private static IReadOnlyList<AppSurfaceDocsSearchQualityBucket> BuildFeedbackBuckets(
        AppSurfaceDocsSearchQualityCounts counts)
    {
        return
        [
            new(
                "Useful recovery feedback",
                counts.UsefulFeedback,
                "Keep these recovery paths prominent and consider linking them from nearby docs pages."),
            new(
                "Not-useful recovery feedback",
                counts.NotUsefulFeedback,
                "Improve the no-results fallback copy or add a more direct docs path for this friction state.")
        ];
    }

    private readonly record struct AppSurfaceDocsSearchQualityCounts(
        int SubmittedSearches,
        int ZeroResultSearches,
        int ResultSelections,
        int RecoverySelections,
        int FilterChanges,
        int FeedbackSubmissions,
        int UsefulFeedback,
        int NotUsefulFeedback)
    {
        public AppSurfaceDocsSearchQualityCounts Add(AppSurfaceDocsSearchQualityContribution contribution)
        {
            return new AppSurfaceDocsSearchQualityCounts(
                SubmittedSearches + contribution.SubmittedSearches,
                ZeroResultSearches + contribution.ZeroResultSearches,
                ResultSelections + contribution.ResultSelections,
                RecoverySelections + contribution.RecoverySelections,
                FilterChanges + contribution.FilterChanges,
                FeedbackSubmissions + contribution.FeedbackSubmissions,
                UsefulFeedback + contribution.UsefulFeedback,
                NotUsefulFeedback + contribution.NotUsefulFeedback);
        }

        public AppSurfaceDocsSearchQualityCounts Subtract(AppSurfaceDocsSearchQualityContribution contribution)
        {
            return new AppSurfaceDocsSearchQualityCounts(
                SubmittedSearches - contribution.SubmittedSearches,
                ZeroResultSearches - contribution.ZeroResultSearches,
                ResultSelections - contribution.ResultSelections,
                RecoverySelections - contribution.RecoverySelections,
                FilterChanges - contribution.FilterChanges,
                FeedbackSubmissions - contribution.FeedbackSubmissions,
                UsefulFeedback - contribution.UsefulFeedback,
                NotUsefulFeedback - contribution.NotUsefulFeedback);
        }
    }

    private sealed record AppSurfaceDocsSearchQualityContribution(
        int SubmittedSearches = 0,
        int ZeroResultSearches = 0,
        int ResultSelections = 0,
        int RecoverySelections = 0,
        int FilterChanges = 0,
        int FeedbackSubmissions = 0,
        int UsefulFeedback = 0,
        int NotUsefulFeedback = 0)
    {
        public static AppSurfaceDocsSearchQualityContribution? From(
            string eventName,
            IReadOnlyDictionary<string, string> properties)
        {
            return eventName switch
            {
                AppSurfaceProductEventRegistry.DocsSearchSubmitted => new(SubmittedSearches: 1),
                AppSurfaceProductEventRegistry.DocsSearchReturnedZeroResults => new(ZeroResultSearches: 1),
                AppSurfaceProductEventRegistry.DocsSearchResultSelected => new(ResultSelections: 1),
                AppSurfaceProductEventRegistry.DocsRecoveryLinkSelected => new(RecoverySelections: 1),
                AppSurfaceProductEventRegistry.DocsSearchFilterChanged => new(FilterChanges: 1),
                AppSurfaceProductEventRegistry.DocsSearchFrictionFeedbackSubmitted => new(
                    FeedbackSubmissions: 1,
                    UsefulFeedback: IsFeedback(properties, "useful") ? 1 : 0,
                    NotUsefulFeedback: IsFeedback(properties, "not_useful") ? 1 : 0),
                _ => null
            };
        }

        private static bool IsFeedback(IReadOnlyDictionary<string, string> properties, string value)
        {
            return properties.TryGetValue("feedback_value", out var feedback)
                   && string.Equals(feedback, value, StringComparison.Ordinal);
        }
    }
}
