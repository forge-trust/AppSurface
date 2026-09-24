using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// A configuration provider that reads settings from JSON files (e.g., appsettings.json, config_*.json).
/// </summary>
/// <remarks>
/// Array indices use the token projection's invariant, unpadded decimal spelling. Incoming layers are validated
/// before replacing values or source histories. Rejected branches retain lower-layer state; valid object siblings
/// can still merge, while collisions remain visible to resolution and audit discovery across later layers.
/// See the <see href="../../docs/designs/config-logical-key-contract.md">logical-key contract</see>
/// for file identity and layer semantics.
/// </remarks>
public class FileBasedConfigProvider : IConfigProvider, IConfigDiagnosticProvider, IConfigAuditKeyEnumerator
{
    private readonly IConfigFileLocationProvider _configFileLocationProvider;
    private readonly ILogger<FileBasedConfigProvider> _logger;
    private readonly ConfigResourceOptions _resourceOptions;

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
    /// <param name="resourceOptions">Optional validated resource limits for file loading and audit behavior.</param>
    public FileBasedConfigProvider(
        IConfigFileLocationProvider configFileLocationProvider,
        ILogger<FileBasedConfigProvider> logger,
        IOptions<ConfigResourceOptions>? resourceOptions = null)
    {
        _configFileLocationProvider = configFileLocationProvider;
        _logger = logger;
        _resourceOptions = (resourceOptions?.Value ?? new ConfigResourceOptions()).Snapshot();

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
        _resourceOptions = new ConfigResourceOptions();
        _snapshotLazy = new Lazy<ConfigFileProviderSnapshot>(() => snapshot, true);
    }

    /// <inheritdoc />
    [Obsolete("Use ConfigProviderRequest and Resolve<T>.")]
    public T? GetValue<T>(string environment, string key)
    {
        var logicalKey = AppSurfaceConfigKey.Parse(key).WithInput(ConfigKeyInputOrigin.StrictString, key);
        var result = Resolve<T>(new ConfigProviderRequest(environment, logicalKey));
        if (result.Status == ConfigProviderValueStatus.Found) return result.Value;
        if (result.Status == ConfigProviderValueStatus.Missing) return default;
        throw new ConfigurationResolutionException(
            environment, logicalKey, Name, result.Diagnostic!);
    }

    /// <summary>
    /// Resolves a typed value using the request's environment and logical key.
    /// </summary>
    /// <param name="request">The typed provider request.</param>
    /// <returns>A found, missing, or terminal result. Null values are reported as missing.</returns>
    public ConfigProviderValueResult<T> Resolve<T>(ConfigProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.CancellationToken.ThrowIfCancellationRequested();
        var logicalKey = request.Key;
        var key = logicalKey.Value;
        var snapshot = _snapshotLazy.Value;
        if (GetResourceFailure(snapshot, request.Environment) is { } limitCode)
            return ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal(limitCode));
        if (IsProjectedInvalid(snapshot, request.Environment, logicalKey))
            return ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-unrepresentable", key));
        if (IsProjectedCollision(snapshot, request.Environment, logicalKey))
        {
            return ConfigProviderValueResult<T>.Terminal(ConfigDiagnosticCatalog.Terminal("config-key-collision", key));
        }
        if (!snapshot.Environments.TryGetValue(request.Environment, out var envConfig)
            || !TryGetNode(envConfig, logicalKey, out var node))
        {
            return ConfigProviderValueResult<T>.Missing();
        }

        try
        {
            var value = node.Deserialize<T>();
            return value is null
                ? ConfigProviderValueResult<T>.Missing()
                : ConfigProviderValueResult<T>.Found(value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return ConfigProviderValueResult<T>.Terminal(new ConfigProviderTerminalDiagnostic("config-file-conversion-failed",
                "The file value could not be converted.", "The selected JSON value does not match the requested type.",
                "Correct the value or the declared configuration type.", ConfigDiagnosticCatalog.Reference, false));
        }
    }

    ConfigValueResolution IConfigDiagnosticProvider.Resolve(
        ConfigProviderRequest request,
        Type valueType,
        ConfigAuditSourceRole role)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.CancellationToken.ThrowIfCancellationRequested();
        var environment = request.Environment;
        var logicalKey = request.Key;
        var key = request.Key.Value;
        var snapshot = _snapshotLazy.Value;
        if (GetResourceFailure(snapshot, environment) is { } limitCode)
            return new ConfigValueResolution(logicalKey, ConfigAuditEntryState.Invalid, null, [],
                [new ConfigAuditDiagnostic { Severity = ConfigAuditDiagnosticSeverity.Error, Code = limitCode,
                    Key = key, ConfigPath = key, Message = ConfigDiagnosticCatalog.Terminal(limitCode).ToDisplayString() }]);
        if (IsProjectedInvalid(snapshot, environment, logicalKey))
            return new ConfigValueResolution(logicalKey, ConfigAuditEntryState.Invalid, null, [], [new ConfigAuditDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Error, Code = "config-key-unrepresentable", Key = key,
                ConfigPath = key, Message = ConfigDiagnosticCatalog.Terminal("config-key-unrepresentable", key).ToDisplayString()
            }]);
        if (IsProjectedCollision(snapshot, environment, logicalKey))
        {
            return ConfigValueResolution.Missing(logicalKey) with
            {
                State = ConfigAuditEntryState.Invalid,
                Sources = [],
                Diagnostics = [new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Error,
                    Code = "config-key-collision",
                    Key = key,
                    ConfigPath = key,
                    Message = ConfigDiagnosticCatalog.Terminal("config-key-collision", key).ToDisplayString()
                }]
            };
        }
        var diagnostics = snapshot.Diagnostics
            .Where(diagnostic => IsDiagnosticInEnvironment(diagnostic, environment)
                                 && IsDiagnosticForKey(diagnostic.Diagnostic, logicalKey))
            .Select(diagnostic => ToPublicDiagnostic(diagnostic.Diagnostic))
            .ToList();
        if (!snapshot.Environments.TryGetValue(environment, out var envConfig)
            || !TryGetNode(envConfig, logicalKey, out var node))
        {
            return ConfigValueResolution.Missing(logicalKey) with { Diagnostics = diagnostics };
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
        var rootSources = GetSourceHistory(snapshot, environment, logicalKey, source)
            .Select(item => AttachSourceLocation(snapshot, item).WithRole(role)).ToList();
        var sources = origins == null
            ? [source]
            : origins
                .Where(origin => AppSurfaceConfigKey.Parse(origin.Key).IsSameOrDescendantOf(logicalKey))
                .OrderBy(origin => origin.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(origin => GetSourceHistory(snapshot, environment, AppSurfaceConfigKey.Parse(origin.Key), origin.Value))
                .Select(item => AttachSourceLocation(snapshot, item).WithRole(role))
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
                return ConfigValueResolution.Missing(logicalKey) with { Diagnostics = diagnostics.Select(ToPublicDiagnostic).ToList() };
            }

            return new ConfigValueResolution(
                logicalKey,
                ConfigAuditEntryState.Resolved,
                value,
                rootSources,
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
                logicalKey,
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
        if (GetResourceFailure(snapshot, environment) is not null
            || !snapshot.Environments.TryGetValue(environment, out var envConfig)
            || envConfig is not JsonObject envObject)
        {
            return [];
        }

        var origins = snapshot.Origins.TryGetValue(environment, out var environmentOrigins)
            ? environmentOrigins
            : new Dictionary<string, ConfigAuditSourceRecord>(StringComparer.OrdinalIgnoreCase);
        var discoveredKeys = new List<ConfigAuditProviderDiscoveredKey>();
        EnumerateObject(envObject, snapshot, origins, parentPath: null, discoveredKeys);
        if (snapshot.SourceProjections.TryGetValue(environment, out var sourceProjection))
        {
            var discoveredIdentities = discoveredKeys.Select(entry => entry.LogicalKey).ToHashSet();
            foreach (var entry in sourceProjection.Entries)
            {
                if (discoveredIdentities.Contains(entry.Key) || !IsProjectedCollision(snapshot, environment, entry.Key)) continue;
                var last = entry.Entries[^1];
                var kind = last.Metadata.Shape switch
                {
                    ConfigFileValueShape.Object => ConfigAuditDiscoveredValueKind.Object,
                    ConfigFileValueShape.Array => ConfigAuditDiscoveredValueKind.Array,
                    _ => ConfigAuditDiscoveredValueKind.Scalar
                };
                // A rejected value never enters the effective tree, but its raw identity remains auditable.
                discoveredKeys.Add(new(entry.Key, null, kind, [CreateFileSource(last.Layer, last.SourceSpelling)], []));
            }
        }

        return discoveredKeys
            .Select(discovered =>
            {
                var key = discovered.LogicalKey;
                var code = IsProjectedInvalid(snapshot, environment, key) ? "config-key-unrepresentable"
                    : IsProjectedCollision(snapshot, environment, key) ? "config-key-collision" : null;
                var history = code is not null && sourceProjection is not null && sourceProjection.TryGet(key, out var projected)
                    ? projected!.Entries.Reverse().Select(entry => CreateFileSource(entry.Layer, entry.SourceSpelling))
                    : GetSourceHistory(snapshot, environment, key, discovered.Sources[0]);
                var sources = history
                    .Select(source => AttachSourceLocation(snapshot, source)).ToList();
                return discovered with
                {
                    Sources = sources,
                    RawValue = code is null ? discovered.RawValue : null,
                    Diagnostics = code is null ? discovered.Diagnostics : discovered.Diagnostics.Append(new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Error,
                        Code = code,
                        Key = key.Value,
                        ConfigPath = key.Value,
                        Message = ConfigDiagnosticCatalog.Terminal(code, key.Value).ToDisplayString()
                    }).ToList()
                };
            })
            .OrderBy(key => key.LogicalKey.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.LogicalKey.Value, StringComparer.Ordinal)
            .ToList();
    }

    private ConfigFileProviderSnapshot InitializeSnapshot()
    {
        var environments = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ConfigFileProviderDiagnostic>();
        var sourceLocationMaps = new Dictionary<string, Lazy<ConfigFileSourceLocationMap>>(StringComparer.OrdinalIgnoreCase);
        var history = new Dictionary<string, Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>>>(StringComparer.OrdinalIgnoreCase);
        var projections = new Dictionary<string, ConfigFileTokenProjection>(StringComparer.OrdinalIgnoreCase);
        var fileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourceSpellings = new Dictionary<string, Dictionary<AppSurfaceConfigKey, string>>(StringComparer.OrdinalIgnoreCase);

        var directory = _configFileLocationProvider.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new ConfigFileProviderSnapshot(environments, origins, diagnostics, sourceLocationMaps);
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
            var fileName = Path.GetFileNameWithoutExtension(file);
            var environment = ExtractEnvironment(fileName);
            var displayFileName = Path.GetFileName(file);
            fileCounts.TryGetValue(environment, out var fileCount);
            if (fileCount >= _resourceOptions.MaxFilesPerEnvironment)
            {
                diagnostics.Add(new ConfigFileProviderDiagnostic(environment, new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Error,
                    Code = "config-file-count-limit",
                    Message = $"Skipping config file {ConfigDiagnosticText.Identifier(displayFileName)} because the configured file-count limit was reached."
                }));
                continue;
            }
            fileCounts[environment] = fileCount + 1;
            JsonNode? root = null;
            try
            {
                using var stream = File.OpenRead(file);
                if (stream.Length > _resourceOptions.MaxFileBytes)
                {
                    diagnostics.Add(new ConfigFileProviderDiagnostic(environment, new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Error,
                        Code = "config-file-byte-limit",
                        Message = $"Skipping config file {ConfigDiagnosticText.Identifier(displayFileName)} because it exceeds the configured file-size limit."
                    }));
                    continue;
                }
                var projection = ConfigFileTokenProjection.Parse(ReadBounded(stream, _resourceOptions.MaxFileBytes));
                root = projection.MaterializedRoot;
                projections[file] = projection;
                sourceLocationMaps[file] = new Lazy<ConfigFileSourceLocationMap>(() => projection.Locations, true);

                if (projection.HasInvalidRootProperties)
                {
                    diagnostics.Add(new ConfigFileProviderDiagnostic(environment, new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Error,
                        Code = "config-file-invalid-root-property",
                        Source = new ConfigAuditSourceRecord
                        {
                            Kind = ConfigAuditSourceKind.File,
                            ProviderName = Name,
                            ProviderPriority = Priority,
                            FilePath = displayFileName,
                            Role = ConfigAuditSourceRole.Base
                        },
                        Message = "One or more root properties in the configuration file were ignored because they could not be represented as logical keys."
                    }));
                }

                var obj = (JsonObject)root;

                if (!environments.TryGetValue(environment, out var existing))
                {
                    existing = new JsonObject();
                    environments[environment] = existing;
                    origins[environment] = new Dictionary<string, ConfigAuditSourceRecord>(StringComparer.OrdinalIgnoreCase);
                    history[environment] = [];
                    sourceSpellings[environment] = [];
                }

                var validation = ValidateLayer(projection, sourceSpellings[environment]);
                MergeJsonObjects((JsonObject)existing, obj, origins[environment], history[environment], file, environment, parentPath: null, diagnostics, validation);
            }
            catch (ConfigResourceLimitException)
            {
                diagnostics.Add(new ConfigFileProviderDiagnostic(environment, new ConfigAuditDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Error,
                    Code = "config-file-byte-limit",
                    Message = "The file exceeded its configured byte limit during capture."
                }));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "Skipping malformed config file {FileName}",
                    ConfigDiagnosticText.Identifier(displayFileName));
                diagnostics.Add(new ConfigFileProviderDiagnostic(
                    environment,
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Warning,
                        Code = "config-file-malformed",
                        Message = $"Skipping malformed config file {displayFileName}."
                    }));

                continue;
            }
        }

        var sourceProjections = projections
            .GroupBy(pair => ExtractEnvironment(Path.GetFileNameWithoutExtension(pair.Key)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ConfigSourceProjection<ConfigFileProjectedEntry>(
                    group.SelectMany(pair => pair.Value.Occurrences.Select(entry => new ConfigSourceEntry<ConfigFileProjectedEntry>(
                        entry.Key,
                        entry.SourceSpelling,
                        pair.Key,
                        $"{pair.Key}:{entry.ValueStart}",
                        entry))),
                    group.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key).ToArray()),
                StringComparer.OrdinalIgnoreCase);

        return new ConfigFileProviderSnapshot(environments, origins, diagnostics, sourceLocationMaps)
        {
            Projections = projections,
            OriginHistory = history,
            SourceProjections = sourceProjections
        };
    }

    /// <summary>Bounds the bytes retained even if a file grows after its initial length check.</summary>
    /// <param name="stream">The readable source at its current position; ownership remains with the caller.</param>
    /// <param name="limit">The validated positive maximum number of bytes.</param>
    /// <returns>A copy of the complete input, only when it fits the limit.</returns>
    /// <exception cref="ConfigResourceLimitException">A byte beyond the limit was read.</exception>
    internal static byte[] ReadBounded(Stream stream, long limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, Math.Min(chunk.Length, limit - buffer.Length) + 1))) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new ConfigResourceLimitException("config-file-byte-limit");
        }
        return buffer.ToArray();
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

    private static bool TryGetNode(JsonNode node, AppSurfaceConfigKey key, out JsonNode currentNode)
    {
        var keys = key.Segments;
        currentNode = node;
        foreach (var k in keys)
        {
            if (currentNode is JsonObject obj)
            {
                var match = obj.FirstOrDefault(pair => string.Equals(pair.Key, k, StringComparison.OrdinalIgnoreCase));
                if (match.Value != null) { currentNode = match.Value; continue; }
            }
            else if (currentNode is JsonArray array
                     && int.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                     && k.Equals(index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                     && index >= 0 && index < array.Count && array[index] is { } item)
            {
                currentNode = item;
                continue;
            }
            return false;
        }

        return true;
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

            var path = string.IsNullOrWhiteSpace(parentPath) ? kvp.Key : $"{parentPath}:{kvp.Key}";
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

        var logicalKey = AppSurfaceConfigKey.Parse(path);
        return new ConfigAuditProviderDiscoveredKey(logicalKey, rawValue, valueKind, [source], diagnostics);
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

    private static bool IsDiagnosticForKey(ConfigAuditDiagnostic diagnostic, AppSurfaceConfigKey key) =>
        (AppSurfaceConfigKey.TryParse(diagnostic.Key, out var diagnosticKey) && diagnosticKey.Equals(key))
        || (AppSurfaceConfigKey.TryParse(diagnostic.ConfigPath, out var path) && path.IsSameOrDescendantOf(key));

    private static bool IsProjectedCollision(ConfigFileProviderSnapshot snapshot, string environment, AppSurfaceConfigKey key)
    {
        foreach (var pair in snapshot.Projections)
        {
            if (!string.Equals(ExtractEnvironment(Path.GetFileNameWithoutExtension(pair.Key)), environment,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pair.Value.TerminalKeys.Any(terminal => terminal.Equals(key) || terminal.IsSameOrDescendantOf(key)))
            {
                return true;
            }

        }

        return snapshot.SourceProjections.TryGetValue(environment, out var projection)
            && projection.Entries.Any(result => result.IsTerminal && result.Key.IsSameOrDescendantOf(key));
    }

    private static bool IsProjectedInvalid(ConfigFileProviderSnapshot snapshot, string environment, AppSurfaceConfigKey key) =>
        snapshot.Projections.Any(pair => string.Equals(ExtractEnvironment(Path.GetFileNameWithoutExtension(pair.Key)), environment, StringComparison.OrdinalIgnoreCase)
            && pair.Value.InvalidKeys.Any(invalid => invalid.Equals(key) || invalid.IsSameOrDescendantOf(key)));

    private static bool IsDiagnosticInEnvironment(ConfigFileProviderDiagnostic diagnostic, string environment) =>
        diagnostic.Environment == null
        || string.Equals(diagnostic.Environment, environment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates every raw occurrence before merging a layer. Exact duplicates reject their whole branch;
    /// case differences also reject the incoming branch against all earlier spellings, including rejected sources.
    /// Terminal ancestors prevent aggregate replacement but permit independent valid object members to merge.
    /// </summary>
    private static FileLayerValidation ValidateLayer(ConfigFileTokenProjection projection,
        Dictionary<AppSurfaceConfigKey, string> earlierSpellings)
    {
        var seen = new HashSet<AppSurfaceConfigKey>();
        var rejected = new HashSet<AppSurfaceConfigKey>();
        var terminals = new HashSet<AppSurfaceConfigKey>(projection.TerminalKeys);
        foreach (var entry in projection.Occurrences)
        {
            if (!seen.Add(entry.Key)
                || (earlierSpellings.TryGetValue(entry.Key, out var spelling)
                    && !string.Equals(spelling, entry.SourceSpelling, StringComparison.Ordinal)))
            {
                rejected.Add(entry.Key);
                terminals.UnionWith(entry.Key.AncestorsAndSelf());
            }
            earlierSpellings.TryAdd(entry.Key, entry.SourceSpelling);
        }

        return new(rejected, terminals);
    }

    /// <summary>Separates rejected source branches from their terminal aggregate ancestors during one layer merge.</summary>
    private sealed record FileLayerValidation(IReadOnlySet<AppSurfaceConfigKey> RejectedKeys,
        IReadOnlySet<AppSurfaceConfigKey> TerminalKeys);

    private void MergeJsonObjects(
        JsonObject target,
        JsonObject source,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>> history,
        string file,
        string environment,
        string? parentPath,
        List<ConfigFileProviderDiagnostic> diagnostics,
        FileLayerValidation validation)
    {
        foreach (var kvp in source)
        {
            var path = string.IsNullOrWhiteSpace(parentPath) ? kvp.Key : $"{parentPath}:{kvp.Key}";
            var logicalKey = AppSurfaceConfigKey.Parse(path);
            if (validation.RejectedKeys.Contains(logicalKey)) continue;
            if (validation.TerminalKeys.Contains(logicalKey))
            {
                // Invalid arrays replace as a unit, and invalid objects cannot replace a lower scalar/array.
                // Objects can still contribute safe siblings without discarding lower descendant origins.
                if (kvp.Value is JsonObject incoming)
                {
                    if (!target.TryGetPropertyValue(kvp.Key, out var existing))
                    {
                        target[kvp.Key] = existing = new JsonObject();
                        RecordOrigin(origins, history, file, path);
                    }
                    if (existing is JsonObject aggregate)
                        MergeJsonObjects(aggregate, incoming, origins, history, file, environment, path, diagnostics, validation);
                }
                continue;
            }
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

            RecordOrigin(origins, history, file, path);
            if (target.ContainsKey(kvp.Key))
            {
                if (target[kvp.Key] is JsonObject targetObj && kvp.Value is JsonObject sourceObj)
                {
                    MergeJsonObjects(targetObj, sourceObj, origins, history, file, environment, path, diagnostics, validation);
                }
                else
                {
                    // Arrays and scalar values use replace semantics: later files
                    // override earlier values for the same key.
                    RemoveDescendantOrigins(origins, history, path);
                    target[kvp.Key] = kvp.Value.DeepClone();
                    if (kvp.Value is JsonObject or JsonArray)
                    {
                        RecordOrigins(kvp.Value, origins, history, file, environment, path, diagnostics);
                    }
                }
            }
            else
            {
                target[kvp.Key] = kvp.Value.DeepClone();
                if (kvp.Value is JsonObject or JsonArray)
                {
                    RecordOrigins(kvp.Value, origins, history, file, environment, path, diagnostics);
                }
            }
        }
    }

    private static void RemoveDescendantOrigins(Dictionary<string, ConfigAuditSourceRecord> origins,
        Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>> history, string path)
    {
        var parent = AppSurfaceConfigKey.Parse(path);
        foreach (var key in origins.Keys.Select(AppSurfaceConfigKey.Parse)
                     .Where(key => !key.Equals(parent) && key.IsSameOrDescendantOf(parent)).ToList())
        {
            origins.Remove(key.Value);
            history.Remove(key);
        }
    }

    private void RecordOrigins(
        JsonNode source,
        Dictionary<string, ConfigAuditSourceRecord> origins,
        Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>> history,
        string file,
        string environment,
        string parentPath,
        List<ConfigFileProviderDiagnostic> diagnostics)
    {
        if (source is JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                var path = $"{parentPath}:{kvp.Key}";
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

                RecordOrigin(origins, history, file, path);
                if (kvp.Value is JsonObject or JsonArray)
                {
                    RecordOrigins(kvp.Value, origins, history, file, environment, path, diagnostics);
                }
            }
        }
        else if (source is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var path = $"{parentPath}:{i}";
                var item = array[i];
                if (item == null)
                {
                    RecordOrigin(origins, history, file, path);
                    continue;
                }

                RecordOrigin(origins, history, file, path);
                if (item is JsonObject or JsonArray)
                {
                    RecordOrigins(item, origins, history, file, environment, path, diagnostics);
                }
            }
        }
    }

    /// <summary>Preserves ordered same-path overrides while replacement removes obsolete descendant histories.</summary>
    private void RecordOrigin(Dictionary<string, ConfigAuditSourceRecord> origins,
        Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>> history, string file, string path)
    {
        var source = CreateFileSource(file, path);
        origins[path] = source;
        var key = AppSurfaceConfigKey.Parse(path);
        if (!history.TryGetValue(key, out var sources)) history[key] = sources = [];
        sources.Add(source);
    }

    private static IEnumerable<ConfigAuditSourceRecord> GetSourceHistory(ConfigFileProviderSnapshot snapshot,
        string environment, AppSurfaceConfigKey key, ConfigAuditSourceRecord fallback) =>
        snapshot.OriginHistory.TryGetValue(environment, out var history) && history.TryGetValue(key, out var sources)
            ? sources.AsEnumerable().Reverse() : [fallback];

    private static string? GetResourceFailure(ConfigFileProviderSnapshot snapshot, string environment) =>
        snapshot.Diagnostics.FirstOrDefault(diagnostic => IsDiagnosticInEnvironment(diagnostic, environment)
            && diagnostic.Diagnostic.Code is "config-file-byte-limit" or "config-file-count-limit")?.Diagnostic.Code;

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

        var segments = path.Split(':');
        var changed = false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (ConfigAuditRedactor.ContainsSensitiveFragment(segments[i]))
            {
                segments[i] = "[redacted-key]";
                changed = true;
            }
        }

        return changed ? string.Join(':', segments) : path;
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
/// The map is the location view of the file token projection, so it shares the provider's exact logical-key walk.
/// </remarks>
internal sealed class ConfigFileSourceLocationMap
{
    private readonly Dictionary<string, ConfigAuditSourceLocation?> _locations;

    internal ConfigFileSourceLocationMap(Dictionary<string, ConfigAuditSourceLocation?> locations)
    {
        _locations = locations;
    }

    /// <summary>Gets an empty map used when source coordinates are unavailable.</summary>
    public static ConfigFileSourceLocationMap Empty { get; } = new(
        new Dictionary<string, ConfigAuditSourceLocation?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Creates a map from the same token projection used by the file provider.</summary>
    public static ConfigFileSourceLocationMap Create(ReadOnlySpan<byte> fileBytes)
    {
        try { return ConfigFileTokenProjection.Parse(fileBytes).Locations; }
        catch (JsonException) { return Empty; }
        catch (ArgumentException) { return Empty; }
    }

    /// <summary>Gets the location for a colon-delimited logical path.</summary>
    public ConfigAuditSourceLocation? GetLocation(string path) =>
        _locations.TryGetValue(path, out var location) ? location : null;
}

internal sealed record ConfigFileProviderSnapshot(
    Dictionary<string, JsonNode> Environments,
    Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>> Origins,
    IReadOnlyList<ConfigFileProviderDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, Lazy<ConfigFileSourceLocationMap>> SourceLocationMaps)
{
    /// <summary>Ordered source origins for effective paths; values are never retained in provenance.</summary>
    internal IReadOnlyDictionary<string, Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>>> OriginHistory { get; init; } =
        new Dictionary<string, Dictionary<AppSurfaceConfigKey, List<ConfigAuditSourceRecord>>>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, ConfigFileTokenProjection> Projections { get; init; } =
        new Dictionary<string, ConfigFileTokenProjection>(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, ConfigSourceProjection<ConfigFileProjectedEntry>> SourceProjections { get; init; } =
        new Dictionary<string, ConfigSourceProjection<ConfigFileProjectedEntry>>(StringComparer.OrdinalIgnoreCase);
    public ConfigFileProviderSnapshot(
        Dictionary<string, JsonNode> environments,
        Dictionary<string, Dictionary<string, ConfigAuditSourceRecord>> origins,
        IReadOnlyList<ConfigFileProviderDiagnostic> diagnostics)
        : this(
            environments,
            origins,
            diagnostics,
            new Dictionary<string, Lazy<ConfigFileSourceLocationMap>>(StringComparer.OrdinalIgnoreCase))
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
