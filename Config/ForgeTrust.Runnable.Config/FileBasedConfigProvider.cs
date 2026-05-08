using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Config;

/// <summary>
/// A configuration provider that reads settings from JSON files (e.g., appsettings.json, config_*.json).
/// </summary>
public class FileBasedConfigProvider : IConfigProvider, IConfigDiagnosticProvider
{
    private readonly IConfigFileLocationProvider _configFileLocationProvider;
    private readonly ILogger<FileBasedConfigProvider> _logger;

    private readonly Lazy<ConfigFileProviderSnapshot> _snapshotLazy;

    /// <inheritdoc />
    public int Priority { get; } = 1;

    /// <inheritdoc />
    public string Name { get; } = nameof(FileBasedConfigProvider);

    /// <summary>
    /// Initializes a new instance of the <see cref="FileBasedConfigProvider"/> class.
    /// </summary>
    /// <param name="configFileLocationProvider">The provider for configuration file locations.</param>
    /// <param name="logger">The logger for file operations.</param>
    public FileBasedConfigProvider(
        IConfigFileLocationProvider configFileLocationProvider,
        ILogger<FileBasedConfigProvider> logger)
    {
        _configFileLocationProvider = configFileLocationProvider;
        _logger = logger;

        _snapshotLazy = new Lazy<ConfigFileProviderSnapshot>(InitializeSnapshot, true);
    }

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        if (_snapshotLazy.Value.Environments.TryGetValue(environment, out var envConfig))
        {
            return GetValue<T>(envConfig, key);
        }

        return default;
    }

    ConfigValueResolution IConfigDiagnosticProvider.Resolve(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role)
    {
        var snapshot = _snapshotLazy.Value;
        var diagnostics = snapshot.Diagnostics
            .Where(diagnostic => IsDiagnosticForKey(diagnostic, key))
            .ToList();
        if (!snapshot.Environments.TryGetValue(environment, out var envConfig)
            || !TryGetNode(envConfig, key, out var node))
        {
            return ConfigValueResolution.Missing(key) with { Diagnostics = diagnostics };
        }

        var source = snapshot.Origins.TryGetValue(environment, out var origins)
                     && origins.TryGetValue(key, out var origin)
            ? origin
            : new ConfigAuditSourceRecord
            {
                Kind = ConfigAuditSourceKind.File,
                ProviderName = Name,
                ProviderPriority = Priority,
                ConfigPath = key,
                AppliedToPath = key,
                Role = role
            };

        source = source.WithRole(role);
        try
        {
            var value = node.Deserialize(valueType);
            if (value == null)
            {
                diagnostics.Add(new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = "config-file-null-value",
                    Key = key,
                    ConfigPath = key,
                    Source = source,
                    Message = $"Configuration key '{key}' resolved to null from file provider."
                });
                return ConfigValueResolution.Missing(key) with { Diagnostics = diagnostics };
            }

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                value,
                [source],
                diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            diagnostics.Add(new ConfigAuditDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Error,
                Code = "config-file-conversion-failed",
                Key = key,
                ConfigPath = key,
                Source = source,
                Message = $"Configuration key '{key}' was found in file configuration but could not be converted to {valueType.Name}."
            });
            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Invalid,
                null,
                [source],
                diagnostics);
        }
    }

    IReadOnlyList<ConfigAuditDiagnostic> IConfigDiagnosticProvider.GetReportDiagnostics(string environment) =>
        _snapshotLazy.Value.Diagnostics
            .Where(diagnostic => diagnostic.Key == null && diagnostic.ConfigPath == null)
            .ToList();

    private ConfigFileProviderSnapshot InitializeSnapshot()
    {
        var environments = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ConfigAuditDiagnostic>();

        var directory = _configFileLocationProvider.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new ConfigFileProviderSnapshot(environments, origins, diagnostics);
        }

        // Collect matching files
        string[] files =
        [
            ..Directory.EnumerateFiles(directory, "appsettings*.json", SearchOption.TopDirectoryOnly),
            ..Directory.EnumerateFiles(directory, "config_*.json", SearchOption.TopDirectoryOnly)
        ];

        // Deterministic order so merges are predictable; later files override earlier ones when keys collide
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            JsonNode? root;
            try
            {
                var text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                root = JsonNode.Parse(text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping malformed config file {FileName}", Path.GetFileName(file));
                diagnostics.Add(new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = "config-file-malformed",
                    Message = $"Skipping malformed config file {Path.GetFileName(file)}."
                });

                continue;
            }

            if (root is not JsonObject obj)
            {
                diagnostics.Add(new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = "config-file-non-object-root",
                    Message = $"Skipping config file {Path.GetFileName(file)} because the root is not a JSON object."
                });
                continue; // Only merge JSON objects at the root
            }

            var fileName = Path.GetFileNameWithoutExtension(file);
            var environment = ExtractEnvironment(fileName);

            if (!environments.TryGetValue(environment, out var existing))
            {
                existing = new JsonObject();
                environments[environment] = existing;
                origins[environment] = new Dictionary<string, ConfigAuditSourceRecord>(StringComparer.OrdinalIgnoreCase);
            }

            if (existing is JsonObject targetObj)
            {
                MergeJsonObjects(targetObj, obj, origins[environment], file, parentPath: null, diagnostics);
            }
        }

        return new ConfigFileProviderSnapshot(environments, origins, diagnostics);
    }

    private static string ExtractEnvironment(string fileName)
    {
        // Patterns supported:
        // appsettings.json => production
        // appsettings.Development.json => Development
        // config_Foo.Development.json => Development
        // config_Foo.json or config.json (if it appears) => production
        // Any other unexpected pattern falls back to production

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("config_", StringComparison.OrdinalIgnoreCase))
        {
            var parts = fileName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                // second segment is environment (appsettings.{Env})
                return parts[1];
            }
        }

        return Environments.Production;
    }

    private T? GetValue<T>(JsonNode node, string key)
    {
        if (!TryGetNode(node, key, out var currentNode))
        {
            return default;
        }

        try
        {
            return currentNode.Deserialize<T>();
        }
        catch
        {
            return default;
        }
    }

    private static bool TryGetNode(JsonNode node, string key, out JsonNode currentNode)
    {
        var keys = key.Split('.');
        currentNode = node;
        foreach (var k in keys)
        {
            if (currentNode is JsonObject obj && obj.TryGetPropertyValue(k, out var nextNode) && nextNode != null)
            {
                currentNode = nextNode;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDiagnosticForKey(ConfigAuditDiagnostic diagnostic, string key)
    {
        if (string.Equals(diagnostic.Key, key, StringComparison.Ordinal))
        {
            return true;
        }

        return diagnostic.ConfigPath != null
               && (string.Equals(diagnostic.ConfigPath, key, StringComparison.Ordinal)
                   || diagnostic.ConfigPath.StartsWith($"{key}.", StringComparison.Ordinal));
    }

    private void MergeJsonObjects(
        JsonObject target,
        JsonObject source,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        string file,
        string? parentPath,
        List<ConfigAuditDiagnostic> diagnostics)
    {
        foreach (var kvp in source)
        {
            var path = string.IsNullOrWhiteSpace(parentPath) ? kvp.Key : $"{parentPath}.{kvp.Key}";
            if (kvp.Value == null)
            {
                // Skip null values in source
                diagnostics.Add(new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Info,
                    Code = "config-file-null-skipped",
                    ConfigPath = path,
                    Message = $"Skipping null file value at '{path}'."
                });
                continue;
            }

            origins[path] = CreateFileSource(file, path);
            if (target.ContainsKey(kvp.Key))
            {
                if (target[kvp.Key] is JsonObject targetObj && kvp.Value is JsonObject sourceObj)
                {
                    MergeJsonObjects(targetObj, sourceObj, origins, file, path, diagnostics);
                }
                else
                {
                    // Arrays and scalar values use replace semantics: later files
                    // override earlier values for the same key.
                    RemoveDescendantOrigins(origins, path);
                    target[kvp.Key] = kvp.Value.DeepClone();
                    if (kvp.Value is JsonObject replacementObj)
                    {
                        RecordOrigins(replacementObj, origins, file, path, diagnostics);
                    }
                }
            }
            else
            {
                target[kvp.Key] = kvp.Value.DeepClone();
                if (kvp.Value is JsonObject sourceObj)
                {
                    RecordOrigins(sourceObj, origins, file, path, diagnostics);
                }
            }
        }
    }

    private static void RemoveDescendantOrigins(Dictionary<string, ConfigAuditSourceRecord> origins, string path)
    {
        foreach (var key in origins.Keys.Where(key => key.StartsWith($"{path}.", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            origins.Remove(key);
        }
    }

    private void RecordOrigins(
        JsonObject source,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        string file,
        string parentPath,
        List<ConfigAuditDiagnostic> diagnostics)
    {
        foreach (var kvp in source)
        {
            var path = $"{parentPath}.{kvp.Key}";
            if (kvp.Value == null)
            {
                diagnostics.Add(new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Info,
                    Code = "config-file-null-skipped",
                    ConfigPath = path,
                    Message = $"Skipping null file value at '{path}'."
                });
                continue;
            }

            origins[path] = CreateFileSource(file, path);
            if (kvp.Value is JsonObject child)
            {
                RecordOrigins(child, origins, file, path, diagnostics);
            }
        }
    }

    private ConfigAuditSourceRecord CreateFileSource(string file, string path) =>
        new()
        {
            Kind = ConfigAuditSourceKind.File,
            ProviderName = Name,
            ProviderPriority = Priority,
            FilePath = file,
            ConfigPath = path,
            AppliedToPath = path,
            Role = ConfigAuditSourceRole.Base
        };
}

internal sealed record ConfigFileProviderSnapshot(
    Dictionary<string, JsonNode> Environments,
    Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>> Origins,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

internal static class ConfigAuditSourceRecordExtensions
{
    public static ConfigAuditSourceRecord WithRole(this ConfigAuditSourceRecord source, ConfigAuditSourceRole role) =>
        new()
        {
            Kind = source.Kind,
            ProviderName = source.ProviderName,
            ProviderPriority = source.ProviderPriority,
            FilePath = source.FilePath,
            EnvironmentVariableName = source.EnvironmentVariableName,
            ConfigPath = source.ConfigPath,
            AppliedToPath = source.AppliedToPath,
            Role = role,
            Sensitivity = source.Sensitivity
        };
}
