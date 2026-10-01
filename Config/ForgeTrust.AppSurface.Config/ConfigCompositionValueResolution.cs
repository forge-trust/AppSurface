using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Config;

/// <summary>A whole-root provider capability that returns text before application-object binding.</summary>
/// <remarks>Alias this interface to the same singleton as <see cref="IConfigProvider"/>. Whole roots remain
/// priority-ordered alternatives, never deep-merged peers. See
/// <see href="https://appsurface.dev/config/secret-references#provider-contract">provider compatibility</see>.</remarks>
public interface IConfigCompositionValueProvider
{
    /// <summary>Resolves one raw root with provenance, preserving missing and terminal behavior.</summary>
    ConfigCompositionValueResolution ResolveRaw(string environment, string logicalKey);
}

/// <summary>Lets a typed-only legacy provider locally prove an opted-in root is unrelated.</summary>
public interface IConfigProviderClaimInspector
{
    /// <summary>Inspects ownership without I/O. Unclaimed providers can be safely excluded from composition.</summary>
    ConfigProviderClaim InspectClaim(string environment, string logicalKey);
}

/// <summary>A local whole-root ownership classification.</summary>
public enum ConfigProviderClaim
{
    /// <summary>This provider cannot contribute to the requested root.</summary>
    Unclaimed,
    /// <summary>This provider might contribute and must support raw resolution.</summary>
    MayClaim
}

/// <summary>Classifies raw root resolution without deserializing an application object.</summary>
public enum ConfigCompositionValueResolutionStatus
{
    /// <summary>The provider does not own this root.</summary>
    Unclaimed,
    /// <summary>The root was absent and lower providers may run.</summary>
    Missing,
    /// <summary>A raw contribution was found, including textual JSON null.</summary>
    Resolved,
    /// <summary>A terminal failure prevents fallback.</summary>
    TerminalFailure
}

/// <summary>Value-safe raw resolution. Found JSON null is a resolved contribution, not absence.</summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class ConfigCompositionValueResolution
{
    [JsonIgnore, DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _raw;
    private ConfigCompositionValueResolution(ConfigCompositionValueResolutionStatus status, string providerName,
        int priority, bool isSensitive, string? raw = null, bool retryable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (providerName.Length > 128 || providerName.Any(char.IsControl))
            throw new ArgumentException("Provider name must be bounded display-safe metadata.", nameof(providerName));
        Status = status;
        ProviderName = providerName;
        Priority = priority;
        IsSensitive = isSensitive;
        _raw = raw;
        Retryable = retryable;
    }
    /// <summary>The whole-root outcome.</summary>
    public ConfigCompositionValueResolutionStatus Status { get; }
    /// <summary>The existing provider name, not an external resource identifier.</summary>
    public string ProviderName { get; }
    /// <summary>The existing whole-root priority.</summary>
    public int Priority { get; }
    /// <summary>Whether scalars from this source may supply secret destinations.</summary>
    public bool IsSensitive { get; }
    /// <summary>Whether a terminal failure may be transient.</summary>
    public bool Retryable { get; }
    /// <summary>Reads transient raw text only inside core composition.</summary>
    internal string? ReadRaw() => _raw;
    /// <summary>Returns an unclaimed root without payload storage.</summary>
    public static ConfigCompositionValueResolution Unclaimed(string providerName, int priority, bool isSensitive = false) => new(ConfigCompositionValueResolutionStatus.Unclaimed, providerName, priority, isSensitive);
    /// <summary>Returns a missing root without payload storage.</summary>
    public static ConfigCompositionValueResolution Missing(string providerName, int priority, bool isSensitive = false) => new(ConfigCompositionValueResolutionStatus.Missing, providerName, priority, isSensitive);
    /// <summary>Returns found raw text; pass "null" to represent a found JSON null.</summary>
    public static ConfigCompositionValueResolution Resolved(string raw, string providerName, int priority, bool isSensitive)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return new(ConfigCompositionValueResolutionStatus.Resolved, providerName, priority, isSensitive, raw);
    }
    /// <summary>Stops lower-root fallback without retaining arbitrary provider text.</summary>
    public static ConfigCompositionValueResolution TerminalFailure(string providerName, int priority, bool isSensitive = true, bool retryable = false) => new(ConfigCompositionValueResolutionStatus.TerminalFailure, providerName, priority, isSensitive, retryable: retryable);
    /// <inheritdoc />
    public override string ToString() => $"{ProviderName}: {Status} (Sensitive={IsSensitive})";
}
