using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;

namespace DurableWorkerTemplate.Tests;

internal sealed class ActivityExporter : BaseExporter<Activity>
{
    private const int MaximumActivities = 16;
    private const int MaximumTagsPerActivity = 32;
    private const int MaximumTagTextLength = 512;
    private readonly ConcurrentQueue<ActivitySnapshot> _activities = new();
    private int _exportCount;
    private int _flushCount;

    internal IReadOnlyList<ActivitySnapshot> Activities => _activities.ToArray();

    internal int ExportCount => Volatile.Read(ref _exportCount);

    internal int FlushCount => Volatile.Read(ref _flushCount);

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
                activity.Events.Count(),
                snapshot));
            batchCount++;
        }

        return batchCount <= MaximumActivities ? ExportResult.Success : ExportResult.Failure;
    }

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        Interlocked.Increment(ref _flushCount);
        return timeoutMilliseconds > 0;
    }

    private static object? BoundTagValue(object? value) => value switch
    {
        string text when text.Length <= MaximumTagTextLength => text,
        string => "<tag-text-over-limit>",
        null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double => value,
        _ => $"<{value.GetType().Name}>",
    };
}

internal sealed record ActivitySnapshot(
    string SourceName,
    string OperationName,
    ActivityKind Kind,
    ActivityStatusCode Status,
    int EventCount,
    IReadOnlyDictionary<string, object?> Tags);
