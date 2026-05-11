using Microsoft.AspNetCore.Http;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Defines the contract for authorizing subscription requests to RazorWire channels.
/// </summary>
public interface IRazorWireChannelAuthorizer
{
    /// <summary>
    /// Determines whether the current HTTP request is permitted to subscribe to the specified channel.
    /// </summary>
    /// <param name="context">The current HTTP context for the subscription request.</param>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns><c>true</c> if subscription is permitted, <c>false</c> otherwise.</returns>
    ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel);
}

/// <summary>
/// Provides a default implementation of <see cref="IRazorWireChannelAuthorizer"/> that permits all subscriptions.
/// </summary>
public class DefaultRazorWireChannelAuthorizer : IRazorWireChannelAuthorizer
{
    /// <summary>
    /// Determine whether the request represented by the <paramref name="context"/> may subscribe to the specified channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>SECURITY WARNING:</strong> This default implementation allows ALL subscriptions to ANY channel.
    /// It is intended for development and prototyping only.
    /// </para>
    /// <para>
    /// In a production environment, you should replace this service with an implementation that enforces
    /// your application's specific authorization rules (e.g., checking user claims or roles).
    /// </para>
    /// </remarks>
    /// <param name="context">The HTTP context of the requesting client.</param>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns>`true` if subscription is allowed, `false` otherwise.</returns>
    public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        return new ValueTask<bool>(true);
    }
}
