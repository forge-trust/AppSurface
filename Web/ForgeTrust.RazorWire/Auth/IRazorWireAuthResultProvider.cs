using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.RazorWire.Auth;

/// <summary>
/// Provides passive auth results for RazorWire auth projection helpers.
/// </summary>
/// <remarks>
/// Implementations adapt a host-owned auth system into <see cref="AppSurfaceAuthResult"/>. They must not sign users
/// in, sign users out, mutate cookies, challenge, forbid, redirect, or create authorization policies. RazorWire uses
/// the returned result only to choose server-rendered UI slots.
/// </remarks>
public interface IRazorWireAuthResultProvider
{
    /// <summary>
    /// Evaluates or retrieves the auth result for a RazorWire auth projection request.
    /// </summary>
    /// <param name="request">The requested host-owned policy/resource pair.</param>
    /// <param name="cancellationToken">Token observed before or during provider work.</param>
    /// <returns>A passive AppSurface auth result for the requested projection.</returns>
    Task<AppSurfaceAuthResult> AuthorizeAsync(
        RazorWireAuthRequest request,
        CancellationToken cancellationToken = default);
}
