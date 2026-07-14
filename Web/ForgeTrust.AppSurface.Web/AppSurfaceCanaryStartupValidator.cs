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

    public AppSurfaceCanaryStartupValidator(AppSurfaceCanaryMappingState mappingState)
    {
        _mappingState = mappingState;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            next(app);
            Validate();
        };
    }

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
