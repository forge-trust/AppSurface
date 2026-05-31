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
    /// <remarks>
    /// Controllers must validate this value with <c>DocsController.IsSafeAppRelativeUrl</c> before assigning
    /// request-derived input. The harvesting Razor view emits this value only through Razor-encoded attributes and links;
    /// raw progress fragments do not receive it.
    /// </remarks>
    public string ReturnUrl { get; init; } = "/";

    /// <summary>
    /// Gets the delay, in milliseconds, before JavaScript users are returned to <see cref="ReturnUrl"/>.
    /// </summary>
    public int CompletionNavigationDelayMilliseconds { get; init; } = 900;

    /// <summary>
    /// Gets a value indicating whether the current request may subscribe to the live harvest progress stream.
    /// </summary>
    /// <remarks>
    /// When this value is <see langword="false"/>, the harvesting view renders the current redacted progress snapshot
    /// and a manual continuation path without emitting a RazorWire stream source. Controllers should compute this from
    /// the effective runtime <c>IRazorWireChannelAuthorizer</c> so the view matches the stream endpoint's authorization
    /// decision.
    /// </remarks>
    public bool CanUseLiveProgress { get; init; } = true;
}

/// <summary>
/// Redacted live progress snapshot for one AppSurface Docs harvest run.
/// </summary>
public sealed record AppSurfaceDocsHarvestProgressSnapshot
{
    /// <summary>
    /// Gets the empty snapshot used before a harvest run has started.
    /// </summary>
    public static AppSurfaceDocsHarvestProgressSnapshot Idle => new()
    {
        State = AppSurfaceDocsHarvestRunState.Idle,
        StartedUtc = DateTimeOffset.UtcNow,
        Status = "Idle"
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
public sealed record AppSurfaceDocsHarvesterProgress
{
    /// <summary>
    /// Initializes a new redacted harvester progress entry.
    /// </summary>
    /// <param name="harvesterType">The non-secret harvester type name used as the stable row identity.</param>
    /// <param name="status">The display status for the harvester, such as <c>Waiting</c>, <c>Running</c>, or a terminal health status.</param>
    /// <param name="docCount">The number of documents reported by this harvester. Values are expected to be zero or greater.</param>
    public AppSurfaceDocsHarvesterProgress(string harvesterType, string status, int docCount)
    {
        HarvesterType = harvesterType;
        Status = status;
        DocCount = docCount;
    }

    /// <summary>
    /// Gets the non-secret harvester type name used as the stable row identity.
    /// </summary>
    public string HarvesterType { get; init; }

    /// <summary>
    /// Gets the display status for this harvester.
    /// </summary>
    public string Status { get; init; }

    /// <summary>
    /// Gets the number of documents reported by this harvester.
    /// </summary>
    public int DocCount { get; init; }
}

/// <summary>
/// One bounded activity entry in the live harvest observatory.
/// </summary>
public sealed record AppSurfaceDocsHarvestActivity
{
    /// <summary>
    /// Initializes a new bounded harvest activity entry.
    /// </summary>
    /// <param name="timestampUtc">The UTC timestamp used for sorting and machine-readable rendering.</param>
    /// <param name="message">The redacted human-readable activity message.</param>
    public AppSurfaceDocsHarvestActivity(DateTimeOffset timestampUtc, string message)
    {
        TimestampUtc = timestampUtc;
        Message = message;
    }

    /// <summary>
    /// Gets the UTC timestamp used for sorting and machine-readable rendering.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Gets the redacted human-readable activity message.
    /// </summary>
    public string Message { get; init; }
}
