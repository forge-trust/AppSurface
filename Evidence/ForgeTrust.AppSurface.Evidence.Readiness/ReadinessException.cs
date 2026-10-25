using System.Net;

namespace ForgeTrust.AppSurface.Evidence.Readiness;

/// <summary>Identifies the phase that failed to complete under the readiness budget.</summary>
public enum ReadinessPhase
{
    /// <summary>The caller-supplied builder factory.</summary>
    Creation,
    /// <summary>Application construction.</summary>
    Build,
    /// <summary>Application startup.</summary>
    Start,
    /// <summary>Native resource health.</summary>
    ResourceHealth,
    /// <summary>Named endpoint allocation and resolution.</summary>
    EndpointResolution,
    /// <summary>The declared HTTP condition.</summary>
    Http,
    /// <summary>Owned resource cleanup.</summary>
    Cleanup,
}

/// <summary>Safe failure categories; deadline and attempt-limit errors retain the last HTTP cause separately.</summary>
public enum ReadinessFailureCategory
{
    /// <summary>The original monotonic deadline expired.</summary>
    Deadline,
    /// <summary>The explicit request cap was reached.</summary>
    AttemptLimit,
    /// <summary>The status predicate rejected a response.</summary>
    StatusRejected,
    /// <summary>The transport failed with a safe HTTP error classification.</summary>
    TransportFailure,
    /// <summary>The request timer or a shorter client timeout expired.</summary>
    RequestTimeout,
    /// <summary>The selected endpoint was unavailable or invalid.</summary>
    EndpointUnavailable,
    /// <summary>Owned cleanup failed.</summary>
    CleanupFailure,
}

/// <summary>Describes a generated readiness failure without retaining request data or raw transport exceptions.</summary>
/// <remarks>Original factory, build, predicate, and programming errors remain their original exceptions.
/// Caller cancellation remains an <see cref="OperationCanceledException"/> carrying the original token.</remarks>
public sealed class ReadinessException : Exception
{
    /// <summary>Creates a safe failure using only fixed text, enum values, and numeric status/attempt data.</summary>
    internal ReadinessException(ReadinessPhase phase, ReadinessFailureCategory category, ReadinessAttempt last = default, Type? cleanupExceptionType = null)
        : base($"Readiness failed in {phase}: {category}; attempts={last.Attempts}; last cause={last.LastCause}; status={(int?)last.Status}; transport={last.HttpError}.")
    {
        Phase = phase;
        Category = category;
        LastCause = last.LastCause;
        LastStatusCode = last.Status;
        LastHttpRequestError = last.HttpError;
        Attempts = last.Attempts;
        (Problem, Cause, NextAction) = category switch
        {
            ReadinessFailureCategory.Deadline when phase is ReadinessPhase.Creation or ReadinessPhase.Build or ReadinessPhase.Start =>
                ("Startup deadline expired.", $"Startup phase: {phase}.", "Check cancellation observation and host-local logs."),
            ReadinessFailureCategory.Deadline when phase == ReadinessPhase.ResourceHealth =>
                ("Resource deadline expired.", "The native resource health condition did not complete.", "Check the explicit resource identity and configured health condition."),
            ReadinessFailureCategory.Deadline => ("Readiness deadline expired.", "The original operation budget was exhausted.", "Check the failed phase, retained HTTP cause, and explicit timeout."),
            ReadinessFailureCategory.AttemptLimit => ("Readiness attempt limit reached.", "Every permitted request failed its condition.", "Check the last cause and the explicit attempt cap."),
            ReadinessFailureCategory.StatusRejected => ("HTTP condition rejected.", $"HTTP status: {(int?)last.Status}.", "Check the path and predicate; check caller authentication for HTTP 401 or 403."),
            ReadinessFailureCategory.TransportFailure => ("HTTP transport failed.", $"HTTP transport category: {last.HttpError}.", "Check reachability and caller proxy or certificate policy."),
            ReadinessFailureCategory.RequestTimeout => ("HTTP request timed out.", "The request budget or a shorter client timer expired.", "Check responsiveness and the explicit request or client limits."),
            ReadinessFailureCategory.EndpointUnavailable => ("Named HTTP endpoint unavailable.", "The explicit resource or endpoint could not be resolved to HTTP(S).", "Check the resource name, endpoint name, and allocated endpoint."),
            _ => ("Owned cleanup failed.", $"Owned cleanup exception type: {SafeCleanupType(cleanupExceptionType)}.", "Inspect owner cleanup without claiming readiness."),
        };
    }

    /// <summary>Maps cleanup types to a fixed BCL allowlist; arbitrary names and exception instances are never retained.</summary>
    private static string SafeCleanupType(Type? type) => type == typeof(InvalidOperationException) ? nameof(InvalidOperationException)
        : type == typeof(ObjectDisposedException) ? nameof(ObjectDisposedException)
        : type == typeof(TimeoutException) ? nameof(TimeoutException)
        : type == typeof(OperationCanceledException) ? nameof(OperationCanceledException)
        : type == typeof(HttpRequestException) ? nameof(HttpRequestException)
        : type == typeof(AggregateException) ? nameof(AggregateException)
        : nameof(Exception);

    /// <summary>Gets the phase active at failure.</summary>
    public ReadinessPhase Phase { get; }
    /// <summary>Gets the terminal failure category.</summary>
    public ReadinessFailureCategory Category { get; }
    /// <summary>Gets the last retryable HTTP cause, or null before any failed attempt.</summary>
    public ReadinessFailureCategory? LastCause { get; }
    /// <summary>Gets the last rejected status; null for transport, timeout, or no attempt.</summary>
    public HttpStatusCode? LastStatusCode { get; }
    /// <summary>Gets only the last transport error enum; null for other causes.</summary>
    public HttpRequestError? LastHttpRequestError { get; }
    /// <summary>Gets the number of requests dispatched.</summary>
    public int Attempts { get; }
    /// <summary>Gets a fixed safe problem description.</summary>
    public string Problem { get; }
    /// <summary>Gets a fixed safe explanation.</summary>
    public string Cause { get; }
    /// <summary>Gets a fixed safe next check.</summary>
    public string NextAction { get; }
    /// <summary>Gets the canonical troubleshooting reference; it contains no request data.</summary>
    public string DocumentationUrl => "https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/README.md#troubleshooting";
}

/// <summary>Constant-space safe state of the most recent HTTP attempt; never stores a request, response, or exception.</summary>
/// <param name="Attempts">Dispatched request count, retained when no further attempt can begin.</param>
/// <param name="LastCause">Last retryable HTTP cause, or null before a failed HTTP observation.</param>
/// <param name="Status">Rejected status only when the last cause is StatusRejected; otherwise null.</param>
/// <param name="HttpError">Transport enum only when the last cause is TransportFailure; otherwise null.</param>
internal readonly record struct ReadinessAttempt(
    int Attempts,
    ReadinessFailureCategory? LastCause = null,
    HttpStatusCode? Status = null,
    HttpRequestError? HttpError = null);
