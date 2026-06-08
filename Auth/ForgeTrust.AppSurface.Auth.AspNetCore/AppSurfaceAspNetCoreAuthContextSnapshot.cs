using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Captures the result of mapping the current ASP.NET Core request principal into an AppSurface auth context.
/// </summary>
/// <remarks>
/// Anonymous callers resolve successfully with <see cref="AppSurfaceAuthContext.Anonymous"/>. A non-null
/// <see cref="Failure"/> means the adapter could not safely map the request, for example because there is no current
/// HTTP request or an authenticated principal did not contain a configured subject claim.
/// </remarks>
public sealed class AppSurfaceAspNetCoreAuthContextSnapshot
{
    /// <summary>
    /// Creates a context mapping snapshot.
    /// </summary>
    /// <param name="context">Mapped AppSurface auth context. Failure snapshots use an anonymous context.</param>
    /// <param name="failure">Optional setup failure that explains why the context could not be mapped safely.</param>
    public AppSurfaceAspNetCoreAuthContextSnapshot(
        AppSurfaceAuthContext context,
        AppSurfaceAuthResult? failure = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Failure = failure;
    }

    /// <summary>
    /// Gets the mapped AppSurface auth context.
    /// </summary>
    public AppSurfaceAuthContext Context { get; }

    /// <summary>
    /// Gets the setup failure when the context could not be mapped safely.
    /// </summary>
    public AppSurfaceAuthResult? Failure { get; }

    /// <summary>
    /// Gets a value indicating whether the current request principal mapped successfully.
    /// </summary>
    public bool Succeeded => Failure is null;
}
