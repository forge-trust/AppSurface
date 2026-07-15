namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Contains a bounded payload matching AppSurface Web's PWA worker schema version 1.</summary>
public sealed class AppSurfaceWebPushNotification
{
    /// <summary>Creates an immutable notification whose bounds are validated by <see cref="IAppSurfaceWebPushSender.SendAsync"/>.</summary>
    /// <param name="title">The required notification title.</param>
    /// <param name="body">The optional notification body.</param>
    /// <param name="iconPath">The optional app-root-relative icon path.</param>
    /// <param name="badgePath">The optional app-root-relative badge path.</param>
    /// <param name="tag">The optional collapse tag.</param>
    /// <param name="destinationPath">The optional app-root-relative click destination.</param>
    public AppSurfaceWebPushNotification(
        string title,
        string? body = null,
        string? iconPath = null,
        string? badgePath = null,
        string? tag = null,
        string? destinationPath = null)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Body = body;
        IconPath = iconPath;
        BadgePath = badgePath;
        Tag = tag;
        DestinationPath = destinationPath;
    }

    /// <summary>Gets the required 1-256 character title.</summary>
    public string Title { get; }
    /// <summary>Gets the optional 1-2048 character body.</summary>
    public string? Body { get; }
    /// <summary>Gets the optional app-root-relative icon path.</summary>
    public string? IconPath { get; }
    /// <summary>Gets the optional app-root-relative badge path.</summary>
    public string? BadgePath { get; }
    /// <summary>Gets the optional 1-128 character notification tag.</summary>
    public string? Tag { get; }
    /// <summary>Gets the optional app-root-relative click destination, which may include one query string.</summary>
    public string? DestinationPath { get; }

    /// <inheritdoc />
    public override string ToString() => "AppSurfaceWebPushNotification { Redacted = true }";
}

/// <summary>Configures one push-service attempt.</summary>
public sealed class AppSurfaceWebPushSendOptions
{
    /// <summary>Creates immutable send options. Bounds are validated by <see cref="IAppSurfaceWebPushSender.SendAsync"/>.</summary>
    /// <param name="timeToLiveSeconds">The retention time, from 1 through 2,419,200 seconds.</param>
    /// <param name="urgency">The push-service urgency.</param>
    /// <param name="topic">The optional 1-32 character URL-safe collapse topic.</param>
    public AppSurfaceWebPushSendOptions(
        int timeToLiveSeconds,
        AppSurfaceWebPushUrgency urgency = AppSurfaceWebPushUrgency.Normal,
        string? topic = null)
    {
        TimeToLiveSeconds = timeToLiveSeconds;
        Urgency = urgency;
        Topic = topic;
    }

    /// <summary>Gets the required retention time in seconds.</summary>
    public int TimeToLiveSeconds { get; }
    /// <summary>Gets the push urgency.</summary>
    public AppSurfaceWebPushUrgency Urgency { get; }
    /// <summary>Gets the optional 1-32 character URL-safe collapse topic.</summary>
    public string? Topic { get; }
}

/// <summary>Defines Web Push urgency.</summary>
public enum AppSurfaceWebPushUrgency
{
    /// <summary>Very low urgency.</summary>
    VeryLow,
    /// <summary>Low urgency.</summary>
    Low,
    /// <summary>Normal urgency, which emits no Urgency header.</summary>
    Normal,
    /// <summary>High urgency.</summary>
    High,
}

/// <summary>Contains one immutable, sensitive single-recipient send request.</summary>
public sealed class AppSurfaceWebPushSendRequest
{
    /// <summary>Creates a single-recipient send request; field and payload bounds are validated when sent.</summary>
    /// <param name="subscription">The complete sensitive subscription snapshot.</param>
    /// <param name="notification">The bounded worker notification.</param>
    /// <param name="options">The one-attempt transport options.</param>
    public AppSurfaceWebPushSendRequest(
        AppSurfaceWebPushSubscription subscription,
        AppSurfaceWebPushNotification notification,
        AppSurfaceWebPushSendOptions options)
    {
        Subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
        Notification = notification ?? throw new ArgumentNullException(nameof(notification));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Gets the complete subscription snapshot.</summary>
    public AppSurfaceWebPushSubscription Subscription { get; }
    /// <summary>Gets the bounded worker payload.</summary>
    public AppSurfaceWebPushNotification Notification { get; }
    /// <summary>Gets the one-attempt transport options.</summary>
    public AppSurfaceWebPushSendOptions Options { get; }

    /// <inheritdoc />
    public override string ToString() => "AppSurfaceWebPushSendRequest { Redacted = true }";
}

/// <summary>Sends a single Web Push notification without retrying.</summary>
public interface IAppSurfaceWebPushSender
{
    /// <summary>
    /// Performs at most one push-service request. <see cref="AppSurfaceWebPushSendOutcome.Accepted"/> proves only an
    /// RFC 8030 <c>201 Created</c> response; it does not prove browser delivery or display.
    /// Caller cancellation propagates. Terminal cleanup uses an independent hard five-second bound and is reported in
    /// the safe result rather than extending the caller's request indefinitely.
    /// </summary>
    /// <param name="request">The validated-at-send, single-recipient request.</param>
    /// <param name="cancellationToken">Cancels the push-service attempt.</param>
    /// <returns>A safe classification containing no subscription, payload, token, body, or exception data.</returns>
    /// <exception cref="ArgumentException">A request field is invalid or the encoded payload exceeds its bound.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the push-service attempt.</exception>
    ValueTask<AppSurfaceWebPushSendResult> SendAsync(
        AppSurfaceWebPushSendRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Classifies the safe result of one send attempt.</summary>
public enum AppSurfaceWebPushSendOutcome
{
    /// <summary>The push service returned exactly HTTP 201; delivery and display remain unproven.</summary>
    Accepted,
    /// <summary>The push service returned HTTP 404 or 410 and cleanup was dispatched.</summary>
    TerminalSubscription,
    /// <summary>A network failure, timeout, HTTP 408, 429, or 5xx occurred.</summary>
    TransientFailure,
    /// <summary>The push service returned another 4xx response.</summary>
    Rejected,
    /// <summary>The subscription references a VAPID key absent from the retained ring.</summary>
    VapidKeyUnavailable,
    /// <summary>The endpoint origin is not in the exact host allowlist; no network request occurred.</summary>
    PushServiceNotAllowed,
    /// <summary>A redirect, unexpected success response, or protocol invariant failure occurred.</summary>
    ProtocolFailure,
}

/// <summary>Classifies conditional terminal cleanup.</summary>
public enum AppSurfaceWebPushCleanupState
{
    /// <summary>No terminal cleanup was required.</summary>
    NotRequired,
    /// <summary>The matching complete snapshot was marked terminal.</summary>
    Completed,
    /// <summary>The snapshot was already terminal or a replacement won the race.</summary>
    AlreadyTerminal,
    /// <summary>Host custody rejected cleanup.</summary>
    Rejected,
    /// <summary>Cleanup was unavailable, timed out, or failed safely.</summary>
    Failed,
}

/// <summary>Contains safe send classification without endpoint, key, payload, token, body, or exception data.</summary>
public sealed class AppSurfaceWebPushSendResult
{
    internal AppSurfaceWebPushSendResult(
        AppSurfaceWebPushSendOutcome outcome,
        AppSurfaceWebPushCleanupState cleanupState,
        int? statusCode,
        TimeSpan? retryAfter,
        string reasonCode,
        string vapidKeyId)
    {
        Outcome = outcome;
        CleanupState = cleanupState;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ReasonCode = reasonCode;
        VapidKeyId = vapidKeyId;
    }

    /// <summary>Gets the safe send outcome.</summary>
    public AppSurfaceWebPushSendOutcome Outcome { get; }
    /// <summary>Gets the terminal cleanup state.</summary>
    public AppSurfaceWebPushCleanupState CleanupState { get; }
    /// <summary>Gets the push-service HTTP status, when a response was observed.</summary>
    public int? StatusCode { get; }
    /// <summary>Gets a safe nonnegative Retry-After delay when supplied by the push service.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Gets the stable safe reason code.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets the safe configured VAPID key identifier.</summary>
    public string VapidKeyId { get; }
}
