namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Provides stable helpers for host-owned AppSurface Docs RazorWire stream authorization.
/// </summary>
/// <remarks>
/// Host applications that implement <c>IRazorWireStreamAuthorizer</c>, or legacy
/// <c>IRazorWireChannelAuthorizer</c> compatibility policies, should use <see cref="IsHarvestProgressChannel(string?)"/>
/// when applying production authorization rules to the AppSurface Docs live harvest progress stream. Prefer the
/// predicate over raw string comparison so future docs-owned stream naming remains centralized.
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
        return string.Equals(channel, HarvestProgressChannel, StringComparison.Ordinal)
               || TryGetNamedInstanceFromHarvestProgressChannel(channel, out _);
    }

    /// <summary>
    /// Builds the isolated harvest-progress channel name for one named Docs product.
    /// </summary>
    /// <param name="instanceName">The normalized Docs product name.</param>
    /// <returns>A stable RazorWire-safe channel name for the supplied product.</returns>
    public static string GetHarvestProgressChannel(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        return $"{HarvestProgressChannel}-{instanceName.Trim()}";
    }

    /// <summary>
    /// Attempts to read the Docs product name from a named harvest-progress channel.
    /// </summary>
    /// <param name="channel">The requested RazorWire channel.</param>
    /// <param name="instanceName">The normalized instance name when the channel belongs to a named Docs product.</param>
    /// <returns><see langword="true" /> when <paramref name="channel" /> is a named Docs harvest channel.</returns>
    public static bool TryGetNamedInstanceFromHarvestProgressChannel(string? channel, out string? instanceName)
    {
        instanceName = null;
        var prefix = HarvestProgressChannel + "-";
        if (string.IsNullOrWhiteSpace(channel)
            || !channel.StartsWith(prefix, StringComparison.Ordinal)
            || channel.Length == prefix.Length)
        {
            return false;
        }

        var candidate = channel[prefix.Length..];
        if (!candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return false;
        }

        instanceName = candidate;
        return true;
    }
}
