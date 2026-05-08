using System.Reflection;
using ForgeTrust.Runnable.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.Runnable.Config;

/// <summary>
/// Produces structured configuration audit reports.
/// </summary>
public interface IConfigAuditReporter
{
    /// <summary>
    /// Builds a configuration audit report for <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The environment to audit.</param>
    /// <returns>The completed audit report.</returns>
    ConfigAuditReport GetReport(string environment);
}

internal sealed class ConfigAuditReporter : IConfigAuditReporter
{
    private static readonly Type ConfigManagerType = typeof(IConfigManager);
    private static readonly Type EnvironmentConfigProviderType = typeof(IEnvironmentConfigProvider);
    private static readonly Type[] ExcludedProviderTypes = [ConfigManagerType, EnvironmentConfigProviderType];

    private readonly IEnvironmentConfigProvider _environmentProvider;
    private readonly IReadOnlyList<IConfigProvider> _otherProviders;
    private readonly IReadOnlyList<ConfigAuditKnownEntry> _knownEntries;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigAuditRedactor _redactor;

    public ConfigAuditReporter(
        IEnvironmentConfigProvider environmentProvider,
        IEnumerable<IConfigProvider>? providers,
        IEnumerable<ConfigAuditKnownEntry>? knownEntries,
        IServiceProvider serviceProvider,
        ConfigAuditRedactor redactor)
    {
        _environmentProvider = environmentProvider;
        _otherProviders = providers?
                              .Where(provider => !ExcludedProviderTypes.Any(t => t.IsInstanceOfType(provider)))
                              .OrderByDescending(provider => provider.Priority)
                              .ToList()
                          ?? [];
        _knownEntries = knownEntries?
                            .GroupBy(entry => new { entry.Key, entry.ValueType, entry.ConfigType })
                            .Select(group => group.First())
                            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                        ?? [];
        _serviceProvider = serviceProvider;
        _redactor = redactor;
    }

    public ConfigAuditReport GetReport(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var entries = _knownEntries.Select(entry => BuildEntry(environment, entry)).ToList();
        return new ConfigAuditReport
        {
            Environment = environment,
            GeneratedAt = DateTimeOffset.UtcNow,
            Providers = BuildProviderList(),
            Entries = entries,
            Diagnostics = BuildReportDiagnostics(environment),
            Redaction = _redactor.CreatePolicy()
        };
    }

    private IReadOnlyList<ConfigAuditDiagnostic> BuildReportDiagnostics(string environment) =>
        new IConfigProvider[] { _environmentProvider }
            .Concat(_otherProviders)
            .OfType<IConfigDiagnosticProvider>()
            .SelectMany(provider => provider.GetReportDiagnostics(environment))
            .ToList();

    private IReadOnlyList<ConfigAuditProvider> BuildProviderList()
    {
        var providers = new List<ConfigAuditProvider>
        {
            new()
            {
                Name = _environmentProvider.Name,
                Priority = _environmentProvider.Priority,
                Precedence = 1,
                IsOverride = true
            }
        };

        var precedence = 2;
        providers.AddRange(_otherProviders.Select(provider => new ConfigAuditProvider
        {
            Name = provider.Name,
            Priority = provider.Priority,
            Precedence = precedence++,
            IsOverride = false
        }));

        return providers;
    }

    private ConfigAuditEntry BuildEntry(string environment, ConfigAuditKnownEntry knownEntry)
    {
        var resolution = Resolve(environment, knownEntry);
        var inspection = InspectWrapper(knownEntry, resolution);
        var rawValue = inspection.Value;
        var state = inspection.State ?? resolution.State;
        var sources = inspection.DefaultSource != null
            ? [inspection.DefaultSource]
            : resolution.Sources;
        var diagnostics = resolution.Diagnostics.Concat(inspection.Diagnostics).ToList();
        var children = BuildChildren(knownEntry.Key, rawValue, sources);
        if (children.Any(child => child.Sources.Any(source => source.Role == ConfigAuditSourceRole.Patch))
            && state == ConfigAuditEntryState.Resolved)
        {
            state = ConfigAuditEntryState.PartiallyResolved;
        }

        var redacted = _redactor.FormatValue(knownEntry.Key, rawValue, sources);
        return new ConfigAuditEntry
        {
            Key = knownEntry.Key,
            DeclaredType = knownEntry.ValueType.FullName,
            State = state,
            DisplayValue = redacted.DisplayValue,
            IsRedacted = redacted.IsRedacted,
            Sources = sources,
            Children = children,
            Diagnostics = diagnostics
        };
    }

    private ConfigValueResolution Resolve(string environment, ConfigAuditKnownEntry knownEntry)
    {
        var envResolution = ResolveProvider(_environmentProvider, environment, knownEntry, ConfigAuditSourceRole.Override);
        if (envResolution.State == ConfigAuditEntryState.Resolved)
        {
            return envResolution;
        }

        var diagnostics = envResolution.Diagnostics.ToList();
        ConfigValueResolution providerResolution = ConfigValueResolution.Missing(knownEntry.Key);
        foreach (var provider in _otherProviders)
        {
            var current = ResolveProvider(provider, environment, knownEntry, ConfigAuditSourceRole.Base);
            diagnostics.AddRange(current.Diagnostics);
            if (current.State == ConfigAuditEntryState.Resolved || current.State == ConfigAuditEntryState.Invalid)
            {
                providerResolution = current;
                break;
            }
        }

        if (_environmentProvider is IConfigDiagnosticPatcher patcher)
        {
            var patch = patcher.TracePatch(environment, knownEntry.Key, providerResolution.Value, knownEntry.ValueType);
            diagnostics.AddRange(patch.Diagnostics);
            if (patch.Patched)
            {
                var sourceRecords = providerResolution.Sources.Concat(patch.Sources).ToList();
                return new ConfigValueResolution(
                    knownEntry.Key,
                    ConfigAuditEntryState.PartiallyResolved,
                    patch.Value,
                    sourceRecords,
                    diagnostics);
            }
        }

        return providerResolution with { Diagnostics = diagnostics };
    }

    private ConfigValueResolution ResolveProvider(
        IConfigProvider provider,
        string environment,
        ConfigAuditKnownEntry knownEntry,
        ConfigAuditSourceRole role)
    {
        if (provider is IConfigDiagnosticProvider diagnosticProvider)
        {
            return diagnosticProvider.Resolve(environment, knownEntry.Key, knownEntry.ValueType, role);
        }

        var method = typeof(IConfigProvider)
            .GetMethod(nameof(IConfigProvider.GetValue))!
            .MakeGenericMethod(knownEntry.ValueType);
        var value = method.Invoke(provider, [environment, knownEntry.Key]);
        if (value == null)
        {
            return ConfigValueResolution.Missing(knownEntry.Key);
        }

        return new ConfigValueResolution(
            knownEntry.Key,
            ConfigAuditEntryState.Resolved,
            value,
            [
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.Provider,
                    ProviderName = provider.Name,
                    ProviderPriority = provider.Priority,
                    ConfigPath = knownEntry.Key,
                    AppliedToPath = knownEntry.Key,
                    Role = role
                }
            ],
            []);
    }

    private ConfigWrapperInspection InspectWrapper(
        ConfigAuditKnownEntry knownEntry,
        ConfigValueResolution resolution)
    {
        if (knownEntry.ConfigType == null)
        {
            return new ConfigWrapperInspection(resolution.Value, resolution.State, null, []);
        }

        object wrapper;
        try
        {
            wrapper = ActivatorUtilities.CreateInstance(_serviceProvider, knownEntry.ConfigType);
        }
        catch (Exception ex)
        {
            return new ConfigWrapperInspection(
                resolution.Value,
                ConfigAuditEntryState.Invalid,
                null,
                [
                    new ConfigAuditDiagnostic
                    {
                        Severity = ConfigAuditDiagnosticSeverity.Error,
                        Code = "config-wrapper-create-failed",
                        Key = knownEntry.Key,
                        Message = $"Could not create config wrapper {knownEntry.ConfigType.Name}: {ex.Message}"
                    }
                ]);
        }

        if (wrapper is not IConfigInspectable inspectable)
        {
            return new ConfigWrapperInspection(resolution.Value, resolution.State, null, []);
        }

        return inspectable.Inspect(knownEntry.Key, resolution.Value, resolution.State);
    }

    private IReadOnlyList<ConfigAuditEntry> BuildChildren(
        string key,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> parentSources)
    {
        if (value == null || ConfigScalarTypes.IsScalar(value.GetType()))
        {
            return [];
        }

        if (value is string)
        {
            return [];
        }

        var valueType = value.GetType();
        var entries = new List<ConfigAuditEntry>();
        foreach (var property in valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod == null)
            {
                continue;
            }

            object? childValue;
            try
            {
                childValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            var childKey = $"{key}.{property.Name}";
            entries.Add(BuildChild(childKey, childValue, parentSources));
        }

        foreach (var field in valueType.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.IsInitOnly)
            {
                continue;
            }

            var childKey = $"{key}.{field.Name}";
            entries.Add(BuildChild(childKey, field.GetValue(value), parentSources));
        }

        return entries;
    }

    private ConfigAuditEntry BuildChild(
        string childKey,
        object? childValue,
        IReadOnlyList<ConfigAuditSourceRecord> parentSources)
    {
        var childSources = parentSources
            .Where(source => IsSourceForChild(source, childKey))
            .DefaultIfEmpty(parentSources.FirstOrDefault())
            .Where(source => source != null)
            .Cast<ConfigAuditSourceRecord>()
            .ToList();
        var redacted = _redactor.FormatValue(childKey, childValue, childSources);
        return new ConfigAuditEntry
        {
            Key = childKey,
            DeclaredType = childValue?.GetType().FullName,
            State = ConfigAuditEntryState.Resolved,
            DisplayValue = redacted.DisplayValue,
            IsRedacted = redacted.IsRedacted,
            Sources = childSources,
            Children = BuildChildren(childKey, childValue, parentSources)
        };
    }

    private static bool IsSourceForChild(ConfigAuditSourceRecord source, string childKey) =>
        string.Equals(source.AppliedToPath, childKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.ConfigPath, childKey, StringComparison.OrdinalIgnoreCase)
        || (source.Role == ConfigAuditSourceRole.Base
            && source.AppliedToPath != null
            && childKey.StartsWith(source.AppliedToPath, StringComparison.OrdinalIgnoreCase));
}

internal sealed record ConfigValueResolution(
    string Key,
    ConfigAuditEntryState State,
    object? Value,
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics)
{
    public static ConfigValueResolution Missing(string key) =>
        new(
            key,
            ConfigAuditEntryState.Missing,
            null,
            [
                new ConfigAuditSourceRecord
                {
                    Kind = ConfigAuditSourceKind.Missing,
                    ConfigPath = key,
                    AppliedToPath = key,
                    Role = ConfigAuditSourceRole.Base
                }
            ],
            []);
}

internal sealed record ConfigPatchDiagnosticResult(
    bool Patched,
    object? Value,
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

internal sealed record ConfigWrapperInspection(
    object? Value,
    ConfigAuditEntryState? State,
    ConfigAuditSourceRecord? DefaultSource,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

internal interface IConfigDiagnosticProvider
{
    ConfigValueResolution Resolve(string environment, string key, Type valueType, ConfigAuditSourceRole role);

    IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment);
}

internal interface IConfigDiagnosticPatcher
{
    ConfigPatchDiagnosticResult TracePatch(string environment, string key, object? currentValue, Type valueType);
}

internal interface IConfigInspectable
{
    ConfigWrapperInspection Inspect(string key, object? rawValue, ConfigAuditEntryState resolutionState);
}
