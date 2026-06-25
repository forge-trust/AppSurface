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

    /// <summary>
    /// Authorizes a stream by mapping the legacy channel authorizer's boolean decision to an auth result.
    /// </summary>
    /// <param name="context">The current stream authorization context.</param>
    /// <param name="channelAuthorizer">
    /// Optional same-request-scope authorizer override. Endpoint code uses this after resolving the legacy authorizer
    /// once so exception paths can still report the concrete legacy authorizer type; tests may also pass a resolved
    /// instance to avoid exercising dependency injection.
    /// </param>
    /// <returns>
    /// The mapped result and the resolved legacy authorizer type. <see langword="true"/> maps to
    /// <see cref="AppSurfaceAuthResult.Allowed"/>; <see langword="false"/> maps to
    /// <see cref="AppSurfaceAuthResult.Challenge"/> for anonymous callers and
    /// <see cref="AppSurfaceAuthResult.Forbidden"/> for authenticated callers.
    /// </returns>
    /// <remarks>
    /// Use <paramref name="channelAuthorizer"/> only when it was resolved from the same
    /// <see cref="HttpContext.RequestServices"/> scope represented by <paramref name="context"/>. Supplying a cached
    /// scoped or transient legacy authorizer from another request can break host lifetime expectations.
    /// </remarks>
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

    /// <summary>
    /// Resolves the effective legacy channel authorizer from the current request service scope.
    /// </summary>
    /// <param name="context">The stream authorization context whose <see cref="HttpContext.RequestServices"/> scope is active.</param>
    /// <returns>The request-scoped <see cref="IRazorWireChannelAuthorizer"/> used for the current subscription attempt.</returns>
    /// <remarks>
    /// This method intentionally resolves per request instead of capturing the channel authorizer in the singleton
    /// adapter. That preserves scoped and transient legacy authorizers and keeps before/after <c>AddRazorWire</c>
    /// registration behavior compatible.
    /// </remarks>
    internal IRazorWireChannelAuthorizer ResolveChannelAuthorizer(RazorWireStreamAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.HttpContext.RequestServices.GetRequiredService<IRazorWireChannelAuthorizer>();
    }

    /// <summary>
    /// Returns the diagnostic type name for a resolved legacy channel authorizer.
    /// </summary>
    /// <param name="channelAuthorizer">The legacy authorizer resolved for the current request.</param>
    /// <returns>
    /// The authorizer's full type name when available, otherwise its simple type name. The returned value is safe for
    /// low-cardinality diagnostics and avoids exception messages, channel names, users, and claims.
    /// </returns>
    internal static string GetAuthorizerType(IRazorWireChannelAuthorizer channelAuthorizer)
    {
        return channelAuthorizer.GetType().FullName ?? channelAuthorizer.GetType().Name;
    }
}
