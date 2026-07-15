using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Maps the explicit protected HTTP adapter for registered named canaries.
/// </summary>
public static partial class AppSurfaceCanaryEndpointRouteBuilderExtensions
{
    private const string DocsLink = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints";
    private const int MaximumMarkerUtf8Bytes = 256;
    private static readonly EventId EvaluationFailureEvent = new(62301, "AppSurfaceCanaryEvaluationFailed");
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps <c>GET /_appsurface/canaries/{name}</c> with a required host-owned authorization policy.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder that receives the fixed route family.</param>
    /// <param name="authorizationPolicyName">The nonblank host-owned ASP.NET Core authorization policy name.</param>
    /// <param name="configure">An optional callback that controls completed-result HTTP status mapping.</param>
    /// <returns>The route handler builder so the host can add ordinary endpoint conventions.</returns>
    /// <remarks>
    /// The default response mode returns 200 for <see cref="AppSurfaceCanaryStatus.Pass"/> and 503 for every other
    /// completed status. Choose <see cref="AppSurfaceCanaryCompletedResponseMode.AlwaysOk"/> only for authenticated
    /// diagnostic consumers that always parse the JSON status. The mapper never configures authentication,
    /// authorization, retries, timeouts, triggers, or readiness behavior. Do not append anonymous metadata: the handler
    /// detects it and fails closed before name lookup or evaluator invocation. Call this method on the application root;
    /// route groups are rejected because they would relocate the fixed route.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="ArgumentException">The policy name is blank or configured response mode is undefined.</exception>
    /// <exception cref="InvalidOperationException">Registrations or authorization services are missing, names are duplicated, the route family was already mapped, or mapping through a route group would relocate the fixed route.</exception>
    public static RouteHandlerBuilder MapAppSurfaceCanaries(
        this IEndpointRouteBuilder endpoints,
        string authorizationPolicyName,
        Action<AppSurfaceCanaryEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(authorizationPolicyName))
        {
            throw new ArgumentException(
                "ASCAN111: A nonblank host-owned authorization policy name is required.",
                nameof(authorizationPolicyName));
        }

        if (endpoints is RouteGroupBuilder)
        {
            throw new InvalidOperationException(
                $"ASCAN115: MapAppSurfaceCanaries must be called on the application root so the fixed route remains '{AppSurfaceCanaryEndpointDefaults.RoutePattern}'. Do not map named canaries through a route group.");
        }

        var services = endpoints.ServiceProvider;
        if (!services.GetServices<AppSurfaceCanaryDescriptor>().Any())
        {
            throw new InvalidOperationException(
                "ASCAN112: No named canaries are registered. Call AddAppSurfaceCanary<TEvaluator> before mapping the endpoint.");
        }

        var serviceProbe = services.GetService<IServiceProviderIsService>();
        if (serviceProbe is null
            || !serviceProbe.IsService(typeof(IAuthorizationService))
            || !serviceProbe.IsService(typeof(IAuthorizationPolicyProvider)))
        {
            throw new InvalidOperationException(
                "ASCAN113: ASP.NET Core authorization services are unavailable. Register authorization and a host-owned policy before mapping named canaries.");
        }

        var options = new AppSurfaceCanaryEndpointOptions();
        configure?.Invoke(options);
        if (!Enum.IsDefined(options.CompletedResponseMode))
        {
            throw new ArgumentException(
                "ASCAN116: CompletedResponseMode must be a defined AppSurfaceCanaryCompletedResponseMode value.",
                nameof(configure));
        }

        var responseMode = options.CompletedResponseMode;

        _ = services.GetRequiredService<AppSurfaceCanaryRegistry>();
        var mappingState = services.GetRequiredService<AppSurfaceCanaryMappingState>();
        if (!mappingState.TryClaim(endpoints.DataSources))
        {
            throw new InvalidOperationException(
                "ASCAN114: MapAppSurfaceCanaries may be called only once for a host.");
        }

        return endpoints
            .MapGet(
                AppSurfaceCanaryEndpointDefaults.RoutePattern,
                (Func<HttpContext, Task>)(httpContext => HandleRequestAsync(
                    httpContext,
                    responseMode,
                    authorizationPolicyName)))
            .WithDisplayName("AppSurface named canary evaluation")
            .WithMetadata(AppSurfaceCanaryRouteMetadata.Instance)
            .ExcludeFromDescription()
            .RequireAuthorization(authorizationPolicyName);
    }

    private static async Task HandleRequestAsync(
        HttpContext httpContext,
        AppSurfaceCanaryCompletedResponseMode responseMode,
        string authorizationPolicyName)
    {
        var endpoint = httpContext.GetEndpoint();
        var hasRequiredPolicy = endpoint?.Metadata
            .OfType<IAuthorizeData>()
            .Any(data => string.Equals(data.Policy, authorizationPolicyName, StringComparison.Ordinal)) == true;
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null || !hasRequiredPolicy)
        {
            SetNoStoreHeaders(httpContext);
            await CreateProblem(
                    StatusCodes.Status500InternalServerError,
                    "AppSurface canary evaluation failed",
                    "ASCAN113",
                    "The named canary endpoint is not safely protected.",
                    "Anonymous endpoint metadata or a removed policy bypassed the required authorization policy.",
                    "Remove AllowAnonymous metadata and retain the host-owned operator policy.")
                .ExecuteAsync(httpContext);
            return;
        }

        SetNoStoreHeaders(httpContext);
        var name = Convert.ToString(httpContext.Request.RouteValues["name"], CultureInfo.InvariantCulture) ?? string.Empty;
        var runner = httpContext.RequestServices.GetRequiredService<AppSurfaceCanaryEvaluationRunner>();
        if (!runner.TryGetDescriptor(name, out var descriptor))
        {
            await CreateProblem(
                    StatusCodes.Status404NotFound,
                    "AppSurface canary not found",
                    "ASCAN203",
                    "The requested named canary is not registered.",
                    "The route name did not match a registered canary exactly.",
                    "Register the canary or correct the exact lowercase name.")
                .ExecuteAsync(httpContext);
            return;
        }

        if (!TryReadMarker(httpContext, descriptor.MarkerRequired, out var marker, out var markerProblem))
        {
            await markerProblem!.ExecuteAsync(httpContext);
            return;
        }

        if (!TryReadFreshSince(httpContext, descriptor.FreshSinceRequired, out var freshSince, out var freshnessProblem))
        {
            await freshnessProblem!.ExecuteAsync(httpContext);
            return;
        }

        AppSurfaceCanaryResult result;
        try
        {
            result = await runner.EvaluateAsync(
                descriptor,
                marker,
                freshSince,
                httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsNonFatalEvaluationFailure(exception))
        {
            httpContext.RequestServices
                .GetRequiredService<ILogger<AppSurfaceCanaryEvaluationRunner>>()
                .LogError(
                    EvaluationFailureEvent,
                    "Named canary {CanaryName} failed with diagnostic {DiagnosticCode} and exception type {ExceptionType}.",
                    descriptor.Name,
                    "ASCAN301",
                    exception.GetType().FullName);

            await CreateProblem(
                    StatusCodes.Status500InternalServerError,
                    "AppSurface canary evaluation failed",
                    "ASCAN301",
                    "The named canary could not be evaluated.",
                    "The evaluator could not be activated, threw, was canceled independently, or returned no result.",
                    "Inspect the evaluator and its dependencies in host-local diagnostics, then retry under caller policy.")
                .ExecuteAsync(httpContext);
            return;
        }

        var statusText = ToWireStatus(result.Status);
        var statusCode = responseMode == AppSurfaceCanaryCompletedResponseMode.AlwaysOk
            || result.Status == AppSurfaceCanaryStatus.Pass
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;

        await Results.Json(
                new AppSurfaceCanaryResponse(name, statusText),
                options: ResponseJsonOptions,
                statusCode: statusCode,
                contentType: $"{MediaTypeNames.Application.Json}; charset={Encoding.UTF8.WebName}")
            .ExecuteAsync(httpContext);
    }

    internal static bool TryReadMarker(
        HttpContext httpContext,
        bool required,
        out string? marker,
        out IResult? problem)
    {
        var values = httpContext.Request.Headers[AppSurfaceCanaryHeaderNames.Marker];
        marker = null;
        problem = null;

        if (values.Count > 1)
        {
            problem = InvalidHeaderProblem("ASCAN202", "The marker header was supplied more than once.", "Send exactly one marker header value.");
            return false;
        }

        var value = values.Count == 0 ? null : values[0];
        if (value?.Any(char.IsControl) == true)
        {
            problem = InvalidHeaderProblem("ASCAN202", "The marker contains a control character.", "Send an opaque marker without control characters.");
            return false;
        }

        marker = string.IsNullOrWhiteSpace(value) ? null : value;
        if (required && marker is null)
        {
            problem = InvalidHeaderProblem("ASCAN201", "A required canary header was missing.", $"Supply {AppSurfaceCanaryHeaderNames.Marker} and retry.");
            return false;
        }

        if (marker is not null && Encoding.UTF8.GetByteCount(marker) > MaximumMarkerUtf8Bytes)
        {
            problem = InvalidHeaderProblem("ASCAN202", "The marker exceeds the 256-byte limit.", "Send a marker of at most 256 UTF-8 bytes.");
            return false;
        }

        return true;
    }

    internal static bool TryReadFreshSince(
        HttpContext httpContext,
        bool required,
        out DateTimeOffset? freshSince,
        out IResult? problem)
    {
        var values = httpContext.Request.Headers[AppSurfaceCanaryHeaderNames.FreshSince];
        freshSince = null;
        problem = null;

        if (values.Count > 1)
        {
            problem = InvalidHeaderProblem("ASCAN202", "The freshness header was supplied more than once.", "Send exactly one freshness header value.");
            return false;
        }

        var value = values.Count == 0 ? null : values[0];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                problem = InvalidHeaderProblem("ASCAN201", "A required canary header was missing.", $"Supply {AppSurfaceCanaryHeaderNames.FreshSince} and retry.");
                return false;
            }

            return true;
        }

        if (!FreshSinceRegex().IsMatch(value)
            || !DateTimeOffset.TryParseExact(
                value,
                ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            problem = InvalidHeaderProblem("ASCAN202", "The freshness header is invalid.", "Send a strict RFC 3339 timestamp with a Z or numeric offset and at most seven fractional digits.");
            return false;
        }

        freshSince = parsed.ToUniversalTime();
        return true;
    }

    private static IResult InvalidHeaderProblem(string code, string cause, string fix) =>
        CreateProblem(
            StatusCodes.Status400BadRequest,
            "Invalid AppSurface canary request",
            code,
            "The canary request is invalid.",
            cause,
            fix);

    private static IResult CreateProblem(
        int statusCode,
        string title,
        string code,
        string problem,
        string cause,
        string fix) =>
        Results.Json(
            new AppSurfaceCanaryProblemResponse(title, statusCode, code, problem, cause, fix, DocsLink),
            options: ResponseJsonOptions,
            statusCode: statusCode,
            contentType: $"application/problem+json; charset={Encoding.UTF8.WebName}");

    private static void SetNoStoreHeaders(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
    }

    internal static string ToWireStatus(AppSurfaceCanaryStatus status) => status switch
    {
        AppSurfaceCanaryStatus.Pass => "pass",
        AppSurfaceCanaryStatus.Pending => "pending",
        AppSurfaceCanaryStatus.Fail => "fail",
        AppSurfaceCanaryStatus.Stale => "stale",
        AppSurfaceCanaryStatus.NotConfigured => "not-configured",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The AppSurface canary status must be defined."),
    };

    internal static bool IsNonFatalEvaluationFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex FreshSinceRegex();

    private sealed class AppSurfaceCanaryResponse(string name, string status)
    {
        [JsonPropertyName("name")]
        public string Name { get; } = name;

        [JsonPropertyName("status")]
        public string Status { get; } = status;
    }

    private sealed class AppSurfaceCanaryProblemResponse(
        string title,
        int status,
        string code,
        string problem,
        string cause,
        string fix,
        string docsLink)
    {
        [JsonPropertyName("title")]
        public string Title { get; } = title;

        [JsonPropertyName("status")]
        public int Status { get; } = status;

        [JsonPropertyName("code")]
        public string Code { get; } = code;

        [JsonPropertyName("problem")]
        public string Problem { get; } = problem;

        [JsonPropertyName("cause")]
        public string Cause { get; } = cause;

        [JsonPropertyName("fix")]
        public string Fix { get; } = fix;

        [JsonPropertyName("docsLink")]
        public string DocsLink { get; } = docsLink;
    }
}
