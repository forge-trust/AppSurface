namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Provides stable helpers for host-owned AppSurface Docs RazorWire stream authorization.
/// </summary>
/// <remarks>
/// Host applications that implement <c>IRazorWireChannelAuthorizer</c> should use
/// <see cref="IsHarvestProgressChannel(string?)"/> when applying production authorization rules to the AppSurface Docs
/// live harvest progress stream. Prefer the predicate over raw string comparison so future docs-owned stream naming
/// remains centralized.
/// </remarks>
public static class AppSurfaceDocsStreamAuthorization
{
    /// <summary>
    /// Gets the RazorWire channel used by AppSurface Docs for live harvest progress.
    /// </summary>
    /// <remarks>
    /// This constant is exposed for diagnostics, tests, and advanced authorizers. Application authorization code should
    /// usually call <see cref="IsHarvestProgressChannel(string?)"/> instead of comparing the value directly.
    /// </remarks>
    public const string HarvestProgressChannel = "appsurfacedocs-harvest";

    /// <summary>
    /// Determines whether a RazorWire channel is the AppSurface Docs live harvest progress channel.
    /// </summary>
    /// <param name="channel">The requested RazorWire channel name.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="channel"/> exactly matches <see cref="HarvestProgressChannel"/>;
    /// otherwise <see langword="false"/>. Null, empty, and differently cased channel names do not match.
    /// </returns>
    public static bool IsHarvestProgressChannel(string? channel)
    {
        return string.Equals(channel, HarvestProgressChannel, StringComparison.Ordinal);
    }
}
