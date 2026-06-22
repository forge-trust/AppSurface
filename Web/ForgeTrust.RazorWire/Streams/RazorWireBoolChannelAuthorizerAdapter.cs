using ForgeTrust.AppSurface.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.RazorWire.Streams;

/// <summary>
/// Bridges legacy bool channel authorizers into the result-bearing stream authorization contract.
/// </summary>
/// <remarks>
/// The adapter resolves <see cref="IRazorWireChannelAuthorizer"/> from the current request services on every
/// authorization call. This preserves legacy before/after <c>AddRazorWire</c> registration behavior and avoids
/// capturing scoped or transient authorizers in a singleton adapter.
/// </remarks>
internal sealed class RazorWireBoolChannelAuthorizerAdapter : IRazorWireStreamAuthorizer
{
    public async ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
    {
        var decision = await AuthorizeWithResolvedAuthorizerAsync(context);

        return decision.Result;
    }

    internal async ValueTask<(AppSurfaceAuthResult Result, string AuthorizerType)> AuthorizeWithResolvedAuthorizerAsync(
        RazorWireStreamAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var channelAuthorizer = context.HttpContext.RequestServices.GetRequiredService<IRazorWireChannelAuthorizer>();
        var allowed = await channelAuthorizer.CanSubscribeAsync(context.HttpContext, context.Channel);

        var result = allowed
            ? AppSurfaceAuthResult.Allowed()
            : AppSurfaceAuthResult.Forbidden();
        var authorizerType = channelAuthorizer.GetType().FullName ?? channelAuthorizer.GetType().Name;

        return (result, authorizerType);
    }
}
