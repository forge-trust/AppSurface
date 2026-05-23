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

    public AppSurfaceDocsHarvestChannelAuthorizer(
        AppSurfaceDocsOptions options,
        IHostEnvironment environment,
        IRazorWireChannelAuthorizer? inner = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _inner = inner;
    }

    public async ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(channel, AppSurfaceDocsHarvestProgressReporter.ChannelName, StringComparison.Ordinal))
        {
            return _inner is null || await _inner.CanSubscribeAsync(context, channel);
        }

        if (!AppSurfaceDocsHarvestHealthVisibility.AreRoutesExposed(_options, _environment))
        {
            return false;
        }

        return _inner is null || await _inner.CanSubscribeAsync(context, channel);
    }
}
