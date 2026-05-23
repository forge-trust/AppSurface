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

    internal AppSurfaceDocsHarvestProgressSnapshot CurrentProgress => _progress.CurrentSnapshot;

    internal int CompletionDelay => _progress.CompletionDelay;

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
