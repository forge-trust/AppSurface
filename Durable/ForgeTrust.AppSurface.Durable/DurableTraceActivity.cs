using System.Diagnostics;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Creates durable runtime activities as new roots with an optional committed-cause link.</summary>
internal static class DurableTraceActivity
{
    internal static DurableTraceActivityScope StartRoot(
        string operationName,
        ActivityKind kind,
        DurableTraceContext? committedCause)
    {
        var links = committedCause is null
            ? null
            : new[] { new ActivityLink(committedCause.ToActivityContext()) };

        if (Activity.Current is null)
        {
            return new DurableTraceActivityScope(Start(operationName, kind, links), disposeOnThreadPool: false);
        }

        // ActivitySource uses Activity.Current when no explicit parent is supplied. A durable resume is a new
        // execution, not a child of the caller that happened to invoke the processor. Start and dispose the root on
        // a ThreadPool callback whose ExecutionContext deliberately does not contain the caller's ambient Activity.
        // This exceptional path keeps the returned Activity unparented and leaves the caller's ambient Activity
        // unchanged; hosted processors normally take the direct path above.
        var start = new RootActivityStart(operationName, kind, links);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Start(),
            start,
            preferLocal: false);
        return new DurableTraceActivityScope(
            start.Completion.Task.GetAwaiter().GetResult(),
            disposeOnThreadPool: true);
    }

    private static Activity? Start(
        string operationName,
        ActivityKind kind,
        IReadOnlyList<ActivityLink>? links) =>
        AppSurfaceActivitySources.Instance.StartActivity(
            operationName,
            kind,
            default(ActivityContext),
            links: links);

    private sealed class RootActivityStart(
        string operationName,
        ActivityKind kind,
        IReadOnlyList<ActivityLink>? links)
    {
        internal TaskCompletionSource<Activity?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Start()
        {
            try
            {
                Completion.SetResult(DurableTraceActivity.Start(operationName, kind, links));
            }
            catch (Exception exception)
            {
                Completion.SetException(exception);
            }
        }
    }
}

/// <summary>Owns a durable root activity and preserves any caller ambient activity when it is disposed.</summary>
/// <remarks>
/// When a caller has an ambient activity, .NET's <see cref="ActivitySource"/> has no root-start overload that both
/// bypasses that parent and preserves it for the caller. This scope therefore starts and disposes the Activity on a
/// ThreadPool callback without the caller's execution context. Consumers can add the fixed durable telemetry tags
/// through <see cref="Activity"/>, but must dispose this scope rather than the exposed activity.
/// </remarks>
internal sealed class DurableTraceActivityScope : IDisposable
{
    private readonly Activity? _activity;
    private readonly bool _disposeOnThreadPool;
    private int _disposed;

    internal DurableTraceActivityScope(Activity? activity, bool disposeOnThreadPool)
    {
        _activity = activity;
        _disposeOnThreadPool = disposeOnThreadPool;
    }

    /// <summary>Gets the short-lived root activity, or <see langword="null"/> when no listener sampled it.</summary>
    internal Activity? Activity => _activity;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _activity is null)
        {
            return;
        }

        if (!_disposeOnThreadPool)
        {
            _activity.Dispose();
            return;
        }

        var stop = new RootActivityStop(_activity);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Stop(),
            stop,
            preferLocal: false);
        stop.Completion.Task.GetAwaiter().GetResult();
    }

    private sealed class RootActivityStop(Activity activity)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Stop()
        {
            try
            {
                activity.Dispose();
                Completion.SetResult();
            }
            catch (Exception exception)
            {
                Completion.SetException(exception);
            }
        }
    }
}
