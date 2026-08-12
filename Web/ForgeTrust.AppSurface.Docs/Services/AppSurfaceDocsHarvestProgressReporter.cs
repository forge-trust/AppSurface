using System.Globalization;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.RazorWire.Bridge;
using ForgeTrust.RazorWire.Streams;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Associates one configured harvester instance with its redacted display name.
/// </summary>
/// <param name="ProgressId">Unique, server-only callback identity for the configured harvester instance.</param>
/// <param name="HarvesterType">Redacted concrete type name shown to the operator.</param>
/// <param name="IsBuiltInProgressHarvester">
/// Whether the registration belongs to a package-owned parser that reports detailed progress.
/// </param>
internal sealed record AppSurfaceDocsHarvesterRegistration(
    string ProgressId,
    string HarvesterType,
    bool IsBuiltInProgressHarvester = false)
{
    /// <summary>
    /// Creates registrations with unique server-only identities for the supplied harvester type names.
    /// </summary>
    /// <param name="harvesterTypes">The redacted harvester type names to register.</param>
    /// <returns>Registrations that preserve input order and distinguish repeated type names.</returns>
    internal static IReadOnlyList<AppSurfaceDocsHarvesterRegistration> Create(
        IReadOnlyList<string> harvesterTypes)
    {
        ArgumentNullException.ThrowIfNull(harvesterTypes);

        var duplicateTypes = harvesterTypes
            .GroupBy(harvesterType => harvesterType, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return harvesterTypes
            .Select(
                (harvesterType, index) => new AppSurfaceDocsHarvesterRegistration(
                    duplicateTypes.Contains(harvesterType)
                        ? $"harvester-{index.ToString(CultureInfo.InvariantCulture)}"
                        : harvesterType,
                    harvesterType))
            .ToArray();
    }
}

/// <summary>
/// Captures redacted live harvest progress and publishes bounded RazorWire updates for late-subscribing docs pages.
/// </summary>
public sealed class AppSurfaceDocsHarvestProgressReporter
{
    internal const string ChannelName = AppSurfaceDocsStreamAuthorization.HarvestProgressChannel;
    private const int MaxActivityCount = 8;
    private const int CompletionDelayMilliseconds = 900;
    private const int RateSampleCapacity = 9;
    private static readonly TimeSpan OrdinaryPublishInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PublicationRetryInterval = TimeSpan.FromSeconds(1);
    private const string QueuedRebuildStatus = "Harvesting (rebuild queued)";
    private const string QueuedRebuildActivity = "A rebuild is queued and will start after this run finishes.";
    // Resolve per subscriber so a shared harvest channel revisits each user's requested docs URL.
    private const string CurrentPageVisitUrl = "#";

    private readonly IServiceProvider _services;
    private readonly ILogger<AppSurfaceDocsHarvestProgressReporter> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly HashSet<string> _completionVisitSuppressedRunIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _progressInstrumentedHarvesterIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HarvesterTelemetry> _harvesterTelemetry = new(StringComparer.Ordinal);
    private readonly RateSample?[] _rateSamples = new RateSample?[RateSampleCapacity];
    private AppSurfaceDocsHarvestProgressSnapshot _snapshot = AppSurfaceDocsHarvestProgressSnapshot.Idle;
    private CancellationTokenSource? _ordinaryPublishCancellation;
    private CancellationTokenSource? _publicationRetryCancellation;
    private ProgressPublication? _pendingPublicationRetry;
    private ProgressPublication? _pendingBackgroundPublication;
    private bool _pendingCompletionVisitRetry;
    private bool _pendingBackgroundCompletionVisit;
    private bool _backgroundPublicationInFlight;
    private bool _suppressNextCompletionVisit;
    private bool _recordQueuedRebuildForNextRun;
    private bool _hasProgressEvidence;
    private int _rateSampleNextIndex;
    private long _generation;
    private long _revision;
    private long _lastPublishedGeneration;
    private long _lastPublishedRevision;
    private string? _queuedRebuildRunId;

    private readonly record struct RateSample(long Timestamp, int DocumentCount);

    private sealed class HarvesterTelemetry
    {
        public int DocumentCount { get; set; }

        public long SourceUnitsProcessed { get; set; }
    }

    private readonly record struct ProgressPublication(
        AppSurfaceDocsHarvestProgressSnapshot Snapshot,
        long Generation,
        long Revision);

    /// <summary>
    /// Initializes a new instance of the harvest progress reporter.
    /// </summary>
    /// <param name="services">The service provider used to resolve the optional RazorWire stream hub lazily.</param>
    /// <param name="logger">Logger used when live progress publication fails without failing the harvest.</param>
    /// <param name="timeProvider">Monotonic time source used for bounded publication and rolling-rate measurement.</param>
    public AppSurfaceDocsHarvestProgressReporter(
        IServiceProvider services,
        ILogger<AppSurfaceDocsHarvestProgressReporter> logger,
        TimeProvider? timeProvider = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the latest redacted harvest progress snapshot.
    /// </summary>
    /// <remarks>
    /// Access is synchronized through the reporter gate so callers receive a consistent snapshot instance. The returned
    /// record is immutable by convention through init-only properties.
    /// </remarks>
    internal AppSurfaceDocsHarvestProgressSnapshot CurrentSnapshot
    {
        get
        {
            lock (_gate)
            {
                return MaterializeTelemetry_NoLock(_snapshot);
            }
        }
    }

    /// <summary>
    /// Gets the client-side completion navigation delay in milliseconds.
    /// </summary>
    internal int CompletionDelay => CompletionDelayMilliseconds;

    /// <summary>
    /// Gets the time source shared with run-scoped test pacing and rate measurement.
    /// </summary>
    internal TimeProvider TimeProvider => _timeProvider;

    /// <summary>
    /// Gets the number of run identifiers whose terminal completion visit is currently suppressed.
    /// </summary>
    /// <remarks>
    /// This test seam keeps suppression cleanup verifiable without exposing the mutable set or using reflection. Production
    /// callers should use <see cref="SuppressCompletionVisitForCurrentOrNextRun"/> instead of observing this count.
    /// </remarks>
    internal int SuppressedCompletionVisitCount
    {
        get
        {
            lock (_gate)
            {
                return _completionVisitSuppressedRunIds.Count;
            }
        }
    }

    /// <summary>
    /// Begins a new harvest run and publishes the initial waiting snapshot.
    /// </summary>
    /// <param name="harvesterTypes">The redacted harvester type names expected in the run.</param>
    /// <returns>The generated run identifier used to correlate later progress callbacks.</returns>
    /// <remarks>
    /// The snapshot update is protected by the reporter gate and live publication is scheduled after the lock is
    /// released. Parser execution never waits for a stream hub response. Because this overload only receives display
    /// names, its registrations remain status-only; <see cref="BeginRunAsync(IReadOnlyList{AppSurfaceDocsHarvesterRegistration})"/>
    /// carries the trusted package-owned parser marker. Passing <see langword="null"/> throws
    /// <see cref="ArgumentNullException"/>.
    /// </remarks>
    internal ValueTask<string> BeginRunAsync(IReadOnlyList<string> harvesterTypes)
    {
        return BeginRunAsync(AppSurfaceDocsHarvesterRegistration.Create(harvesterTypes));
    }

    /// <summary>
    /// Begins a new harvest run with stable, server-only identities for each configured harvester instance.
    /// </summary>
    /// <param name="harvesterRegistrations">Redacted display names paired with unique callback identities.</param>
    /// <returns>The generated run identifier used to correlate later progress callbacks.</returns>
    internal ValueTask<string> BeginRunAsync(IReadOnlyList<AppSurfaceDocsHarvesterRegistration> harvesterRegistrations)
    {
        ArgumentNullException.ThrowIfNull(harvesterRegistrations);
        if (harvesterRegistrations.Any(registration => string.IsNullOrWhiteSpace(registration.ProgressId)
                                                        || string.IsNullOrWhiteSpace(registration.HarvesterType)))
        {
            throw new ArgumentException("Harvester registrations require non-blank progress IDs and type names.", nameof(harvesterRegistrations));
        }

        if (harvesterRegistrations.Select(registration => registration.ProgressId).Distinct(StringComparer.Ordinal).Count()
            != harvesterRegistrations.Count)
        {
            throw new ArgumentException("Harvester progress IDs must be unique within a run.", nameof(harvesterRegistrations));
        }

        var runId = Guid.NewGuid().ToString("N");
        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        ProgressPublication publication;
        lock (_gate)
        {
            CancelOrdinaryPublish_NoLock();
            CancelPublicationRetry_NoLock();
            _generation++;
            _revision = 0;
            _hasProgressEvidence = false;
            _rateSampleNextIndex = 0;
            Array.Clear(_rateSamples);
            _progressInstrumentedHarvesterIds.Clear();
            _harvesterTelemetry.Clear();
            var harvesters = harvesterRegistrations
                .Select(
                    registration => new AppSurfaceDocsHarvesterProgress(registration.HarvesterType, "Waiting", 0)
                    {
                        ProgressId = registration.ProgressId,
                        IsBuiltInProgressHarvester = registration.IsBuiltInProgressHarvester
                    })
                .ToArray();
            foreach (var harvester in harvesters)
            {
                _harvesterTelemetry.TryAdd(harvester.ProgressId, new HarvesterTelemetry());
            }
            if (_suppressNextCompletionVisit)
            {
                _completionVisitSuppressedRunIds.Add(runId);
                _suppressNextCompletionVisit = false;
            }

            var status = "Harvesting";
            IReadOnlyList<AppSurfaceDocsHarvestActivity> activity =
                [new AppSurfaceDocsHarvestActivity(DateTimeOffset.UtcNow, "Harvest started.")];
            if (_recordQueuedRebuildForNextRun)
            {
                status = QueuedRebuildStatus;
                activity = AddActivity(activity, QueuedRebuildActivity);
                _queuedRebuildRunId = runId;
                _recordQueuedRebuildForNextRun = false;
            }

            snapshot = new AppSurfaceDocsHarvestProgressSnapshot
            {
                RunId = runId,
                State = AppSurfaceDocsHarvestRunState.Running,
                StartedUtc = DateTimeOffset.UtcNow,
                TotalHarvesters = harvesters.Length,
                Status = status,
                Harvesters = harvesters,
                Activity = activity
            };
            AddRateSample_NoLock(documentCount: 0);
            _snapshot = snapshot;
            publication = CreatePublication_NoLock(snapshot);
        }

        PublishInBackground(publication);
        return ValueTask.FromResult(runId);
    }

    /// <summary>
    /// Suppresses the terminal browser visit for the active run, or for the next run if the coordinator has scheduled
    /// work before the run has published its identifier.
    /// </summary>
    /// <returns>
    /// The run identifier that was suppressed, or <see langword="null"/> when suppression was deferred to the next run.
    /// </returns>
    /// <remarks>
    /// A run can be terminal in the snapshot while its asynchronous completion publish is still in progress. Completed
    /// snapshots are therefore still eligible for suppression so a queued rebuild cannot race with a stale terminal visit.
    /// </remarks>
    internal string? SuppressCompletionVisitForCurrentOrNextRun()
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_snapshot.RunId))
            {
                if (_snapshot.State is AppSurfaceDocsHarvestRunState.Running or AppSurfaceDocsHarvestRunState.Completed)
                {
                    _completionVisitSuppressedRunIds.Add(_snapshot.RunId);
                }

                return _snapshot.RunId;
            }

            _suppressNextCompletionVisit = true;
            _recordQueuedRebuildForNextRun = true;
            return null;
        }
    }

    /// <summary>
    /// Records that a trusted rebuild is queued behind the active run.
    /// </summary>
    /// <param name="supersededRunId">
    /// The active run identifier returned by <see cref="SuppressCompletionVisitForCurrentOrNextRun"/>, or
    /// <see langword="null"/> when the suppression applies to the next unpublished run.
    /// </param>
    /// <returns>A completed task after the updated snapshot has been retained and publication has been scheduled.</returns>
    /// <remarks>
    /// When a run identifier is supplied, the queued-state update is ignored if a newer run has already replaced the
    /// superseded snapshot. This keeps delayed queued notifications from decorating a fresh rebuild run.
    /// </remarks>
    internal ValueTask RebuildQueuedAsync(string? supersededRunId)
    {
        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        ProgressPublication publication;
        lock (_gate)
        {
            if (_snapshot.State != AppSurfaceDocsHarvestRunState.Running
                || !IsQueuedRebuildRun(supersededRunId))
            {
                return ValueTask.CompletedTask;
            }

            if (string.Equals(_snapshot.Status, QueuedRebuildStatus, StringComparison.Ordinal))
            {
                _queuedRebuildRunId = null;
                return ValueTask.CompletedTask;
            }

            snapshot = MaterializeTelemetry_NoLock(_snapshot) with
            {
                Status = QueuedRebuildStatus,
                Activity = AddActivity(_snapshot.Activity, QueuedRebuildActivity)
            };
            _snapshot = snapshot;
            _queuedRebuildRunId = null;
            publication = CreatePublication_NoLock(snapshot);
        }

        PublishInBackground(publication);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Marks a harvester as running for the correlated harvest run.
    /// </summary>
    /// <param name="runId">The run identifier returned from the matching begin call.</param>
    /// <param name="progressId">The server-only identity of the harvester instance to update.</param>
    /// <returns>A completed task after the updated snapshot has been retained and publication has been scheduled.</returns>
    internal ValueTask HarvesterStartedAsync(string runId, string progressId)
    {
        return UpdateHarvesterAsync(runId, progressId, "Running", 0, "started.");
    }

    /// <summary>
    /// Marks a harvester as terminal and records its document count.
    /// </summary>
    /// <param name="runId">The run identifier returned from the matching begin call.</param>
    /// <param name="progressId">The server-only identity of the harvester instance to update.</param>
    /// <param name="status">The terminal health status reported by the harvester.</param>
    /// <param name="docCount">The non-negative document count reported by the harvester.</param>
    /// <returns>A completed task after the updated snapshot has been retained and publication has been scheduled.</returns>
    internal ValueTask HarvesterCompletedAsync(string runId, string progressId, DocHarvesterHealthStatus status, int docCount)
    {
        return UpdateHarvesterAsync(
            runId,
            progressId,
            status.ToString(),
            docCount,
            $"finished with {docCount.ToString(CultureInfo.InvariantCulture)} docs.");
    }

    /// <summary>
    /// Updates the in-progress document count for a harvester.
    /// </summary>
    /// <param name="runId">The run identifier returned from the matching begin call.</param>
    /// <param name="progressId">The server-only identity of the harvester instance to update.</param>
    /// <param name="docCount">The current non-negative document count.</param>
    /// <returns>A completed task after the updated snapshot has been retained and publication has been scheduled.</returns>
    internal ValueTask HarvesterDocumentCountUpdatedAsync(string runId, string progressId, int docCount)
    {
        return UpdateHarvesterAsync(
            runId,
            progressId,
            "Running",
            docCount,
            $"processed {docCount.ToString(CultureInfo.InvariantCulture)} docs.");
    }

    /// <summary>
    /// Creates a safe, run-scoped session for one package-owned harvester.
    /// </summary>
    /// <remarks>
    /// The session accepts only closed phase values and numeric deltas. It intentionally has no source-path or display
    /// text parameters, so built-in parser telemetry cannot widen the progress redaction boundary.
    /// </remarks>
    /// <param name="runId">The run identifier returned from the matching begin call.</param>
    /// <param name="progressId">The server-only identity of the package-owned harvester instance.</param>
    /// <param name="testingDelayPerDocumentMilliseconds">Optional test-only delay applied once per positive output document.</param>
    /// <param name="cancellationToken">Harvester cancellation token used to stop test pacing with the parser.</param>
    internal AppSurfaceDocsHarvestProgressSession CreateSession(
        string runId,
        string progressId,
        int testingDelayPerDocumentMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(progressId);
        return new AppSurfaceDocsHarvestProgressSession(
            this,
            runId,
            progressId,
            testingDelayPerDocumentMilliseconds,
            cancellationToken);
    }

    internal ValueTask TransitionAsync(
        string runId,
        string progressId,
        AppSurfaceDocsHarvestProgressPhase phase)
    {
        ProgressPublication publication;
        lock (_gate)
        {
            if (!IsCurrentRunningHarvester_NoLock(runId, progressId)
                || phase is AppSurfaceDocsHarvestProgressPhase.Waiting or AppSurfaceDocsHarvestProgressPhase.Terminal)
            {
                return ValueTask.CompletedTask;
            }

            var snapshotWithTelemetry = MaterializeTelemetry_NoLock(_snapshot);
            var current = snapshotWithTelemetry.Harvesters.First(item => string.Equals(item.ProgressId, progressId, StringComparison.Ordinal));
            if ((int)phase != (int)current.Phase + 1)
            {
                return ValueTask.CompletedTask;
            }

            var harvesters = snapshotWithTelemetry.Harvesters
                .Select(item => string.Equals(item.ProgressId, progressId, StringComparison.Ordinal)
                    ? item with { Status = "Running", Phase = phase }
                    : item)
                .ToArray();
            var snapshot = snapshotWithTelemetry with
            {
                Harvesters = harvesters,
                Activity = AddActivity(snapshotWithTelemetry.Activity, $"{FriendlyHarvesterName(current.HarvesterType)} {FormatPhase(phase)}.")
            };
            _snapshot = snapshot;
            publication = CreatePublication_NoLock(snapshot);
        }

        PublishInBackground(publication);
        return ValueTask.CompletedTask;
    }

    internal void ReportSourceUnit(string runId, string progressId, int documentsFoundDelta)
    {
        ReportProgress(runId, progressId, documentsFoundDelta, sourceUnit: true);
    }

    internal void ReportOutputOnly(string runId, string progressId, int documentsFoundDelta)
    {
        ReportProgress(runId, progressId, documentsFoundDelta, sourceUnit: false);
    }

    private void ReportProgress(string runId, string progressId, int documentsFoundDelta, bool sourceUnit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentsFoundDelta);

        lock (_gate)
        {
            if (!IsCurrentRunningHarvester_NoLock(runId, progressId))
            {
                return;
            }

            var telemetry = _harvesterTelemetry[progressId];
            telemetry.DocumentCount = checked(telemetry.DocumentCount + documentsFoundDelta);
            if (sourceUnit)
            {
                telemetry.SourceUnitsProcessed = checked(telemetry.SourceUnitsProcessed + 1);
            }

            _progressInstrumentedHarvesterIds.Add(progressId);
            _hasProgressEvidence = true;
            ScheduleOrdinaryPublish_NoLock();
        }
    }

    /// <summary>
    /// Adds a bounded activity message to the current run.
    /// </summary>
    /// <param name="runId">The run identifier returned from the matching begin call.</param>
    /// <param name="message">The redacted activity message to prepend.</param>
    /// <returns>A completed task after the updated snapshot has been retained and publication has been scheduled.</returns>
    /// <remarks>
    /// Messages are kept newest-first and capped to the renderer's activity budget. A stale <paramref name="runId"/> is
    /// ignored, and blank messages throw <see cref="ArgumentException"/>.
    /// </remarks>
    internal ValueTask ActivityAsync(string runId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        ProgressPublication publication;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal)
                || _snapshot.State != AppSurfaceDocsHarvestRunState.Running)
            {
                return ValueTask.CompletedTask;
            }

            snapshot = MaterializeTelemetry_NoLock(_snapshot) with
            {
                Activity = AddActivity(_snapshot.Activity, message)
            };
            _snapshot = snapshot;
            publication = CreatePublication_NoLock(snapshot);
        }

        PublishInBackground(publication);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Completes the correlated run from the final harvest-health snapshot.
    /// </summary>
    /// <param name="runId">The run identifier returned from the matching begin call.</param>
    /// <param name="health">The final redacted health snapshot used to populate terminal state, counts, and diagnostics.</param>
    /// <returns>A completed task after the updated snapshot has been retained and publication has been scheduled.</returns>
    /// <remarks>
    /// A failed aggregate health status maps to <see cref="AppSurfaceDocsHarvestRunState.Failed"/>; all other terminal
    /// statuses map to <see cref="AppSurfaceDocsHarvestRunState.Completed"/>. A stale <paramref name="runId"/> is ignored.
    /// </remarks>
    internal ValueTask CompleteRunAsync(string runId, DocHarvestHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);

        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        ProgressPublication publication;
        var publishCompletionVisit = false;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal))
            {
                return ValueTask.CompletedTask;
            }

            if (_snapshot.State != AppSurfaceDocsHarvestRunState.Running)
            {
                return ValueTask.CompletedTask;
            }

            CancelOrdinaryPublish_NoLock();
            var previousState = _snapshot.State;
            var snapshotWithTelemetry = MaterializeTelemetry_NoLock(_snapshot);
            var availableByHarvesterType = snapshotWithTelemetry.Harvesters
                .GroupBy(item => item.HarvesterType, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new Queue<AppSurfaceDocsHarvesterProgress>(group),
                    StringComparer.Ordinal);
            // Final health can reconcile or deduplicate node counts. Sample it before terminal replacement so the
            // rolling built-in rate remains evidence from real parser reports rather than a synthetic health jump.
            var rateSnapshot = RecordRateSample_NoLock(snapshotWithTelemetry);
            var terminalHarvesters = health.Harvesters
                .Select(
                    item => availableByHarvesterType.TryGetValue(item.HarvesterType, out var matchingHarvesters)
                            && matchingHarvesters.TryDequeue(out var existing)
                        ? existing with
                        {
                            Status = item.Status.ToString(),
                            DocCount = item.DocCount,
                            Phase = AppSurfaceDocsHarvestProgressPhase.Terminal
                        }
                        : new AppSurfaceDocsHarvesterProgress(item.HarvesterType, item.Status.ToString(), item.DocCount)
                        {
                            ProgressId = item.HarvesterType,
                            Phase = AppSurfaceDocsHarvestProgressPhase.Terminal
                        })
                .ToArray();
            SynchronizeHarvesterTelemetry_NoLock(terminalHarvesters);
            snapshot = snapshotWithTelemetry with
            {
                State = health.Status == DocHarvestHealthStatus.Failed
                    ? AppSurfaceDocsHarvestRunState.Failed
                    : AppSurfaceDocsHarvestRunState.Completed,
                CompletedUtc = DateTimeOffset.UtcNow,
                Status = health.Status.ToString(),
                TotalDocs = health.TotalDocs,
                CompletedHarvesters = health.TotalHarvesters,
                Harvesters = terminalHarvesters,
                Diagnostics = health.Diagnostics
                    .Select(AppSurfaceDocsHarvestDiagnosticResponse.FromDiagnostic)
                    .ToArray(),
                BuiltInDocumentsPerSecond = rateSnapshot.BuiltInDocumentsPerSecond,
                Activity = AddActivity(_snapshot.Activity, $"Harvest completed with {health.TotalDocs.ToString(CultureInfo.InvariantCulture)} docs.")
            };
            _snapshot = snapshot;
            publication = CreatePublication_NoLock(snapshot);
            if (snapshot.State == AppSurfaceDocsHarvestRunState.Failed)
            {
                _completionVisitSuppressedRunIds.Remove(runId);
            }

            publishCompletionVisit = previousState != AppSurfaceDocsHarvestRunState.Completed
                                     && snapshot.State == AppSurfaceDocsHarvestRunState.Completed;
        }

        PublishInBackground(publication, publishCompletionVisit);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Terminalizes a begun run when aggregation fails after individual harvester execution.
    /// </summary>
    /// <remarks>
    /// The original aggregation exception remains owned by the caller. This rescue publishes only a generic redacted
    /// failure state so an operator is not left with a permanently active observatory if snapshot post-processing fails.
    /// </remarks>
    internal ValueTask FailRunAsync(string runId)
    {
        ProgressPublication publication;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal)
                || _snapshot.State != AppSurfaceDocsHarvestRunState.Running)
            {
                return ValueTask.CompletedTask;
            }

            CancelOrdinaryPublish_NoLock();
            var snapshotWithTelemetry = MaterializeTelemetry_NoLock(_snapshot);
            var rateSnapshot = RecordRateSample_NoLock(snapshotWithTelemetry);
            var harvesters = snapshotWithTelemetry.Harvesters
                .Select(
                    item => item with
                    {
                        Status = IsTerminalStatus(item.Status) ? item.Status : DocHarvesterHealthStatus.Failed.ToString(),
                        Phase = AppSurfaceDocsHarvestProgressPhase.Terminal
                    })
                .ToArray();
            SynchronizeHarvesterTelemetry_NoLock(harvesters);
            var snapshot = snapshotWithTelemetry with
            {
                State = AppSurfaceDocsHarvestRunState.Failed,
                CompletedUtc = DateTimeOffset.UtcNow,
                Status = DocHarvestHealthStatus.Failed.ToString(),
                CompletedHarvesters = _snapshot.TotalHarvesters,
                Harvesters = harvesters,
                BuiltInDocumentsPerSecond = rateSnapshot.BuiltInDocumentsPerSecond,
                Diagnostics =
                [
                    new AppSurfaceDocsHarvestDiagnosticResponse
                    {
                        Code = "appsurfacedocs.harvest.aggregate_failed",
                        Severity = "Error",
                        Problem = "AppSurface Docs could not finish building the harvest snapshot.",
                        Cause = "Harvest post-processing failed after parser execution.",
                        Fix = "Inspect the host logs for exception details, then retry the harvest."
                    }
                ],
                Activity = AddActivity(_snapshot.Activity, "Harvest snapshot assembly failed.")
            };
            _snapshot = snapshot;
            _completionVisitSuppressedRunIds.Remove(runId);
            publication = CreatePublication_NoLock(snapshot);
        }

        PublishInBackground(publication);
        return ValueTask.CompletedTask;
    }

    private ValueTask UpdateHarvesterAsync(string runId, string progressId, string status, int docCount, string activity)
    {
        AppSurfaceDocsHarvestProgressSnapshot snapshot;
        ProgressPublication publication;
        lock (_gate)
        {
            if (!string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal)
                || _snapshot.State != AppSurfaceDocsHarvestRunState.Running)
            {
                return ValueTask.CompletedTask;
            }

            var snapshotWithTelemetry = MaterializeTelemetry_NoLock(_snapshot);
            var completedDelta = IsTerminalStatus(status) && !snapshotWithTelemetry.Harvesters.Any(
                item => string.Equals(item.ProgressId, progressId, StringComparison.Ordinal)
                        && IsTerminalStatus(item.Status))
                ? 1
                : 0;
            var current = snapshotWithTelemetry.Harvesters.FirstOrDefault(
                item => string.Equals(item.ProgressId, progressId, StringComparison.Ordinal));
            if (current is null)
            {
                return ValueTask.CompletedTask;
            }

            var harvesters = snapshotWithTelemetry.Harvesters
                .Select(item => string.Equals(item.ProgressId, progressId, StringComparison.Ordinal)
                    ? item with
                    {
                        Status = status,
                        DocCount = docCount,
                        Phase = IsTerminalStatus(status)
                            ? AppSurfaceDocsHarvestProgressPhase.Terminal
                            : item.Phase
                    }
                    : item)
                .ToArray();

            SynchronizeHarvesterTelemetry_NoLock(harvesters);
            snapshot = snapshotWithTelemetry with
            {
                CompletedHarvesters = Math.Min(snapshotWithTelemetry.TotalHarvesters, snapshotWithTelemetry.CompletedHarvesters + completedDelta),
                TotalDocs = harvesters.Sum(item => item.DocCount),
                Harvesters = harvesters,
                Activity = AddActivity(snapshotWithTelemetry.Activity, $"{FriendlyHarvesterName(current.HarvesterType)} {activity}")
            };
            _snapshot = snapshot;
            publication = CreatePublication_NoLock(snapshot);
        }

        PublishInBackground(publication);
        return ValueTask.CompletedTask;
    }

    private async ValueTask PublishAsync(
        ProgressPublication publication,
        bool publishCompletionVisit = false)
    {
        lock (_gate)
        {
            var publicationIsSuperseded = publication.Generation != _generation
                                           || publication.Revision < _revision;
            if (publicationIsSuperseded)
            {
                if (_pendingPublicationRetry is { } pendingPublication
                    && pendingPublication.Equals(publication))
                {
                    _pendingPublicationRetry = null;
                    _pendingCompletionVisitRetry = false;
                }

                return;
            }

            var publicationWasAlreadyDelivered = publication.Generation < _lastPublishedGeneration
                                                || (publication.Generation == _lastPublishedGeneration
                                                    && publication.Revision <= _lastPublishedRevision);
            var completionVisitStillPending = publishCompletionVisit
                                              && string.Equals(_snapshot.RunId, publication.Snapshot.RunId, StringComparison.Ordinal)
                                              && _snapshot.State == AppSurfaceDocsHarvestRunState.Completed
                                              && !_completionVisitSuppressedRunIds.Contains(publication.Snapshot.RunId);
            if (publicationWasAlreadyDelivered && !completionVisitStillPending)
            {
                return;
            }
        }

        var hub = _services.GetService<IRazorWireStreamHub>();
        if (hub is null)
        {
            return;
        }

        try
        {
            var message = AppSurfaceDocsHarvestProgressRenderer.RenderTurboStream(
                publication.Snapshot,
                CompletionDelayMilliseconds);
            await hub.PublishAsync(
                ChannelName,
                message,
                new RazorWireStreamPublishOptions { Replay = true });

            if (publishCompletionVisit && CanPublishCompletionVisit(publication.Snapshot.RunId))
            {
                var visitMessage = new RazorWireStreamBuilder()
                    .Visit(CurrentPageVisitUrl, RazorWireVisitAction.Replace)
                    .Build();
                await hub.PublishAsync(
                    ChannelName,
                    visitMessage,
                    new RazorWireStreamPublishOptions { Replay = false });
                MarkCompletionVisitPublished(publication.Snapshot.RunId);
            }

            lock (_gate)
            {
                _lastPublishedGeneration = publication.Generation;
                _lastPublishedRevision = publication.Revision;
                if (_pendingPublicationRetry is { } pendingPublication
                    && !IsPublicationNewer(pendingPublication, publication))
                {
                    CancelPublicationRetry_NoLock();
                }
            }
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            _logger.LogWarning(ex, "AppSurface Docs harvest progress publish failed.");
            lock (_gate)
            {
                SchedulePublicationRetry_NoLock(publication, publishCompletionVisit);
            }
        }
    }

    private void PublishInBackground(
        ProgressPublication publication,
        bool publishCompletionVisit = false)
    {
        lock (_gate)
        {
            if (_backgroundPublicationInFlight)
            {
                QueueBackgroundPublication_NoLock(publication, publishCompletionVisit);
                return;
            }

            _backgroundPublicationInFlight = true;
        }

        _ = PublishBackgroundQueueAsync(publication, publishCompletionVisit);
    }

    private async Task PublishBackgroundQueueAsync(
        ProgressPublication publication,
        bool publishCompletionVisit)
    {
        while (true)
        {
            await PublishAsync(publication, publishCompletionVisit);

            lock (_gate)
            {
                if (_pendingBackgroundPublication is not { } pendingPublication)
                {
                    _backgroundPublicationInFlight = false;
                    return;
                }

                publication = pendingPublication;
                publishCompletionVisit = _pendingBackgroundCompletionVisit;
                _pendingBackgroundPublication = null;
                _pendingBackgroundCompletionVisit = false;
            }
        }
    }

    private void QueueBackgroundPublication_NoLock(
        ProgressPublication publication,
        bool publishCompletionVisit)
    {
        if (_pendingBackgroundPublication is null || IsPublicationNewer(publication, _pendingBackgroundPublication.Value))
        {
            _pendingBackgroundPublication = publication;
            _pendingBackgroundCompletionVisit = publishCompletionVisit;
            return;
        }

        if (publication.Generation == _pendingBackgroundPublication.Value.Generation
            && publication.Revision == _pendingBackgroundPublication.Value.Revision)
        {
            _pendingBackgroundCompletionVisit |= publishCompletionVisit;
        }
    }

    private bool IsCurrentRunningHarvester_NoLock(string runId, string progressId)
    {
        return _snapshot.State == AppSurfaceDocsHarvestRunState.Running
               && string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal)
               && _snapshot.Harvesters.Any(item => string.Equals(item.ProgressId, progressId, StringComparison.Ordinal)
                                                   && item.Phase != AppSurfaceDocsHarvestProgressPhase.Terminal);
    }

    private AppSurfaceDocsHarvestProgressSnapshot MaterializeTelemetry_NoLock(
        AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        if (_harvesterTelemetry.Count == 0)
        {
            return snapshot;
        }

        AppSurfaceDocsHarvesterProgress[]? materializedHarvesters = null;
        for (var index = 0; index < snapshot.Harvesters.Count; index++)
        {
            var harvester = snapshot.Harvesters[index];
            if (!_harvesterTelemetry.TryGetValue(harvester.ProgressId, out var telemetry)
                || (harvester.DocCount == telemetry.DocumentCount
                    && harvester.SourceUnitsProcessed == telemetry.SourceUnitsProcessed))
            {
                continue;
            }

            materializedHarvesters ??= snapshot.Harvesters.ToArray();
            materializedHarvesters[index] = harvester with
            {
                DocCount = telemetry.DocumentCount,
                SourceUnitsProcessed = telemetry.SourceUnitsProcessed
            };
        }

        if (materializedHarvesters is null)
        {
            return snapshot;
        }

        return snapshot with
        {
            TotalDocs = snapshot.State == AppSurfaceDocsHarvestRunState.Running
                ? materializedHarvesters.Sum(harvester => harvester.DocCount)
                : snapshot.TotalDocs,
            Harvesters = materializedHarvesters
        };
    }

    private void SynchronizeHarvesterTelemetry_NoLock(
        IReadOnlyList<AppSurfaceDocsHarvesterProgress> harvesters)
    {
        foreach (var harvester in harvesters)
        {
            if (_harvesterTelemetry.TryGetValue(harvester.ProgressId, out var telemetry))
            {
                telemetry.DocumentCount = harvester.DocCount;
                telemetry.SourceUnitsProcessed = harvester.SourceUnitsProcessed;
            }
            else
            {
                _harvesterTelemetry[harvester.ProgressId] = new HarvesterTelemetry
                {
                    DocumentCount = harvester.DocCount,
                    SourceUnitsProcessed = harvester.SourceUnitsProcessed
                };
            }
        }
    }

    private ProgressPublication CreatePublication_NoLock(AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        return new ProgressPublication(snapshot, _generation, ++_revision);
    }

    private static bool IsPublicationNewer(ProgressPublication candidate, ProgressPublication existing)
    {
        return candidate.Generation > existing.Generation
               || (candidate.Generation == existing.Generation && candidate.Revision > existing.Revision);
    }

    private AppSurfaceDocsHarvestProgressSnapshot RecordRateSample_NoLock(AppSurfaceDocsHarvestProgressSnapshot snapshot)
    {
        if (!_hasProgressEvidence)
        {
            return snapshot;
        }

        var timestamp = _timeProvider.GetTimestamp();
        var builtInDocuments = _progressInstrumentedHarvesterIds
            .Sum(progressId => _harvesterTelemetry[progressId].DocumentCount);
        AddRateSample_NoLock(builtInDocuments, timestamp);

        RateSample? oldest = null;
        RateSample? newest = null;
        for (var index = 0; index < _rateSamples.Length; index++)
        {
            var sample = _rateSamples[index];
            if (sample is null)
            {
                continue;
            }

            if (newest is null || sample.Value.Timestamp > newest.Value.Timestamp)
            {
                newest = sample;
            }
        }

        if (newest is null)
        {
            return snapshot;
        }

        for (var index = 0; index < _rateSamples.Length; index++)
        {
            var sample = _rateSamples[index];
            if (sample is null
                || _timeProvider.GetElapsedTime(sample.Value.Timestamp, newest.Value.Timestamp) > TimeSpan.FromSeconds(2))
            {
                continue;
            }

            if (oldest is null || sample.Value.Timestamp < oldest.Value.Timestamp)
            {
                oldest = sample;
            }
        }

        if (oldest is null)
        {
            return snapshot;
        }

        var elapsed = _timeProvider.GetElapsedTime(oldest.Value.Timestamp, newest.Value.Timestamp);
        if (elapsed < OrdinaryPublishInterval)
        {
            return snapshot with { BuiltInDocumentsPerSecond = null };
        }

        var delta = newest.Value.DocumentCount - oldest.Value.DocumentCount;
        return snapshot with
        {
            BuiltInDocumentsPerSecond = Math.Max(0, delta) / elapsed.TotalSeconds
        };
    }

    private void AddRateSample_NoLock(int documentCount, long? timestamp = null)
    {
        _rateSamples[_rateSampleNextIndex] = new RateSample(timestamp ?? _timeProvider.GetTimestamp(), documentCount);
        _rateSampleNextIndex = (_rateSampleNextIndex + 1) % RateSampleCapacity;
    }

    private void ScheduleOrdinaryPublish_NoLock()
    {
        if (_ordinaryPublishCancellation is not null)
        {
            return;
        }

        var generation = _generation;
        var cancellation = new CancellationTokenSource();
        _ordinaryPublishCancellation = cancellation;
        _ = FlushOrdinaryPublishAsync(generation, cancellation);
    }

    private async Task FlushOrdinaryPublishAsync(long generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(OrdinaryPublishInterval, _timeProvider, cancellation.Token);
            ProgressPublication? publication = null;
            lock (_gate)
            {
                if (ReferenceEquals(_ordinaryPublishCancellation, cancellation))
                {
                    _ordinaryPublishCancellation = null;
                }

                if (!cancellation.IsCancellationRequested
                    && generation == _generation
                    && _snapshot.State == AppSurfaceDocsHarvestRunState.Running)
                {
                    var snapshot = RecordRateSample_NoLock(MaterializeTelemetry_NoLock(_snapshot));
                    _snapshot = snapshot;
                    publication = CreatePublication_NoLock(snapshot);
                }
            }

            if (publication is { } pendingPublication)
            {
                PublishInBackground(pendingPublication);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelOrdinaryPublish_NoLock()
    {
        var cancellation = _ordinaryPublishCancellation;
        _ordinaryPublishCancellation = null;
        cancellation?.Cancel();
    }

    private void SchedulePublicationRetry_NoLock(ProgressPublication publication, bool publishCompletionVisit)
    {
        if (_pendingPublicationRetry is null || IsPublicationNewer(publication, _pendingPublicationRetry.Value))
        {
            _pendingPublicationRetry = publication;
            _pendingCompletionVisitRetry = publishCompletionVisit;
        }
        else if (publication.Generation == _pendingPublicationRetry.Value.Generation
                 && publication.Revision == _pendingPublicationRetry.Value.Revision)
        {
            _pendingCompletionVisitRetry |= publishCompletionVisit;
        }

        if (_publicationRetryCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _publicationRetryCancellation = cancellation;
        _ = RetryPendingPublicationAsync(cancellation);
    }

    private async Task RetryPendingPublicationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(PublicationRetryInterval, _timeProvider, cancellation.Token);
            ProgressPublication? publication = null;
            var publishCompletionVisit = false;
            lock (_gate)
            {
                if (ReferenceEquals(_publicationRetryCancellation, cancellation))
                {
                    _publicationRetryCancellation = null;
                }

                if (!cancellation.IsCancellationRequested)
                {
                    publication = _pendingPublicationRetry;
                    publishCompletionVisit = _pendingCompletionVisitRetry;
                    _pendingPublicationRetry = null;
                    _pendingCompletionVisitRetry = false;
                }
            }

            if (publication is { } pendingPublication)
            {
                PublishInBackground(pendingPublication, publishCompletionVisit);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelPublicationRetry_NoLock()
    {
        var cancellation = _publicationRetryCancellation;
        _publicationRetryCancellation = null;
        _pendingPublicationRetry = null;
        _pendingCompletionVisitRetry = false;
        cancellation?.Cancel();
    }

    private static string FormatPhase(AppSurfaceDocsHarvestProgressPhase phase)
    {
        return phase switch
        {
            AppSurfaceDocsHarvestProgressPhase.Discovering => "is discovering sources",
            AppSurfaceDocsHarvestProgressPhase.Parsing => "is parsing sources",
            AppSurfaceDocsHarvestProgressPhase.Finalizing => "is finalizing output",
            _ => "updated progress"
        };
    }

    private bool CanPublishCompletionVisit(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }

        lock (_gate)
        {
            return string.Equals(_snapshot.RunId, runId, StringComparison.Ordinal)
                   && _snapshot.State == AppSurfaceDocsHarvestRunState.Completed
                   && !_completionVisitSuppressedRunIds.Contains(runId);
        }
    }

    private void MarkCompletionVisitPublished(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        lock (_gate)
        {
            _completionVisitSuppressedRunIds.Remove(runId);
        }
    }

    /// <summary>
    /// Determines whether a queued-rebuild status update still belongs to the currently retained running snapshot.
    /// </summary>
    /// <param name="supersededRunId">
    /// The run identifier captured when the rebuild was queued, or <see langword="null"/> when the queue marker was
    /// deferred until the next run published its identifier.
    /// </param>
    /// <returns><see langword="true"/> when the current snapshot is the run that should carry queued state.</returns>
    /// <remarks>
    /// This guard prevents delayed queued-state publishes from decorating a fresh rebuild after the original run has
    /// already completed. Callers must hold <c>_gate</c> while invoking this helper so the snapshot and deferred marker are
    /// compared atomically.
    /// </remarks>
    private bool IsQueuedRebuildRun(string? supersededRunId)
    {
        if (!string.IsNullOrWhiteSpace(supersededRunId))
        {
            return string.Equals(_snapshot.RunId, supersededRunId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(_queuedRebuildRunId)
               && string.Equals(_snapshot.RunId, _queuedRebuildRunId, StringComparison.Ordinal);
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
