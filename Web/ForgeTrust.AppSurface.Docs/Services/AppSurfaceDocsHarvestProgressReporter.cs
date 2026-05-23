using System.Globalization;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.RazorWire.Streams;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Captures redacted live harvest progress and publishes bounded RazorWire updates for late-subscribing docs pages.
/// </summary>
public sealed class AppSurfaceDocsHarvestProgressReporter
{
    internal const string ChannelName = "appsurfacedocs-harvest";
    private const int MaxActivityCount = 8;
    private const int CompletionDelayMilliseconds = 900;

    private readonly IServiceProvider _services;
    private readonly ILogger<AppSurfaceDocsHarvestProgressReporter> _logger;
    private readonly object _gate = new();
    private AppSurfaceDocsHarvestProgressSnapshot _snapshot = AppSurfaceDocsHarvestProgressSnapshot.Idle;

    /// <summary>
    /// Initializes a new instance of the harvest progress reporter.
    /// </summary>
    /// <param name="services">The service provider used to resolve the optional RazorWire stream hub lazily.</param>
    /// <param name="logger">Logger used when live progress publication fails without failing the harvest.</param>
    public AppSurfaceDocsHarvestProgressReporter(
        IServiceProvider services,
        ILogger<AppSurfaceDocsHarvestProgressReporter> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal AppSurfaceDocsHarvestProgressSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    internal int CompletionDelay => CompletionDelayMilliseconds;

    internal async ValueTask<string> BeginRunAsync(IReadOnlyList<string> harvesterTypes)
    {
        ArgumentNullException.ThrowIfNull(harvesterTypes);

        var runId = Guid.NewGuid().ToString("N");
        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        lock (_gate)
        {
            var harvesters = harvesterTypes
                .Select(type => new AppSurfaceDocsHarvesterProgress(type, "Waiting", 0))
                .ToArray();
            snapshot = new AppSurfaceDocsHarvestProgressSnapshot
            {
                RunId = runId,
                State = AppSurfaceDocsHarvestRunState.Running,
                StartedUtc = DateTimeOffset.UtcNow,
                TotalHarvesters = harvesters.Length,
                Status = "Harvesting",
                Harvesters = harvesters,
                Activity = [new AppSurfaceDocsHarvestActivity(DateTimeOffset.UtcNow, "Harvest started.")]
            };
            _snapshot = snapshot;
        }

        await PublishAsync(snapshot);
        return runId;
    }

    internal ValueTask HarvesterStartedAsync(string runId, string harvesterType)
    {
        return UpdateHarvesterAsync(runId, harvesterType, "Running", 0, $"{FriendlyHarvesterName(harvesterType)} started.");
    }

    internal ValueTask HarvesterCompletedAsync(string runId, string harvesterType, DocHarvesterHealthStatus status, int docCount)
    {
        return UpdateHarvesterAsync(
            runId,
            harvesterType,
            status.ToString(),
            docCount,
            $"{FriendlyHarvesterName(harvesterType)} finished with {docCount.ToString(CultureInfo.InvariantCulture)} docs.");
    }

    internal ValueTask HarvesterDocumentCountUpdatedAsync(string runId, string harvesterType, int docCount)
    {
        return UpdateHarvesterAsync(
            runId,
            harvesterType,
            "Running",
            docCount,
            $"{FriendlyHarvesterName(harvesterType)} processed {docCount.ToString(CultureInfo.InvariantCulture)} docs.");
    }

    internal async ValueTask ActivityAsync(string runId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal))
            {
                return;
            }

            snapshot = _snapshot with
            {
                Activity = AddActivity(_snapshot.Activity, message)
            };
            _snapshot = snapshot;
        }

        await PublishAsync(snapshot);
    }

    internal async ValueTask CompleteRunAsync(string runId, DocHarvestHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);

        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal))
            {
                return;
            }

            snapshot = _snapshot with
            {
                State = health.Status == DocHarvestHealthStatus.Failed
                    ? AppSurfaceDocsHarvestRunState.Failed
                    : AppSurfaceDocsHarvestRunState.Completed,
                CompletedUtc = DateTimeOffset.UtcNow,
                Status = health.Status.ToString(),
                TotalDocs = health.TotalDocs,
                CompletedHarvesters = health.TotalHarvesters,
                Harvesters = health.Harvesters
                    .Select(item => new AppSurfaceDocsHarvesterProgress(item.HarvesterType, item.Status.ToString(), item.DocCount))
                    .ToArray(),
                Diagnostics = health.Diagnostics
                    .Select(AppSurfaceDocsHarvestDiagnosticResponse.FromDiagnostic)
                    .ToArray(),
                Activity = AddActivity(_snapshot.Activity, $"Harvest completed with {health.TotalDocs.ToString(CultureInfo.InvariantCulture)} docs.")
            };
            _snapshot = snapshot;
        }

        await PublishAsync(snapshot);
    }

    private async ValueTask UpdateHarvesterAsync(string runId, string harvesterType, string status, int docCount, string activity)
    {
        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal))
            {
                return;
            }

            var completedDelta = IsTerminalStatus(status) && !_snapshot.Harvesters.Any(
                item => string.Equals(item.HarvesterType, harvesterType, StringComparison.Ordinal)
                        && IsTerminalStatus(item.Status))
                ? 1
                : 0;

            var harvesters = _snapshot.Harvesters
                .Select(item => string.Equals(item.HarvesterType, harvesterType, StringComparison.Ordinal)
                    ? item with { Status = status, DocCount = docCount }
                    : item)
                .ToArray();

            snapshot = _snapshot with
            {
                CompletedHarvesters = Math.Min(_snapshot.TotalHarvesters, _snapshot.CompletedHarvesters + completedDelta),
                TotalDocs = harvesters.Sum(item => item.DocCount),
                Harvesters = harvesters,
                Activity = AddActivity(_snapshot.Activity, activity)
            };
            _snapshot = snapshot;
        }

        await PublishAsync(snapshot);
    }

    private async ValueTask PublishAsync(AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        var hub = _services.GetService<IRazorWireStreamHub>();
        if (hub is null)
        {
            return;
        }

        try
        {
            var message = AppSurfaceDocsHarvestProgressRenderer.RenderTurboStream(
                snapshot,
                returnUrl: "/",
                CompletionDelayMilliseconds);
            await hub.PublishAsync(
                ChannelName,
                message,
                new RazorWireStreamPublishOptions { Replay = true });
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            _logger.LogWarning(ex, "AppSurface Docs harvest progress publish failed.");
        }
    }

    private static IReadOnlyList<AppSurfaceDocsHarvestActivity> AddActivity(
        IReadOnlyList<AppSurfaceDocsHarvestActivity> existing,
        string message)
    {
        return existing
            .Prepend(new AppSurfaceDocsHarvestActivity(DateTimeOffset.UtcNow, message))
            .Take(MaxActivityCount)
            .ToArray();
    }

    private static bool IsTerminalStatus(string status)
    {
        return string.Equals(status, DocHarvesterHealthStatus.Succeeded.ToString(), StringComparison.Ordinal)
               || string.Equals(status, DocHarvesterHealthStatus.ReturnedEmpty.ToString(), StringComparison.Ordinal)
               || string.Equals(status, DocHarvesterHealthStatus.Failed.ToString(), StringComparison.Ordinal)
               || string.Equals(status, DocHarvesterHealthStatus.TimedOut.ToString(), StringComparison.Ordinal)
               || string.Equals(status, DocHarvesterHealthStatus.Canceled.ToString(), StringComparison.Ordinal);
    }

    private static string FriendlyHarvesterName(string harvesterType)
    {
        return harvesterType switch
        {
            nameof(MarkdownHarvester) => "Markdown",
            nameof(CSharpDocHarvester) => "C# API",
            nameof(JavaScriptDocHarvester) => "JavaScript public API",
            _ => harvesterType
        };
    }

    private static bool IsFatalException(Exception exception)
    {
        return exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    }
}
