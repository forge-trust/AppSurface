using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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

        var convention = new AppSurfacePolicyEndpointConvention(policyName);
        builder.Add(convention.Apply);
        return builder;
    }

    private sealed class AppSurfacePolicyEndpointConvention
    {
        private readonly string _policyName;

        public AppSurfacePolicyEndpointConvention(string policyName)
        {
            _policyName = policyName;
        }

        public void Apply(EndpointBuilder endpointBuilder)
        {
            endpointBuilder.Metadata.Add(new AppSurfacePolicyEndpointMetadata(_policyName));
            endpointBuilder.Metadata.Add(new AllowAnonymousAttribute());
            endpointBuilder.FilterFactories.Add(CreateFilter);
        }

        private EndpointFilterDelegate CreateFilter(EndpointFilterFactoryContext _, EndpointFilterDelegate next)
        {
            var filter = new AppSurfacePolicyEndpointFilter(_policyName);
            return new AppSurfacePolicyEndpointFilterAdapter(filter, next).InvokeAsync;
        }
    }

    private sealed class AppSurfacePolicyEndpointFilterAdapter
    {
        private readonly AppSurfacePolicyEndpointFilter _filter;
        private readonly EndpointFilterDelegate _next;

        public AppSurfacePolicyEndpointFilterAdapter(AppSurfacePolicyEndpointFilter filter, EndpointFilterDelegate next)
        {
            _filter = filter;
            _next = next;
        }

        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context)
        {
            return _filter.InvokeAsync(context, _next);
        }
    }
}
