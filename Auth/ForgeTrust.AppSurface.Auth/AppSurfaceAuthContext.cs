namespace ForgeTrust.AppSurface.Auth;

/// <summary>
/// Captures the passive user and session context available to AppSurface auth-aware modules.
/// </summary>
/// <remarks>
/// A context with no <see cref="User"/> is a valid anonymous context. The context does not evaluate policies, read the
/// current request, or wrap ASP.NET Core <c>ClaimsPrincipal</c>; host-specific adapters own those mappings.
/// </remarks>
public sealed class AppSurfaceAuthContext
{
    /// <summary>
    /// Creates an auth context description for AppSurface auth-aware modules.
    /// </summary>
    /// <param name="user">Optional authenticated user description.</param>
    /// <param name="session">Optional session description associated with the user.</param>
    /// <param name="metadata">Optional display or diagnostic metadata copied with ordinal keys.</param>
    public AppSurfaceAuthContext(
        AppSurfaceUser? user = null,
        AppSurfaceSession? session = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        User = user;
        Session = session;
        Metadata = AppSurfaceAuthMetadata.Normalize(metadata, nameof(metadata));
    }

    /// <summary>
    /// Gets an anonymous auth context with no user, no session, and no metadata.
    /// </summary>
    public static AppSurfaceAuthContext Anonymous { get; } = new();

    /// <summary>
    /// Gets the optional authenticated user description.
    /// </summary>
    public AppSurfaceUser? User { get; }

    /// <summary>
    /// Gets the optional session description.
    /// </summary>
    public AppSurfaceSession? Session { get; }

    /// <summary>
    /// Gets copied metadata that can help adapters or diagnostics preserve host-specific context.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets a value indicating whether the context contains a user description.
    /// </summary>
    public bool IsAuthenticated => User is not null;
}
