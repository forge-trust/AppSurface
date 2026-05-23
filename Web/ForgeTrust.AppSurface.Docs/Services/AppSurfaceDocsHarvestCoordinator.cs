using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Coordinates the shared initial AppSurface Docs harvest so startup warmup and first requests use the same memoized work.
/// </summary>
public sealed class AppSurfaceDocsHarvestCoordinator
{
    private readonly DocAggregator _aggregator;
    private readonly AppSurfaceDocsHarvestProgressReporter _progress;
    private readonly object _gate = new();
    private Task<DocHarvestHealthSnapshot>? _initialHarvest;

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
            if (_initialHarvest is null || _initialHarvest.IsFaulted || _initialHarvest.IsCanceled)
            {
                _initialHarvest = Task.Run(
                    () => _aggregator.GetHarvestHealthAsync(CancellationToken.None),
                    CancellationToken.None);
            }

            return _initialHarvest;
        }
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
}
