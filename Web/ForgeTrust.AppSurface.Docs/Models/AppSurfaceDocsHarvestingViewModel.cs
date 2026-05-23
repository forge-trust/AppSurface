using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// View model for the AppSurface Docs live harvest observatory.
/// </summary>
public sealed record AppSurfaceDocsHarvestingViewModel
{
    /// <summary>
    /// Gets the current redacted harvest progress snapshot rendered by the observatory.
    /// </summary>
    public AppSurfaceDocsHarvestProgressSnapshot Progress { get; init; } = AppSurfaceDocsHarvestProgressSnapshot.Idle;

    /// <summary>
    /// Gets the app-relative URL the browser should open after harvest completion.
    /// </summary>
    public string ReturnUrl { get; init; } = "/";

    /// <summary>
    /// Gets the delay, in milliseconds, before JavaScript users are returned to <see cref="ReturnUrl"/>.
    /// </summary>
    public int CompletionNavigationDelayMilliseconds { get; init; } = 900;
}

/// <summary>
/// Redacted live progress snapshot for one AppSurface Docs harvest run.
/// </summary>
public sealed record AppSurfaceDocsHarvestProgressSnapshot
{
    /// <summary>
    /// Gets the empty snapshot used before a harvest run has started.
    /// </summary>
    public static AppSurfaceDocsHarvestProgressSnapshot Idle { get; } = new()
    {
        State = AppSurfaceDocsHarvestRunState.Idle,
        StartedUtc = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Gets the unique run identifier.
    /// </summary>
    [JsonPropertyName("runId")]
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current run state.
    /// </summary>
    [JsonPropertyName("state")]
    public AppSurfaceDocsHarvestRunState State { get; init; }

    /// <summary>
    /// Gets the UTC time when the run started.
    /// </summary>
    [JsonPropertyName("startedUtc")]
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    /// Gets the UTC time when the run completed.
    /// </summary>
    [JsonPropertyName("completedUtc")]
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>
    /// Gets the number of active harvesters expected in the run.
    /// </summary>
    [JsonPropertyName("totalHarvesters")]
    public int TotalHarvesters { get; init; }

    /// <summary>
    /// Gets the number of harvesters that have completed.
    /// </summary>
    [JsonPropertyName("completedHarvesters")]
    public int CompletedHarvesters { get; init; }

    /// <summary>
    /// Gets the number of docs in the final snapshot, or the currently known count while running.
    /// </summary>
    [JsonPropertyName("totalDocs")]
    public int TotalDocs { get; init; }

    /// <summary>
    /// Gets the redacted aggregate harvest status when known.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "Starting";

    /// <summary>
    /// Gets redacted per-harvester progress entries.
    /// </summary>
    [JsonPropertyName("harvesters")]
    public IReadOnlyList<AppSurfaceDocsHarvesterProgress> Harvesters { get; init; } = [];

    /// <summary>
    /// Gets bounded recent activity entries, newest first.
    /// </summary>
    [JsonPropertyName("activity")]
    public IReadOnlyList<AppSurfaceDocsHarvestActivity> Activity { get; init; } = [];

    /// <summary>
    /// Gets redacted diagnostics surfaced only when the run is degraded or failed.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<AppSurfaceDocsHarvestDiagnosticResponse> Diagnostics { get; init; } = [];
}

/// <summary>
/// State for the current live harvest run.
/// </summary>
public enum AppSurfaceDocsHarvestRunState
{
    /// <summary>
    /// No harvest run has published progress yet.
    /// </summary>
    Idle = 0,

    /// <summary>
    /// A harvest run is currently executing.
    /// </summary>
    Running = 1,

    /// <summary>
    /// The harvest run completed with a healthy, empty, or degraded snapshot.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// The harvest run completed with an aggregate failed snapshot.
    /// </summary>
    Failed = 3
}

/// <summary>
/// Redacted progress for one AppSurface Docs harvester.
/// </summary>
public sealed record AppSurfaceDocsHarvesterProgress(
    string HarvesterType,
    string Status,
    int DocCount);

/// <summary>
/// One bounded activity entry in the live harvest observatory.
/// </summary>
public sealed record AppSurfaceDocsHarvestActivity(
    DateTimeOffset TimestampUtc,
    string Message);
