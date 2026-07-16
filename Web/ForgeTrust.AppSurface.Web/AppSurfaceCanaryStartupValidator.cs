using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Validates the final endpoint pipeline before server startup, rejecting a relocated framework route or host-owned
/// routes that could shadow or overlap the reserved named-canary namespace.
/// </summary>
internal sealed class AppSurfaceCanaryStartupValidator : IStartupFilter
{
    private const string ReservedPrefix = "/_appsurface/canaries";
    private readonly AppSurfaceCanaryMappingState _mappingState;

    /// <summary>Initializes the validator over the process-wide mapping state.</summary>
    /// <param name="mappingState">The mapping state that exposes captured endpoint data sources after mapping.</param>
    public AppSurfaceCanaryStartupValidator(AppSurfaceCanaryMappingState mappingState)
    {
        _mappingState = mappingState;
    }

    /// <summary>Appends reserved-route validation after the host finishes composing its pipeline.</summary>
    /// <param name="next">The remaining startup-filter pipeline.</param>
    /// <returns>A pipeline action that invokes <paramref name="next"/> before validating final endpoints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            next(app);
            Validate();
        };
    }

    /// <summary>
    /// Validates that the framework route retained its fixed location and that no host route overlaps the reserved
    /// named-canary namespace. The method is inactive before mapping captures endpoint data sources.
    /// </summary>
    /// <exception cref="InvalidOperationException">The framework route was relocated or another route overlaps the reserved namespace.</exception>
    internal void Validate()
    {
        var dataSources = _mappingState.DataSources;
        if (dataSources is null)
        {
            return;
        }

        foreach (var endpoint in dataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>())
        {
            var route = Normalize(endpoint.RoutePattern.RawText);
            if (endpoint.Metadata.GetMetadata<AppSurfaceCanaryRouteMetadata>() is not null)
            {
                if (!string.Equals(
                    route,
                    AppSurfaceCanaryEndpointDefaults.RoutePattern,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"ASCAN115: The AppSurface named-canary endpoint resolved to '{route}' instead of the fixed route '{AppSurfaceCanaryEndpointDefaults.RoutePattern}'. Map named canaries at the application root, outside route groups.");
                }

                continue;
            }

            if (string.Equals(route, ReservedPrefix, StringComparison.OrdinalIgnoreCase)
                || route.StartsWith($"{ReservedPrefix}/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"ASCAN115: Endpoint route '{route}' conflicts with the reserved AppSurface named-canary namespace '{ReservedPrefix}'. Move the host endpoint outside that namespace.");
            }
        }
    }

    /// <summary>Normalizes a defensive route value for ordinal reserved-namespace comparison.</summary>
    /// <param name="route">The possibly blank route text.</param>
    /// <returns>A leading-slash route with trailing slashes removed, or <c>/</c> for a blank value.</returns>
    internal static string Normalize(string? route)
    {
        var value = string.IsNullOrWhiteSpace(route) ? "/" : route.Trim();
        if (!value.StartsWith('/'))
        {
            value = $"/{value}";
        }

        return value.Length > 1 ? value.TrimEnd('/') : value;
    }
}
