using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Defines the result-bearing contract for authorizing RazorWire stream subscription requests.
/// </summary>
/// <remarks>
/// Implement this interface when a stream needs to distinguish unauthenticated, forbidden, stale-session,
/// unsafe-navigation, or host setup-failure outcomes. RazorWire consumes the passive
/// <see cref="AppSurfaceAuthResult"/> before opening Server-Sent Events; it does not challenge, forbid, redirect,
/// mutate cookies, evaluate host policies, or echo app-supplied auth messages.
/// </remarks>
public interface IRazorWireStreamAuthorizer
{
    /// <summary>
    /// Authorizes the current HTTP request for the requested RazorWire stream channel.
    /// </summary>
    /// <param name="context">The stream authorization context for this subscription request.</param>
    /// <returns>
    /// A non-null <see cref="AppSurfaceAuthResult"/> describing whether the subscription may proceed. RazorWire
    /// defensively treats a <see langword="null"/> result from a misbehaving implementation as a setup failure.
    /// </returns>
    ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context);
}

/// <summary>
/// Carries request and configuration context for a RazorWire stream authorization decision.
/// </summary>
/// <remarks>
/// <see cref="ConfiguredAuthorizationMode"/> is diagnostic context from
/// <see cref="RazorWireOptions.Streams"/>. It is not proof of the effective decision when the host registers a custom
/// <see cref="IRazorWireStreamAuthorizer"/> or legacy <see cref="IRazorWireChannelAuthorizer"/>.
/// </remarks>
public sealed class RazorWireStreamAuthorizationContext
{
    /// <summary>
    /// Creates a RazorWire stream authorization context.
    /// </summary>
    /// <param name="httpContext">The current HTTP request context.</param>
    /// <param name="channel">The validated stream channel name.</param>
    /// <param name="configuredAuthorizationMode">The configured stream authorization mode.</param>
    public RazorWireStreamAuthorizationContext(
        HttpContext httpContext,
        string channel,
        RazorWireStreamAuthorizationMode configuredAuthorizationMode)
    {
        HttpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
        Channel = !string.IsNullOrWhiteSpace(channel)
            ? channel
            : throw new ArgumentException("A RazorWire stream channel is required.", nameof(channel));
        ConfiguredAuthorizationMode = configuredAuthorizationMode;
    }

    /// <summary>
    /// Gets the current HTTP request context.
    /// </summary>
    public HttpContext HttpContext { get; }

    /// <summary>
    /// Gets the validated stream channel name requested by the client.
    /// </summary>
    public string Channel { get; }

    /// <summary>
    /// Gets the configured stream authorization mode from <see cref="RazorWireOptions.Streams"/>.
    /// </summary>
    public RazorWireStreamAuthorizationMode ConfiguredAuthorizationMode { get; }

    /// <summary>
    /// Gets the request-aborted cancellation token for the stream subscription request.
    /// </summary>
    public CancellationToken RequestAborted => HttpContext.RequestAborted;
}
