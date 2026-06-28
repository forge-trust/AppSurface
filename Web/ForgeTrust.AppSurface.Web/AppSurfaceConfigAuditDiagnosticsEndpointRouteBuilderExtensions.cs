using System.Net.Mime;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Maps opt-in AppSurface configuration-audit HTTP diagnostics endpoints.
/// </summary>
public static class AppSurfaceConfigAuditDiagnosticsEndpointRouteBuilderExtensions
{
    private const string DocsLink = "Web/ForgeTrust.AppSurface.Web/README.md#config-audit-http-diagnostics";

    private static readonly JsonSerializerOptions ReportJsonOptions = new();

    /// <summary>
    /// Maps the AppSurface configuration-audit diagnostics endpoint at the default route.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder that receives the diagnostics endpoint.</param>
    /// <param name="authorizationPolicyName">The non-blank host-owned ASP.NET Core authorization policy required for the endpoint.</param>
    /// <returns>The route handler builder so hosts can add their own endpoint metadata.</returns>
    /// <remarks>
    /// The endpoint is never mapped automatically. Hosts must call this method deliberately, register AppSurface Config
    /// services, configure authentication and authorization middleware, and supply an authorization policy that is safe
    /// for support-sensitive deployment diagnostics.
    /// </remarks>
    public static RouteHandlerBuilder MapAppSurfaceConfigAuditDiagnostics(
        this IEndpointRouteBuilder endpoints,
        string authorizationPolicyName) =>
        endpoints.MapAppSurfaceConfigAuditDiagnostics(
            AppSurfaceConfigAuditDiagnosticsDefaults.DefaultRoute,
            authorizationPolicyName);

    /// <summary>
    /// Maps the AppSurface configuration-audit diagnostics endpoint at a custom route.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder that receives the diagnostics endpoint.</param>
    /// <param name="pattern">The non-blank route pattern for the endpoint.</param>
    /// <param name="authorizationPolicyName">The non-blank host-owned ASP.NET Core authorization policy required for the endpoint.</param>
    /// <returns>The route handler builder so hosts can add their own endpoint metadata.</returns>
    /// <remarks>
    /// The endpoint returns the active host's sanitized <see cref="ConfigAuditReport"/> as JSON and sets no-store
    /// response headers. It is excluded from API description by default because redacted audit reports can still expose
    /// support-sensitive provider names, paths, configuration keys, and deployment structure.
    /// </remarks>
    public static RouteHandlerBuilder MapAppSurfaceConfigAuditDiagnostics(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string authorizationPolicyName)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicyName);

        return endpoints
            .MapGet(pattern, CreateReportResult)
            .WithDisplayName("AppSurface configuration audit diagnostics")
            .ExcludeFromDescription()
            .RequireAuthorization(authorizationPolicyName);
    }

    private static IResult CreateReportResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        SetNoStoreHeaders(httpContext);

        var services = httpContext.RequestServices;
        var reporter = services.GetService<IConfigAuditReporter>();
        var environmentProvider = services.GetService<IEnvironmentProvider>();

        if (reporter is null)
        {
            return CreateSetupProblem(
                "AppSurface config audit services are unavailable.",
                "Configuration audit HTTP diagnostics could not start.",
                "The request service provider does not contain an IConfigAuditReporter.",
                "Register AppSurfaceConfigModule before mapping the diagnostics endpoint.");
        }

        if (environmentProvider is null)
        {
            return CreateSetupProblem(
                "AppSurface environment services are unavailable.",
                "Configuration audit HTTP diagnostics could not determine the active AppSurface environment.",
                "The request service provider does not contain an IEnvironmentProvider.",
                "Register the normal AppSurface host environment services before mapping the diagnostics endpoint.");
        }

        string? environment;
        try
        {
            environment = environmentProvider.Environment;
        }
        catch (Exception ex) when (IsNonFatalDiagnosticsFailure(ex))
        {
            return CreateSetupProblem(
                "AppSurface environment services failed.",
                "Configuration audit HTTP diagnostics could not determine the active AppSurface environment.",
                "The active environment provider threw while reading the environment name.",
                "Fix the host environment provider, then retry the diagnostics request.");
        }

        if (string.IsNullOrWhiteSpace(environment))
        {
            return CreateSetupProblem(
                "AppSurface environment is empty.",
                "Configuration audit HTTP diagnostics could not determine the active AppSurface environment.",
                "The active environment provider returned an empty environment name.",
                "Set the AppSurface host environment before requesting diagnostics.");
        }

        ConfigAuditReport report;
        try
        {
            report = reporter.GetReport(environment);
        }
        catch (Exception ex) when (IsNonFatalDiagnosticsFailure(ex))
        {
            return CreateRuntimeProblem(
                "AppSurface config audit failed.",
                "Configuration audit HTTP diagnostics could not build the active environment report.",
                "The config audit reporter failed while building the sanitized report.",
                "Inspect host configuration providers and local diagnostics output, then retry the request.");
        }

        try
        {
            var json = JsonSerializer.Serialize(report, ReportJsonOptions);
            return Results.Text(json, MediaTypeNames.Application.Json, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return CreateRuntimeProblem(
                "AppSurface config audit JSON failed.",
                "Configuration audit HTTP diagnostics could not serialize the sanitized report.",
                "The report contained a value that System.Text.Json could not serialize with the AppSurface support-artifact JSON contract.",
                "Verify the report uses the public ConfigAuditReport model and retry the request.");
        }
    }

    private static void SetNoStoreHeaders(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
    }

    private static IResult CreateSetupProblem(string title, string problem, string cause, string fix) =>
        CreateProblem(title, problem, cause, fix);

    private static IResult CreateRuntimeProblem(string title, string problem, string cause, string fix) =>
        CreateProblem(title, problem, cause, fix);

    private static IResult CreateProblem(string title, string problem, string cause, string fix) =>
        Results.Problem(
            title: title,
            detail: $"Problem: {problem} Cause: {cause} Fix: {fix} Docs: {DocsLink}",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>
            {
                ["problem"] = problem,
                ["cause"] = cause,
                ["fix"] = fix,
                ["docsLink"] = DocsLink
            });

    private static bool IsNonFatalDiagnosticsFailure(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException;
}
