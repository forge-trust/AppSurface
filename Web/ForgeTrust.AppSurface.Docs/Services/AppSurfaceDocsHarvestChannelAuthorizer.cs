using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Authorizes the AppSurface Docs harvest progress stream with harvest visibility and host-owned stream policy.
/// </summary>
internal sealed class AppSurfaceDocsHarvestChannelAuthorizer : IRazorWireChannelAuthorizer
{
    private readonly IRazorWireStreamAuthorizer _streamAuthorizer;

    /// <summary>
    /// Creates an authorizer that applies docs-harvest route visibility to the harvest progress stream.
    /// </summary>
    /// <param name="options">Docs options used by <see cref="AppSurfaceDocsHarvestHealthVisibility"/>.</param>
    /// <param name="environment">The host environment used with <paramref name="options"/> for visibility decisions.</param>
    /// <param name="inner">Optional inner authorizer for existing RazorWire channel rules.</param>
    /// <remarks>
    /// The harvest channel is denied when harvest routes are hidden. Development hosts may use the default live
    /// observatory without a custom authorizer. Non-development hosts must provide a custom, non-built-in
    /// <see cref="IRazorWireStreamAuthorizer"/> or legacy <see cref="IRazorWireChannelAuthorizer"/> that allows the
    /// AppSurface Docs harvest progress channel. This legacy constructor builds an
    /// <see cref="AppSurfaceDocsHarvestStreamAuthorizer"/> and supplies <paramref name="inner"/> as the bool-facade
    /// fallback. Non-harvest channels delegate through the effective result authorizer, including the legacy
    /// <paramref name="inner"/> fallback when supplied, and otherwise remain denied so registering AppSurface Docs does
    /// not make unrelated RazorWire streams public.
    /// </remarks>
    public AppSurfaceDocsHarvestChannelAuthorizer(
        AppSurfaceDocsOptions options,
        IHostEnvironment environment,
        IRazorWireChannelAuthorizer? inner = null)
        : this(new AppSurfaceDocsHarvestStreamAuthorizer(options, environment, innerChannelAuthorizer: inner))
    {
    }

    /// <summary>
    /// Creates a bool facade over the effective result-bearing Docs harvest stream authorizer.
    /// </summary>
    /// <param name="streamAuthorizer">The effective stream authorizer used by the RazorWire endpoint.</param>
    public AppSurfaceDocsHarvestChannelAuthorizer(IRazorWireStreamAuthorizer streamAuthorizer)
    {
        _streamAuthorizer = streamAuthorizer ?? throw new ArgumentNullException(nameof(streamAuthorizer));
    }

    /// <summary>
    /// Determines whether the current request can subscribe to the requested RazorWire channel.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="channel">The requested channel name.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="channel"/> passes harvest visibility checks and optional delegated
    /// authorization; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <see cref="AppSurfaceDocsStreamAuthorization.IsHarvestProgressChannel(string?)"/> identifies the harvest
    /// progress stream so only that channel is tied to
    /// <see cref="AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(AppSurfaceDocsOptions, IHostEnvironment)"/>.
    /// The method has no shared mutable state beyond reading constructor dependencies, but the delegated authorizer may
    /// perform asynchronous policy checks.
    /// </remarks>
    public async ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        ArgumentNullException.ThrowIfNull(context);

        return (await _streamAuthorizer.AuthorizeAsync(
            new RazorWireStreamAuthorizationContext(
                context,
                channel,
                context.RequestServices?.GetService<RazorWireOptions>()?.Streams.AuthorizationMode
                ?? RazorWireStreamAuthorizationMode.DenyAll))).IsAllowed;
    }
}
