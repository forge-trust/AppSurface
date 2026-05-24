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
/// Provides a built-in implementation of <see cref="IRazorWireChannelAuthorizer"/> that denies all subscriptions.
/// </summary>
/// <remarks>
/// This is RazorWire's safe default when no app-specific authorizer is registered. Use
/// <see cref="AllowAllRazorWireChannelAuthorizer"/> or
/// <see cref="RazorWireStreamOptions.AuthorizationMode"/> only for public/demo streams, and register a custom
/// <see cref="IRazorWireChannelAuthorizer"/> for user, tenant, or workflow-specific channels.
/// </remarks>
public sealed class DenyAllRazorWireChannelAuthorizer : IRazorWireChannelAuthorizer
{
    /// <summary>
    /// Determines whether the request represented by the <paramref name="context"/> may subscribe to the specified channel.
    /// </summary>
    /// <param name="context">The HTTP context of the requesting client.</param>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns><see langword="false"/> for every request.</returns>
    public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        return new ValueTask<bool>(false);
    }
}

/// <summary>
/// Provides a built-in implementation of <see cref="IRazorWireChannelAuthorizer"/> that permits all subscriptions.
/// </summary>
/// <remarks>
/// This authorizer is intended for public, demo, or local-development streams only. Do not use it for channels that
/// include user-specific, tenant-specific, workflow-specific, or otherwise sensitive data.
/// </remarks>
public sealed class AllowAllRazorWireChannelAuthorizer : IRazorWireChannelAuthorizer
{
    /// <summary>
    /// Determines whether the request represented by the <paramref name="context"/> may subscribe to the specified channel.
    /// </summary>
    /// <remarks>
    /// Prefer a custom <see cref="IRazorWireChannelAuthorizer"/> for production streams that depend on
    /// <see cref="HttpContext.User"/> or other request state.
    /// </remarks>
    /// <param name="context">The HTTP context of the requesting client.</param>
    /// <param name="channel">The name of the channel to subscribe to.</param>
    /// <returns><see langword="true"/> for every request.</returns>
    public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        return new ValueTask<bool>(true);
    }
}
