using ForgeTrust.RazorWire;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Describes split-origin hybrid export behavior.
/// </summary>
public sealed class ExportHybridOptions
{
    /// <summary>
    /// Gets a default instance that preserves existing export behavior.
    /// </summary>
    public static ExportHybridOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets the live origin used by RazorWire-managed dynamic references.
    /// </summary>
    public string? LiveOrigin { get; set; }

    /// <summary>
    /// Gets or sets the credential behavior used for RazorWire-managed live references.
    /// </summary>
    public RazorWireHybridCredentialsMode CredentialsMode { get; set; } = RazorWireHybridCredentialsMode.Auto;

    /// <summary>
    /// Gets a value indicating whether split-origin live references are enabled.
    /// </summary>
    internal bool HasLiveOrigin => !string.IsNullOrWhiteSpace(LiveOrigin);

    /// <summary>
    /// Gets a value indicating whether managed live calls should include credentials.
    /// </summary>
    internal bool IncludesCredentials => CredentialsMode is RazorWireHybridCredentialsMode.Include
        || (CredentialsMode is RazorWireHybridCredentialsMode.Auto && HasLiveOrigin);

    /// <summary>
    /// Normalizes an optional origin string for hybrid export.
    /// </summary>
    /// <param name="origin">Origin value to normalize.</param>
    /// <param name="normalizedOrigin">Normalized origin, or null when blank.</param>
    /// <returns><see langword="true"/> when the origin is blank or valid; otherwise <see langword="false"/>.</returns>
    public static bool TryNormalizeOrigin(string? origin, out string? normalizedOrigin)
    {
        normalizedOrigin = null;
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        var trimmed = origin.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        normalizedOrigin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
