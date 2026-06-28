using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.RazorWire.Auth;

/// <summary>
/// Stable UI states emitted by RazorWire auth projection helpers.
/// </summary>
public enum RazorWireAuthProjectionState
{
    /// <summary>
    /// The provider has not produced an auth result.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The requested host policy allowed the caller.
    /// </summary>
    Allowed = 1,

    /// <summary>
    /// The caller must authenticate before retrying.
    /// </summary>
    Anonymous = 2,

    /// <summary>
    /// The caller is authenticated but not allowed by the host policy.
    /// </summary>
    Forbidden = 3,

    /// <summary>
    /// Host auth setup is missing, misconfigured, or unavailable.
    /// </summary>
    SetupFailure = 4,

    /// <summary>
    /// A return or navigation target was unsafe.
    /// </summary>
    UnsafeNavigation = 5,

    /// <summary>
    /// The session could not be trusted or resolved.
    /// </summary>
    StaleOrUnknownSession = 6,
}

/// <summary>
/// Describes the safe UI projection for a passive AppSurface auth result.
/// </summary>
public sealed class RazorWireAuthProjection
{
    private RazorWireAuthProjection(
        RazorWireAuthProjectionState state,
        AppSurfaceAuthOutcome? outcome,
        AppSurfaceAuthReason? reason)
    {
        State = state;
        Outcome = outcome;
        Reason = reason;
    }

    /// <summary>
    /// Gets the stable RazorWire UI state token.
    /// </summary>
    public RazorWireAuthProjectionState State { get; }

    /// <summary>
    /// Gets the optional AppSurface auth outcome that produced this state.
    /// </summary>
    public AppSurfaceAuthOutcome? Outcome { get; }

    /// <summary>
    /// Gets the optional AppSurface auth reason that produced this state.
    /// </summary>
    public AppSurfaceAuthReason? Reason { get; }

    /// <summary>
    /// Gets the lowercase HTML token for <see cref="State"/>.
    /// </summary>
    public string StateToken => State switch
    {
        RazorWireAuthProjectionState.Allowed => "allowed",
        RazorWireAuthProjectionState.Anonymous => "anonymous",
        RazorWireAuthProjectionState.Forbidden => "forbidden",
        RazorWireAuthProjectionState.SetupFailure => "setup-failure",
        RazorWireAuthProjectionState.UnsafeNavigation => "unsafe-navigation",
        RazorWireAuthProjectionState.StaleOrUnknownSession => "stale-or-unknown-session",
        _ => "unknown",
    };

    /// <summary>
    /// Creates a projection from a passive AppSurface auth result.
    /// </summary>
    /// <param name="result">The auth result to project.</param>
    /// <returns>A stable UI projection.</returns>
    public static RazorWireAuthProjection FromResult(AppSurfaceAuthResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var state = result.Outcome switch
        {
            AppSurfaceAuthOutcome.Allowed => RazorWireAuthProjectionState.Allowed,
            AppSurfaceAuthOutcome.Challenge => RazorWireAuthProjectionState.Anonymous,
            AppSurfaceAuthOutcome.Forbid => RazorWireAuthProjectionState.Forbidden,
            AppSurfaceAuthOutcome.SetupFailure => RazorWireAuthProjectionState.SetupFailure,
            AppSurfaceAuthOutcome.UnsafeNavigation => RazorWireAuthProjectionState.UnsafeNavigation,
            AppSurfaceAuthOutcome.StaleOrUnknownSession => RazorWireAuthProjectionState.StaleOrUnknownSession,
            _ => RazorWireAuthProjectionState.Unknown,
        };

        return new RazorWireAuthProjection(state, result.Outcome, result.Reason);
    }

    /// <summary>
    /// Creates the safe not-yet-evaluated projection.
    /// </summary>
    /// <returns>An unknown-state projection without auth outcome details.</returns>
    public static RazorWireAuthProjection Unknown()
    {
        return new RazorWireAuthProjection(RazorWireAuthProjectionState.Unknown, null, null);
    }
}
