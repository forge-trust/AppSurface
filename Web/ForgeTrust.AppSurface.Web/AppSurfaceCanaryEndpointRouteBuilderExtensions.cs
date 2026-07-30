using System.Diagnostics;
using System.Globalization;
using System.Net.Mime;
using System.Security.Cryptography;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Maps the explicit protected HTTP adapter for registered named canaries.
/// </summary>
public static partial class AppSurfaceCanaryEndpointRouteBuilderExtensions
{
    private const string DocsLink = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Web/README.md#named-canary-endpoints";
    private const int MaximumHostNameUtf8Bytes = 128;
    private const int MaximumMarkerUtf8Bytes = 256;
    private const int MaximumSnapshotNames = 64;
    private const int MaximumSnapshotTags = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly EventId EvaluationFailureEvent = new(62301, "AppSurfaceCanaryEvaluationFailed");
    private static readonly JsonSerializerOptions ResponseJsonOptions = CreateResponseJsonOptions();

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

        if (!HasAuthorizationServices(services))
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

        ValidateSnapshotOptions(options.Snapshot);
        var responseMode = options.CompletedResponseMode;
        var snapshotOptions = options.Snapshot.Copy();
        var hostEnvironment = services.GetService<IHostEnvironment>();
        var applicationName = NormalizeHostName(hostEnvironment?.ApplicationName);
        var environmentName = NormalizeHostName(hostEnvironment?.EnvironmentName);

        _ = services.GetRequiredService<AppSurfaceCanaryRegistry>();
        var mappingState = services.GetRequiredService<AppSurfaceCanaryMappingState>();
        if (!mappingState.TryClaim(endpoints.DataSources))
        {
            throw new InvalidOperationException(
                "ASCAN114: MapAppSurfaceCanaries may be called only once for a host.");
        }

        _ = endpoints
            .MapGet(
                AppSurfaceCanaryEndpointDefaults.SnapshotRoutePattern,
                (Func<HttpContext, Task>)(httpContext => HandleSnapshotRequestAsync(
                    httpContext,
                    responseMode,
                    authorizationPolicyName,
                    applicationName,
                    environmentName,
                    snapshotOptions)))
            .WithDisplayName("AppSurface named canary snapshot")
            .WithMetadata(AppSurfaceCanaryRouteMetadata.Instance)
            .ExcludeFromDescription()
            .RequireAuthorization(authorizationPolicyName);

        return endpoints
            .MapGet(
                AppSurfaceCanaryEndpointDefaults.RoutePattern,
                (Func<HttpContext, Task>)(httpContext => HandleRequestAsync(
                    httpContext,
                    responseMode,
                    authorizationPolicyName,
                    applicationName,
                    environmentName)))
            .WithDisplayName("AppSurface named canary evaluation")
            .WithMetadata(AppSurfaceCanaryRouteMetadata.Instance)
            .ExcludeFromDescription()
            .RequireAuthorization(authorizationPolicyName);
    }

    private static void ValidateSnapshotOptions(AppSurfaceCanarySnapshotOptions options)
    {
        if (options.MaxSelectedCanaries is < 1 or > 256
            || options.MaxConcurrency is < 1 or > 64
            || options.PerCheckTimeout <= TimeSpan.Zero
            || options.OverallTimeout < options.PerCheckTimeout)
        {
            throw new ArgumentException(
                "ASCAN117: Snapshot limits must be positive; MaxSelectedCanaries is 1-256, MaxConcurrency is 1-64, and OverallTimeout must not be shorter than PerCheckTimeout.",
                "configure");
        }
    }

    private static bool HasAuthorizationServices(IServiceProvider services)
    {
        var serviceProbe = services.GetService<IServiceProviderIsService>();
        if (serviceProbe is not null)
        {
            return serviceProbe.IsService(typeof(IAuthorizationService))
                && serviceProbe.IsService(typeof(IAuthorizationPolicyProvider));
        }

        var scopeFactory = services.GetService<IServiceScopeFactory>();
        if (scopeFactory is null)
        {
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        try
        {
            return scope.ServiceProvider.GetService<IAuthorizationService>() is not null
                && scope.ServiceProvider.GetService<IAuthorizationPolicyProvider>() is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task HandleRequestAsync(
        HttpContext httpContext,
        AppSurfaceCanaryCompletedResponseMode responseMode,
        string authorizationPolicyName,
        string applicationName,
        string environmentName)
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
        var name = httpContext.Request.RouteValues["name"] as string ?? string.Empty;
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
        var evaluationStarted = Stopwatch.GetTimestamp();
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
            LogEvaluationFailure(
                httpContext.RequestServices.GetRequiredService<ILogger<AppSurfaceCanaryEvaluationRunner>>(),
                descriptor.Name,
                exception.GetType().FullName ?? exception.GetType().Name);

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
        var ready = result.Status == AppSurfaceCanaryStatus.Pass;
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(evaluationStarted).TotalMilliseconds;
        var markerFingerprint = marker is null ? null : CreateMarkerFingerprint(marker);
        var normalizedFreshSince = freshSince?.ToUniversalTime();

        CanaryEvaluationCompleted(
            httpContext.RequestServices.GetRequiredService<ILogger<AppSurfaceCanaryEvaluationRunner>>(),
            descriptor.Name,
            statusText,
            ready,
            result.ObservedAt,
            normalizedFreshSince,
            result.MatchedCount,
            elapsedMilliseconds,
            applicationName,
            environmentName);

        var statusCode = responseMode == AppSurfaceCanaryCompletedResponseMode.AlwaysOk
            || ready
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;

        await Results.Json(
                new AppSurfaceCanaryResponse(
                    name,
                    ready,
                    statusText,
                    markerFingerprint,
                    normalizedFreshSince,
                    result.ObservedAt,
                    result.MatchedCount,
                    result.ReasonCode,
                    result.Summary,
                    result.Details.Count == 0 ? null : result.Details,
                    result.CorrelationId),
                options: ResponseJsonOptions,
                statusCode: statusCode,
                contentType: $"{MediaTypeNames.Application.Json}; charset={Encoding.UTF8.WebName}")
            .ExecuteAsync(httpContext);
    }

    private static async Task HandleSnapshotRequestAsync(
        HttpContext httpContext,
        AppSurfaceCanaryCompletedResponseMode responseMode,
        string authorizationPolicyName,
        string applicationName,
        string environmentName,
        AppSurfaceCanarySnapshotOptions snapshotOptions)
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
                    "AppSurface canary snapshot failed",
                    "ASCAN113",
                    "The named canary endpoint is not safely protected.",
                    "Anonymous endpoint metadata or a removed policy bypassed the required authorization policy.",
                    "Remove AllowAnonymous metadata and retain the host-owned operator policy.")
                .ExecuteAsync(httpContext);
            return;
        }

        SetNoStoreHeaders(httpContext);
        if (!TryReadSnapshotSelectors(httpContext, out var names, out var tags, out var selectorProblem))
        {
            await selectorProblem!.ExecuteAsync(httpContext);
            return;
        }

        var coordinator = httpContext.RequestServices.GetRequiredService<AppSurfaceCanarySnapshotCoordinator>();
        if (!coordinator.ContainsAllNames(names))
        {
            await CreateProblem(
                    StatusCodes.Status404NotFound,
                    "AppSurface canary selection not found",
                    "ASCAN203",
                    "No registered canary matched the requested selection.",
                    "An exact name selector did not match a registered canary.",
                    "Correct the selector or register the canary with its exact lowercase name.")
                .ExecuteAsync(httpContext);
            return;
        }

        if (!coordinator.TrySelect(names, tags, snapshotOptions.MaxSelectedCanaries, out var descriptors))
        {
            await CreateProblem(
                    StatusCodes.Status400BadRequest,
                    "Invalid AppSurface canary request",
                    "ASCAN204",
                    "The selected canary set exceeds the configured limit.",
                    "The requested names/tags select more canaries than this host allows in one snapshot.",
                    "Narrow the selectors or ask the host to configure an appropriate snapshot cap.")
                .ExecuteAsync(httpContext);
            return;
        }

        if (descriptors.Count == 0)
        {
            await CreateProblem(
                    StatusCodes.Status404NotFound,
                    "AppSurface canary selection not found",
                    "ASCAN203",
                    "No registered canary matched the requested selection.",
                    "The supplied name or tag did not select a registered canary.",
                    "Correct the selector or register a canary with the required durable tag.")
                .ExecuteAsync(httpContext);
            return;
        }

        if (!TryReadMarker(httpContext, descriptors.Any(descriptor => descriptor.MarkerRequired), out var marker, out var markerProblem))
        {
            await markerProblem!.ExecuteAsync(httpContext);
            return;
        }

        if (!TryReadFreshSince(httpContext, descriptors.Any(descriptor => descriptor.FreshSinceRequired), out var freshSince, out var freshnessProblem))
        {
            await freshnessProblem!.ExecuteAsync(httpContext);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var snapshot = await coordinator.EvaluateAsync(
            descriptors,
            marker,
            freshSince,
            snapshotOptions,
            httpContext.RequestAborted);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var ready = snapshot.Ready;
        var statusCode = responseMode == AppSurfaceCanaryCompletedResponseMode.AlwaysOk || ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<AppSurfaceCanaryEvaluationRunner>>();

        foreach (var item in snapshot.Items.Where(item => item.Result is not null))
        {
            var result = item.Result!;
            CanaryEvaluationCompleted(
                logger,
                item.Name,
                ToWireStatus(result.Status),
                item.Ready,
                result.ObservedAt,
                freshSince?.ToUniversalTime(),
                result.MatchedCount,
                item.ElapsedMilliseconds ?? 0,
                applicationName,
                environmentName);
        }

        CanarySnapshotCompleted(
            logger,
            descriptors.Count,
            snapshot.Items.Count(item => item.Outcome != "not-started"),
            snapshot.Items.Count(item => item.Outcome == "completed"),
            snapshot.Items.Count(item => item.Outcome == "timed-out"),
            snapshot.Items.Count(item => item.Outcome == "failed"),
            snapshot.Items.Count(item => item.Outcome == "not-started"),
            ready,
            elapsedMilliseconds,
            applicationName,
            environmentName);

        await Results.Json(
                new AppSurfaceCanarySnapshotResponse(
                    ready,
                    snapshot.OverallTimedOut,
                    elapsedMilliseconds,
                    snapshot.Items.Select(item => AppSurfaceCanarySnapshotResponseItem.From(
                        item,
                        marker is null ? null : CreateMarkerFingerprint(marker),
                        freshSince?.ToUniversalTime())).ToArray()),
                options: ResponseJsonOptions,
                statusCode: statusCode,
                contentType: $"{MediaTypeNames.Application.Json}; charset={Encoding.UTF8.WebName}")
            .ExecuteAsync(httpContext);
    }

    private static bool TryReadSnapshotSelectors(
        HttpContext httpContext,
        out IReadOnlyCollection<string> names,
        out IReadOnlyCollection<string> tags,
        out IResult? problem)
    {
        var requestedNames = httpContext.Request.Query["name"].Select(value => value ?? string.Empty).ToArray();
        var requestedTags = httpContext.Request.Query["tag"].Select(value => value ?? string.Empty).ToArray();
        names = [];
        tags = [];
        problem = null;
        if (requestedNames.Length > MaximumSnapshotNames
            || requestedTags.Length > MaximumSnapshotTags
            || requestedNames.Any(name => !AppSurfaceCanaryValidation.IsValidName(name))
            || requestedTags.Any(tag => !AppSurfaceCanaryValidation.IsValidTag(tag)))
        {
            problem = CreateProblem(
                StatusCodes.Status400BadRequest,
                "Invalid AppSurface canary request",
                "ASCAN202",
                "A snapshot selector is invalid.",
                "Names and tags must use the same bounded lowercase grammar as their registrations and include at most 64 name values and 16 tag values.",
                "Send at most 64 repeated name values and 16 repeated tag values using registered identifier grammar.");
            return false;
        }

        names = requestedNames.Distinct(StringComparer.Ordinal).ToArray();
        tags = requestedTags.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    /// <summary>
    /// Reads the optional marker header and enforces the named-canary marker profile.
    /// </summary>
    /// <param name="httpContext">The request whose marker header is inspected.</param>
    /// <param name="required">Whether a missing or blank marker is invalid.</param>
    /// <param name="marker">The single nonblank marker value when one was supplied; otherwise <see langword="null"/>.</param>
    /// <param name="problem">The safe 400 response when validation fails; otherwise <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the optional value is absent or one valid value of at most 256 UTF-8 bytes is
    /// present; otherwise <see langword="false"/>. Repeated values, malformed Unicode, and control characters are
    /// rejected.
    /// </returns>
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

        try
        {
            if (marker is not null && StrictUtf8.GetByteCount(marker) > MaximumMarkerUtf8Bytes)
            {
                problem = InvalidHeaderProblem("ASCAN202", "The marker exceeds the 256-byte limit.", "Send a marker of at most 256 UTF-8 bytes.");
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            problem = InvalidHeaderProblem("ASCAN202", "The marker contains malformed Unicode.", "Send a marker containing well-formed Unicode scalar values.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the optional freshness header using the strict named-canary RFC 3339 profile.
    /// </summary>
    /// <param name="httpContext">The request whose freshness header is inspected.</param>
    /// <param name="required">Whether a missing or blank freshness boundary is invalid.</param>
    /// <param name="freshSince">The parsed timestamp normalized to UTC when supplied; otherwise <see langword="null"/>.</param>
    /// <param name="problem">The safe 400 response when validation fails; otherwise <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the optional value is absent or exactly one strict RFC 3339 timestamp with a
    /// <c>Z</c> or numeric offset and at most seven fractional digits is present; otherwise <see langword="false"/>.
    /// </returns>
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

    private static JsonSerializerOptions CreateResponseJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new CanonicalUtcDateTimeOffsetConverter());
        return options;
    }

    private static string NormalizeHostName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return "unknown";
            }
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= MaximumHostNameUtf8Bytes ? value : "unknown";
        }
        catch (EncoderFallbackException)
        {
            return "unknown";
        }
    }

    private static string CreateMarkerFingerprint(string marker) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(marker))).ToLowerInvariant()}";

    /// <summary>Maps a defined canary status to its stable lowercase wire value.</summary>
    /// <param name="status">The status to map.</param>
    /// <returns>The stable JSON status text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    internal static string ToWireStatus(AppSurfaceCanaryStatus status) => status switch
    {
        AppSurfaceCanaryStatus.Pass => "pass",
        AppSurfaceCanaryStatus.Pending => "pending",
        AppSurfaceCanaryStatus.Fail => "fail",
        AppSurfaceCanaryStatus.Stale => "stale",
        AppSurfaceCanaryStatus.NotConfigured => "not-configured",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The AppSurface canary status must be defined."),
    };

    /// <summary>Determines whether an evaluator failure can be converted into a safe package-owned problem response.</summary>
    /// <param name="exception">The exception raised during evaluator activation or execution.</param>
    /// <returns><see langword="false"/> for fatal process/runtime exceptions; otherwise <see langword="true"/>.</returns>
    internal static bool IsNonFatalEvaluationFailure(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;

    private static void LogEvaluationFailure(ILogger logger, string canaryName, string exceptionType) =>
        logger.LogError(
            EvaluationFailureEvent,
            "Named canary {CanaryName} failed with diagnostic {DiagnosticCode} and exception type {ExceptionType}.",
            canaryName,
            "ASCAN301",
            exceptionType);

    [LoggerMessage(
        EventId = 62401,
        Level = LogLevel.Information,
        EventName = "AppSurfaceCanaryEvaluationCompleted",
        Message = "Named canary {CanaryName} completed with status {CanaryStatus}; ready {Ready}; observed {ObservedAt}; fresh since {FreshSince}; matched {MatchedCount}; elapsed {ElapsedMilliseconds}; application {ApplicationName}; environment {EnvironmentName}.")]
    private static partial void CanaryEvaluationCompleted(
        ILogger logger,
        string canaryName,
        string canaryStatus,
        bool ready,
        DateTimeOffset? observedAt,
        DateTimeOffset? freshSince,
        int? matchedCount,
        double elapsedMilliseconds,
        string applicationName,
        string environmentName);

    [LoggerMessage(
        EventId = 62402,
        Level = LogLevel.Information,
        EventName = "AppSurfaceCanarySnapshotCompleted",
        Message = "Named canary snapshot selected {Selected}; started {Started}; completed {Completed}; timed out {TimedOut}; failed {Failed}; not started {NotStarted}; ready {Ready}; elapsed {ElapsedMilliseconds}; application {ApplicationName}; environment {EnvironmentName}.")]
    private static partial void CanarySnapshotCompleted(
        ILogger logger,
        int selected,
        int started,
        int completed,
        int timedOut,
        int failed,
        int notStarted,
        bool ready,
        double elapsedMilliseconds,
        string applicationName,
        string environmentName);

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex FreshSinceRegex();

    private sealed class AppSurfaceCanaryResponse(
        string name,
        bool ready,
        string status,
        string? markerFingerprint,
        DateTimeOffset? freshSince,
        DateTimeOffset? observedAt,
        int? matchedCount,
        string? reasonCode,
        string? summary,
        IReadOnlyDictionary<string, string>? details,
        string? correlationId)
    {
        [JsonPropertyName("name")]
        public string Name { get; } = name;

        [JsonPropertyName("ready")]
        public bool Ready { get; } = ready;

        [JsonPropertyName("status")]
        public string Status { get; } = status;

        [JsonPropertyName("markerFingerprint")]
        public string? MarkerFingerprint { get; } = markerFingerprint;

        [JsonPropertyName("freshSince")]
        public DateTimeOffset? FreshSince { get; } = freshSince;

        [JsonPropertyName("observedAt")]
        public DateTimeOffset? ObservedAt { get; } = observedAt;

        [JsonPropertyName("matchedCount")]
        public int? MatchedCount { get; } = matchedCount;

        [JsonPropertyName("reasonCode")]
        public string? ReasonCode { get; } = reasonCode;

        [JsonPropertyName("summary")]
        public string? Summary { get; } = summary;

        [JsonPropertyName("details")]
        public IReadOnlyDictionary<string, string>? Details { get; } = details;

        [JsonPropertyName("correlationId")]
        public string? CorrelationId { get; } = correlationId;
    }

    private sealed class AppSurfaceCanarySnapshotResponse(
        bool ready,
        bool overallTimedOut,
        double elapsedMilliseconds,
        IReadOnlyList<AppSurfaceCanarySnapshotResponseItem> results)
    {
        [JsonPropertyName("ready")]
        public bool Ready { get; } = ready;

        [JsonPropertyName("overallTimedOut")]
        public bool OverallTimedOut { get; } = overallTimedOut;

        [JsonPropertyName("elapsedMilliseconds")]
        public double ElapsedMilliseconds { get; } = elapsedMilliseconds;

        [JsonPropertyName("results")]
        public IReadOnlyList<AppSurfaceCanarySnapshotResponseItem> Results { get; } = results;
    }

    private sealed class AppSurfaceCanarySnapshotResponseItem(
        string name,
        string outcome,
        AppSurfaceCanaryResponse? result,
        string? code)
    {
        [JsonPropertyName("name")] public string Name { get; } = name;
        [JsonPropertyName("outcome")] public string Outcome { get; } = outcome;
        [JsonPropertyName("result")] public AppSurfaceCanaryResponse? Result { get; } = result;
        [JsonPropertyName("code")] public string? Code { get; } = code;

        internal static AppSurfaceCanarySnapshotResponseItem From(
            AppSurfaceCanarySnapshotItem item,
            string? markerFingerprint,
            DateTimeOffset? freshSince)
        {
            if (item.Result is null)
            {
                return new AppSurfaceCanarySnapshotResponseItem(item.Name, item.Outcome, null, item.ReasonCode);
            }

            var result = item.Result;
            return new AppSurfaceCanarySnapshotResponseItem(
                item.Name,
                item.Outcome,
                new AppSurfaceCanaryResponse(
                    item.Name,
                    item.Ready,
                    ToWireStatus(result.Status),
                    markerFingerprint,
                    freshSince,
                    result.ObservedAt,
                    result.MatchedCount,
                    result.ReasonCode,
                    result.Summary,
                    result.Details.Count == 0 ? null : result.Details,
                    result.CorrelationId),
                null);
        }
    }

    /// <summary>Reads and writes named-canary timestamps using the canonical UTC wire format.</summary>
    internal sealed class CanonicalUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        /// <inheritdoc />
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !DateTimeOffset.TryParseExact(
                    reader.GetString(),
                    Format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var value))
            {
                throw new JsonException("A canary timestamp must be a canonical UTC JSON string.");
            }

            return value;
        }

        /// <inheritdoc />
        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
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
