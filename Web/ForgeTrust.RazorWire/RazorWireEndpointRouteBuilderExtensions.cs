using System.Threading.Channels;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    private static readonly EventId StreamSubscriptionDeniedEventId = new(13700, "StreamSubscriptionDenied");
    private static readonly EventId StreamAdmissionRejectedEventId = new(13701, "StreamAdmissionRejected");
    private const string StreamLoggerCategory = "ForgeTrust.RazorWire.Streams";
    private const string DevelopmentDeniedSubscriptionMessage =
        "RazorWire denied this stream subscription.\n\n" +
        "Streams deny subscriptions by default. Set RazorWireOptions.Streams.AuthorizationMode = " +
        "RazorWireStreamAuthorizationMode.AllowAll only for public or demo streams. Register " +
        "IRazorWireChannelAuthorizer for user, tenant, or workflow-specific streams.";

    /// <summary>
    /// Registers a Server-Sent Events (SSE) GET endpoint at the configured streams base path that streams messages for a named channel.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <remarks>
    /// The endpoint enforces channel subscription authorization, streams hub messages as SSE (each line emitted as a `data:` event), sends a 20-second heartbeat comment when idle, and unsubscribes on client disconnect.
    /// A <c>replay</c> query value of <c>1</c> or <c>true</c> maps to
    /// <see cref="RazorWireStreamSubscribeOptions.Replay"/> and asks the hub to deliver retained messages before live
    /// messages. Replay is disabled when the query is absent or has any other value. The helper that parses this input is
    /// intentionally narrow so live delivery remains the default and replay stays a one-time historical catch-up before
    /// ongoing stream delivery.
    /// </remarks>
    /// <returns>The original <see cref="IEndpointRouteBuilder"/> instance.</returns>
    public static IEndpointRouteBuilder MapRazorWire(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<RazorWireOptions>();
        var admission = endpoints.ServiceProvider.GetService<RazorWireStreamAdmissionController>()
            ?? new RazorWireStreamAdmissionController(options);

        endpoints.MapGet(
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

                    var authorizer = context.RequestServices.GetRequiredService<IRazorWireChannelAuthorizer>();
                    if (!await authorizer.CanSubscribeAsync(context, channel))
                    {
                        LogDeniedSubscription(
                            context,
                            options.Streams.AuthorizationMode,
                            authorizer.GetType().FullName ?? authorizer.GetType().Name,
                            channel);

                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        if (IsDevelopment(context))
                        {
                            context.Response.ContentType = "text/plain; charset=utf-8";
                            await context.Response.WriteAsync(
                                DevelopmentDeniedSubscriptionMessage,
                                context.RequestAborted);
                        }

                        return;
                    }

                    var admissionResult = admission.TryAcquire(channel);
                    if (!admissionResult.Accepted)
                    {
                        await RejectAdmissionAsync(
                            context,
                            options,
                            admissionResult,
                            authorizer.GetType().FullName ?? authorizer.GetType().Name);

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

        return endpoints;
    }

    private static bool IsDevelopment(HttpContext context)
    {
        return context.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true;
    }

    private static void LogDeniedSubscription(
        HttpContext context,
        RazorWireStreamAuthorizationMode authorizationMode,
        string authorizerType,
        string channel)
    {
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(StreamLoggerCategory);
        if (logger is null)
        {
            return;
        }

        var environment = context.RequestServices.GetService<IWebHostEnvironment>()?.EnvironmentName ?? "Unknown";
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;

        logger.LogWarning(
            StreamSubscriptionDeniedEventId,
            "RazorWire stream subscription denied. Environment: {Environment}; ConfiguredAuthorizationMode: {ConfiguredAuthorizationMode}; AuthorizerType: {AuthorizerType}; IsAuthenticated: {IsAuthenticated}; ChannelLength: {ChannelLength}",
            environment,
            authorizationMode,
            authorizerType,
            isAuthenticated,
            channel.Length);
    }

    private static async Task RejectAdmissionAsync(
        HttpContext context,
        RazorWireOptions options,
        RazorWireStreamAdmissionResult result,
        string authorizerType)
    {
        var reason = result.RejectionReason!.Value;

        LogRejectedAdmission(context, options.Streams.AuthorizationMode, authorizerType, result);

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
