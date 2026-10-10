using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;

namespace DurableWorkerTemplate.Tests;

/// <summary>Captures bounded completed activities from the generated host's OpenTelemetry SDK for assertions.</summary>
/// <remarks>
/// Each test owns a fresh instance attached through <see cref="SimpleActivityExportProcessor"/>; the host's
/// telemetry registration owns source selection and sampling. Export is synchronous and retains at most 16
/// snapshots with 32 tags each. Tag/status strings longer than 512 characters and nonprimitive values become placeholders.
/// Do not print retained values: bounded text can still contain secrets. The SDK owns exporter disposal; this
/// exporter never owns or disposes the shared activity source. Flush observes SDK delivery, not remote ingestion.
/// </remarks>
internal sealed class ActivityExporter : BaseExporter<Activity>
{
    private const int MaximumActivities = 16;
    private const int MaximumTagsPerActivity = 32;
    private const int MaximumTagTextLength = 512;
    private readonly ConcurrentQueue<ActivitySnapshot> _activities = new();
    private int _exportCount;
    private int _flushCount;

    /// <summary>Gets a detached array of completed observations, initially empty, in export order.</summary>
    internal IReadOnlyList<ActivitySnapshot> Activities => _activities.ToArray();

    /// <summary>Gets attempted activity exports, including the attempt that exceeds the 16-activity limit.</summary>
    internal int ExportCount => Volatile.Read(ref _exportCount);

    /// <summary>Gets SDK force-flush callbacks observed by this instance, initially zero.</summary>
    internal int FlushCount => Volatile.Read(ref _flushCount);

    /// <summary>Copies completed activity metadata; excess activities or tags fail export rather than dropping silently.</summary>
    /// <param name="batch">SDK-owned activities consumed synchronously without retaining the activity objects.</param>
    /// <returns>Success within the bounds, or failure after any already-captured prefix of the batch.</returns>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        var batchCount = 0;
        foreach (var activity in batch)
        {
            if (Interlocked.Increment(ref _exportCount) > MaximumActivities)
            {
                return ExportResult.Failure;
            }

            var tags = activity.TagObjects.Take(MaximumTagsPerActivity + 1).ToArray();
            if (tags.Length > MaximumTagsPerActivity)
            {
                return ExportResult.Failure;
            }

            var snapshot = tags.ToDictionary(
                static tag => tag.Key,
                static tag => BoundTagValue(tag.Value),
                StringComparer.Ordinal);
            _activities.Enqueue(new ActivitySnapshot(
                activity.Source.Name,
                activity.OperationName,
                activity.Kind,
                activity.Status,
                BoundTagValue(activity.StatusDescription) as string,
                activity.Events.Count(),
                snapshot));
            batchCount++;
        }

        return batchCount <= MaximumActivities ? ExportResult.Success : ExportResult.Failure;
    }

    /// <summary>Records synchronous delivery without starting asynchronous or network work.</summary>
    /// <param name="timeoutMilliseconds">SDK-provided flush budget in milliseconds.</param>
    /// <returns>True only for a positive budget; this exporter has no pending queue to drain.</returns>
    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        Interlocked.Increment(ref _flushCount);
        return timeoutMilliseconds > 0;
    }

    /// <summary>Preserves bounded strings and primitives without invoking arbitrary value formatting.</summary>
    private static object? BoundTagValue(object? value) => value switch
    {
        string text when text.Length <= MaximumTagTextLength => text,
        string => "<tag-text-over-limit>",
        null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double => value,
        _ => $"<{value.GetType().Name}>",
    };
}

/// <summary>Detached metadata for one completed, SDK-sampled activity; no exception or event payload is retained.</summary>
/// <param name="SourceName">Emitting source identity, used to assert the canonical AppSurface source.</param>
/// <param name="OperationName">Completed activity operation name.</param>
/// <param name="Kind">Declared activity kind.</param>
/// <param name="Status">Final status, including Unset when no error was recorded.</param>
/// <param name="StatusDescription">Null by default; bounded status text or an over-limit placeholder.</param>
/// <param name="EventCount">Number of events; zero proves that no exception or other event text was emitted.</param>
/// <param name="Tags">Ordinal tag map containing at most 32 bounded primitive values.</param>
/// <remarks>
/// Captured at Export, not at activity creation. Absence of a snapshot can mean sampling or listener failure;
/// tests must assert both delivery and content. Keep snapshots within the owning test's lifetime and never log values.
/// </remarks>
internal sealed record ActivitySnapshot(
    string SourceName,
    string OperationName,
    ActivityKind Kind,
    ActivityStatusCode Status,
    string? StatusDescription,
    int EventCount,
    IReadOnlyDictionary<string, object?> Tags);
