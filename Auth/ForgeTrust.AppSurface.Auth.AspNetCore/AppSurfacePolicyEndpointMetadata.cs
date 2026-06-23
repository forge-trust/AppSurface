namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Describes an AppSurface endpoint policy requirement attached by <see cref="AppSurfaceEndpointConventionBuilderExtensions.RequireSurfacePolicy{TBuilder}" />.
/// </summary>
/// <remarks>
/// This metadata is diagnostic only. ASP.NET Core authorization policies remain host-owned and are evaluated by
/// <see cref="IAppSurfaceAspNetCorePolicyEvaluator" /> when the endpoint filter runs.
/// </remarks>
public sealed class AppSurfacePolicyEndpointMetadata
{
    /// <summary>
    /// Creates endpoint metadata for an AppSurface policy requirement.
    /// </summary>
    /// <param name="policyName">The non-blank ASP.NET Core authorization policy name evaluated for the endpoint.</param>
    public AppSurfacePolicyEndpointMetadata(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        PolicyName = policyName;
    }

    /// <summary>
    /// Gets the ASP.NET Core authorization policy name evaluated for the endpoint.
    /// </summary>
    public string PolicyName { get; }
}
