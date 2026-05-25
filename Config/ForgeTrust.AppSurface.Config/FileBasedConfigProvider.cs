using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config;

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
            .Select(ToPublicDiagnostic)
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
        var sources = origins == null
            ? [source]
            : origins
                .Where(origin => string.Equals(origin.Key, key, StringComparison.OrdinalIgnoreCase)
                                 || origin.Key.StartsWith($"{key}.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(origin => origin.Key, StringComparer.OrdinalIgnoreCase)
                .Select(origin => origin.Value.WithRole(role))
                .DefaultIfEmpty(source)
                .ToList();
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
                return ConfigValueResolution.Missing(key) with { Diagnostics = diagnostics.Select(ToPublicDiagnostic).ToList() };
            }

            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                value,
                [source],
                diagnostics)
            {
                AuditSources = sources
            };
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
                diagnostics.Select(ToPublicDiagnostic).ToList());
        }
    }

    IReadOnlyList<ConfigAuditDiagnostic> IConfigDiagnosticProvider.GetReportDiagnostics(string environment) =>
        _snapshotLazy.Value.Diagnostics
            .Where(diagnostic => diagnostic.Key == null && diagnostic.ConfigPath == null)
            .Select(ToPublicDiagnostic)
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
                var bytes = File.ReadAllBytes(file);
                var text = ReadFileText(bytes);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                root = JsonNode.Parse(text);
                var sourceLocations = ConfigFileSourceLocationMap.Create(bytes);

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
                    MergeJsonObjects(targetObj, obj, origins[environment], file, sourceLocations, parentPath: null, diagnostics);
                }
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
        }

        return new ConfigFileProviderSnapshot(environments, origins, diagnostics);
    }

    private static string ReadFileText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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
        ConfigFileSourceLocationMap sourceLocations,
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

            origins[path] = CreateFileSource(file, path, sourceLocations);
            if (target.ContainsKey(kvp.Key))
            {
                if (target[kvp.Key] is JsonObject targetObj && kvp.Value is JsonObject sourceObj)
                {
                    MergeJsonObjects(targetObj, sourceObj, origins, file, sourceLocations, path, diagnostics);
                }
                else
                {
                    // Arrays and scalar values use replace semantics: later files
                    // override earlier values for the same key.
                    RemoveDescendantOrigins(origins, path);
                    target[kvp.Key] = kvp.Value.DeepClone();
                    if (kvp.Value is JsonObject or JsonArray)
                    {
                        RecordOrigins(kvp.Value, origins, file, sourceLocations, path, diagnostics);
                    }
                }
            }
            else
            {
                target[kvp.Key] = kvp.Value.DeepClone();
                if (kvp.Value is JsonObject or JsonArray)
                {
                    RecordOrigins(kvp.Value, origins, file, sourceLocations, path, diagnostics);
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
        JsonNode source,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        string file,
        ConfigFileSourceLocationMap sourceLocations,
        string parentPath,
        List<ConfigAuditDiagnostic> diagnostics)
    {
        if (source is JsonObject obj)
        {
            foreach (var kvp in obj)
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

                origins[path] = CreateFileSource(file, path, sourceLocations);
                if (kvp.Value is JsonObject or JsonArray)
                {
                    RecordOrigins(kvp.Value, origins, file, sourceLocations, path, diagnostics);
                }
            }
        }
        else if (source is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var path = $"{parentPath}.{i}";
                var item = array[i];
                if (item == null)
                {
                    origins[path] = CreateFileSource(file, path, sourceLocations);
                    continue;
                }

                origins[path] = CreateFileSource(file, path, sourceLocations);
                if (item is JsonObject or JsonArray)
                {
                    RecordOrigins(item, origins, file, sourceLocations, path, diagnostics);
                }
            }
        }
    }

    private ConfigAuditSourceRecord CreateFileSource(
        string file,
        string path,
        ConfigFileSourceLocationMap sourceLocations) =>
        new()
        {
            Kind = ConfigAuditSourceKind.File,
            ProviderName = Name,
            ProviderPriority = Priority,
            FilePath = file,
            ConfigPath = path,
            AppliedToPath = path,
            Location = sourceLocations.GetLocation(path),
            Role = ConfigAuditSourceRole.Base
        };

    private static ConfigAuditDiagnostic ToPublicDiagnostic(ConfigAuditDiagnostic diagnostic)
    {
        var key = RedactSensitivePath(diagnostic.Key);
        var configPath = RedactSensitivePath(diagnostic.ConfigPath);
        var source = diagnostic.Source == null ? null : ToPublicSource(diagnostic.Source);
        var message = RedactDiagnosticMessage(diagnostic.Message, diagnostic, key, configPath, source);

        return new ConfigAuditDiagnostic
        {
            Severity = diagnostic.Severity,
            Code = diagnostic.Code,
            Key = key,
            ConfigPath = configPath,
            Source = source,
            Message = message
        };
    }

    private static ConfigAuditSourceRecord ToPublicSource(ConfigAuditSourceRecord source) =>
        new()
        {
            Kind = source.Kind,
            ProviderName = source.ProviderName,
            ProviderPriority = source.ProviderPriority,
            FilePath = source.FilePath,
            EnvironmentVariableName = source.EnvironmentVariableName,
            ConfigPath = RedactSensitivePath(source.ConfigPath),
            AppliedToPath = RedactSensitivePath(source.AppliedToPath),
            Location = source.Location,
            Role = source.Role,
            Sensitivity = source.Sensitivity
        };

    private static string? RedactSensitivePath(string? path)
    {
        if (path == null)
        {
            return null;
        }

        var segments = path.Split('.');
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (ConfigAuditRedactor.ContainsSensitiveFragment(segments[i]))
            {
                segments[i] = "[redacted-key]";
                changed = true;
            }
        }

        return changed ? string.Join('.', segments) : path;
    }

    private static string RedactDiagnosticMessage(
        string message,
        ConfigAuditDiagnostic diagnostic,
        string? key,
        string? configPath,
        ConfigAuditSourceRecord? source)
    {
        return ReplaceIfChanged(
            ReplaceIfChanged(
                ReplaceIfChanged(
                    ReplaceIfChanged(message, diagnostic.ConfigPath, configPath),
                    diagnostic.Key,
                    key),
                diagnostic.Source?.ConfigPath,
                source?.ConfigPath),
            diagnostic.Source?.AppliedToPath,
            source?.AppliedToPath);
    }

    private static string ReplaceIfChanged(string value, string? raw, string? safe) =>
        string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(safe) || string.Equals(raw, safe, StringComparison.Ordinal)
            ? value
            : value.Replace(raw, safe, StringComparison.Ordinal);
}

/// <summary>
/// Maps JSON object member paths to conservative file source locations for audit provenance.
/// </summary>
/// <remarks>
/// The map is advisory and intentionally narrower than JSON parsing: it records object property-name token locations,
/// suppresses ambiguous case-insensitive paths, and omits array descendants so callers never receive a coordinate that
/// is more specific than the file provider's merge/origin model.
/// </remarks>
internal sealed class ConfigFileSourceLocationMap
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly Dictionary<string, ConfigAuditSourceLocation?> _locations;

    private ConfigFileSourceLocationMap(Dictionary<string, ConfigAuditSourceLocation?> locations)
    {
        _locations = locations;
    }

    /// <summary>
    /// Gets an empty map used when source coordinates are unavailable.
    /// </summary>
    public static ConfigFileSourceLocationMap Empty { get; } = new(
        new Dictionary<string, ConfigAuditSourceLocation?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Creates a source-location map from the raw file bytes used to initialize the file provider snapshot.
    /// </summary>
    /// <param name="fileBytes">The raw JSON file bytes.</param>
    /// <returns>A map of supported config paths to source locations, or an empty map when the bytes cannot be mapped.</returns>
    public static ConfigFileSourceLocationMap Create(ReadOnlySpan<byte> fileBytes)
    {
        var jsonBytes = StripUtf8Bom(fileBytes);
        var locations = new Dictionary<string, ConfigAuditSourceLocation?>(StringComparer.OrdinalIgnoreCase);
        if (jsonBytes.IsEmpty)
        {
            return Empty;
        }

        try
        {
            var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return Empty;
            }

            var lineStarts = BuildLineStarts(jsonBytes);
            var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ambiguousPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ReadObject(
                ref reader,
                parentPath: null,
                suppressLocations: false,
                lineStarts,
                locations,
                canonicalPaths,
                ambiguousPaths);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Empty;
        }

        return new ConfigFileSourceLocationMap(locations);
    }

    /// <summary>
    /// Gets the location for <paramref name="path"/> when the path was mapped without ambiguity.
    /// </summary>
    /// <param name="path">The dotted config path used by the file provider origin record.</param>
    /// <returns>The source location, or <see langword="null"/> when no truthful coordinate is available.</returns>
    public ConfigAuditSourceLocation? GetLocation(string path) =>
        _locations.TryGetValue(path, out var location) ? location : null;

    private static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> fileBytes) =>
        fileBytes.StartsWith(Utf8Bom) ? fileBytes[Utf8Bom.Length..] : fileBytes;

    private static void ReadObject(
        ref Utf8JsonReader reader,
        string? parentPath,
        bool suppressLocations,
        int[] lineStarts,
        Dictionary<string, ConfigAuditSourceLocation?> locations,
        Dictionary<string, string> canonicalPaths,
        HashSet<string> ambiguousPaths)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            var propertyName = reader.GetString()!;
            var path = string.IsNullOrEmpty(parentPath) ? propertyName : $"{parentPath}.{propertyName}";
            var unsupportedPath = suppressLocations || propertyName.Contains('.', StringComparison.Ordinal);
            if (unsupportedPath)
            {
                RecordAmbiguousPath(path, locations, ambiguousPaths);
            }
            else
            {
                RecordLocation(
                    path,
                    CreateLocation(reader.TokenStartIndex, lineStarts),
                    locations,
                    canonicalPaths,
                    ambiguousPaths);
            }

            reader.Read();

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ReadObject(ref reader, path, unsupportedPath, lineStarts, locations, canonicalPaths, ambiguousPaths);
            }
            else
            {
                SkipValue(ref reader);
            }
        }
    }

    private static void RecordLocation(
        string path,
        ConfigAuditSourceLocation location,
        Dictionary<string, ConfigAuditSourceLocation?> locations,
        Dictionary<string, string> canonicalPaths,
        HashSet<string> ambiguousPaths)
    {
        if (ambiguousPaths.Contains(path))
        {
            locations[path] = null;
            return;
        }

        if (canonicalPaths.TryGetValue(path, out var existingPath))
        {
            if (!string.Equals(existingPath, path, StringComparison.Ordinal))
            {
                RecordAmbiguousPath(path, locations, ambiguousPaths);
                return;
            }
        }
        else
        {
            canonicalPaths[path] = path;
        }

        locations[path] = location;
    }

    private static void RecordAmbiguousPath(
        string path,
        Dictionary<string, ConfigAuditSourceLocation?> locations,
        HashSet<string> ambiguousPaths)
    {
        ambiguousPaths.Add(path);
        locations[path] = null;
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not JsonTokenType.StartObject and not JsonTokenType.StartArray)
        {
            return;
        }

        var depth = 0;
        do
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
            }
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                depth--;
            }
        }
        while (depth > 0 && reader.Read());
    }

    private static int[] BuildLineStarts(ReadOnlySpan<byte> jsonBytes)
    {
        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < jsonBytes.Length; index++)
        {
            if (jsonBytes[index] == (byte)'\n'
                || (jsonBytes[index] == (byte)'\r'
                    && (index + 1 >= jsonBytes.Length || jsonBytes[index + 1] != (byte)'\n')))
            {
                lineStarts.Add(index + 1);
            }
        }

        return [.. lineStarts];
    }

    private static ConfigAuditSourceLocation CreateLocation(long tokenStartIndex, int[] lineStarts)
    {
        var byteOffset = (int)tokenStartIndex;
        var lineIndex = Array.BinarySearch(lineStarts, byteOffset);
        if (lineIndex < 0)
        {
            lineIndex = ~lineIndex - 1;
        }

        var lineNumber = lineIndex + 1;
        var byteColumnNumber = byteOffset - lineStarts[lineIndex] + 1;
        return new ConfigAuditSourceLocation(lineNumber, byteColumnNumber);
    }
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
            Location = source.Location,
            Role = role,
            Sensitivity = source.Sensitivity
        };
}
