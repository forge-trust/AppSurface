using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Evaluates host-owned ASP.NET Core authorization policies and maps the outcome to AppSurface auth results.
/// </summary>
/// <remarks>
/// This service does not create policies, register schemes, challenge responses, forbid responses, redirect callers, or
/// mutate cookies. It evaluates policies registered by the host and returns a passive AppSurface result for downstream
/// AppSurface surfaces.
/// </remarks>
public interface IAppSurfaceAspNetCorePolicyEvaluator
{
    /// <summary>
    /// Evaluates a named ASP.NET Core authorization policy for the current HTTP request.
    /// </summary>
    /// <param name="policyName">Non-empty host-owned policy name.</param>
    /// <param name="resource">
    /// Optional authorization resource passed to ASP.NET Core policy evaluation. When null, the current
    /// <c>HttpContext</c> is used.
    /// </param>
    /// <param name="cancellationToken">Token observed before and during policy lookup.</param>
    /// <returns>A passive AppSurface auth result for the policy decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="policyName"/> is null, empty, or whitespace.</exception>
    /// <remarks>
    /// ASP.NET Core authorization execution itself does not expose a cancellation-token overload. Cancellation is
    /// observed before policy lookup, during asynchronous policy lookup, and immediately before policy evaluation.
    /// Exceptions from host policy providers or handlers are allowed to propagate so host bugs are not hidden as denials.
    /// </remarks>
    Task<AppSurfaceAuthResult> AuthorizeAsync(
        string policyName,
        object? resource = null,
        CancellationToken cancellationToken = default);
}
