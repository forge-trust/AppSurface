using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace ForgeTrust.AppSurface.Auth.AspNetCore;

/// <summary>
/// Adds AppSurface auth policy helpers to ASP.NET Core endpoint builders.
/// </summary>
public static class AppSurfaceEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Requires an AppSurface-shaped ASP.NET Core policy result for a Minimal API endpoint or route group.
    /// </summary>
    /// <typeparam name="TBuilder">Endpoint convention builder type being configured.</typeparam>
    /// <param name="builder">Endpoint builder that receives the policy filter and metadata.</param>
    /// <param name="policyName">The non-blank host-owned ASP.NET Core authorization policy name to evaluate.</param>
    /// <returns>The same endpoint builder for chaining.</returns>
    /// <remarks>
    /// The helper evaluates the named host policy through <see cref="IAppSurfaceAspNetCorePolicyEvaluator" /> and maps
    /// failures to API-safe ProblemDetails responses. It does not call ASP.NET Core challenge or forbid handlers, does
    /// not redirect, and does not replace native <c>RequireAuthorization(...)</c> for browser flows. The endpoint is
    /// marked as anonymous for ASP.NET Core authorization middleware so host fallback policies do not run before the
    /// AppSurface endpoint filter.
    /// </remarks>
    public static TBuilder RequireSurfacePolicy<TBuilder>(this TBuilder builder, string policyName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        builder.Add(endpointBuilder =>
        {
            endpointBuilder.Metadata.Add(new AppSurfacePolicyEndpointMetadata(policyName));
            endpointBuilder.Metadata.Add(new AllowAnonymousAttribute());
            endpointBuilder.FilterFactories.Add((_, next) =>
            {
                var filter = new AppSurfacePolicyEndpointFilter(policyName);
                return context => filter.InvokeAsync(context, next);
            });
        });
        return builder;
    }
}
