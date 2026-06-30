using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ForgeTrust.AppSurface.Web;

internal static class HealthEndpointMapper
{
    public static void Map(IEndpointRouteBuilder endpoints, HealthOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        ValidateReservedPathsAreAvailable(endpoints, options);
        MapEndpoint(
            endpoints,
            options.HealthPath,
            "AppSurface health",
            _ => true);
        MapEndpoint(
            endpoints,
            options.ReadyPath,
            "AppSurface readiness",
            registration => registration.Tags.Contains(options.ReadyTag, StringComparer.Ordinal));
    }

    private static void MapEndpoint(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string displayName,
        Func<HealthCheckRegistration, bool> predicate)
    {
        endpoints.MapMethods(
                pattern,
                [HttpMethods.Get, HttpMethods.Head],
                (Func<HttpContext, HealthCheckService, Task>)((httpContext, healthCheckService) =>
                    WriteHealthResultAsync(httpContext, healthCheckService, predicate)))
            .WithDisplayName(displayName)
            .WithMetadata(new AllowAnonymousAttribute())
            .ExcludeFromDescription();
    }

    private static async Task WriteHealthResultAsync(
        HttpContext httpContext,
        HealthCheckService healthCheckService,
        Func<HealthCheckRegistration, bool> predicate)
    {
        SetNoStoreHeaders(httpContext);
        var report = await healthCheckService.CheckHealthAsync(predicate, httpContext.RequestAborted);
        httpContext.Response.StatusCode = report.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.ContentType = $"{MediaTypeNames.Text.Plain}; charset=utf-8";

        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        await httpContext.Response.WriteAsync(report.Status.ToString(), Encoding.UTF8, httpContext.RequestAborted);
    }

    private static void ValidateReservedPathsAreAvailable(IEndpointRouteBuilder endpoints, HealthOptions options)
    {
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HealthOptionsValidator.NormalizeRoutePattern(options.HealthPath),
            HealthOptionsValidator.NormalizeRoutePattern(options.ReadyPath),
        };

        var conflict = FindReservedPathConflict(endpoints, reservedPaths);

        if (conflict is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"AppSurface health endpoint path conflict: the configured health or readiness path '{conflict.ReservedPath}' conflicts with existing endpoint '{conflict.EndpointPath}'. "
            + "Change WebOptions.Health.HealthPath or WebOptions.Health.ReadyPath, disable WebOptions.Health, or move the existing endpoint.");
    }

    private static ReservedPathConflict? FindReservedPathConflict(
        IEndpointRouteBuilder endpoints,
        HashSet<string> reservedPaths)
    {
        foreach (var endpoint in endpoints.DataSources.SelectMany(dataSource => dataSource.Endpoints).OfType<RouteEndpoint>())
        {
            var rawText = endpoint.RoutePattern.RawText;
            if (string.IsNullOrWhiteSpace(rawText))
            {
                continue;
            }

            var endpointPath = HealthOptionsValidator.NormalizeRoutePattern(rawText);
            var reservedPath = reservedPaths.FirstOrDefault(path =>
                string.Equals(endpointPath, path, StringComparison.OrdinalIgnoreCase)
                || IsBrowserStatusPageReservedRouteConflict(endpointPath, path));
            if (reservedPath is not null)
            {
                return new ReservedPathConflict(endpointPath, reservedPath);
            }
        }

        return null;
    }

    private static bool IsBrowserStatusPageReservedRouteConflict(string endpointPath, string reservedPath)
    {
        if (!string.Equals(endpointPath, BrowserStatusPageDefaults.ReservedRoutePattern, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(reservedPath, BrowserStatusPageDefaults.ReservedUnauthorizedRoute, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reservedPath, BrowserStatusPageDefaults.ReservedForbiddenRoute, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reservedPath, BrowserStatusPageDefaults.ReservedNotFoundRoute, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetNoStoreHeaders(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
    }

    private sealed record ReservedPathConflict(string EndpointPath, string ReservedPath);
}
