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
    /// <see langword="true"/> when <paramref name="channel"/> exactly matches <see cref="HarvestProgressChannel"/> or
    /// a valid named product channel returned by <see cref="GetHarvestProgressChannel"/>; otherwise
    /// <see langword="false"/>. Null, empty, malformed, and differently cased channel names do not match.
    /// </returns>
    public static bool IsHarvestProgressChannel(string? channel)
    {
        return IsLegacyHarvestProgressChannel(channel)
               || TryGetNamedInstanceFromHarvestProgressChannel(channel, out _);
    }

    /// <summary>
    /// Determines whether a RazorWire channel is the one legacy AppSurface Docs harvest-progress channel.
    /// </summary>
    /// <remarks>
    /// Legacy Docs registration owns only <see cref="HarvestProgressChannel"/>. Named Docs channels must be handled by
    /// <c>AppSurfaceDocsNamedHarvestStreamAuthorizationFilter</c>; treating every named-looking channel as legacy Docs
    /// would override unrelated host channel policies that happen to use the same prefix.
    /// </remarks>
    /// <param name="channel">The requested RazorWire channel name.</param>
    /// <returns><see langword="true"/> only for an exact legacy Docs harvest-progress channel match.</returns>
    internal static bool IsLegacyHarvestProgressChannel(string? channel)
    {
        return string.Equals(channel, HarvestProgressChannel, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the isolated harvest-progress channel name for one named Docs product.
    /// </summary>
    /// <param name="instanceName">
    /// The Docs product name to normalize. It must contain 1-64 ASCII letters, digits, hyphens, or underscores.
    /// </param>
    /// <returns>A stable, lowercase RazorWire-safe channel name for the supplied product.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="instanceName"/> is blank or is not a valid Docs product name.</exception>
    public static string GetHarvestProgressChannel(string instanceName)
    {
        return $"{HarvestProgressChannel}-{AppSurfaceDocsInstanceDeclaration.NormalizeName(instanceName, nameof(instanceName)).ToLowerInvariant()}";
    }

    /// <summary>
    /// Attempts to read the Docs product name from a named harvest-progress channel.
    /// </summary>
    /// <param name="channel">The requested RazorWire channel.</param>
    /// <param name="instanceName">The canonical lowercase instance name when the channel belongs to a named Docs product.</param>
    /// <returns>
    /// <see langword="true" /> when <paramref name="channel" /> is a named Docs harvest channel with a 1-64 character
    /// Docs product suffix; otherwise <see langword="false"/>.
    /// </returns>
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
        if (candidate.Length > AppSurfaceDocsInstanceDeclaration.MaximumNameLength
            || !candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return false;
        }

        var canonicalChannel = GetHarvestProgressChannel(candidate);
        if (!string.Equals(channel, canonicalChannel, StringComparison.Ordinal))
        {
            return false;
        }

        instanceName = candidate.ToLowerInvariant();
        return true;
    }
}
