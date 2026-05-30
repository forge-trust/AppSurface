namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Defines the high-level auth outcome AppSurface modules can understand without owning host authentication.
/// </summary>
public enum AppSurfaceAuthOutcome
{
    /// <summary>
    /// The requested operation is allowed.
    /// </summary>
    Allowed = 0,

    /// <summary>
    /// The caller should authenticate before the operation can proceed.
    /// </summary>
    Challenge = 1,

    /// <summary>
    /// The authenticated caller is not permitted to perform the operation.
    /// </summary>
    Forbid = 2,

    /// <summary>
    /// Host auth services, policies, or configuration are missing or unusable.
    /// </summary>
    SetupFailure = 3,

    /// <summary>
    /// A navigation or return target was unsafe.
    /// </summary>
    UnsafeNavigation = 4,

    /// <summary>
    /// The session is stale, expired, missing, or cannot be resolved.
    /// </summary>
    StaleOrUnknownSession = 5,
}

/// <summary>
/// Defines the concrete reason associated with an AppSurface auth outcome.
/// </summary>
public enum AppSurfaceAuthReason
{
    /// <summary>
    /// No failure reason applies.
    /// </summary>
    None = 0,

    /// <summary>
    /// The caller is not authenticated.
    /// </summary>
    Unauthenticated = 1,

    /// <summary>
    /// The authenticated caller did not satisfy the required host-owned policy.
    /// </summary>
    Forbidden = 2,

    /// <summary>
    /// A required host-owned policy name or policy definition is missing.
    /// </summary>
    MissingPolicy = 3,

    /// <summary>
    /// Required host-owned auth services are missing.
    /// </summary>
    MissingServices = 4,

    /// <summary>
    /// A requested return or navigation target is unsafe.
    /// </summary>
    UnsafeReturnUrl = 5,

    /// <summary>
    /// The session is stale, expired, missing, or cannot be resolved.
    /// </summary>
    StaleOrUnknownSession = 6,
}

/// <summary>
/// Represents a passive AppSurface auth decision.
/// </summary>
/// <remarks>
/// <see cref="AppSurfaceAuthResult"/> describes an auth decision; it does not challenge, forbid, redirect, evaluate
/// policies, sign users in, or sign users out. Host-specific packages map these outcomes to platform behavior.
/// </remarks>
public sealed class AppSurfaceAuthResult
{
    private AppSurfaceAuthResult(
        AppSurfaceAuthOutcome outcome,
        AppSurfaceAuthReason reason,
        AppSurfaceAuthContext? context,
        string? message,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ValidateCombination(outcome, reason);
        Outcome = outcome;
        Reason = reason;
        Context = context;
        Message = AppSurfaceAuthMetadata.NormalizeOptionalText(message);
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets the high-level auth outcome.
    /// </summary>
    public AppSurfaceAuthOutcome Outcome { get; }

    /// <summary>
    /// Gets the concrete reason associated with <see cref="Outcome"/>.
    /// </summary>
    public AppSurfaceAuthReason Reason { get; }

    /// <summary>
    /// Gets the optional auth context that was evaluated.
    /// </summary>
    public AppSurfaceAuthContext? Context { get; }

    /// <summary>
    /// Gets an optional display-safe message supplied by the host adapter.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets a value indicating whether the outcome allows the requested operation.
    /// </summary>
    public bool IsAllowed => Outcome == AppSurfaceAuthOutcome.Allowed;

    /// <summary>
    /// Gets a value indicating whether the caller should authenticate before retrying.
    /// </summary>
    public bool RequiresAuthentication => Outcome == AppSurfaceAuthOutcome.Challenge;

    /// <summary>
    /// Gets a value indicating whether the outcome represents host setup or configuration failure.
    /// </summary>
    public bool IsConfigurationFailure => Outcome == AppSurfaceAuthOutcome.SetupFailure;

    /// <summary>
    /// Creates a result that allows the requested operation.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>An allowed auth result.</returns>
    public static AppSurfaceAuthResult Allowed(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.Allowed,
            AppSurfaceAuthReason.None,
            context,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a result indicating that the caller should authenticate before retrying.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A challenge auth result.</returns>
    public static AppSurfaceAuthResult Challenge(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.Challenge,
            AppSurfaceAuthReason.Unauthenticated,
            context,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a result indicating that the caller is not authenticated.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A challenge auth result.</returns>
    public static AppSurfaceAuthResult Unauthenticated(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Challenge(context, message, metadata);
    }

    /// <summary>
    /// Creates a result indicating that the authenticated caller is forbidden.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A forbidden auth result.</returns>
    public static AppSurfaceAuthResult Forbid(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.Forbid,
            AppSurfaceAuthReason.Forbidden,
            context,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a result indicating that the authenticated caller is forbidden.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A forbidden auth result.</returns>
    public static AppSurfaceAuthResult Forbidden(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return Forbid(context, message, metadata);
    }

    /// <summary>
    /// Creates a setup-failure result for a missing host-owned policy.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A setup-failure auth result.</returns>
    public static AppSurfaceAuthResult MissingPolicy(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.SetupFailure,
            AppSurfaceAuthReason.MissingPolicy,
            context,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a setup-failure result for missing host-owned auth services.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A setup-failure auth result.</returns>
    public static AppSurfaceAuthResult MissingServices(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.SetupFailure,
            AppSurfaceAuthReason.MissingServices,
            context,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a result for an unsafe return or navigation target.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>An unsafe-navigation auth result.</returns>
    public static AppSurfaceAuthResult UnsafeReturnUrl(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.UnsafeNavigation,
            AppSurfaceAuthReason.UnsafeReturnUrl,
            context,
            message,
            metadata);
    }

    /// <summary>
    /// Creates a result for stale, expired, missing, or unresolved session state.
    /// </summary>
    /// <param name="context">Optional auth context that was evaluated.</param>
    /// <param name="message">Optional display-safe message supplied by the host adapter.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    /// <returns>A stale-or-unknown-session auth result.</returns>
    public static AppSurfaceAuthResult StaleOrUnknownSession(
        AppSurfaceAuthContext? context = null,
        string? message = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new AppSurfaceAuthResult(
            AppSurfaceAuthOutcome.StaleOrUnknownSession,
            AppSurfaceAuthReason.StaleOrUnknownSession,
            context,
            message,
            metadata);
    }

    internal static void ValidateCombination(AppSurfaceAuthOutcome outcome, AppSurfaceAuthReason reason)
    {
        var isValid = outcome switch
        {
            AppSurfaceAuthOutcome.Allowed => reason == AppSurfaceAuthReason.None,
            AppSurfaceAuthOutcome.Challenge => reason == AppSurfaceAuthReason.Unauthenticated,
            AppSurfaceAuthOutcome.Forbid => reason == AppSurfaceAuthReason.Forbidden,
            AppSurfaceAuthOutcome.SetupFailure => reason is AppSurfaceAuthReason.MissingPolicy
                or AppSurfaceAuthReason.MissingServices,
            AppSurfaceAuthOutcome.UnsafeNavigation => reason == AppSurfaceAuthReason.UnsafeReturnUrl,
            AppSurfaceAuthOutcome.StaleOrUnknownSession => reason == AppSurfaceAuthReason.StaleOrUnknownSession,
            _ => false,
        };

        if (!isValid)
        {
            throw new ArgumentException(
                $"Auth outcome '{outcome}' cannot be combined with reason '{reason}'.",
                nameof(reason));
        }
    }
}
