using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Http;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Authorizes the AppSurface Docs harvest progress stream with the same visibility policy as harvest health.
/// </summary>
internal sealed class AppSurfaceDocsHarvestChannelAuthorizer : IRazorWireChannelAuthorizer
{
    private readonly AppSurfaceDocsOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly IRazorWireChannelAuthorizer? _inner;

    /// <summary>
    /// Creates an authorizer that applies docs-harvest route visibility to the harvest progress stream.
    /// </summary>
    /// <param name="options">Docs options used by <see cref="AppSurfaceDocsHarvestHealthVisibility"/>.</param>
    /// <param name="environment">The host environment used with <paramref name="options"/> for visibility decisions.</param>
    /// <param name="inner">Optional inner authorizer for existing RazorWire channel rules.</param>
    /// <remarks>
    /// The harvest channel is denied when harvest routes are hidden, even if <paramref name="inner"/> would allow it.
    /// Non-harvest channels delegate to <paramref name="inner"/> when supplied and otherwise remain denied so registering
    /// AppSurface Docs does not make unrelated RazorWire streams public.
    /// </remarks>
    public AppSurfaceDocsHarvestChannelAuthorizer(
        AppSurfaceDocsOptions options,
        IHostEnvironment environment,
        IRazorWireChannelAuthorizer? inner = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _inner = inner;
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
    /// <see cref="AppSurfaceDocsHarvestProgressReporter.ChannelName"/> is compared ordinally so only the harvest
    /// progress stream is tied to <see cref="AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(AppSurfaceDocsOptions, IHostEnvironment)"/>.
    /// The method has no shared mutable state beyond reading constructor dependencies, but the delegated authorizer may
    /// perform asynchronous policy checks.
    /// </remarks>
    public async ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(channel, AppSurfaceDocsHarvestProgressReporter.ChannelName, StringComparison.Ordinal))
        {
            return _inner is not null && await _inner.CanSubscribeAsync(context, channel);
        }

        if (!AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(_options, _environment))
        {
            return false;
        }

        return _inner is null || await _inner.CanSubscribeAsync(context, channel);
    }
}
