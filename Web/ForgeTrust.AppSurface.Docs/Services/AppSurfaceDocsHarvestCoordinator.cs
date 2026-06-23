using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Coordinates shared AppSurface Docs harvest work so startup warmup, first requests, and trusted operator rebuilds use
/// one ordered source-backed loop.
/// </summary>
public sealed class AppSurfaceDocsHarvestCoordinator
{
    private readonly DocAggregator _aggregator;
    private readonly AppSurfaceDocsHarvestProgressReporter _progress;
    private readonly object _gate = new();
    private Task<DocHarvestHealthSnapshot>? _activeHarvest;
    private bool _pendingRebuild;

    /// <summary>
    /// Initializes a new instance of the initial harvest coordinator.
    /// </summary>
    /// <param name="aggregator">The docs aggregator that owns the shared memoized harvest.</param>
    /// <param name="progress">The progress reporter that exposes the current redacted snapshot to the observatory page.</param>
    public AppSurfaceDocsHarvestCoordinator(
        DocAggregator aggregator,
        AppSurfaceDocsHarvestProgressReporter progress)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
    }

    /// <summary>
    /// Gets the latest redacted harvest progress snapshot published by the reporter.
    /// </summary>
    internal AppSurfaceDocsHarvestProgressSnapshot CurrentProgress => _progress.CurrentSnapshot;

    /// <summary>
    /// Gets the completion-navigation delay, in milliseconds, used by the progress reporter.
    /// </summary>
    internal int CompletionDelay => _progress.CompletionDelay;

    /// <summary>
    /// Gets a value indicating whether a harvest is running or a queued rebuild is waiting for the running harvest.
    /// </summary>
    internal bool HasActiveOrQueuedHarvest
    {
        get
        {
            lock (_gate)
            {
                return _pendingRebuild || _activeHarvest is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Starts or reuses the shared initial harvest task.
    /// </summary>
    /// <returns>
    /// The memoized harvest-health task for the current run. A new task is created when no task exists or when the
    /// prior task was canceled or faulted.
    /// </returns>
    /// <remarks>
    /// Callers share one background harvest through this coordinator. The task itself runs with
    /// <see cref="CancellationToken.None"/> so one impatient request cannot cancel warmup for later requests.
    /// </remarks>
    internal Task<DocHarvestHealthSnapshot> EnsureStarted()
    {
        lock (_gate)
        {
            if (_activeHarvest is null || _activeHarvest.IsFaulted || _activeHarvest.IsCanceled)
            {
                _activeHarvest = StartHarvestLocked();
            }

            return _activeHarvest;
        }
    }

    /// <summary>
    /// Requests a trusted operator rebuild of the full source-backed docs harvest.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the request decision before any rebuild is queued.</param>
    /// <returns>
    /// <see cref="AppSurfaceDocsHarvestRebuildRequestResult.Started"/> when a fresh rebuild started immediately,
    /// <see cref="AppSurfaceDocsHarvestRebuildRequestResult.Queued"/> when the active run will be followed by one rebuild,
    /// or <see cref="AppSurfaceDocsHarvestRebuildRequestResult.AlreadyQueued"/> when a rebuild was already pending.
    /// </returns>
    /// <remarks>
    /// The shared harvest itself runs with <see cref="CancellationToken.None"/>. Canceling the operator request cannot
    /// cancel a harvest already visible to other docs requests. When a rebuild is queued behind an active run, the
    /// active run's completion visit is suppressed so only the superseding rebuild returns the browser to the verified
    /// docs context.
    /// </remarks>
    public async ValueTask<AppSurfaceDocsHarvestRebuildRequestResult> RequestRebuildAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AppSurfaceDocsHarvestRebuildRequestResult result;
        string? supersededRunId = null;
        lock (_gate)
        {
            if (_activeHarvest is null || _activeHarvest.IsCompleted)
            {
                _pendingRebuild = false;
                _aggregator.InvalidateCache();
                _activeHarvest = StartHarvestLocked();
                return AppSurfaceDocsHarvestRebuildRequestResult.Started;
            }

            if (_pendingRebuild)
            {
                result = AppSurfaceDocsHarvestRebuildRequestResult.AlreadyQueued;
            }
            else
            {
                _pendingRebuild = true;
                supersededRunId = _progress.SuppressCompletionVisitForCurrentOrNextRun();
                _ = RunQueuedRebuildAfterAsync(_activeHarvest);
                result = AppSurfaceDocsHarvestRebuildRequestResult.Queued;
            }
        }

        if (result == AppSurfaceDocsHarvestRebuildRequestResult.Queued)
        {
            await _progress.RebuildQueuedAsync(supersededRunId);
        }

        return result;
    }

    /// <summary>
    /// Waits for the shared initial harvest to complete within the provided wait budget.
    /// </summary>
    /// <param name="waitBudget">The maximum time to wait. Non-positive values return <see langword="false"/> immediately.</param>
    /// <param name="cancellationToken">A token used to cancel the caller's wait operation.</param>
    /// <returns><see langword="true"/> when the harvest task completes within budget; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// The caller's token cancels only the wait, not the shared harvest task. Exceptions from the harvest task are
    /// observed when the task completes before the timeout.
    /// </remarks>
    internal async Task<bool> WaitForCompletionAsync(TimeSpan waitBudget, CancellationToken cancellationToken)
    {
        var task = EnsureStarted();
        if (task.IsCompleted)
        {
            await task.WaitAsync(cancellationToken);
            return true;
        }

        if (waitBudget <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            await task.WaitAsync(waitBudget, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private Task<DocHarvestHealthSnapshot> StartHarvestLocked()
    {
        return Task.Run(
            () => _aggregator.GetHarvestHealthAsync(CancellationToken.None),
            CancellationToken.None);
    }

    private async Task RunQueuedRebuildAfterAsync(Task<DocHarvestHealthSnapshot> activeHarvest)
    {
        try
        {
            await activeHarvest.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatalHarvestException(exception))
        {
            // A failed active run still yields to the queued rebuild. The final failure is already represented by
            // the harvest reporter and health snapshot path.
        }

        lock (_gate)
        {
            if (!_pendingRebuild || !ReferenceEquals(_activeHarvest, activeHarvest))
            {
                return;
            }

            _pendingRebuild = false;
            _aggregator.InvalidateCache();
            _activeHarvest = StartHarvestLocked();
        }
    }

    /// <summary>
    /// Determines whether a queued rebuild continuation should avoid catching an exception that represents process-level
    /// failure rather than recoverable harvest failure.
    /// </summary>
    /// <param name="exception">The exception observed from the active harvest task.</param>
    /// <returns><see langword="true"/> for process-fatal exception types; otherwise <see langword="false"/>.</returns>
    internal static bool IsFatalHarvestException(Exception exception)
    {
        return exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    }
}

/// <summary>
/// Result of a trusted operator request to rebuild the live AppSurface Docs harvest.
/// </summary>
/// <remarks>
/// The default enum value, <c>0</c>, is not a valid rebuild request result. Callers that bind, deserialize, or display
/// unknown values should treat them as "no request result." While a harvest is active, rebuild requests are coalesced so
/// at most one superseding rebuild is queued behind the active run. The numeric member values are part of the public
/// compatibility contract and must not be reordered or renumbered.
/// </remarks>
public enum AppSurfaceDocsHarvestRebuildRequestResult
{
    /// <summary>
    /// The rebuild started immediately because no harvest was running.
    /// </summary>
    Started = 1,

    /// <summary>
    /// A rebuild was queued to start after the current harvest finishes.
    /// </summary>
    /// <remarks>Additional operator requests made while this rebuild is pending coalesce into the same queued run.</remarks>
    Queued = 2,

    /// <summary>
    /// A rebuild was already queued, so no additional work was scheduled.
    /// </summary>
    /// <remarks>The pending queued rebuild already represents all superseding operator requests.</remarks>
    AlreadyQueued = 3
}
