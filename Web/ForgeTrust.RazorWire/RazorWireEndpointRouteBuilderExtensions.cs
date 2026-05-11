using System.Threading.Channels;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire;

/// <summary>
/// Provides extension methods for <see cref="IEndpointRouteBuilder"/> to map RazorWire endpoints.
/// </summary>
public static class RazorWireEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers a Server-Sent Events (SSE) GET endpoint at the configured streams base path that streams messages for a named channel.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <remarks>
    /// The endpoint enforces channel subscription authorization, streams hub messages as SSE (each line emitted as a `data:` event), sends a 20-second heartbeat comment when idle, and unsubscribes on client disconnect.
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
                        context.Response.StatusCode = 403;

                        return;
                    }

                    var hub = context.RequestServices.GetRequiredService<IRazorWireStreamHub>();

                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.Headers.Connection = "keep-alive";
                    context.Response.Headers.Pragma = "no-cache";

                    var reader = hub.Subscribe(channel);

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
}
