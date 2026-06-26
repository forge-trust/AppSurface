using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Maps passive stream authorization results to safe pre-SSE HTTP responses and log fields.
/// </summary>
internal sealed class RazorWireStreamAuthorizationResponseMapper
{
    private static readonly EventId StreamSubscriptionDeniedEventId = new(13700, "StreamSubscriptionDenied");
    private const string StreamLoggerCategory = "ForgeTrust.RazorWire.Streams";
    private const string DocsPath = "Web/ForgeTrust.RazorWire/Docs/stream-authorization.md";

    public async ValueTask WriteDeniedAsync(
        HttpContext context,
        RazorWireOptions options,
        AppSurfaceAuthResult result,
        string authorizerType,
        string channel,
        string? failureKind = null,
        string? exceptionType = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(result);

        var statusCode = GetStatusCode(result);
        LogDeniedSubscription(context, options.Streams.AuthorizationMode, result, authorizerType, channel, statusCode, failureKind, exceptionType);

        context.Response.StatusCode = statusCode;
        if (IsDevelopment(context))
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                CreateDevelopmentMessage(options.Streams.AuthorizationMode, result, statusCode, failureKind),
                context.RequestAborted);
        }
    }

    internal static int GetStatusCode(AppSurfaceAuthResult result)
    {
        return result.Outcome switch
        {
            AppSurfaceAuthOutcome.Challenge => StatusCodes.Status401Unauthorized,
            AppSurfaceAuthOutcome.Forbid => StatusCodes.Status403Forbidden,
            AppSurfaceAuthOutcome.SetupFailure => StatusCodes.Status500InternalServerError,
            AppSurfaceAuthOutcome.UnsafeNavigation => StatusCodes.Status400BadRequest,
            AppSurfaceAuthOutcome.StaleOrUnknownSession => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status500InternalServerError
        };
    }

    private static bool IsDevelopment(HttpContext context)
    {
        return context.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true;
    }

    private static string CreateDevelopmentMessage(
        RazorWireStreamAuthorizationMode authorizationMode,
        AppSurfaceAuthResult result,
        int statusCode,
        string? failureKind)
    {
        return
            "RazorWire denied this stream subscription.\n\n" +
            $"Problem: {GetProblem(result)}\n" +
            $"Cause: {GetCause(result, failureKind)}\n" +
            $"Fix: {GetFix(result)}\n" +
            $"Docs: {DocsPath}\n" +
            $"Outcome: {result.Outcome}.\n" +
            $"Reason: {result.Reason}.\n" +
            $"StatusCode: {statusCode}.\n" +
            $"ConfiguredAuthorizationMode: {authorizationMode}.";
    }

    private static string GetProblem(AppSurfaceAuthResult result)
    {
        return result.Outcome switch
        {
            AppSurfaceAuthOutcome.Challenge => "The caller must authenticate before this stream can open.",
            AppSurfaceAuthOutcome.Forbid => "The caller is not permitted to subscribe to this stream.",
            AppSurfaceAuthOutcome.SetupFailure => "The stream authorizer could not produce a usable authorization decision.",
            AppSurfaceAuthOutcome.UnsafeNavigation => "The stream authorization decision reported unsafe navigation context.",
            AppSurfaceAuthOutcome.StaleOrUnknownSession => "The stream authorization decision reported stale or unresolved session state.",
            _ => "The stream authorization decision used an unsupported outcome."
        };
    }

    private static string GetCause(AppSurfaceAuthResult result, string? failureKind)
    {
        return result.Outcome == AppSurfaceAuthOutcome.SetupFailure && !string.IsNullOrWhiteSpace(failureKind)
            ? $"Setup failure kind: {failureKind}."
            : $"Authorization result reason: {result.Reason}.";
    }

    private static string GetFix(AppSurfaceAuthResult result)
    {
        return result.Outcome switch
        {
            AppSurfaceAuthOutcome.Challenge =>
                "Authenticate the request in host middleware before MapRazorWire or return Allowed only for public streams.",
            AppSurfaceAuthOutcome.Forbid =>
                "Streams deny subscriptions by default. Register an IRazorWireStreamAuthorizer, keep an existing IRazorWireChannelAuthorizer, or set RazorWireStreamAuthorizationMode.AllowAll only for public or demo streams.",
            AppSurfaceAuthOutcome.SetupFailure =>
                "Fix the host policy, services, subject mapping, or authorizer exception before enabling this stream.",
            AppSurfaceAuthOutcome.UnsafeNavigation =>
                "Validate any return or navigation target before returning a stream authorization result.",
            AppSurfaceAuthOutcome.StaleOrUnknownSession =>
                "Refresh or rebuild the caller session before retrying the stream subscription.",
            _ => "Return a supported AppSurfaceAuthResult outcome from the stream authorizer."
        };
    }

    private static void LogDeniedSubscription(
        HttpContext context,
        RazorWireStreamAuthorizationMode authorizationMode,
        AppSurfaceAuthResult result,
        string authorizerType,
        string channel,
        int statusCode,
        string? failureKind,
        string? exceptionType)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(StreamLoggerCategory);
        if (logger is null)
        {
            return;
        }

        var environment = context.RequestServices.GetService<IWebHostEnvironment>()?.EnvironmentName ?? "Unknown";
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
        var logLevel = result.Outcome == AppSurfaceAuthOutcome.SetupFailure ? LogLevel.Warning : LogLevel.Information;

        logger.Log(
            logLevel,
            StreamSubscriptionDeniedEventId,
            "RazorWire stream subscription denied. Environment: {Environment}; ConfiguredAuthorizationMode: {ConfiguredAuthorizationMode}; AuthorizerType: {AuthorizerType}; IsAuthenticated: {IsAuthenticated}; Outcome: {Outcome}; Reason: {Reason}; StatusCode: {StatusCode}; FailureKind: {FailureKind}; ExceptionType: {ExceptionType}; ChannelLength: {ChannelLength}",
            environment,
            authorizationMode,
            authorizerType,
            isAuthenticated,
            result.Outcome,
            result.Reason,
            statusCode,
            failureKind ?? "None",
            exceptionType ?? "None",
            channel.Length);
    }
}
