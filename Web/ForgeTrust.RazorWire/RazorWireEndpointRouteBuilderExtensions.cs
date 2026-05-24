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

                    var authorizer = context.RequestServices.GetRequiredService<IRazorWireChannelAuthorizer>();
                    if (!await authorizer.CanSubscribeAsync(context, channel))
                    {
                        LogDeniedSubscription(context, options.Streams.AuthorizationMode, channel);

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

                    var hub = context.RequestServices.GetRequiredService<IRazorWireStreamHub>();

                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.Headers.Connection = "keep-alive";
                    context.Response.Headers.Pragma = "no-cache";

                    var reader = hub.Subscribe(
                        channel,
                        new RazorWireStreamSubscribeOptions
                        {
                            Replay = IsReplayRequested(context.Request.Query)
                        });

                    try
                    {
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
                        hub.Unsubscribe(channel, reader);
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
            "RazorWire stream subscription denied. Environment: {Environment}; AuthorizationMode: {AuthorizationMode}; IsAuthenticated: {IsAuthenticated}; ChannelLength: {ChannelLength}",
            environment,
            authorizationMode,
            isAuthenticated,
            channel.Length);
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
