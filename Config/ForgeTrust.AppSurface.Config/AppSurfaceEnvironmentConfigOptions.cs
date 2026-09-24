using System.Collections.Frozen;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>Configures explicit native environment names for logical configuration keys.</summary>
/// <remarks>
/// Mappings are exact suffix replacements in both scoped and unscoped layers. The suffix is deliberately narrower
/// than a logical segment so the environment codec remains injective and collision checks remain deterministic.
/// Configure these options before the host starts. Provider construction snapshots them; subsequent mutations
/// do not change an existing provider. Canonical and historical aliases are both replaced by a mapping.
/// The replacement also applies when resolving a member through its aggregate: MapKey("App:Name", "CUSTOM_NAME")
/// binds App.Name from CUSTOM_NAME or its scoped form, even when no APP__ prefix is present. APP__NAME is ignored.
/// </remarks>
/// <seealso href="https://appsurface.dev/guides/config-logical-keys">Logical configuration keys</seealso>
public sealed class AppSurfaceEnvironmentConfigOptions
{
    /// <summary>Gets or sets the maximum distinct environment/key reverse claims retained by one provider; defaults to 4,096.</summary>
    /// <remarks>Must be positive. A new claim beyond this capacity fails closed; existing claims remain usable.</remarks>
    public int MaxAdHocClaims { get; set; } = 4096;

    private readonly List<(AppSurfaceConfigKey Key, string Suffix)> _mappings = [];

    /// <summary>Maps a typed logical key to an exact native suffix.</summary>
    /// <param name="logicalKey">Immutable logical identity; comparisons ignore ordinal casing.</param>
    /// <param name="nativeSuffix">Exact ASCII letters, digits, and single interior underscores; no edge underscores or double underscores.</param>
    /// <returns>This builder, for chained registrations.</returns>
    /// <remarks>
    /// For example, Payments_ApiKey produces Production's scoped name PRODUCTION__Payments_ApiKey and the unscoped
    /// name Payments_ApiKey. Casing is preserved and required at lookup. Duplicates and reverse claims are validated
    /// when options and finalized declarations are frozen; equal values never make a collision valid.
    /// </remarks>
    public AppSurfaceEnvironmentConfigOptions MapKey(AppSurfaceConfigKey logicalKey, string nativeSuffix)
    {
        ArgumentNullException.ThrowIfNull(logicalKey);
        ValidateSuffix(nativeSuffix);
        _mappings.Add((logicalKey, nativeSuffix));
        return this;
    }

    /// <summary>Maps a strict logical key string to an exact native suffix.</summary>
    /// <param name="logicalKey">Colon-delimited key; dots and hyphens remain literal segment content.</param>
    /// <param name="nativeSuffix">Exact suffix in the same grammar as the typed overload.</param>
    /// <returns>This builder, for chained registrations.</returns>
    public AppSurfaceEnvironmentConfigOptions MapKey(string logicalKey, string nativeSuffix) =>
        MapKey(AppSurfaceConfigKey.Parse(logicalKey), nativeSuffix);

    /// <summary>Validates the complete registration set and returns an immutable forward mapping.</summary>
    internal FrozenDictionary<AppSurfaceConfigKey, string> Snapshot()
    {
        if (MaxAdHocClaims <= 0)
        {
            throw new OptionsValidationException(nameof(AppSurfaceEnvironmentConfigOptions),
                typeof(AppSurfaceEnvironmentConfigOptions), ["MaxAdHocClaims must be positive."]);
        }

        var byKey = new Dictionary<AppSurfaceConfigKey, string>();
        var bySuffix = new Dictionary<string, AppSurfaceConfigKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, suffix) in _mappings)
        {
            if (!byKey.TryAdd(key, suffix))
            {
                throw new OptionsValidationException(nameof(AppSurfaceEnvironmentConfigOptions),
                    typeof(AppSurfaceEnvironmentConfigOptions), ["Each logical key may be mapped once."]);
            }

            if (!bySuffix.TryAdd(suffix, key))
            {
                throw new OptionsValidationException(nameof(AppSurfaceEnvironmentConfigOptions),
                    typeof(AppSurfaceEnvironmentConfigOptions), ["Native suffixes must be unique ignoring case."]);
            }

            if (EnvironmentConfigCodec.TryEncode(key, out var canonical)
                && !suffix.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                // A mapping may intentionally replace its own canonical name, but cannot claim another
                // key's known canonical suffix because that would make reverse ownership ambiguous.
                foreach (var other in _mappings)
                {
                    if (!other.Key.Equals(key)
                        && EnvironmentConfigCodec.TryEncode(other.Key, out var otherCanonical)
                        && suffix.Equals(otherCanonical, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new OptionsValidationException(nameof(AppSurfaceEnvironmentConfigOptions),
                            typeof(AppSurfaceEnvironmentConfigOptions), ["Explicit native suffix collides with a known canonical key."]);
                    }
                }
            }
        }

        return byKey.ToFrozenDictionary();
    }

    /// <summary>Rejects suffixes that could collapse a native hierarchy or require lossy spelling conversion.</summary>
    internal static void ValidateSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix) || suffix[0] == '_' || suffix[^1] == '_' || suffix.Contains("__", StringComparison.Ordinal))
        {
            throw new ArgumentException("Native suffix must be nonempty and cannot have edge or repeated underscores.", nameof(suffix));
        }

        foreach (var c in suffix)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                throw new ArgumentException("Native suffix may contain only ASCII letters, digits, and single underscores.", nameof(suffix));
            }
        }
    }
}
