using System.Diagnostics;

namespace ForgeTrust.AppSurface.Web;

/// <summary>Runs a selected named-canary snapshot with bounded concurrent, cooperative evaluation.</summary>
internal sealed class AppSurfaceCanarySnapshotCoordinator
{
    private readonly AppSurfaceCanaryRegistry _registry;
    private readonly AppSurfaceCanaryEvaluationRunner _runner;

    /// <summary>
    /// Initializes a coordinator over the immutable registry and the evaluator runner that resolves registered services.
    /// </summary>
    /// <param name="registry">The immutable named-canary registry used for selection.</param>
    /// <param name="runner">The runner used to invoke a selected evaluator.</param>
    internal AppSurfaceCanarySnapshotCoordinator(
        AppSurfaceCanaryRegistry registry,
        AppSurfaceCanaryEvaluationRunner runner)
    {
        _registry = registry;
        _runner = runner;
    }

    /// <summary>
    /// Selects registered descriptors by exact name or tag and reports whether the selected set fits the supplied limit.
    /// </summary>
    /// <param name="names">The distinct exact names requested by the caller.</param>
    /// <param name="tags">The distinct durable tags requested by the caller.</param>
    /// <param name="maximum">The maximum number of selected descriptors permitted for the snapshot.</param>
    /// <param name="selected">
    /// The ordinally ordered selected descriptors. This value is assigned even when the method returns
    /// <see langword="false"/> because the selection exceeded <paramref name="maximum"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="selected"/> contains no more than <paramref name="maximum"/>
    /// descriptors; otherwise <see langword="false"/>. Empty <paramref name="names"/> and <paramref name="tags"/>
    /// select every registered canary.
    /// </returns>
    internal bool TrySelect(
        IReadOnlyCollection<string> names,
        IReadOnlyCollection<string> tags,
        int maximum,
        out IReadOnlyList<AppSurfaceCanaryDescriptor> selected)
    {
        var requestedNames = names.ToHashSet(StringComparer.Ordinal);
        var requestedTags = tags.ToHashSet(StringComparer.Ordinal);
        selected = _registry.OrderedDescriptors
            .Where(descriptor =>
                (requestedNames.Count == 0 && requestedTags.Count == 0)
                || requestedNames.Contains(descriptor.Name)
                || descriptor.Tags.Overlaps(requestedTags))
            .ToArray();
        return selected.Count <= maximum;
    }

    /// <summary>Determines whether every exact name selector is registered without resolving evaluators.</summary>
    /// <param name="names">The exact names to check.</param>
    /// <returns><see langword="true"/> when every name is registered; otherwise <see langword="false"/>.</returns>
    internal bool ContainsAllNames(IReadOnlyCollection<string> names) =>
        _registry.ContainsAllNames(names);

    /// <summary>Evaluates the supplied ordinal descriptor selection with the configured bounded snapshot policy.</summary>
    /// <param name="descriptors">The already selected descriptors, in their required output order.</param>
    /// <param name="marker">The validated optional marker forwarded to every selected evaluator.</param>
    /// <param name="freshSince">The validated optional freshness boundary forwarded to every selected evaluator.</param>
    /// <param name="options">The immutable-at-mapping host snapshot policy.</param>
    /// <param name="requestAborted">The request cancellation token that must be propagated without conversion.</param>
    /// <returns>A settled aggregate containing one privacy-safe item for every supplied descriptor.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="requestAborted"/> was canceled.</exception>
    /// <remarks>
    /// The method waits for every started evaluator to settle. An evaluator that ignores cooperative cancellation delays
    /// the response but cannot contribute a successful result after either deadline expires.
    /// </remarks>
    internal async Task<AppSurfaceCanarySnapshotOutcome> EvaluateAsync(
        IReadOnlyList<AppSurfaceCanaryDescriptor> descriptors,
        string? marker,
        DateTimeOffset? freshSince,
        AppSurfaceCanarySnapshotOptions options,
        CancellationToken requestAborted)
    {
        using var overallCancellation = new CancellationTokenSource(options.OverallTimeout);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted,
            overallCancellation.Token);
        using var concurrency = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        var context = new AppSurfaceCanarySnapshotContext(
            PerCheckTimeout: options.PerCheckTimeout,
            OverallTimeout: options.OverallTimeout,
            Concurrency: concurrency,
            OverallCancellation: overallCancellation.Token,
            RequestAborted: requestAborted,
            OperationCancellation: operationCancellation.Token);

        var results = new AppSurfaceCanarySnapshotItem?[descriptors.Count];
        var tasks = descriptors
            .Select((descriptor, index) => EvaluateOneAsync(
                descriptor,
                index,
                marker,
                freshSince,
                context,
                results))
            .ToArray();

        await Task.WhenAll(tasks);
        var settled = results
            .Select((result, index) => result ?? AppSurfaceCanarySnapshotItem.NotStarted(descriptors[index].Name))
            .ToArray();
        return new AppSurfaceCanarySnapshotOutcome(
            settled,
            settled.Any(item => item.BoundedByOverallDeadline));
    }

    private async Task EvaluateOneAsync(
        AppSurfaceCanaryDescriptor descriptor,
        int index,
        string? marker,
        DateTimeOffset? freshSince,
        AppSurfaceCanarySnapshotContext context,
        AppSurfaceCanarySnapshotItem?[] results)
    {
        try
        {
            await context.Concurrency.WaitAsync(context.OperationCancellation);
        }
        catch (OperationCanceledException) when (context.OverallCancellation.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            results[index] = AppSurfaceCanarySnapshotItem.NotStarted(descriptor.Name);
            return;
        }

        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();

            if (context.OverallCancellation.IsCancellationRequested)
            {
                results[index] = AppSurfaceCanarySnapshotItem.NotStarted(descriptor.Name);
                return;
            }

            using var perCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.OperationCancellation);
            if (context.PerCheckTimeout < context.OverallTimeout)
            {
                perCheckCancellation.CancelAfter(context.PerCheckTimeout);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await _runner.EvaluateAsync(descriptor, marker, freshSince, perCheckCancellation.Token);
                context.RequestAborted.ThrowIfCancellationRequested();

                if (context.OverallCancellation.IsCancellationRequested)
                {
                    results[index] = AppSurfaceCanarySnapshotItem.OverallTimedOut(descriptor.Name);
                }
                else if (perCheckCancellation.IsCancellationRequested)
                {
                    results[index] = AppSurfaceCanarySnapshotItem.PerCheckTimedOut(descriptor.Name);
                }
                else
                {
                    results[index] = AppSurfaceCanarySnapshotItem.Completed(
                        descriptor.Name,
                        result,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (perCheckCancellation.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                results[index] = context.OverallCancellation.IsCancellationRequested
                    ? AppSurfaceCanarySnapshotItem.OverallTimedOut(descriptor.Name)
                    : AppSurfaceCanarySnapshotItem.PerCheckTimedOut(descriptor.Name);
            }
            catch (Exception exception) when (AppSurfaceCanaryEvaluationFailurePolicy.IsNonFatal(exception))
            {
                results[index] = AppSurfaceCanarySnapshotItem.Failed(descriptor.Name);
            }
        }
        finally
        {
            context.Concurrency.Release();
        }
    }

    private readonly record struct AppSurfaceCanarySnapshotContext(
        TimeSpan PerCheckTimeout,
        TimeSpan OverallTimeout,
        SemaphoreSlim Concurrency,
        CancellationToken OverallCancellation,
        CancellationToken RequestAborted,
        CancellationToken OperationCancellation);
}

/// <summary>Represents the settled, ordered result of one bounded snapshot request.</summary>
internal sealed class AppSurfaceCanarySnapshotOutcome
{
    /// <summary>Initializes the settled aggregate and its overall-deadline projection.</summary>
    /// <param name="items">The settled items in descriptor ordinal order.</param>
    /// <param name="overallTimedOut">Whether the overall deadline prevented completion of one or more items.</param>
    internal AppSurfaceCanarySnapshotOutcome(IReadOnlyList<AppSurfaceCanarySnapshotItem> items, bool overallTimedOut)
    {
        Items = items;
        OverallTimedOut = overallTimedOut;
    }

    /// <summary>Gets one settled privacy-safe item for each selected descriptor, in ordinal order.</summary>
    internal IReadOnlyList<AppSurfaceCanarySnapshotItem> Items { get; }

    /// <summary>Gets whether the overall deadline timed out a started or queued evaluator.</summary>
    internal bool OverallTimedOut { get; }

    /// <summary>Gets whether every selected item completed successfully before the overall deadline.</summary>
    internal bool Ready => !OverallTimedOut && Items.All(item => item.Ready);
}

/// <summary>Represents one privacy-safe aggregate item outcome.</summary>
internal sealed class AppSurfaceCanarySnapshotItem
{
    /// <summary>Initializes one settled item and its optional completed evidence.</summary>
    /// <param name="name">The exact registered canary name.</param>
    /// <param name="state">The typed internal settlement state.</param>
    /// <param name="result">The evaluator result when the item completed; otherwise <see langword="null"/>.</param>
    /// <param name="elapsedMilliseconds">The elapsed evaluator time when the item completed; otherwise <see langword="null"/>.</param>
    private AppSurfaceCanarySnapshotItem(
        string name,
        AppSurfaceCanarySnapshotItemState state,
        AppSurfaceCanaryResult? result,
        double? elapsedMilliseconds)
    {
        Name = name;
        State = state;
        Result = result;
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    /// <summary>Gets the exact registered canary name.</summary>
    internal string Name { get; }

    /// <summary>Gets the typed settlement state used for internal aggregation.</summary>
    internal AppSurfaceCanarySnapshotItemState State { get; }

    /// <summary>Gets the stable response-envelope outcome projected from <see cref="State"/>.</summary>
    internal string Outcome => State switch
    {
        AppSurfaceCanarySnapshotItemState.Completed => "completed",
        AppSurfaceCanarySnapshotItemState.Failed => "failed",
        AppSurfaceCanarySnapshotItemState.PerCheckTimedOut or AppSurfaceCanarySnapshotItemState.OverallTimedOut => "timed-out",
        AppSurfaceCanarySnapshotItemState.NotStarted => "not-started",
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "The snapshot item state must be defined."),
    };

    /// <summary>Gets the safe non-completed diagnostic projected from <see cref="State"/>, when applicable.</summary>
    internal string? ReasonCode => State switch
    {
        AppSurfaceCanarySnapshotItemState.Completed => null,
        AppSurfaceCanarySnapshotItemState.Failed => "ASCAN301",
        AppSurfaceCanarySnapshotItemState.PerCheckTimedOut => "ASCAN302",
        AppSurfaceCanarySnapshotItemState.OverallTimedOut => "ASCAN303",
        AppSurfaceCanarySnapshotItemState.NotStarted => "ASCAN304",
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "The snapshot item state must be defined."),
    };

    /// <summary>Gets the completed evaluator result; otherwise <see langword="null"/>.</summary>
    internal AppSurfaceCanaryResult? Result { get; }

    /// <summary>Gets the completed evaluator elapsed time in milliseconds; otherwise <see langword="null"/>.</summary>
    internal double? ElapsedMilliseconds { get; }

    /// <summary>Gets whether the overall deadline settled this item before it completed.</summary>
    internal bool BoundedByOverallDeadline => State is AppSurfaceCanarySnapshotItemState.OverallTimedOut or AppSurfaceCanarySnapshotItemState.NotStarted;

    /// <summary>Gets whether the item completed with a passing evaluator result.</summary>
    internal bool Ready => State == AppSurfaceCanarySnapshotItemState.Completed && Result?.Status == AppSurfaceCanaryStatus.Pass;

    /// <summary>Creates a completed item containing the evaluator result.</summary>
    internal static AppSurfaceCanarySnapshotItem Completed(string name, AppSurfaceCanaryResult result, double elapsedMilliseconds) =>
        new(name, AppSurfaceCanarySnapshotItemState.Completed, result, elapsedMilliseconds);

    /// <summary>Creates a redacted evaluator-failure item.</summary>
    internal static AppSurfaceCanarySnapshotItem Failed(string name) =>
        new(name, AppSurfaceCanarySnapshotItemState.Failed, null, null);

    /// <summary>Creates an item that exceeded its per-check deadline.</summary>
    internal static AppSurfaceCanarySnapshotItem PerCheckTimedOut(string name) =>
        new(name, AppSurfaceCanarySnapshotItemState.PerCheckTimedOut, null, null);

    /// <summary>Creates a started item that was canceled by the overall deadline.</summary>
    internal static AppSurfaceCanarySnapshotItem OverallTimedOut(string name) =>
        new(name, AppSurfaceCanarySnapshotItemState.OverallTimedOut, null, null);

    /// <summary>Creates an item that was never admitted before the overall deadline.</summary>
    internal static AppSurfaceCanarySnapshotItem NotStarted(string name) =>
        new(name, AppSurfaceCanarySnapshotItemState.NotStarted, null, null);
}

/// <summary>Defines the typed internal settlement states for a snapshot item.</summary>
internal enum AppSurfaceCanarySnapshotItemState
{
    /// <summary>The evaluator completed and returned a result.</summary>
    Completed,

    /// <summary>The evaluator failed with a non-fatal failure.</summary>
    Failed,

    /// <summary>A started evaluator exceeded its per-check deadline.</summary>
    PerCheckTimedOut,

    /// <summary>A started evaluator was canceled by the overall snapshot deadline.</summary>
    OverallTimedOut,

    /// <summary>The overall deadline elapsed before the evaluator acquired a concurrency slot.</summary>
    NotStarted,
}
