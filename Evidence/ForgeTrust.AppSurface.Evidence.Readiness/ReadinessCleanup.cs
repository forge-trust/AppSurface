using System.Runtime.ExceptionServices;

namespace ForgeTrust.AppSurface.Evidence.Readiness;

/// <summary>Preserves fatal cleanup leaves before an adapter summarizes a secondary failure.</summary>
/// <remarks>Each adapter supplies its existing fatal-type policy; aggregate traversal does not broaden that policy.</remarks>
internal static class ReadinessCleanup
{
    /// <summary>Finds the first fatal exception in depth-first aggregate order without replacing its identity.</summary>
    /// <param name="exception">The non-null cleanup or diagnostic-storage failure.</param>
    /// <param name="isFatal">The owning adapter's non-null fatal-type classifier.</param>
    /// <returns>The original fatal exception, or null when every leaf is nonfatal.</returns>
    internal static Exception? FindFatal(Exception exception, Func<Exception, bool> isFatal)
    {
        if (isFatal(exception))
        {
            return exception;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (FindFatal(inner, isFatal) is { } fatal)
                {
                    return fatal;
                }
            }
        }

        return null;
    }

    /// <summary>Rethrows an original fatal leaf before a failure can be converted into safe secondary metadata.</summary>
    /// <param name="exception">The non-null owned cleanup or diagnostic-storage failure.</param>
    /// <param name="isFatal">The owning adapter's existing non-null fatal-type classifier.</param>
    internal static void ThrowIfFatal(Exception exception, Func<Exception, bool> isFatal)
    {
        if (FindFatal(exception, isFatal) is { } fatal)
        {
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }
    }
}
