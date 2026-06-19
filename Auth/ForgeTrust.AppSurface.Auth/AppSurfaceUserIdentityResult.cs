namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Defines result states for resolving an external subject to an app-owned user id.
/// </summary>
public enum AppSurfaceUserIdentityStatus
{
    /// <summary>
    /// The external subject resolved to a durable app-owned user id.
    /// </summary>
    Resolved = 0,

    /// <summary>
    /// No external subject was available to resolve.
    /// </summary>
    MissingSubject = 1,

    /// <summary>
    /// The external subject was present but invalid for the configured resolver.
    /// </summary>
    MalformedSubject = 2,

    /// <summary>
    /// The mapped app user exists but is disabled.
    /// </summary>
    DisabledAppUser = 3,

    /// <summary>
    /// The host session is stale, expired, unknown, or cannot be trusted for identity mapping.
    /// </summary>
    StaleOrUnknownSession = 4,

    /// <summary>
    /// More than one app user mapping matched the same external subject tuple.
    /// </summary>
    DuplicateMapping = 5,

    /// <summary>
    /// The app-owned identity store was unavailable.
    /// </summary>
    StoreUnavailable = 6,

    /// <summary>
    /// The app declined to provision or attach a user for the external subject.
    /// </summary>
    ProvisioningDenied = 7,
}

/// <summary>
/// Represents the result of resolving an external subject to a durable app-owned user id.
/// </summary>
/// <remarks>
/// This result family is intentionally separate from <see cref="AppSurfaceAuthResult"/>. Authentication, policy, and
/// navigation outcomes remain host-auth decisions; identity resolution describes the app-owned mapping step that can
/// happen after the host has authenticated a subject. Messages and metadata should be display-safe and avoid raw
/// subjects, emails, tokens, and provider payloads by default.
/// </remarks>
public sealed class AppSurfaceUserIdentityResult
{
    private AppSurfaceUserIdentityResult(
        AppSurfaceUserIdentityStatus status,
        AppUserId? appUserId,
        ExternalSubject? subject,
        string? message,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ValidateCombination(status, appUserId, subject);
        Status = status;
        AppUserId = appUserId;
        Subject = subject;
        Message = AppSurfaceAuthMetadata.NormalizeOptionalText(message);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the identity resolution status.
    /// </summary>
    public AppSurfaceUserIdentityStatus Status { get; }

    /// <summary>
    /// Gets the resolved app-owned user id when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </summary>
    public AppUserId? AppUserId { get; }

    /// <summary>
    /// Gets the external subject tuple involved in resolution when it was available and valid enough to report safely.
    /// </summary>
    public ExternalSubject? Subject { get; }

    /// <summary>
    /// Gets an optional display-safe message supplied by the app resolver.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve app-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets a value indicating whether the external subject resolved to an app-owned user id.
    /// </summary>
    public bool Succeeded => Status == AppSurfaceUserIdentityStatus.Resolved;

    /// <summary>
    /// Creates a successful identity resolution result.
    /// </summary>
    /// <param name="appUserId">The durable app-owned user id.</param>
    /// <param name="subject">The external subject tuple that resolved to the app user.</param>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A resolved identity result.</returns>
    public static AppSurfaceUserIdentityResult Resolved(
        AppUserId appUserId,
        ExternalSubject subject,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceUserIdentityResult(
            AppSurfaceUserIdentityStatus.Resolved,
            appUserId,
            subject,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a failure result for a missing external subject.
    /// </summary>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A missing-subject identity result.</returns>
    public static AppSurfaceUserIdentityResult MissingSubject(
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.MissingSubject, subject: null, message, metadata);
    }

    /// <summary>
    /// Creates a failure result for a malformed external subject.
    /// </summary>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A malformed-subject identity result.</returns>
    public static AppSurfaceUserIdentityResult MalformedSubject(
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.MalformedSubject, subject: null, message, metadata);
    }

    /// <summary>
    /// Creates a failure result for a disabled app user.
    /// </summary>
    /// <param name="subject">Optional external subject involved in the failed resolution.</param>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A disabled-app-user identity result.</returns>
    public static AppSurfaceUserIdentityResult DisabledAppUser(
        ExternalSubject? subject = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.DisabledAppUser, subject, message, metadata);
    }

    /// <summary>
    /// Creates a failure result for a stale or unknown session.
    /// </summary>
    /// <param name="subject">Optional external subject involved in the failed resolution.</param>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A stale-or-unknown-session identity result.</returns>
    public static AppSurfaceUserIdentityResult StaleOrUnknownSession(
        ExternalSubject? subject = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.StaleOrUnknownSession, subject, message, metadata);
    }

    /// <summary>
    /// Creates a failure result for duplicate mappings.
    /// </summary>
    /// <param name="subject">Optional external subject involved in the failed resolution.</param>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A duplicate-mapping identity result.</returns>
    public static AppSurfaceUserIdentityResult DuplicateMapping(
        ExternalSubject? subject = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.DuplicateMapping, subject, message, metadata);
    }

    /// <summary>
    /// Creates a failure result for an unavailable app-owned identity store.
    /// </summary>
    /// <param name="subject">Optional external subject involved in the failed resolution.</param>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A store-unavailable identity result.</returns>
    public static AppSurfaceUserIdentityResult StoreUnavailable(
        ExternalSubject? subject = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.StoreUnavailable, subject, message, metadata);
    }

    /// <summary>
    /// Creates a failure result when the app declines provisioning or attachment.
    /// </summary>
    /// <param name="subject">Optional external subject involved in the failed resolution.</param>
    /// <param name="message">Optional display-safe message supplied by the app resolver.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A provisioning-denied identity result.</returns>
    public static AppSurfaceUserIdentityResult ProvisioningDenied(
        ExternalSubject? subject = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Failure(AppSurfaceUserIdentityStatus.ProvisioningDenied, subject, message, metadata);
    }

    private static AppSurfaceUserIdentityResult Failure(
        AppSurfaceUserIdentityStatus status,
        ExternalSubject? subject,
        string? message,
        IReadOnlyDictionary<string, string>? metadata)
    {
        return new AppSurfaceUserIdentityResult(status, appUserId: null, subject, message, metadata);
    }

    private static void ValidateCombination(
        AppSurfaceUserIdentityStatus status,
        AppUserId? appUserId,
        ExternalSubject? subject)
    {
        if (status == AppSurfaceUserIdentityStatus.Resolved)
        {
            if (appUserId is null)
            {
                throw new ArgumentException("Resolved identity results require an app user id.", nameof(appUserId));
            }

            if (subject is null)
            {
                throw new ArgumentException("Resolved identity results require an external subject.", nameof(subject));
            }

            appUserId.Value.EnsureInitialized(nameof(appUserId));
            subject.Value.EnsureInitialized(nameof(subject));
            return;
        }

        if (appUserId is not null)
        {
            throw new ArgumentException("Failed identity results must not include an app user id.", nameof(appUserId));
        }

        if (subject is not null)
        {
            subject.Value.EnsureInitialized(nameof(subject));
        }
    }
}
