using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// A configuration provider that reads settings from JSON files (e.g., appsettings.json, config_*.json).
/// </summary>
public class FileBasedConfigProvider : IConfigProvider, IConfigCompositionValueProvider, IConfigDiagnosticProvider, IConfigAuditKeyEnumerator
{
    private readonly IConfigFileLocationProvider _configFileLocationProvider;
    private readonly ILogger<FileBasedConfigProvider> _logger;
    private readonly Func<string, bool> _directoryExists = Directory.Exists;
    private readonly Func<string, string, SearchOption, IEnumerable<string>> _enumerateFiles = Directory.EnumerateFiles;
    private readonly Func<string, byte[]> _readAllBytes = File.ReadAllBytes;

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
        : this(configFileLocationProvider, logger, Directory.Exists, Directory.EnumerateFiles, File.ReadAllBytes)
    {
    }

    /// <summary>Initializes the production snapshot pipeline with deterministic file I/O operations.</summary>
    /// <param name="configFileLocationProvider">The provider for configuration file locations.</param>
    /// <param name="logger">The logger for sanitized file-operation diagnostics.</param>
    /// <param name="directoryExists">Tests directory existence, with the same semantics as Directory.Exists.</param>
    /// <param name="enumerateFiles">Enumerates paths for the given directory, search pattern and search option.
    /// Enumeration may throw immediately or while advancing the returned sequence.</param>
    /// <param name="readAllBytes">Reads one enumerated path, or throws an I/O/access exception.</param>
    /// <remarks>This internal seam substitutes only filesystem access. Lazy snapshot creation, ordering,
    /// collision detection, parsing, merging, source locations and load events use the same code as the public
    /// constructor. Tests can model case-sensitive paths and access failures without relying on host filesystem
    /// behavior or supplying prebuilt events. No operations run until the snapshot is first requested.</remarks>
    internal FileBasedConfigProvider(
        IConfigFileLocationProvider configFileLocationProvider,
        ILogger<FileBasedConfigProvider> logger,
        Func<string, bool> directoryExists,
        Func<string, string, SearchOption, IEnumerable<string>> enumerateFiles,
        Func<string, byte[]> readAllBytes)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(enumerateFiles);
        ArgumentNullException.ThrowIfNull(readAllBytes);
        _configFileLocationProvider = configFileLocationProvider;
        _logger = logger;
        _directoryExists = directoryExists;
        _enumerateFiles = enumerateFiles;
        _readAllBytes = readAllBytes;

        _snapshotLazy = new Lazy<ConfigFileProviderSnapshot>(InitializeSnapshot, true);
    }

    /// <summary>
    /// Initializes a provider around an already-built snapshot for focused audit enumeration tests.
    /// </summary>
    /// <param name="snapshot">The snapshot returned by the provider during audit operations.</param>
    internal FileBasedConfigProvider(ConfigFileProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _configFileLocationProvider = null!;
        _logger = null!;
        _snapshotLazy = new Lazy<ConfigFileProviderSnapshot>(() => snapshot, true);
    }

    /// <summary>
    /// Gets the provider snapshot for the type-aware composition compiler.
    /// </summary>
    /// <remarks>
    /// Existing merged members remain authoritative for ordinary reads and audit enumeration.
    /// Composition code uses <see cref="ConfigFileProviderSnapshot.Layers"/> and
    /// <see cref="ConfigFileProviderSnapshot.LoadEvents"/> to inspect file declarations.
    /// </remarks>
    internal ConfigFileProviderSnapshot Snapshot => _snapshotLazy.Value;

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        if (_snapshotLazy.Value.Environments.TryGetValue(environment, out var envConfig))
        {
            return GetValue<T>(envConfig, key);
        }

        return default;
    }

    /// <summary>
    /// Resolves a legacy file root as raw JSON for type-aware composition.
    /// </summary>
    /// <remarks>
    /// The returned payload is the existing merged file view. It is deliberately marked non-sensitive because
    /// file configuration is not a secret-capable source. A present JSON <see langword="null"/> is serialized as
    /// the raw JSON token <c>null</c> and remains a resolved contribution; only an absent path is missing.
    /// </remarks>
    /// <param name="environment">The environment whose merged file view should be queried.</param>
    /// <param name="logicalKey">The root or logical path within the merged view. Dot and colon separators
    /// and ordinal case-insensitive segments are supported, including flattened JSON member paths.</param>
    /// <returns>A value-safe raw resolution with the provider's existing priority and identity.</returns>
    ConfigCompositionValueResolution IConfigCompositionValueProvider.ResolveRaw(string environment, string logicalKey)
    {
        var snapshot = _snapshotLazy.Value;
        if (!snapshot.Environments.TryGetValue(environment, out var environmentConfig))
        {
            return ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: false);
        }

        if (!TryGetNodeIncludingNull(environmentConfig, logicalKey, out var node))
        {
            return ConfigCompositionValueResolution.Missing(Name, Priority, isSensitive: false);
        }

        return ConfigCompositionValueResolution.Resolved(
            node?.ToJsonString() ?? "null",
            Name,
            Priority,
            isSensitive: false);
    }

    ConfigValueResolution IConfigDiagnosticProvider.Resolve(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role)
    {
        var snapshot = _snapshotLazy.Value;
        var diagnostics = snapshot.Diagnostics
            .Where(diagnostic => IsDiagnosticInEnvironment(diagnostic, environment)
                                 && IsDiagnosticForKey(diagnostic.Diagnostic, key))
            .Select(diagnostic => ToPublicDiagnostic(diagnostic.Diagnostic))
            .ToList();
        if (!snapshot.Environments.TryGetValue(environment, out var envConfig)
            || !TryGetNode(envConfig, key, out var node))
        {
            return ConfigValueResolution.Missing(key) with { Diagnostics = diagnostics };
        }

        var source = snapshot.Origins.TryGetValue(environment, out var origins)
                     && origins.TryGetValue(key, out var origin)
            ? AttachSourceLocation(snapshot, origin)
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
                .Select(origin => AttachSourceLocation(snapshot, origin.Value).WithRole(role))
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
            .Where(diagnostic => IsDiagnosticInEnvironment(diagnostic, environment)
                                 && diagnostic.Diagnostic.Key == null
                                 && (diagnostic.Diagnostic.ConfigPath == null
                                     || diagnostic.Diagnostic.Code == "config-file-null-skipped"))
            .Select(diagnostic => ToPublicDiagnostic(diagnostic.Diagnostic))
            .ToList();

    IReadOnlyList<ConfigAuditProviderDiscoveredKey> IConfigAuditKeyEnumerator.EnumerateKeys(string environment)
    {
        var snapshot = _snapshotLazy.Value;
        if (!snapshot.Environments.TryGetValue(environment, out var envConfig)
            || envConfig is not JsonObject envObject)
        {
            return [];
        }

        var origins = snapshot.Origins.TryGetValue(environment, out var environmentOrigins)
            ? environmentOrigins
            : new Dictionary<string, ConfigAuditSourceRecord>(StringComparer.OrdinalIgnoreCase);
        var discoveredKeys = new List<ConfigAuditProviderDiscoveredKey>();
        EnumerateObject(envObject, snapshot, origins, parentPath: null, discoveredKeys);

        return discoveredKeys
            .OrderBy(key => key.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ConfigFileProviderSnapshot InitializeSnapshot()
    {
        var environments = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ConfigFileProviderDiagnostic>();
        var sourceLocationMaps = new Dictionary<string, Lazy<ConfigFileSourceLocationMap>>(StringComparer.OrdinalIgnoreCase);
        var layers = new List<ConfigFileLayer>();
        var loadEvents = new List<ConfigFileLoadEvent>();

        var directory = _configFileLocationProvider.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !_directoryExists(directory))
        {
            return new ConfigFileProviderSnapshot(
                environments,
                origins,
                diagnostics,
                sourceLocationMaps,
                layers.ToImmutableArray(),
                loadEvents.ToImmutableArray());
        }

        // Collect matching files
        string[] files;
        try
        {
            files =
            [
                .._enumerateFiles(directory, "appsettings*.json", SearchOption.TopDirectoryOnly),
                .._enumerateFiles(directory, "config_*.json", SearchOption.TopDirectoryOnly)
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var displayDirectory = SanitizeDisplayPath(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
            if (string.IsNullOrEmpty(displayDirectory))
            {
                displayDirectory = "configuration-directory";
            }

            const string code = "config-file-directory-unreadable";
            _logger.LogWarning("Skipping unreadable configuration directory {DirectoryName}", displayDirectory);
            loadEvents.Add(new ConfigFileLoadFailure(
                code,
                "*",
                displayDirectory,
                0,
                ConfigFileLoadFailureClassification.Read));
            diagnostics.Add(new ConfigFileProviderDiagnostic(
                Environments.Production,
                new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = code,
                    Message = $"Skipping unreadable configuration directory {displayDirectory}."
                }));

            return new ConfigFileProviderSnapshot(
                environments,
                origins,
                diagnostics,
                sourceLocationMaps,
                layers.ToImmutableArray(),
                loadEvents.ToImmutableArray());
        }

        // Deterministic order so merges are predictable; later files override earlier ones when keys collide
        var seenFullPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var file in files
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(f => f, StringComparer.Ordinal))
        {
            var eventOrder = order++;
            var fileName = Path.GetFileNameWithoutExtension(file);
            var environment = ExtractEnvironment(fileName);
            var displayFileName = SanitizeDisplayPath(Path.GetFileName(file));
            var fullPath = Path.GetFullPath(file);
            if (seenFullPaths.ContainsKey(fullPath))
            {
                loadEvents.Add(new ConfigFileLoadFailure(
                    "config-file-path-collision",
                    environment,
                    displayFileName,
                    eventOrder,
                    ConfigFileLoadFailureClassification.PathCollision));
                diagnostics.Add(new ConfigFileProviderDiagnostic(environment, new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = "config-file-path-collision",
                    Message = $"Skipping config file {displayFileName} because it collides with another file path."
                }));
                continue;
            }

            seenFullPaths[fullPath] = file;
            JsonNode? root;
            try
            {
                var bytes = _readAllBytes(file);
                var text = ReadFileText(bytes);
                if (string.IsNullOrWhiteSpace(text))
                {
                    loadEvents.Add(new ConfigFileLoadFailure("config-file-empty", environment, displayFileName, eventOrder, ConfigFileLoadFailureClassification.Parse));
                    continue;
                }

                if (FindDuplicateMember(bytes) != null)
                {
                    loadEvents.Add(new ConfigFileLoadFailure("config-file-duplicate-member", environment, displayFileName, eventOrder, ConfigFileLoadFailureClassification.Parse));
                    diagnostics.Add(new ConfigFileProviderDiagnostic(environment, new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Warning,
                        Code = "config-file-duplicate-member",
                        Message = $"Skipping config file {displayFileName} because it contains duplicate JSON members."
                    }));
                    continue;
                }

                root = JsonNode.Parse(text);
                var sourceLocationMap = new Lazy<ConfigFileSourceLocationMap>(
                    () => ConfigFileSourceLocationMap.Create(bytes),
                    isThreadSafe: true);
                sourceLocationMaps[file] = sourceLocationMap;

                if (root is not JsonObject obj)
                {
                    loadEvents.Add(new ConfigFileLoadFailure("config-file-non-object-root", environment, displayFileName, eventOrder, ConfigFileLoadFailureClassification.Parse));
                    diagnostics.Add(new ConfigFileProviderDiagnostic(
                        environment,
                        new ConfigAuditDiagnostic
                        {
                            Severity = ConfigAuditDiagnosticSeverity.Warning,
                            Code = "config-file-non-object-root",
                            Message = $"Skipping config file {displayFileName} because the root is not a JSON object."
                        }));
                    continue; // Only merge JSON objects at the root
                }

                var layer = new ConfigFileLayer(environment, fullPath, eventOrder, (JsonObject)obj.DeepClone(), sourceLocationMap);
                layers.Add(layer);
                loadEvents.Add(layer);

                if (!environments.TryGetValue(environment, out var existing))
                {
                    existing = new JsonObject();
                    environments[environment] = existing;
                    origins[environment] = new Dictionary<string, ConfigAuditSourceRecord>(StringComparer.OrdinalIgnoreCase);
                }

                if (existing is JsonObject targetObj)
                {
                    MergeJsonObjects(targetObj, obj, origins[environment], file, environment, parentPath: null, diagnostics);
                }
            }
            catch (Exception ex)
            {
                var classification = ex is UnauthorizedAccessException or IOException
                    ? ConfigFileLoadFailureClassification.Read
                    : ConfigFileLoadFailureClassification.Parse;
                var code = classification == ConfigFileLoadFailureClassification.Read ? "config-file-unreadable" : "config-file-malformed";
                _logger.LogWarning(
                    "Skipping {Classification} config file {FileName}",
                    classification,
                    displayFileName);
                loadEvents.Add(new ConfigFileLoadFailure(code, environment, displayFileName, eventOrder, classification));
                diagnostics.Add(new ConfigFileProviderDiagnostic(
                    environment,
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Warning,
                        Code = code,
                        Message = classification == ConfigFileLoadFailureClassification.Read
                            ? $"Skipping unreadable config file {displayFileName}."
                            : $"Skipping malformed config file {displayFileName}."
                    }));

                continue;
            }
        }

        return new ConfigFileProviderSnapshot(
            environments,
            origins,
            diagnostics,
            sourceLocationMaps,
            layers.ToImmutableArray(),
            loadEvents.ToImmutableArray());
    }

    private static string? FindDuplicateMember(ReadOnlySpan<byte> bytes)
    {
        var jsonBytes = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes[3..] : bytes;
        var reader = new Utf8JsonReader(jsonBytes, isFinalBlock: true, state: default);
        return reader.Read() ? ScanValue(ref reader, null) : null;
    }

    private static string? ScanValue(ref Utf8JsonReader reader, string? parentPath)
    {
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected a JSON property name.");
                }

                var name = reader.GetString()!;
                if (!names.Add(name))
                {
                    return string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}.{name}";
                }

                if (!reader.Read())
                {
                    throw new JsonException("Incomplete JSON value.");
                }

                var duplicate = ScanValue(ref reader, string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}.{name}");
                if (duplicate != null)
                {
                    return duplicate;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new JsonException("Incomplete JSON object.");
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var duplicate = ScanValue(ref reader, parentPath);
                if (duplicate != null)
                {
                    return duplicate;
                }
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("Incomplete JSON array.");
            }
        }

        return null;
    }

    private static string ReadFileText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string SanitizeDisplayPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return string.Concat(path.Where(character => !char.IsControl(character)));
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

    /// <summary>Finds the first canonical raw-root match, preserving a present JSON null as a contribution.</summary>
    /// <remarks>Uses the compiler's dot/colon segment model for both requested and serialized file paths.
    /// Empty keys retain the whole-document behavior. Canonical collisions are rejected by the compiler before
    /// composition; this lookup does not merge competing matches or change legacy typed traversal.</remarks>
    private static bool TryGetNodeIncludingNull(JsonNode node, string key, out JsonNode? currentNode)
    {
        currentNode = node;
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        ConfigLogicalPath requestedPath;
        try
        {
            requestedPath = ConfigLogicalPath.Parse(key);
        }
        catch (ArgumentException)
        {
            currentNode = null;
            return false;
        }

        return TryGetLogicalNode(node, requestedPath, parentPath: null, out currentNode);
    }

    /// <summary>Traverses matching object ancestors in insertion order, interpreting flattened member names as paths.</summary>
    /// <remarks>Arrays and scalar nodes cannot be traversed. Invalid member paths are skipped here; the compiler
    /// reports their diagnostics before execution. A complete match returns its original subtree without rewriting names.</remarks>
    private static bool TryGetLogicalNode(
        JsonNode? node, ConfigLogicalPath requestedPath, ConfigLogicalPath? parentPath, out JsonNode? currentNode)
    {
        if (node is JsonObject obj)
        {
            foreach (var member in obj)
            {
                ConfigLogicalPath memberPath;
                try
                {
                    memberPath = ConfigLogicalPath.Parse(parentPath is null
                        ? member.Key
                        : $"{parentPath.Canonical}:{member.Key}");
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (memberPath.Equals(requestedPath))
                {
                    currentNode = member.Value;
                    return true;
                }

                if (memberPath.IsAncestorOrEqual(requestedPath)
                    && TryGetLogicalNode(member.Value, requestedPath, memberPath, out currentNode))
                {
                    return true;
                }
            }
        }

        currentNode = null;
        return false;
    }

    private void EnumerateObject(
        JsonObject obj,
        ConfigFileProviderSnapshot snapshot,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        string? parentPath,
        List<ConfigAuditProviderDiscoveredKey> discoveredKeys)
    {
        foreach (var kvp in obj.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kvp.Value == null)
            {
                continue;
            }

            var path = string.IsNullOrWhiteSpace(parentPath) ? kvp.Key : $"{parentPath}.{kvp.Key}";
            switch (kvp.Value)
            {
                case JsonObject childObject:
                    discoveredKeys.Add(CreateDiscoveredKey(path, null, ConfigAuditDiscoveredValueKind.Object, snapshot, origins));
                    EnumerateObject(childObject, snapshot, origins, path, discoveredKeys);
                    break;
                case JsonArray:
                    discoveredKeys.Add(CreateDiscoveredKey(path, null, ConfigAuditDiscoveredValueKind.Array, snapshot, origins));
                    break;
                case JsonValue scalar:
                    discoveredKeys.Add(CreateDiscoveredKey(
                        path,
                        ConvertJsonScalar(scalar),
                        ConfigAuditDiscoveredValueKind.Scalar,
                        snapshot,
                        origins));
                    break;
            }
        }
    }

    private ConfigAuditProviderDiscoveredKey CreateDiscoveredKey(
        string path,
        object? rawValue,
        ConfigAuditDiscoveredValueKind valueKind,
        ConfigFileProviderSnapshot snapshot,
        Dictionary<string, ConfigAuditSourceRecord> origins)
    {
        var diagnostics = new List<ConfigAuditDiagnostic>();
        var source = origins.TryGetValue(path, out var origin)
            ? AttachSourceLocation(snapshot, origin)
            : new ConfigAuditSourceRecord
            {
                Kind = ConfigAuditSourceKind.File,
                ProviderName = Name,
                ProviderPriority = Priority,
                ConfigPath = path,
                AppliedToPath = path,
                Role = ConfigAuditSourceRole.Base
            };

        if (!origins.ContainsKey(path))
        {
            diagnostics.Add(new ConfigAuditDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = "config-provider-discovered-key-origin-missing",
                Key = path,
                ConfigPath = path,
                Source = source,
                Message = $"Discovered file configuration key '{path}' did not have source origin metadata."
            });
        }

        return new ConfigAuditProviderDiscoveredKey(path, rawValue, valueKind, [source], diagnostics);
    }

    private static object? ConvertJsonScalar(JsonValue scalar)
    {
        if (scalar.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        if (scalar.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        if (scalar.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (scalar.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (scalar.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue;
        }

        return scalar.TryGetValue<double>(out var doubleValue) ? doubleValue : null;
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

    private static bool IsDiagnosticInEnvironment(ConfigFileProviderDiagnostic diagnostic, string environment) =>
        diagnostic.Environment == null
        || string.Equals(diagnostic.Environment, environment, StringComparison.OrdinalIgnoreCase);

    private void MergeJsonObjects(
        JsonObject target,
        JsonObject source,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        string file,
        string environment,
        string? parentPath,
        List<ConfigFileProviderDiagnostic> diagnostics)
    {
        foreach (var kvp in source)
        {
            var path = string.IsNullOrWhiteSpace(parentPath) ? kvp.Key : $"{parentPath}.{kvp.Key}";
            if (kvp.Value == null)
            {
                // Skip null values in source
                diagnostics.Add(new ConfigFileProviderDiagnostic(
                    environment,
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Info,
                        Code = "config-file-null-skipped",
                        ConfigPath = path,
                        Message = $"Skipping null file value at '{path}'."
                    }));
                continue;
            }

            origins[path] = CreateFileSource(file, path);
            if (target.ContainsKey(kvp.Key))
            {
                if (target[kvp.Key] is JsonObject targetObj && kvp.Value is JsonObject sourceObj)
                {
                    MergeJsonObjects(targetObj, sourceObj, origins, file, environment, path, diagnostics);
                }
                else
                {
                    // Arrays and scalar values use replace semantics: later files
                    // override earlier values for the same key.
                    RemoveDescendantOrigins(origins, path);
                    target[kvp.Key] = kvp.Value.DeepClone();
                    if (kvp.Value is JsonObject or JsonArray)
                    {
                        RecordOrigins(kvp.Value, origins, file, environment, path, diagnostics);
                    }
                }
            }
            else
            {
                target[kvp.Key] = kvp.Value.DeepClone();
                if (kvp.Value is JsonObject or JsonArray)
                {
                    RecordOrigins(kvp.Value, origins, file, environment, path, diagnostics);
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
        string environment,
        string parentPath,
        List<ConfigFileProviderDiagnostic> diagnostics)
    {
        if (source is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                var path = $"{parentPath}.{kvp.Key}";
                if (kvp.Value == null)
                {
                    diagnostics.Add(new ConfigFileProviderDiagnostic(
                        environment,
                        new ConfigAuditDiagnostic
                        {
                            Severity = ConfigAuditDiagnosticSeverity.Info,
                            Code = "config-file-null-skipped",
                            ConfigPath = path,
                            Message = $"Skipping null file value at '{path}'."
                        }));
                    continue;
                }

                origins[path] = CreateFileSource(file, path);
                if (kvp.Value is JsonObject or JsonArray)
                {
                    RecordOrigins(kvp.Value, origins, file, environment, path, diagnostics);
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
                    origins[path] = CreateFileSource(file, path);
                    continue;
                }

                origins[path] = CreateFileSource(file, path);
                if (item is JsonObject or JsonArray)
                {
                    RecordOrigins(item, origins, file, environment, path, diagnostics);
                }
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

    private static ConfigAuditSourceRecord AttachSourceLocation(ConfigFileProviderSnapshot snapshot, ConfigAuditSourceRecord source)
    {
        if (source.Location != null
            || source.Kind != ConfigAuditSourceKind.File
            || string.IsNullOrEmpty(source.FilePath)
            || string.IsNullOrEmpty(source.ConfigPath)
            || !snapshot.SourceLocationMaps.TryGetValue(source.FilePath, out var locationMap))
        {
            return source;
        }

        var location = locationMap.Value.GetLocation(source.ConfigPath);
        return location == null
            ? source
            : new ConfigAuditSourceRecord
            {
                Kind = source.Kind,
                ProviderName = source.ProviderName,
                ProviderPriority = source.ProviderPriority,
                FilePath = source.FilePath,
                EnvironmentVariableName = source.EnvironmentVariableName,
                ConfigPath = source.ConfigPath,
                AppliedToPath = source.AppliedToPath,
                Location = location,
                Role = source.Role,
                Sensitivity = source.Sensitivity
            };
    }

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

/// <summary>
/// The immutable ordered JSON layer captured from one successfully parsed configuration file.
/// </summary>
internal sealed record ConfigFileLayer : ConfigFileLoadEvent
{
    private readonly JsonObject _document;

    internal ConfigFileLayer(
        string environment,
        string filePath,
        int order,
        JsonObject document,
        Lazy<ConfigFileSourceLocationMap> sourceLocationMap)
        : base(order, environment, filePath)
    {
        _document = (JsonObject)document.DeepClone();
        SourceLocationMap = sourceLocationMap;
    }

    public Lazy<ConfigFileSourceLocationMap> SourceLocationMap { get; }

    /// <summary>
    /// Gets an isolated copy of the parsed JSON document for compiler traversal.
    /// </summary>
    /// <remarks>
    /// The layer owns its captured document. Returning a copy keeps compiler and test mutations from changing
    /// the snapshot, while retaining the existing <c>layer.Document</c> access used by the compiler.
    /// </remarks>
    public JsonObject Document => (JsonObject)_document.DeepClone();

    /// <inheritdoc />
    public override string ToString() => nameof(ConfigFileLayer);
}

/// <summary>
/// Classifies a file load failure without retaining exception text or configuration values.
/// </summary>
internal enum ConfigFileLoadFailureClassification
{
    Parse,
    Read,
    PathCollision
}

/// <summary>
/// An immutable, value-free record of a file that could not contribute a JSON layer.
/// </summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Environment">The environment inferred from the file name.</param>
/// <param name="DisplayPath">The file name safe for diagnostics.</param>
/// <param name="Order">The deterministic discovery order of the file.</param>
/// <param name="Classification">The sanitized read or parse classification.</param>
internal sealed record ConfigFileLoadFailure(
    string Code,
    string Environment,
    string DisplayPath,
    int Order,
    ConfigFileLoadFailureClassification Classification) : ConfigFileLoadEvent(Order, Environment, DisplayPath);

/// <summary>
/// Base type for the ordered file load history consumed by the type-aware compiler.
/// </summary>
internal abstract record ConfigFileLoadEvent(int Order, string Environment, string FilePath);

internal sealed record ConfigFileProviderSnapshot(
    Dictionary<string, JsonNode> Environments,
    Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>> Origins,
    IReadOnlyList<ConfigFileProviderDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, Lazy<ConfigFileSourceLocationMap>> SourceLocationMaps,
    ImmutableArray<ConfigFileLayer> Layers,
    ImmutableArray<ConfigFileLoadEvent> LoadEvents)
{
    public ConfigFileProviderSnapshot(
        Dictionary<string, JsonNode> environments,
        Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>> origins,
        IReadOnlyList<ConfigFileProviderDiagnostic> diagnostics)
        : this(
            environments,
            origins,
            diagnostics,
            new Dictionary<string, Lazy<ConfigFileSourceLocationMap>>(StringComparer.OrdinalIgnoreCase),
            [],
            [])
    {
    }

    public ConfigFileProviderSnapshot(
        Dictionary<string, JsonNode> environments,
        Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>> origins,
        IReadOnlyList<ConfigFileProviderDiagnostic> diagnostics,
        IReadOnlyDictionary<string, Lazy<ConfigFileSourceLocationMap>> sourceLocationMaps)
        : this(environments, origins, diagnostics, sourceLocationMaps, [], [])
    {
    }
}

internal sealed record ConfigFileProviderDiagnostic(string? Environment, ConfigAuditDiagnostic Diagnostic);

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
