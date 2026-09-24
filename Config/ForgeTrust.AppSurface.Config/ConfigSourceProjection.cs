using System.Collections.Frozen;

namespace ForgeTrust.AppSurface.Config;

/// <summary>A source entry before any case-insensitive insertion can discard collision evidence.</summary>
/// <typeparam name="T">Provider-owned retrieval metadata, never rendered by the shared projection.</typeparam>
internal sealed record ConfigSourceEntry<T>(
    AppSurfaceConfigKey Key,
    string SourceSpelling,
    string Layer,
    string NativeIdentifier,
    T Metadata,
    bool IsLegacyAlias = false);

/// <summary>The identity outcome for an entire collision domain.</summary>
internal enum ConfigSourceProjectionStatus
{
    /// <summary>One unambiguous source identity is present.</summary>
    Unique,
    /// <summary>Multiple sources provide the same identity in explicit precedence order.</summary>
    IntentionalOverride,
    /// <summary>One layer repeats the same native identifier for a key; resolution fails closed.</summary>
    Duplicate,
    /// <summary>Source spellings differ, or one layer supplies distinct native identifiers for a key; resolution fails closed.</summary>
    Collision,
    /// <summary>Distinct logical keys share a native identifier under the provider's comparer; resolution fails closed.</summary>
    Unrepresentable
}

/// <summary>Retains every origin, the declared precedence winner, and any terminal identity condition.</summary>
internal sealed record ConfigSourceProjectionResult<T>(
    AppSurfaceConfigKey Key,
    ConfigSourceProjectionStatus Status,
    IReadOnlyList<ConfigSourceEntry<T>> Entries)
{
    /// <summary>Gets whether the collision domain must fail closed before selecting a value.</summary>
    internal bool IsTerminal => Status is ConfigSourceProjectionStatus.Duplicate
        or ConfigSourceProjectionStatus.Collision or ConfigSourceProjectionStatus.Unrepresentable;

    /// <summary>Gets the last declared layer's entry only when identity is unambiguous.</summary>
    internal ConfigSourceEntry<T>? Winner => IsTerminal ? null : Entries[^1];
}

/// <summary>Builds an immutable typed index after retaining and validating all native source occurrences.</summary>
internal sealed class ConfigSourceProjection<T>
{
    private readonly FrozenDictionary<AppSurfaceConfigKey, ConfigSourceProjectionResult<T>> _entries;

    /// <summary>Projects already-ordered source layers, optionally rejecting shared native identifiers.</summary>
    /// <param name="entries">Raw entries; enumeration order defines precedence only within declared layer order.</param>
    /// <param name="orderedLayers">Explicit lower-to-higher layer identities; timing never determines precedence.</param>
    /// <param name="nativeComparer">Native collision comparison, or null when the provider checks native claims separately.</param>
    internal ConfigSourceProjection(IEnumerable<ConfigSourceEntry<T>> entries, IReadOnlyList<string> orderedLayers,
        IEqualityComparer<string>? nativeComparer = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(orderedLayers);
        var layers = orderedLayers.Select((layer, index) => (layer, index))
            .ToDictionary(item => item.layer, item => item.index, StringComparer.Ordinal);
        var raw = entries.ToArray();
        if (raw.Any(entry => !layers.ContainsKey(entry.Layer)))
        {
            throw new ArgumentException("Every source must name a declared layer.", nameof(entries));
        }

        var nativeConflicts = new HashSet<AppSurfaceConfigKey>();
        if (nativeComparer is not null)
        {
            foreach (var group in raw.GroupBy(entry => entry.NativeIdentifier, nativeComparer))
            {
                var identities = group.Select(entry => entry.Key).Distinct().ToArray();
                if (identities.Length > 1)
                {
                    nativeConflicts.UnionWith(identities);
                }
            }
        }

        _entries = raw.GroupBy(entry => entry.Key).Select(group =>
        {
            var ordered = group.OrderBy(entry => layers[entry.Layer]).ToArray();
            var status = nativeConflicts.Contains(group.Key)
                ? ConfigSourceProjectionStatus.Unrepresentable
                : Classify(ordered);
            return new ConfigSourceProjectionResult<T>(ordered[^1].Key, status, Array.AsReadOnly(ordered));
        }).ToFrozenDictionary(result => result.Key);
    }

    /// <summary>Gets deterministic projected entries, retaining their exact native locators.</summary>
    internal IEnumerable<ConfigSourceProjectionResult<T>> Entries => _entries.Values
        .OrderBy(entry => entry.Key.Value, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Key.Value, StringComparer.Ordinal);

    /// <summary>Finds one logical identity without selecting a value from a terminal collision domain.</summary>
    internal bool TryGet(AppSurfaceConfigKey key, out ConfigSourceProjectionResult<T>? result) =>
        _entries.TryGetValue(key, out result);

    private static ConfigSourceProjectionStatus Classify(ConfigSourceEntry<T>[] entries)
    {
        if (entries.Select(entry => entry.SourceSpelling).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            return ConfigSourceProjectionStatus.Collision;
        }

        foreach (var group in entries.GroupBy(entry => entry.Layer, StringComparer.Ordinal))
        {
            if (group.Skip(1).Any())
            {
                return group.Select(entry => entry.NativeIdentifier).Distinct(StringComparer.Ordinal).Skip(1).Any()
                    ? ConfigSourceProjectionStatus.Collision
                    : ConfigSourceProjectionStatus.Duplicate;
            }
        }

        return entries.Length > 1 ? ConfigSourceProjectionStatus.IntentionalOverride : ConfigSourceProjectionStatus.Unique;
    }
}
