using System.Collections.Concurrent;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// In-memory LocalSecrets store intended for tests and controlled local examples.
/// </summary>
/// <remarks>
/// This store is not durable and is not a production or development secret store. It exists as a package-local test seam
/// so apps can verify LocalSecrets provider behavior without touching the platform credential store.
/// </remarks>
public sealed class InMemoryAppSurfaceLocalSecretStore : IAppSurfaceLocalSecretStore
{
    private readonly ConcurrentDictionary<string, (AppSurfaceLocalSecretIdentity Identity, string Value)> _values =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Name => nameof(InMemoryAppSurfaceLocalSecretStore);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return _values.TryGetValue(identity.StorageName, out var entry)
            ? AppSurfaceLocalSecretResult.Found(entry.Value, Name)
            : AppSurfaceLocalSecretResult.Missing(Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(value);

        _values[identity.StorageName] = (identity, value);
        return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return _values.TryRemove(identity.StorageName, out _)
            ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
            : AppSurfaceLocalSecretResult.Missing(Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix)
    {
        var keys = _values.Values
            .Where(entry => string.Equals(entry.Identity.ApplicationName, applicationName, StringComparison.Ordinal)
                            && string.Equals(entry.Identity.Environment, environment, StringComparison.Ordinal)
                            && string.Equals(entry.Identity.KeyPrefix, keyPrefix, StringComparison.Ordinal))
            .Select(entry => entry.Identity.Key);

        return AppSurfaceLocalSecretListResult.Found(keys, Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
        AppSurfaceLocalSecretResult.NotFound(
            LocalSecretResultStatus.Missing,
            new AppSurfaceLocalSecretDiagnostic(
                "local-secret-store-ready",
                "Local secret test store is ready.",
                "The in-memory store is available for the current process only.",
                "Use the platform store for durable local development secrets.",
                "local-secrets-without-a-remote-vault"),
            Name);
}
