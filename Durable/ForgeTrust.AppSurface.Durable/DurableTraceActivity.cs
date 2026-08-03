using System.Diagnostics;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Creates durable runtime activities as new roots with an optional committed-cause link.</summary>
internal static class DurableTraceActivity
{
    internal static Activity? StartRoot(
        string operationName,
        ActivityKind kind,
        DurableTraceContext? committedCause)
    {
        var links = committedCause is null
            ? null
            : new[] { new ActivityLink(committedCause.ToActivityContext()) };

        if (Activity.Current is null)
        {
            return Start(operationName, kind, links);
        }

        // ActivitySource uses Activity.Current when the caller has an ambient activity. A durable resume is a new
        // execution, not a child of the caller that happened to invoke the processor. Queue the StartActivity call
        // without flowing ExecutionContext so the ActivitySource owns normal StartActivity/Dispose lifetime handling
        // without assigning Activity.Current directly. This path is exceptional: hosted processors normally have no
        // ambient activity, in which case the direct path above is used.
        var start = new RootActivityStart(operationName, kind, links);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Start(),
            start,
            preferLocal: false);
        return start.Completion.Task.GetAwaiter().GetResult();
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
