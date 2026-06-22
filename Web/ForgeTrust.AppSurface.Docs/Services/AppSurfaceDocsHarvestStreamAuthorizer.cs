using ForgeTrust.AppSurface.Auth;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Authorizes the AppSurface Docs harvest progress stream with result-bearing RazorWire authorization semantics.
/// </summary>
internal sealed class AppSurfaceDocsHarvestStreamAuthorizer : IRazorWireStreamAuthorizer
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly IRazorWireStreamAuthorizer? _innerStreamAuthorizer;
    private readonly IRazorWireChannelAuthorizer? _innerChannelAuthorizer;

    /// <summary>
    /// Creates an authorizer that applies Docs harvest visibility and host stream authorization to the harvest channel.
    /// </summary>
    /// <param name="options">Docs options used for harvest route visibility.</param>
    /// <param name="environment">The current host environment.</param>
    /// <param name="innerStreamAuthorizer">Optional result-bearing host stream authorizer captured before Docs registration.</param>
    /// <param name="innerChannelAuthorizer">Optional legacy bool host channel authorizer captured before Docs registration.</param>
    public AppSurfaceDocsHarvestStreamAuthorizer(
        AppSurfaceDocsOptions options,
        IHostEnvironment environment,
        IRazorWireStreamAuthorizer? innerStreamAuthorizer = null,
        IRazorWireChannelAuthorizer? innerChannelAuthorizer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _innerStreamAuthorizer = innerStreamAuthorizer;
        _innerChannelAuthorizer = innerChannelAuthorizer;
    }

    /// <summary>
    /// Authorizes the requested Docs or host stream channel.
    /// </summary>
    /// <param name="context">The RazorWire stream authorization context.</param>
    /// <returns>A passive AppSurface auth result for the stream subscription.</returns>
    public async ValueTask<AppSurfaceAuthResult> AuthorizeAsync(RazorWireStreamAuthorizationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TryGetReplacementChannelAuthorizer(context.HttpContext) is { } replacementChannelAuthorizer)
        {
            return await AuthorizeWithLegacyChannelAuthorizerAsync(context, replacementChannelAuthorizer);
        }

        if (!AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(context.Channel))
        {
            return await AuthorizeHostChannelAsync(context);
        }

        if (!AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(_options, _environment))
        {
            return AppSurfaceAuthResult.Forbidden();
        }

        if (_environment.IsDevelopment())
        {
            return AppSurfaceAuthResult.Allowed();
        }

        if (_innerStreamAuthorizer is not null)
        {
            return await _innerStreamAuthorizer.AuthorizeAsync(context);
        }

        if (_innerChannelAuthorizer is not null && !IsBuiltInAuthorizer(_innerChannelAuthorizer))
        {
            return await AuthorizeWithLegacyChannelAuthorizerAsync(context, _innerChannelAuthorizer);
        }

        return AppSurfaceAuthResult.Forbidden();
    }

    private async ValueTask<AppSurfaceAuthResult> AuthorizeHostChannelAsync(RazorWireStreamAuthorizationContext context)
    {
        if (_innerStreamAuthorizer is not null)
        {
            return await _innerStreamAuthorizer.AuthorizeAsync(context);
        }

        return _innerChannelAuthorizer is not null
            ? await AuthorizeWithLegacyChannelAuthorizerAsync(context, _innerChannelAuthorizer)
            : AppSurfaceAuthResult.Forbidden();
    }

    private static async ValueTask<AppSurfaceAuthResult> AuthorizeWithLegacyChannelAuthorizerAsync(
        RazorWireStreamAuthorizationContext context,
        IRazorWireChannelAuthorizer authorizer)
    {
        var allowed = await authorizer.CanSubscribeAsync(context.HttpContext, context.Channel);

        return allowed
            ? AppSurfaceAuthResult.Allowed()
            : AppSurfaceAuthResult.Forbidden();
    }

    private static bool IsBuiltInAuthorizer(IRazorWireChannelAuthorizer authorizer)
    {
        return authorizer is DenyAllRazorWireChannelAuthorizer or AllowAllRazorWireChannelAuthorizer;
    }

    private static IRazorWireChannelAuthorizer? TryGetReplacementChannelAuthorizer(HttpContext context)
    {
        var authorizer = context.RequestServices?.GetService<IRazorWireChannelAuthorizer>();

        return authorizer is null or AppSurfaceDocsHarvestChannelAuthorizer ? null : authorizer;
    }
}
