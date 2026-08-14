using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Provides one package-owned harvester with a redacted, run-scoped progress reporting surface.
/// </summary>
/// <remarks>
/// This internal session accepts only a closed phase enum and numeric document deltas. It deliberately cannot carry a
/// file name, path, source text, exception, percentage, or arbitrary activity string. Custom <c>IDocHarvester</c>
/// implementations never receive this type and retain the established status-only progress contract.
/// </remarks>
internal sealed class AppSurfaceDocsHarvestProgressSession
{
    private readonly AppSurfaceDocsHarvestProgressReporter _reporter;
    private readonly string _runId;
    private readonly string _progressId;
    private readonly int _testingDelayPerDocumentMilliseconds;
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Initializes a run-scoped session for one built-in harvester.
    /// </summary>
    internal AppSurfaceDocsHarvestProgressSession(
        AppSurfaceDocsHarvestProgressReporter reporter,
        string runId,
        string progressId,
        int testingDelayPerDocumentMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(progressId);
        ArgumentOutOfRangeException.ThrowIfNegative(testingDelayPerDocumentMilliseconds);

        _runId = runId;
        _progressId = progressId;
        _testingDelayPerDocumentMilliseconds = testingDelayPerDocumentMilliseconds;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Records a safe parser lifecycle transition.
    /// </summary>
    /// <param name="phase">The next lifecycle phase. Waiting, terminal, stale, and non-sequential transitions are ignored.</param>
    /// <returns>A task that completes after the transition is retained and a live publication is scheduled.</returns>
    /// <remarks>
    /// This method does not throw for an ignored transition. The server-issued progress identity prevents one harvester
    /// from mutating another harvester's state.
    /// </remarks>
    internal ValueTask TransitionAsync(AppSurfaceDocsHarvestProgressPhase phase)
    {
        return _reporter.TransitionAsync(_runId, _progressId, phase);
    }

    /// <summary>
    /// Records one inspected source unit and an optional output delta.
    /// </summary>
    /// <param name="documentsFoundDelta">The non-negative number of documents found in the inspected source unit. Zero records source inspection without output.</param>
    /// <returns>A task that completes after the delta is recorded and any configured test pacing has elapsed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="documentsFoundDelta"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">Thrown when harvester cancellation interrupts configured pacing for a positive delta.</exception>
    /// <remarks>
    /// A positive output delta is paced one document at a time only when the local test delay is configured. The delay
    /// follows the real report that found those documents; source units that yield no documents are never delayed.
    /// </remarks>
    internal async ValueTask ReportSourceUnitAsync(int documentsFoundDelta)
    {
        _reporter.ReportSourceUnit(_runId, _progressId, documentsFoundDelta);
        await PaceRealOutputAsync(documentsFoundDelta);
    }

    /// <summary>
    /// Records materialized output that is not attributable to one newly inspected source unit.
    /// </summary>
    /// <param name="documentsFoundDelta">The non-negative number of additional documents materialized without inspecting a source unit.</param>
    /// <returns>A task that completes after the delta is recorded and any configured test pacing has elapsed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="documentsFoundDelta"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">Thrown when harvester cancellation interrupts configured pacing for a positive delta.</exception>
    /// <remarks>
    /// Zero is valid and is not paced. Negative values throw <see cref="ArgumentOutOfRangeException"/> from the
    /// reporter. Only positive deltas are delayed, so reporting a source unit that produces no documents never slows
    /// a harvester.
    /// </remarks>
    internal async ValueTask ReportOutputOnlyAsync(int documentsFoundDelta)
    {
        _reporter.ReportOutputOnly(_runId, _progressId, documentsFoundDelta);
        await PaceRealOutputAsync(documentsFoundDelta);
    }

    private async ValueTask PaceRealOutputAsync(int documentsFoundDelta)
    {
        if (documentsFoundDelta <= 0 || _testingDelayPerDocumentMilliseconds == 0)
        {
            return;
        }

        for (var index = 0; index < documentsFoundDelta; index++)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(_testingDelayPerDocumentMilliseconds),
                _reporter.TimeProvider,
                _cancellationToken);
        }
    }
}
