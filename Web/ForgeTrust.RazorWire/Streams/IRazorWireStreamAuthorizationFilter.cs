using ForgeTrust.AppSurface.Auth;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Provides an optional pre-authorizer for RazorWire stream subscriptions before the active stream authorizer runs.
/// </summary>
/// <remarks>
/// Register filters when a package or module owns a reserved channel and must enforce a channel-specific gate that normal
/// host <see cref="IRazorWireStreamAuthorizer"/> replacement should not bypass. Return <see langword="null"/> when the
/// filter does not apply to the requested channel. Return an allowed result to let later filters and the active stream
/// authorizer continue, or return a denial/setup-failure result to stop the subscription before the stream opens.
/// </remarks>
public interface IRazorWireStreamAuthorizationFilter
{
    /// <summary>
    /// Evaluates a pre-authorization gate for the requested stream subscription.
    /// </summary>
    /// <param name="context">The current stream authorization context.</param>
    /// <returns>
    /// <see langword="null"/> when the filter does not apply; otherwise an AppSurface auth result whose denial stops the
    /// subscription and whose allowed outcome lets normal stream authorization continue.
    /// </returns>
    ValueTask<AppSurfaceAuthResult?> AuthorizeAsync(RazorWireStreamAuthorizationContext context);
}
