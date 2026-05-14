using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// A configuration provider that retrieves values from environment variables.
/// </summary>
internal class EnvironmentConfigProvider : IEnvironmentConfigProvider, IConfigValuePatcher, IConfigDiagnosticProvider, IConfigDiagnosticPatcher
{
    // Safety limit for indexed env-var collections (KEY__0, KEY__1, ...).
    // Prevents unbounded probing while still supporting large lists.
    private const int MaxIndexedCollectionEntries = 1024;

    private readonly IEnvironmentProvider _environmentProvider;

    // Priority is technically ignored because DefaultConfigManager special-cases this provider
    // to always check it first, effectively giving it overrides-all priority.
    /// <inheritdoc />
    public int Priority { get; } = -1;

    /// <inheritdoc />
    public string Name { get; } = nameof(EnvironmentConfigProvider);

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentConfigProvider"/> class.
    /// </summary>
    /// <param name="environmentProvider">The environment provider.</param>
    public EnvironmentConfigProvider(IEnvironmentProvider environmentProvider)
    {
        _environmentProvider = environmentProvider;
    }

    /// <inheritdoc />
    public T? GetValue<T>(string environment, string key)
    {
        var envPrefix = NormalizeSegment(environment);
        var legacyKey = NormalizeSegment(key);
        var hierarchicalKey = NormalizeHierarchicalKey(key);

        var directCandidates = BuildDirectCandidates(envPrefix, legacyKey, hierarchicalKey);

        foreach (var candidate in directCandidates)
        {
            var value = _environmentProvider.GetEnvironmentVariable(candidate);
            if (value == null)
            {
                continue;
            }

            if (TryConvertStringValue<T>(value, out var parsed))
            {
                return parsed;
            }

            // Prefer the first parseable candidate while still allowing
            // lower-priority key formats as fallback when parsing fails.
        }

        if (TryReadIndexedCollection(typeof(T), $"{envPrefix}__{hierarchicalKey}", out var envScopedCollection))
        {
            return (T?)envScopedCollection;
        }

        if (TryReadIndexedCollection(typeof(T), hierarchicalKey, out var collection))
        {
            return (T?)collection;
        }

        return default;
    }

    ConfigValueResolution IConfigDiagnosticProvider.Resolve(
        string environment,
        string key,
        Type valueType,
        ConfigAuditSourceRole role)
    {
        var envPrefix = NormalizeSegment(environment);
        var legacyKey = NormalizeSegment(key);
        var hierarchicalKey = NormalizeHierarchicalKey(key);
        var diagnostics = new List<ConfigAuditDiagnostic>();

        foreach (var candidate in BuildDirectCandidates(envPrefix, legacyKey, hierarchicalKey))
        {
            var rawValue = _environmentProvider.GetEnvironmentVariable(candidate);
            if (rawValue == null)
            {
                continue;
            }

            var source = CreateEnvironmentSource(candidate, key, role);
            if (TryConvertStringToType(rawValue, valueType, out var parsed))
            {
                return new ConfigValueResolution(
                    key,
                    ConfigAuditEntryState.Resolved,
                    parsed,
                    [source],
                    diagnostics);
            }

            diagnostics.Add(CreateConversionDiagnostic(key, key, valueType, source));
        }

        if (TryReadIndexedCollectionDiagnostic(valueType, $"{envPrefix}__{hierarchicalKey}", key, role, diagnostics, out var envScopedCollection, out var envScopedSources))
        {
            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                envScopedCollection,
                envScopedSources,
                diagnostics);
        }

        if (TryReadIndexedCollectionDiagnostic(valueType, hierarchicalKey, key, role, diagnostics, out var collection, out var sources))
        {
            return new ConfigValueResolution(
                key,
                ConfigAuditEntryState.Resolved,
                collection,
                sources,
                diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            return new ConfigValueResolution(key, ConfigAuditEntryState.Invalid, null, [], diagnostics);
        }

        return ConfigValueResolution.Missing(key);
    }

    IReadOnlyList<ConfigAuditDiagnostic> IConfigDiagnosticProvider.GetReportDiagnostics(string environment) => [];

    /// <inheritdoc />
    public string Environment => _environmentProvider.Environment;

    /// <inheritdoc />
    public bool IsDevelopment => _environmentProvider.IsDevelopment;

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
        _environmentProvider.GetEnvironmentVariable(name, defaultValue);

    /// <inheritdoc />
    public bool TryPatch<T>(string environment, string key, T? currentValue, out T? patchedValue)
    {
        patchedValue = default;

        var targetType = typeof(T);
        if (!IsPatchableComplexType(targetType) && currentValue == null)
        {
            return false;
        }

        var runtimeType = currentValue?.GetType() ?? targetType;
        if (!IsPatchableComplexType(runtimeType))
        {
            return false;
        }

        object? target = currentValue;
        if (target == null && !TryCreateInstance(runtimeType, out target))
        {
            return false;
        }

        var envPrefix = NormalizeSegment(environment);
        var hierarchicalKey = NormalizeHierarchicalKey(key);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        if (!TryPatchObject(target!, runtimeType, envPrefix, hierarchicalKey, visited))
        {
            return false;
        }

        patchedValue = (T?)target!;
        return true;
    }

    ConfigPatchDiagnosticResult IConfigDiagnosticPatcher.TracePatch(
        string environment,
        string key,
        object? currentValue,
        Type valueType)
    {
        var diagnostics = new List<ConfigAuditDiagnostic>();
        var sources = new List<ConfigAuditSourceRecord>();
        if (!IsPatchableComplexType(valueType) && currentValue == null)
        {
            return new ConfigPatchDiagnosticResult(false, null, sources, diagnostics);
        }

        var runtimeType = currentValue?.GetType() ?? valueType;
        if (!IsPatchableComplexType(runtimeType))
        {
            return new ConfigPatchDiagnosticResult(false, null, sources, diagnostics);
        }

        object? target;
        if (currentValue == null)
        {
            if (!TryCreateInstance(runtimeType, out target))
            {
                return new ConfigPatchDiagnosticResult(false, null, sources, diagnostics);
            }
        }
        else if (!TryClone(currentValue, runtimeType, out target))
        {
            diagnostics.Add(new ConfigAuditDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = "config-patch-clone-failed",
                Key = key,
                ConfigPath = key,
                Message = $"Could not clone value for '{key}', so patch diagnostics were skipped to avoid mutating the original value."
            });
            return new ConfigPatchDiagnosticResult(false, null, sources, diagnostics);
        }

        var envPrefix = NormalizeSegment(environment);
        var hierarchicalKey = NormalizeHierarchicalKey(key);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var patched = TryPatchObjectDiagnostic(
            target!,
            runtimeType,
            envPrefix,
            hierarchicalKey,
            key,
            visited,
            sources,
            diagnostics);

        return new ConfigPatchDiagnosticResult(patched, patched ? target : null, sources, diagnostics);
    }

    /// <summary>
    /// Converts a key/environment segment to uppercase (via <see cref="string.ToUpperInvariant"/>)
    /// and flattens separators by replacing '.' and '-' with a single '_'.
    /// Used for legacy flat environment-variable lookup.
    /// </summary>
    private static string NormalizeSegment(string value) =>
        value.ToUpperInvariant()
            .Replace('.', '_')
            .Replace('-', '_');

    /// <summary>
    /// Converts a key to uppercase (via <see cref="string.ToUpperInvariant"/>), splits on '.' and '-'
    /// as hierarchical delimiters, removes empty segments, and joins segments using "__".
    /// Used for hierarchical environment-variable lookup while preserving path boundaries.
    /// </summary>
    private static string NormalizeHierarchicalKey(string value)
    {
        var segments = value.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("__", segments.Select(s => s.ToUpperInvariant()));
    }

    private static IReadOnlyList<string> BuildDirectCandidates(string envPrefix, string legacyKey, string hierarchicalKey)
    {
        var ordered = new[]
        {
            $"{envPrefix}_{legacyKey}",
            legacyKey,
            $"{envPrefix}__{hierarchicalKey}",
            hierarchicalKey
        };

        var distinct = new List<string>(ordered.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ordered)
        {
            if (seen.Add(candidate))
            {
                distinct.Add(candidate);
            }
        }

        return distinct;
    }

    private static bool TryConvertStringValue<T>(string value, out T? parsed)
    {
        if (!TryConvertStringToType(value, typeof(T), out var obj))
        {
            parsed = default;
            return false;
        }

        parsed = (T?)obj;
        return true;
    }

    private bool TryReadIndexedCollection(Type targetType, string keyPrefix, out object? parsed)
    {
        parsed = default;
        var elementType = GetCollectionElementType(targetType);
        if (elementType == null)
        {
            return false;
        }

        var values = new List<string>();
        for (var index = 0; index < MaxIndexedCollectionEntries; index++)
        {
            var value = _environmentProvider.GetEnvironmentVariable($"{keyPrefix}__{index}");
            if (value == null)
            {
                if (index == 0)
                {
                    return false;
                }

                break;
            }

            values.Add(value);
        }

        var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var value in values)
        {
            if (!TryConvertStringToType(value, elementType, out var element))
            {
                return false;
            }

            typedList.Add(element);
        }

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, typedList.Count);
            typedList.CopyTo(array, 0);
            parsed = array;
            return true;
        }

        parsed = typedList;
        return true;
    }

    private bool TryReadIndexedCollectionDiagnostic(
        Type targetType,
        string keyPrefix,
        string configPath,
        ConfigAuditSourceRole role,
        List<ConfigAuditDiagnostic> diagnostics,
        out object? parsed,
        out IReadOnlyList<ConfigAuditSourceRecord> sources)
    {
        parsed = default;
        sources = [];
        var elementType = GetCollectionElementType(targetType);
        if (elementType == null)
        {
            return false;
        }

        var values = new List<object?>();
        var sourceRecords = new List<ConfigAuditSourceRecord>();
        for (var index = 0; index < MaxIndexedCollectionEntries; index++)
        {
            var variableName = $"{keyPrefix}__{index}";
            var value = _environmentProvider.GetEnvironmentVariable(variableName);
            if (value == null)
            {
                if (index == 0)
                {
                    return false;
                }

                break;
            }

            var source = CreateEnvironmentSource(variableName, $"{configPath}.{index}", role);
            if (!TryConvertStringToType(value, elementType, out var element))
            {
                diagnostics.Add(CreateConversionDiagnostic(configPath, $"{configPath}.{index}", elementType, source));
                parsed = default;
                sources = sourceRecords;
                return false;
            }

            values.Add(element);
            sourceRecords.Add(source);
        }

        var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var value in values)
        {
            typedList.Add(value);
        }

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, typedList.Count);
            typedList.CopyTo(array, 0);
            parsed = array;
        }
        else
        {
            parsed = typedList;
        }

        sources = sourceRecords;
        return true;
    }

    private static Type? GetCollectionElementType(Type targetType)
    {
        if (targetType.IsArray)
        {
            return targetType.GetElementType();
        }

        if (targetType.IsGenericType)
        {
            var genericDef = targetType.GetGenericTypeDefinition();
            if (genericDef == typeof(List<>)
                || genericDef == typeof(IList<>)
                || genericDef == typeof(IEnumerable<>)
                || genericDef == typeof(IReadOnlyList<>)
                || genericDef == typeof(ICollection<>))
            {
                return targetType.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static bool TryConvertStringToType(string value, Type targetType, out object? parsed)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (nullableUnderlying != null)
        {
            if (string.IsNullOrEmpty(value))
            {
                parsed = null;
                return true;
            }

            targetType = nullableUnderlying;
        }

        if (targetType == typeof(string))
        {
            parsed = value;
            return true;
        }

        if (targetType.IsEnum)
        {
            if (Enum.TryParse(targetType, value, true, out var enumValue))
            {
                parsed = enumValue;
                return true;
            }

            parsed = null;
            return false;
        }

        if (targetType == typeof(Guid))
        {
            if (Guid.TryParse(value, out var guid))
            {
                parsed = guid;
                return true;
            }

            parsed = null;
            return false;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                parsed = dto;
                return true;
            }

            parsed = null;
            return false;
        }

        if (targetType == typeof(TimeSpan))
        {
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts))
            {
                parsed = ts;
                return true;
            }

            parsed = null;
            return false;
        }

        try
        {
            if (IsSimpleType(targetType))
            {
                parsed = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                return true;
            }

            parsed = JsonSerializer.Deserialize(value, targetType);
            return parsed != null;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException
                                       or ArgumentException or JsonException or NotSupportedException)
        {
            parsed = null;
            return false;
        }
    }

    private static bool IsSimpleType(Type targetType) =>
        targetType.IsPrimitive
        || targetType == typeof(decimal)
        || targetType == typeof(DateTime);

    private bool TryPatchObject(
        object target,
        Type targetType,
        string envPrefix,
        string hierarchicalKey,
        HashSet<object> visited)
    {
        if (!visited.Add(target))
        {
            return false;
        }

        var patched = false;

        foreach (var property in targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var childKey = CombineHierarchicalKey(hierarchicalKey, property.Name);
            if (TryReadMemberValue(property.PropertyType, envPrefix, childKey, out var propertyValue))
            {
                if (HasPublicSetter(property))
                {
                    property.SetValue(target, propertyValue);
                    patched = true;
                    continue;
                }

                if (property.GetMethod != null
                    && TryPatchExistingCollection(property.GetValue(target), propertyValue))
                {
                    patched = true;
                    continue;
                }

                continue;
            }

            if (!IsPatchableComplexType(property.PropertyType))
            {
                continue;
            }

            object? child = null;
            if (property.GetMethod != null)
            {
                child = property.GetValue(target);
            }

            if (child == null)
            {
                if (!HasPublicSetter(property)
                    || !TryCreateInstance(property.PropertyType, out child))
                {
                    continue;
                }
            }

            var childToPatch = child!;
            if (TryPatchObject(childToPatch, childToPatch.GetType(), envPrefix, childKey, visited))
            {
                if (HasPublicSetter(property))
                {
                    property.SetValue(target, childToPatch);
                }

                patched = true;
            }
        }

        foreach (var field in targetType.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.IsInitOnly)
            {
                continue;
            }

            var childKey = CombineHierarchicalKey(hierarchicalKey, field.Name);
            if (TryReadMemberValue(field.FieldType, envPrefix, childKey, out var fieldValue))
            {
                field.SetValue(target, fieldValue);
                patched = true;
                continue;
            }

            if (!IsPatchableComplexType(field.FieldType))
            {
                continue;
            }

            var child = field.GetValue(target);
            if (child == null)
            {
                if (!TryCreateInstance(field.FieldType, out child))
                {
                    continue;
                }
            }

            var childToPatch = child!;
            if (TryPatchObject(childToPatch, childToPatch.GetType(), envPrefix, childKey, visited))
            {
                field.SetValue(target, childToPatch);
                patched = true;
            }
        }

        // Remove on unwind so DAG-shaped graphs can revisit the same instance by another path;
        // visited.Add(target) still returns false early for true cycles.
        visited.Remove(target);
        return patched;
    }

    private bool TryReadMemberValue(Type targetType, string envPrefix, string hierarchicalKey, out object? parsed)
    {
        var legacyKey = hierarchicalKey.Replace("__", "_", StringComparison.Ordinal);
        foreach (var candidate in BuildDirectCandidates(envPrefix, legacyKey, hierarchicalKey))
        {
            var value = _environmentProvider.GetEnvironmentVariable(candidate);
            if (value == null)
            {
                continue;
            }

            if (TryConvertStringToType(value, targetType, out parsed))
            {
                return true;
            }
        }

        if (TryReadIndexedCollection(targetType, $"{envPrefix}__{hierarchicalKey}", out parsed))
        {
            return true;
        }

        if (TryReadIndexedCollection(targetType, hierarchicalKey, out parsed))
        {
            return true;
        }

        parsed = default;
        return false;
    }

    private bool TryReadMemberValueDiagnostic(
        Type targetType,
        string envPrefix,
        string hierarchicalKey,
        string configPath,
        List<ConfigAuditDiagnostic> diagnostics,
        out object? parsed,
        out IReadOnlyList<ConfigAuditSourceRecord> sources)
    {
        sources = [];
        var legacyKey = hierarchicalKey.Replace("__", "_", StringComparison.Ordinal);
        foreach (var candidate in BuildDirectCandidates(envPrefix, legacyKey, hierarchicalKey))
        {
            var value = _environmentProvider.GetEnvironmentVariable(candidate);
            if (value == null)
            {
                continue;
            }

            var source = CreateEnvironmentSource(candidate, configPath, ConfigAuditSourceRole.Patch);
            if (TryConvertStringToType(value, targetType, out parsed))
            {
                sources = [source];
                return true;
            }

            diagnostics.Add(CreateConversionDiagnostic(configPath, configPath, targetType, source));
        }

        if (TryReadIndexedCollectionDiagnostic(targetType, $"{envPrefix}__{hierarchicalKey}", configPath, ConfigAuditSourceRole.Patch, diagnostics, out parsed, out var envScopedSources))
        {
            sources = envScopedSources;
            return true;
        }

        if (TryReadIndexedCollectionDiagnostic(targetType, hierarchicalKey, configPath, ConfigAuditSourceRole.Patch, diagnostics, out parsed, out var collectionSources))
        {
            sources = collectionSources;
            return true;
        }

        parsed = default;
        return false;
    }

    private bool TryPatchObjectDiagnostic(
        object target,
        Type targetType,
        string envPrefix,
        string hierarchicalKey,
        string configPath,
        HashSet<object> visited,
        List<ConfigAuditSourceRecord> sources,
        List<ConfigAuditDiagnostic> diagnostics)
    {
        if (!visited.Add(target))
        {
            return false;
        }

        var patched = false;
        foreach (var property in targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var childKey = CombineHierarchicalKey(hierarchicalKey, property.Name);
            var childPath = CombineConfigPath(configPath, property.Name);
            if (TryReadMemberValueDiagnostic(property.PropertyType, envPrefix, childKey, childPath, diagnostics, out var propertyValue, out var propertySources))
            {
                if (HasPublicSetter(property))
                {
                    property.SetValue(target, propertyValue);
                    sources.AddRange(propertySources);
                    patched = true;
                    continue;
                }

                if (property.GetMethod != null
                    && TryPatchExistingCollection(property.GetValue(target), propertyValue))
                {
                    sources.AddRange(propertySources);
                    patched = true;
                    continue;
                }

                continue;
            }

            if (!IsPatchableComplexType(property.PropertyType))
            {
                continue;
            }

            object? child = property.GetMethod == null ? null : property.GetValue(target);
            if (child == null)
            {
                if (!HasPublicSetter(property)
                    || !TryCreateInstance(property.PropertyType, out child))
                {
                    continue;
                }
            }

            var childToPatch = child!;
            if (TryPatchObjectDiagnostic(childToPatch, childToPatch.GetType(), envPrefix, childKey, childPath, visited, sources, diagnostics))
            {
                if (HasPublicSetter(property))
                {
                    property.SetValue(target, childToPatch);
                }

                patched = true;
            }
        }

        foreach (var field in targetType.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.IsInitOnly)
            {
                continue;
            }

            var childKey = CombineHierarchicalKey(hierarchicalKey, field.Name);
            var childPath = CombineConfigPath(configPath, field.Name);
            if (TryReadMemberValueDiagnostic(field.FieldType, envPrefix, childKey, childPath, diagnostics, out var fieldValue, out var fieldSources))
            {
                field.SetValue(target, fieldValue);
                sources.AddRange(fieldSources);
                patched = true;
                continue;
            }

            if (!IsPatchableComplexType(field.FieldType))
            {
                continue;
            }

            var child = field.GetValue(target);
            if (child == null && !TryCreateInstance(field.FieldType, out child))
            {
                continue;
            }

            if (TryPatchObjectDiagnostic(child!, child!.GetType(), envPrefix, childKey, childPath, visited, sources, diagnostics))
            {
                field.SetValue(target, child);
                patched = true;
            }
        }

        visited.Remove(target);
        return patched;
    }

    private static bool TryPatchExistingCollection(object? targetCollection, object? replacement)
    {
        if (targetCollection is not IList targetList
            || replacement is not IEnumerable replacementValues
            || targetList.IsReadOnly
            || targetList.IsFixedSize)
        {
            return false;
        }

        targetList.Clear();
        foreach (var value in replacementValues)
        {
            targetList.Add(value);
        }

        return true;
    }

    private static bool HasPublicSetter(PropertyInfo property) =>
        property.SetMethod?.IsPublic == true;

    private static string CombineHierarchicalKey(string parentKey, string memberName)
    {
        var memberKey = NormalizeHierarchicalKey(memberName);
        return string.IsNullOrEmpty(parentKey) ? memberKey : $"{parentKey}__{memberKey}";
    }

    private static string CombineConfigPath(string parentPath, string memberName) =>
        string.IsNullOrEmpty(parentPath) ? memberName : $"{parentPath}.{memberName}";

    private static bool IsPatchableComplexType(Type targetType)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(targetType);
        if (nullableUnderlying != null)
        {
            targetType = nullableUnderlying;
        }

        return targetType != typeof(string)
               && !IsSimpleType(targetType)
               && !targetType.IsEnum
               && targetType != typeof(Guid)
               && targetType != typeof(DateTimeOffset)
               && targetType != typeof(TimeSpan)
               && GetCollectionElementType(targetType) == null
               && !typeof(IDictionary).IsAssignableFrom(targetType);
    }

    private static bool TryCreateInstance(Type targetType, out object? instance)
    {
        instance = null;

        if (targetType.IsAbstract || targetType.IsInterface)
        {
            return false;
        }

        try
        {
            instance = Activator.CreateInstance(targetType);
            return instance != null;
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryClone(object value, Type targetType, out object? clone)
    {
        clone = null;
        try
        {
            var json = JsonSerializer.Serialize(value, targetType);
            clone = JsonSerializer.Deserialize(json, targetType);
            return clone != null;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private ConfigAuditSourceRecord CreateEnvironmentSource(
        string variableName,
        string configPath,
        ConfigAuditSourceRole role) =>
        new()
        {
            Kind = ConfigAuditSourceKind.EnvironmentVariable,
            ProviderName = Name,
            ProviderPriority = Priority,
            EnvironmentVariableName = variableName,
            ConfigPath = configPath,
            AppliedToPath = configPath,
            Role = role
        };

    private static ConfigAuditDiagnostic CreateConversionDiagnostic(
        string key,
        string configPath,
        Type targetType,
        ConfigAuditSourceRecord source) =>
        new()
        {
            Severity = ConfigAuditDiagnosticSeverity.Warning,
            Code = "config-environment-conversion-failed",
            Key = key,
            ConfigPath = configPath,
            Source = source,
            Message = $"Ignored environment variable {source.EnvironmentVariableName} because its value could not be converted to {targetType.Name}."
        };
}
