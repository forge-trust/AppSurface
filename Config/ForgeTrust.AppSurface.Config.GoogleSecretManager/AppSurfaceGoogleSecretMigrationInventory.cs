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
        ArgumentNullException.ThrowIfNull(knownKeys);

        var entries = new Dictionary<AppSurfaceConfigKey, AppSurfaceGoogleSecretMigrationEntry>();
        foreach (var key in knownKeys)
        {
            ArgumentNullException.ThrowIfNull(key);
            foreach (var convention in options.Conventions)
            {
                var prefix = AppSurfaceConfigKey.Parse(convention.LogicalKeyPrefix);
                if (!key.IsSameOrDescendantOf(prefix))
                {
                    continue;
                }

                var legacyId = convention.SecretIdPrefix
                    + key.Value[prefix.Value.Length..].Replace(':', '-').Replace('.', '-').Replace('_', '-').ToLowerInvariant();
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
