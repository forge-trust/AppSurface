using System.Diagnostics;

namespace ForgeTrust.AppSurface.Web;

/// <summary>Runs a selected named-canary snapshot with bounded concurrent, cooperative evaluation.</summary>
internal sealed class AppSurfaceCanarySnapshotCoordinator
{
    private readonly AppSurfaceCanaryRegistry _registry;
    private readonly AppSurfaceCanaryEvaluationRunner _runner;

    internal AppSurfaceCanarySnapshotCoordinator(
        AppSurfaceCanaryRegistry registry,
        AppSurfaceCanaryEvaluationRunner runner)
    {
        _registry = registry;
        _runner = runner;
    }

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

    internal bool ContainsAllNames(IReadOnlyCollection<string> names) =>
        _registry.ContainsAllNames(names);

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

        var results = new AppSurfaceCanarySnapshotItem?[descriptors.Count];
        var tasks = descriptors
            .Select((descriptor, index) => EvaluateOneAsync(
                descriptor,
                index,
                marker,
                freshSince,
                options.PerCheckTimeout,
                options.OverallTimeout,
                concurrency,
                overallCancellation.Token,
                requestAborted,
                operationCancellation.Token,
                results))
            .ToArray();

        await Task.WhenAll(tasks);
        var settled = results
            .Select((result, index) => result ?? AppSurfaceCanarySnapshotItem.NotStarted(descriptors[index].Name))
            .ToArray();
        return new AppSurfaceCanarySnapshotOutcome(
            settled,
            settled.Any(item => item.ReasonCode is "ASCAN303" or "ASCAN304"));
    }

    private async Task EvaluateOneAsync(
        AppSurfaceCanaryDescriptor descriptor,
        int index,
        string? marker,
        DateTimeOffset? freshSince,
        TimeSpan perCheckTimeout,
        TimeSpan overallTimeout,
        SemaphoreSlim concurrency,
        CancellationToken overallCancellation,
        CancellationToken requestAborted,
        CancellationToken operationCancellation,
        AppSurfaceCanarySnapshotItem?[] results)
    {
        try
        {
            await concurrency.WaitAsync(operationCancellation);
        }
        catch (OperationCanceledException) when (overallCancellation.IsCancellationRequested && !requestAborted.IsCancellationRequested)
        {
            results[index] = AppSurfaceCanarySnapshotItem.NotStarted(descriptor.Name);
            return;
        }

        try
        {
            if (requestAborted.IsCancellationRequested)
            {
                throw new OperationCanceledException(requestAborted);
            }

            if (overallCancellation.IsCancellationRequested)
            {
                results[index] = AppSurfaceCanarySnapshotItem.NotStarted(descriptor.Name);
                return;
            }

            using var perCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation);
            if (perCheckTimeout < overallTimeout)
            {
                perCheckCancellation.CancelAfter(perCheckTimeout);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await _runner.EvaluateAsync(descriptor, marker, freshSince, perCheckCancellation.Token);
                if (requestAborted.IsCancellationRequested)
                {
                    throw new OperationCanceledException(requestAborted);
                }

                if (overallCancellation.IsCancellationRequested)
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
            catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (perCheckCancellation.IsCancellationRequested && !requestAborted.IsCancellationRequested)
            {
                results[index] = overallCancellation.IsCancellationRequested
                    ? AppSurfaceCanarySnapshotItem.OverallTimedOut(descriptor.Name)
                    : AppSurfaceCanarySnapshotItem.PerCheckTimedOut(descriptor.Name);
            }
            catch (Exception exception) when (AppSurfaceCanaryEndpointRouteBuilderExtensions.IsNonFatalEvaluationFailure(exception))
            {
                results[index] = AppSurfaceCanarySnapshotItem.Failed(descriptor.Name);
            }
        }
        finally
        {
            concurrency.Release();
        }
    }
}

/// <summary>Represents the settled, ordered result of one bounded snapshot request.</summary>
internal sealed class AppSurfaceCanarySnapshotOutcome
{
    internal AppSurfaceCanarySnapshotOutcome(IReadOnlyList<AppSurfaceCanarySnapshotItem> items, bool overallTimedOut)
    {
        Items = items;
        OverallTimedOut = overallTimedOut;
    }

    internal IReadOnlyList<AppSurfaceCanarySnapshotItem> Items { get; }

    internal bool OverallTimedOut { get; }

    internal bool Ready => !OverallTimedOut && Items.All(item => item.Ready);
}

/// <summary>Represents one privacy-safe aggregate item outcome.</summary>
internal sealed class AppSurfaceCanarySnapshotItem(
    string name,
    string outcome,
    AppSurfaceCanaryResult? result,
    string? reasonCode,
    double? elapsedMilliseconds)
{
    internal string Name { get; } = name;
    internal string Outcome { get; } = outcome;
    internal AppSurfaceCanaryResult? Result { get; } = result;
    internal string? ReasonCode { get; } = reasonCode;
    internal double? ElapsedMilliseconds { get; } = elapsedMilliseconds;
    internal bool Ready => Result?.Status == AppSurfaceCanaryStatus.Pass && Outcome == "completed";

    internal static AppSurfaceCanarySnapshotItem Completed(string name, AppSurfaceCanaryResult result, double elapsedMilliseconds) =>
        new(name, "completed", result, null, elapsedMilliseconds);

    internal static AppSurfaceCanarySnapshotItem Failed(string name) =>
        new(name, "failed", null, "ASCAN301", null);

    internal static AppSurfaceCanarySnapshotItem PerCheckTimedOut(string name) =>
        new(name, "timed-out", null, "ASCAN302", null);

    internal static AppSurfaceCanarySnapshotItem OverallTimedOut(string name) =>
        new(name, "timed-out", null, "ASCAN303", null);

    internal static AppSurfaceCanarySnapshotItem NotStarted(string name) =>
        new(name, "not-started", null, "ASCAN304", null);
}
