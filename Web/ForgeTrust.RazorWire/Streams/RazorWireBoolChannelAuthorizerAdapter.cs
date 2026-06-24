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
        RazorWireStreamAuthorizationContext context,
        IRazorWireChannelAuthorizer? channelAuthorizer = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        channelAuthorizer ??= ResolveChannelAuthorizer(context);
        var allowed = await channelAuthorizer.CanSubscribeAsync(context.HttpContext, context.Channel);
        var isAuthenticated = context.HttpContext.User?.Identity?.IsAuthenticated == true;

        var result = allowed
            ? AppSurfaceAuthResult.Allowed()
            : isAuthenticated
                ? AppSurfaceAuthResult.Forbidden()
                : AppSurfaceAuthResult.Challenge();
        var authorizerType = GetAuthorizerType(channelAuthorizer);

        return (result, authorizerType);
    }

    internal IRazorWireChannelAuthorizer ResolveChannelAuthorizer(RazorWireStreamAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.HttpContext.RequestServices.GetRequiredService<IRazorWireChannelAuthorizer>();
    }

    internal static string GetAuthorizerType(IRazorWireChannelAuthorizer channelAuthorizer)
    {
        return channelAuthorizer.GetType().FullName ?? channelAuthorizer.GetType().Name;
    }
}
