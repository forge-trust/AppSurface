using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.GoogleSecretManager;

/// <summary>Describes one known Google convention identity that needs an explicit migration mapping.</summary>
public sealed record AppSurfaceGoogleSecretMigrationEntry(
    AppSurfaceConfigKey LogicalKey,
    string LegacySecretId,
    string NewSecretId,
    string SuggestedMapSecret,
    string? Version)
{
    /// <summary>Gets the exact options call that pins the existing legacy secret id.</summary>
    public string MapSecretSnippet => SuggestedMapSecret;
}

/// <summary>Computes Google convention migration mappings without accessing Secret Manager.</summary>
public static class AppSurfaceGoogleSecretMigrationInventory
{
    /// <summary>Computes mappings for known keys whose old lossy id differs from the new injective id.</summary>
    public static IReadOnlyList<AppSurfaceGoogleSecretMigrationEntry> Inventory(
        AppSurfaceGoogleSecretManagerOptions options,
        IEnumerable<AppSurfaceConfigKey> knownKeys)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Inventory(options, knownKeys, convention => convention.SecretIdPrefix, (key, convention) =>
            {
                var prefix = AppSurfaceConfigKey.Parse(convention.LogicalKeyPrefix);
                return key.IsSameOrDescendantOf(prefix) ? prefix.Value.Length : -1;
            });
    }

    /// <summary>Computes migration mappings using caller-supplied raw legacy convention prefixes.</summary>
    /// <param name="options">The current options, which determine new encoded ids and versions.</param>
    /// <param name="knownKeys">The logical keys to inspect.</param>
    /// <param name="legacyLogicalKeyPrefix">The historical key prefix, checked with ordinal <see cref="string.StartsWith(string, StringComparison)"/> semantics after keys are scoped to a current convention by segment ancestry.</param>
    /// <param name="legacySecretIdPrefix">The historical id prefix; an empty value is supported.</param>
    /// <returns>Mappings for keys whose historical normalized id differs from the current encoded id.</returns>
    /// <exception cref="ArgumentNullException">An argument or a key in <paramref name="knownKeys"/> is null.</exception>
    /// <exception cref="ArgumentException">The raw legacy key prefix is empty.</exception>
    public static IReadOnlyList<AppSurfaceGoogleSecretMigrationEntry> Inventory(
        AppSurfaceGoogleSecretManagerOptions options,
        IEnumerable<AppSurfaceConfigKey> knownKeys,
        string legacyLogicalKeyPrefix,
        string legacySecretIdPrefix)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(knownKeys);
        ArgumentNullException.ThrowIfNull(legacyLogicalKeyPrefix);
        ArgumentNullException.ThrowIfNull(legacySecretIdPrefix);
        if (legacyLogicalKeyPrefix.Length == 0)
        {
            throw new ArgumentException("The raw legacy key prefix must not be empty.", nameof(legacyLogicalKeyPrefix));
        }

        return Inventory(options, knownKeys, _ => legacySecretIdPrefix,
            (key, convention) => key.IsSameOrDescendantOf(AppSurfaceConfigKey.Parse(convention.LogicalKeyPrefix))
                && key.Value.StartsWith(legacyLogicalKeyPrefix, StringComparison.Ordinal)
                ? legacyLogicalKeyPrefix.Length
                : -1);
    }

    private static IReadOnlyList<AppSurfaceGoogleSecretMigrationEntry> Inventory(
        AppSurfaceGoogleSecretManagerOptions options,
        IEnumerable<AppSurfaceConfigKey> knownKeys,
        Func<AppSurfaceGoogleSecretConvention, string> legacyIdPrefix,
        Func<AppSurfaceConfigKey, AppSurfaceGoogleSecretConvention, int> getPrefixLength)
    {
        ArgumentNullException.ThrowIfNull(knownKeys);

        var entries = new Dictionary<AppSurfaceConfigKey, AppSurfaceGoogleSecretMigrationEntry>();
        foreach (var key in knownKeys)
        {
            ArgumentNullException.ThrowIfNull(key);
            foreach (var convention in options.Conventions)
            {
                var prefixLength = getPrefixLength(key, convention);
                if (prefixLength < 0)
                {
                    continue;
                }

                var legacyId = legacyIdPrefix(convention)
                    + key.Value[prefixLength..].Replace(':', '-').Replace('.', '-').Replace('_', '-').ToLowerInvariant();
                if (!GoogleSecretManagerSecretReference.TryEncodeKey(key, out var encoded))
                {
                    break;
                }

                var newId = convention.SecretIdPrefix + encoded;
                if (!StringComparer.Ordinal.Equals(legacyId, newId))
                {
                    var version = convention.Version ?? options.DefaultVersion;
                    var versionArgument = version == null ? string.Empty : $", version: \"{version}\"";
                    entries[key] = new AppSurfaceGoogleSecretMigrationEntry(key, legacyId, newId,
                        $"options.MapSecret(AppSurfaceConfigKey.Parse(\"{key.Value}\"), \"{legacyId}\"{versionArgument});",
                        version);
                }

                break;
            }
        }

        return entries.Values.OrderBy(entry => entry.LogicalKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LogicalKey.Value, StringComparer.Ordinal).ToArray();
    }
}
