using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using ForgeTrust.AppSurface.Auth;
using ForgeTrust.AppSurface.Intelligence;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire;

/// <summary>
/// Provides extension methods for <see cref="IEndpointRouteBuilder"/> to map RazorWire endpoints.
/// </summary>
public static class RazorWireEndpointRouteBuilderExtensions
{
    private static readonly EventId StreamAdmissionRejectedEventId = new(13701, "StreamAdmissionRejected");
    private const string StreamLoggerCategory = "ForgeTrust.RazorWire.Streams";

    /// <summary>
    /// Registers a Server-Sent Events (SSE) GET endpoint at the configured streams base path that streams messages for a named channel.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <remarks>
    /// The endpoint maps both RazorWire live transport surfaces: a stream endpoint at
    /// <see cref="RazorWireStreamOptions.BasePath"/> plus a form anti-forgery token endpoint at
    /// <see cref="RazorWireFormAntiforgeryOptions.TokenEndpointPath"/>. The stream endpoint enforces channel subscription
    /// authorization, streams hub messages as SSE (each line emitted as a <c>data:</c> event), sends a 20-second
    /// heartbeat comment when idle, and unsubscribes on client disconnect.
    /// A <c>replay</c> query value of <c>1</c> or <c>true</c> maps to
    /// <see cref="RazorWireStreamSubscribeOptions.Replay"/> and asks the hub to deliver retained messages before live
    /// messages. Replay is disabled when the query is absent or has any other value. The helper that parses this input is
    /// intentionally narrow so live delivery remains the default and replay stays a one-time historical catch-up before
    /// ongoing stream delivery. The anti-forgery endpoint returns JSON for the runtime's lazy form-token refresh flow,
    /// sets no-store cache headers, and applies the configured hybrid CORS policy when one is set. Call
    /// <see cref="RazorWireServiceCollectionExtensions.AddRazorWire"/> before mapping this endpoint so the hub,
    /// authorizer, options, and ASP.NET Core anti-forgery services are registered.
    /// </remarks>
    /// <returns>The original <see cref="IEndpointRouteBuilder"/> instance.</returns>
    public static IEndpointRouteBuilder MapRazorWire(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<RazorWireOptions>();
        var admission = endpoints.ServiceProvider.GetService<RazorWireStreamAdmissionController>()
            ?? new RazorWireStreamAdmissionController(options);

        var streamEndpoint = endpoints.MapGet(
                $"{options.Streams.BasePath}/{{channel}}",
                async context =>
                {
                    var channel = context.Request.RouteValues["channel"] as string;
                    if (string.IsNullOrEmpty(channel))
                    {
                        context.Response.StatusCode = 400;

                        return;
                    }

                    var channelValidation = RazorWireStreamChannelValidation.Validate(channel, options.Streams);
                    if (!channelValidation.IsValid)
                    {
                        var result = admission.RejectPreAuthorizationValidation(
                            channel,
                            channelValidation.RejectionReason!.Value);

                        await RejectAdmissionAsync(context, options, result, authorizerType: "NotResolved");

                        return;
                    }

                    var streamAuthorizer = context.RequestServices.GetRequiredService<IRazorWireStreamAuthorizer>();
                    var authorizationFilters =
                        context.RequestServices.GetServices<IRazorWireStreamAuthorizationFilter>();
                    var authorization = await TryAuthorizeStreamAsync(
                        context,
                        options,
                        streamAuthorizer,
                        authorizationFilters,
                        channel);
                    if (authorization is null)
                    {
                        return;
                    }

                    if (!authorization.Result.IsAllowed)
                    {
                        var responseMapper = context.RequestServices.GetRequiredService<RazorWireStreamAuthorizationResponseMapper>();
                        await responseMapper.WriteDeniedAsync(
                            context,
                            options,
                            authorization.Result,
                            authorization.AuthorizerType,
                            channel,
                            authorization.FailureKind,
                            authorization.ExceptionType);

                        return;
                    }

                    var admissionResult = admission.TryAcquire(channel);
                    if (!admissionResult.Accepted)
                    {
                        await RejectAdmissionAsync(
                            context,
                            options,
                            admissionResult,
                            authorization.AuthorizerType);

                        return;
                    }

                    var hub = context.RequestServices.GetRequiredService<IRazorWireStreamHub>();
                    using var lease = admissionResult.Lease!;
                    ChannelReader<string>? reader = null;

                    try
                    {
                        reader = hub.Subscribe(
                            channel,
                            new RazorWireStreamSubscribeOptions
                            {
                                Replay = IsReplayRequested(context.Request.Query)
                            });

                        context.Response.ContentType = "text/event-stream";
                        context.Response.Headers.CacheControl = "no-cache";
                        context.Response.Headers.Connection = "keep-alive";
                        context.Response.Headers.Pragma = "no-cache";

                        // 1. Send initial comment to establish connection and flush headers
                        await context.Response.WriteAsync(":\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);

                        // 2. Loop with heartbeat support
                        while (!context.RequestAborted.IsCancellationRequested)
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                            cts.CancelAfter(20000); // 20s heartbeat

                            try
                            {
                                var message = await reader.ReadAsync(cts.Token);
                                using var stringReader = new StringReader(message);
                                while (stringReader.ReadLine() is { } line)
                                {
                                    await context.Response.WriteAsync($"data: {line}\n", context.RequestAborted);
                                }

                                await context.Response.WriteAsync("\n", context.RequestAborted);
                                await context.Response.Body.FlushAsync(context.RequestAborted);
                            }
                            catch (ChannelClosedException)
                            {
                                // Normal exit on channel completion
                                break;
                            }
                            catch (OperationCanceledException) when (cts.IsCancellationRequested
                                                                     && !context.RequestAborted.IsCancellationRequested)
                            {
                                // Send heartbeat comment
                                await context.Response.WriteAsync(":\n\n", context.RequestAborted);
                                await context.Response.Body.FlushAsync(context.RequestAborted);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal exit on client disconnect
                    }
                    finally
                    {
                        if (reader is not null)
                        {
                            hub.Unsubscribe(channel, reader);
                        }
                    }
                })
            .ExcludeFromDescription();

        if (!string.IsNullOrWhiteSpace(options.Hybrid.CorsPolicyName))
        {
            streamEndpoint.RequireCors(options.Hybrid.CorsPolicyName);
        }

        var tokenEndpoint = endpoints.MapGet(
                options.Forms.Antiforgery.TokenEndpointPath,
                (HttpContext context, [FromServices] IAntiforgery antiforgery) =>
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";

                    return Results.Json(new
                    {
                        formFieldName = tokens.FormFieldName ?? "__RequestVerificationToken",
                        requestToken = tokens.RequestToken ?? string.Empty,
                        headerName = tokens.HeaderName ?? "RequestVerificationToken"
                    });
                })
            .ExcludeFromDescription();

        if (!string.IsNullOrWhiteSpace(options.Hybrid.CorsPolicyName))
        {
            tokenEndpoint.RequireCors(options.Hybrid.CorsPolicyName);
        }

        return endpoints;
    }

    private static bool IsDevelopment(HttpContext context)
    {
        return context.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true;
    }

    private static async ValueTask<RazorWireStreamAuthorizationDecision?> TryAuthorizeStreamAsync(
        HttpContext context,
        RazorWireOptions options,
        IRazorWireStreamAuthorizer authorizer,
        IEnumerable<IRazorWireStreamAuthorizationFilter> authorizationFilters,
        string channel)
    {
        var authorizerType = authorizer.GetType().FullName ?? authorizer.GetType().Name;
        var authorizationContext = new RazorWireStreamAuthorizationContext(
            context,
            channel,
            options.Streams.AuthorizationMode);

        try
        {
            foreach (var filter in authorizationFilters)
            {
                var filterType = filter.GetType().FullName ?? filter.GetType().Name;
                authorizerType = filterType;
                var filterResult = await filter.AuthorizeAsync(authorizationContext);
                if (filterResult is not null && !filterResult.IsAllowed)
                {
                    return new RazorWireStreamAuthorizationDecision(filterResult, filterType);
                }
            }

            authorizerType = authorizer.GetType().FullName ?? authorizer.GetType().Name;
            if (authorizer is RazorWireBoolChannelAuthorizerAdapter boolAdapter)
            {
                var channelAuthorizer = boolAdapter.ResolveChannelAuthorizer(authorizationContext);
                authorizerType = RazorWireBoolChannelAuthorizerAdapter.GetAuthorizerType(channelAuthorizer);
                var boolDecision = await boolAdapter.AuthorizeWithResolvedAuthorizerAsync(
                    authorizationContext,
                    channelAuthorizer);

                return new RazorWireStreamAuthorizationDecision(boolDecision.Result, boolDecision.AuthorizerType);
            }

            var result = await authorizer.AuthorizeAsync(authorizationContext);
            if (result is not null)
            {
                return new RazorWireStreamAuthorizationDecision(result, authorizerType);
            }

            return new RazorWireStreamAuthorizationDecision(
                AppSurfaceAuthResult.MissingServices(),
                authorizerType,
                "NullResult");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException ex)
        {
            return new RazorWireStreamAuthorizationDecision(
                AppSurfaceAuthResult.MissingServices(),
                authorizerType,
                "Canceled",
                ex.GetType().FullName ?? ex.GetType().Name);
        }
        catch (Exception ex)
        {
            return new RazorWireStreamAuthorizationDecision(
                AppSurfaceAuthResult.MissingServices(),
                authorizerType,
                "Exception",
                ex.GetType().FullName ?? ex.GetType().Name);
        }
    }

    private sealed record RazorWireStreamAuthorizationDecision(
        AppSurfaceAuthResult Result,
        string AuthorizerType,
        string? FailureKind = null,
        string? ExceptionType = null);

    private static async Task RejectAdmissionAsync(
        HttpContext context,
        RazorWireOptions options,
        RazorWireStreamAdmissionResult result,
        string authorizerType)
    {
        var reason = result.RejectionReason!.Value;

        LogRejectedAdmission(context, options.Streams.AuthorizationMode, authorizerType, result);
        await CaptureStreamAdmissionRejectedAsync(context, options.Streams, result);

        context.Response.StatusCode = GetAdmissionRejectionStatusCode(reason);
        if (IsDevelopment(context))
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                CreateDevelopmentAdmissionRejectionMessage(options.Streams, result),
                context.RequestAborted);
        }
    }

    private static int GetAdmissionRejectionStatusCode(RazorWireStreamAdmissionRejectionReason reason)
    {
        return reason is RazorWireStreamAdmissionRejectionReason.InvalidChannelName
            or RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status429TooManyRequests;
    }

    private static string CreateDevelopmentAdmissionRejectionMessage(
        RazorWireStreamOptions options,
        RazorWireStreamAdmissionResult result)
    {
        var reason = result.RejectionReason!.Value;
        var optionName = GetOptionName(reason);
        var configuredLimit = GetConfiguredLimit(options, reason);

        return
            "RazorWire rejected this stream subscription.\n\n" +
            $"Reason: {reason}.\n" +
            $"Option: RazorWireOptions.Streams.{optionName}.\n" +
            $"ConfiguredLimit: {configuredLimit}.\n" +
            $"Current: {result.Current}.\n" +
            $"LiveSubscriptions: {result.Snapshot.LiveSubscriptions}.\n" +
            $"LiveChannels: {result.Snapshot.LiveChannels}.\n" +
            $"Fix: {GetFixMessage(reason)}";
    }

    private static string GetOptionName(RazorWireStreamAdmissionRejectionReason reason)
    {
        return reason switch
        {
            RazorWireStreamAdmissionRejectionReason.InvalidChannelName => nameof(RazorWireStreamOptions.MaxChannelNameLength),
            RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong => nameof(RazorWireStreamOptions.MaxChannelNameLength),
            RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels => nameof(RazorWireStreamOptions.MaxLiveChannels),
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions => nameof(RazorWireStreamOptions.MaxLiveSubscriptions),
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptionsPerChannel => nameof(RazorWireStreamOptions.MaxLiveSubscriptionsPerChannel),
            _ => nameof(RazorWireStreamOptions.MaxLiveSubscriptions)
        };
    }

    private static int GetConfiguredLimit(
        RazorWireStreamOptions options,
        RazorWireStreamAdmissionRejectionReason reason)
    {
        return reason switch
        {
            RazorWireStreamAdmissionRejectionReason.InvalidChannelName => options.MaxChannelNameLength,
            RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong => options.MaxChannelNameLength,
            RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels => options.MaxLiveChannels,
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions => options.MaxLiveSubscriptions,
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptionsPerChannel => options.MaxLiveSubscriptionsPerChannel,
            _ => options.MaxLiveSubscriptions
        };
    }

    private static string GetFixMessage(RazorWireStreamAdmissionRejectionReason reason)
    {
        return reason switch
        {
            RazorWireStreamAdmissionRejectionReason.InvalidChannelName =>
                "Use a finite channel name containing only ASCII letters, digits, '.', '_', '-', and ':'.",
            RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong =>
                "Shorten the channel name or raise MaxChannelNameLength for a finite namespaced channel scheme.",
            RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels =>
                "Reuse a finite set of channel names, lower client fanout, or raise MaxLiveChannels for this process.",
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions =>
                "Reduce open stream sources, close unused tabs, or raise MaxLiveSubscriptions for this process.",
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptionsPerChannel =>
                "Reduce fanout for this channel or raise MaxLiveSubscriptionsPerChannel for this process.",
            _ => "Review RazorWire stream admission settings."
        };
    }

    private static void LogRejectedAdmission(
        HttpContext context,
        RazorWireStreamAuthorizationMode authorizationMode,
        string authorizerType,
        RazorWireStreamAdmissionResult result)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(StreamLoggerCategory);
        if (logger is null)
        {
            return;
        }

        var environment = context.RequestServices.GetService<IWebHostEnvironment>()?.EnvironmentName ?? "Unknown";
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
        var reason = result.RejectionReason!.Value;

        logger.LogWarning(
            StreamAdmissionRejectedEventId,
            "RazorWire stream admission rejected. Environment: {Environment}; ConfiguredAuthorizationMode: {ConfiguredAuthorizationMode}; AuthorizerType: {AuthorizerType}; IsAuthenticated: {IsAuthenticated}; RejectionReason: {RejectionReason}; StatusCode: {StatusCode}; OptionName: {OptionName}; Current: {Current}; LiveSubscriptions: {LiveSubscriptions}; LiveChannels: {LiveChannels}; ChannelLength: {ChannelLength}",
            environment,
            authorizationMode,
            authorizerType,
            isAuthenticated,
            reason,
            GetAdmissionRejectionStatusCode(reason),
            GetOptionName(reason),
            result.Current,
            result.Snapshot.LiveSubscriptions,
            result.Snapshot.LiveChannels,
            result.ChannelLength);
    }

    private static async ValueTask CaptureStreamAdmissionRejectedAsync(
        HttpContext context,
        RazorWireStreamOptions streamOptions,
        RazorWireStreamAdmissionResult result)
    {
        var intelligence = context.RequestServices.GetService<IAppSurfaceProductIntelligence>();
        if (intelligence is null || result.RejectionReason is not { } reason)
        {
            return;
        }

        var properties = new Dictionary<string, string>
        {
            ["rejection_reason"] = reason.ToString(),
            ["limit_name"] = GetAdmissionLimitName(reason),
            ["current_count"] = result.Current.ToString(CultureInfo.InvariantCulture),
            ["authorization_mode"] = streamOptions.AuthorizationMode.ToString()
        };

        try
        {
            using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            captureCts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await intelligence.CaptureAsync(
                new AppSurfaceProductEvent(
                    AppSurfaceProductEventRegistry.RazorWireStreamAdmissionRejected,
                    DateTimeOffset.UtcNow,
                    properties,
                    correlationId: Activity.Current?.Id,
                    route: $"{streamOptions.BasePath}/{{channel}}"),
                captureCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Product-intelligence dogfood must not change stream admission behavior.
        }
    }

    private static string GetAdmissionLimitName(RazorWireStreamAdmissionRejectionReason reason)
    {
        return reason switch
        {
            RazorWireStreamAdmissionRejectionReason.InvalidChannelName => "channel_name",
            RazorWireStreamAdmissionRejectionReason.ChannelNameTooLong => "channel_name_length",
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptions => "max_live_subscriptions",
            RazorWireStreamAdmissionRejectionReason.TooManyLiveSubscriptionsPerChannel => "max_live_subscriptions_per_channel",
            RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels => "max_live_channels",
            _ => "unknown"
        };
    }

    private static bool IsReplayRequested(IQueryCollection query)
    {
        if (!query.TryGetValue("replay", out var values))
        {
            return false;
        }

        return values.Any(value => value == "1"
                                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }
}
