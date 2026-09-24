using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>Decodes only the native namespace envelope; historical source suffixes never pass through the logical parser.</summary>
internal static class LocalSecretMigrationIdentity
{
    /// <summary>Returns the metadata-only failure for a store without a proven destination encoding.</summary>
    internal static AppSurfaceLocalSecretIdentityResult UnsupportedDestination() =>
        AppSurfaceLocalSecretIdentityResult.Invalid(new AppSurfaceLocalSecretDiagnostic(
            "local-secret-migration-unsupported", "Exact-key migration is unavailable for this store.",
            "The selected store does not expose a supported migration destination encoding.",
            "Select a store with complete exact-key migration support.", "local-secrets-migration"));

    internal static AppSurfaceLocalSecretIdentity? Resolve(string applicationName, string environment,
        string? keyPrefix, string storedKey, bool v2)
    {
        var prefix = v2
            ? $"appsurface:v2:{applicationName}:{environment}:{keyPrefix}:"
            : $"appsurface:{applicationName}:{environment}:" + (string.IsNullOrEmpty(keyPrefix) ? string.Empty : keyPrefix + ":");
        if (!storedKey.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var suffix = storedKey[prefix.Length..];
        if (suffix.Length == 0 || suffix is "__appsurface_index__" or "__appsurface_doctor__") return null;
        // Key is a non-routing placeholder. Native reads and index updates use StoredKey/StorageName exclusively.
        return new AppSurfaceLocalSecretIdentity(applicationName, environment, keyPrefix,
            AppSurfaceConfigKey.Parse("__migration_source__"), storedKey)
        { MigrationStoredKey = suffix };
    }
}
