namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Provides scoped access to the current ASP.NET Core request mapped into AppSurface auth contracts.
/// </summary>
/// <remarks>
/// Resolve and use this service after ASP.NET Core authentication middleware has populated the current
/// <c>HttpContext.User</c>. The mapping is lazy and memoized for the scoped service instance.
/// </remarks>
public interface IAppSurfaceAspNetCoreAuthContextAccessor
{
    /// <summary>
    /// Gets the current request's AppSurface auth context mapping snapshot.
    /// </summary>
    /// <returns>
    /// A mapping snapshot. Anonymous callers resolve successfully; setup failures are exposed through
    /// <see cref="AppSurfaceAspNetCoreAuthContextSnapshot.Failure"/>.
    /// </returns>
    AppSurfaceAspNetCoreAuthContextSnapshot GetCurrentContext();
}
