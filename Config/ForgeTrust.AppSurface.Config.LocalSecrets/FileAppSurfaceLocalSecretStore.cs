using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// File-backed LocalSecrets store for deterministic local workflows and tests.
/// </summary>
/// <remarks>
/// This store is useful when OS credential tooling is unavailable in CI or examples. It is not the default platform
/// store and should not be used as a production vault. The file contains secret values and must stay outside source
/// control.
/// </remarks>
public sealed class FileAppSurfaceLocalSecretStore : IAppSurfaceLocalSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FileAppSurfaceLocalSecretStore"/> class.
    /// </summary>
    /// <param name="path">The JSON file that stores local secrets.</param>
    public FileAppSurfaceLocalSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public string Name => nameof(FileAppSurfaceLocalSecretStore);

    /// <summary>
    /// Gets the default per-user AppSurface local secret file path.
    /// </summary>
    /// <returns>A path under the user's local application data directory.</returns>
    public static string GetDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".appsurface");
        }

        return Path.Combine(root, "AppSurface", "local-secrets.json");
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            var data = Read();
            return data.TryGetValue(identity.StorageName, out var entry)
                ? AppSurfaceLocalSecretResult.Found(entry.Value, Name)
                : AppSurfaceLocalSecretResult.Missing(Name);
        }
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            var data = Read();
            data[identity.StorageName] = new FileSecretEntry(identity.ApplicationName, identity.Environment, identity.KeyPrefix, identity.Key, value);
            Write(data);
        }

        return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            var data = Read();
            if (!data.Remove(identity.StorageName))
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            Write(data);
        }

        return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix)
    {
        lock (_gate)
        {
            var keys = Read().Values
                .Where(entry => string.Equals(entry.ApplicationName, applicationName, StringComparison.Ordinal)
                                && string.Equals(entry.Environment, environment, StringComparison.Ordinal)
                                && string.Equals(entry.KeyPrefix, keyPrefix, StringComparison.Ordinal))
                .Select(entry => entry.Key);

            return AppSurfaceLocalSecretListResult.Found(keys, Name);
        }
    }

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.Missing,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-ready",
                    "Local secret file store is ready.",
                    "The configured file can be opened for read/write access.",
                    "Use this store only for deterministic local examples or tests; prefer the OS-backed store for normal local development.",
                    "local-secrets-without-a-remote-vault"),
                Name);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(LocalSecretResultStatus.Locked, "local-secret-store-locked", "Local secret file store cannot be opened.", "The current user cannot read or write the configured local secret file.", "Fix file permissions or choose an OS-backed store.");
        }
        catch (IOException)
        {
            return Failure(LocalSecretResultStatus.Unavailable, "local-secret-store-unavailable", "Local secret file store is unavailable.", "The local secret file could not be opened.", "Close other processes using the file and retry.", retryable: true);
        }
    }

    private Dictionary<string, FileSecretEntry> Read()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, FileSecretEntry>(StringComparer.Ordinal);
        }

        try
        {
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new Dictionary<string, FileSecretEntry>(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, FileSecretEntry>>(text, JsonOptions)
                   ?? new Dictionary<string, FileSecretEntry>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Local secret file could not be parsed.");
        }
    }

    private void Write(Dictionary<string, FileSecretEntry> data)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private AppSurfaceLocalSecretResult Failure(
        LocalSecretResultStatus status,
        string code,
        string problem,
        string cause,
        string fix,
        bool retryable = false) =>
        AppSurfaceLocalSecretResult.NotFound(
            status,
            new AppSurfaceLocalSecretDiagnostic(code, problem, cause, fix, "local-secrets-without-a-remote-vault", retryable),
            Name);

    private sealed record FileSecretEntry(
        string ApplicationName,
        string Environment,
        string? KeyPrefix,
        string Key,
        string Value);
}
