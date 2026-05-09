using System.Reflection;
using ForgeTrust.Runnable.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.Runnable.Config;

/// <summary>
/// Produces structured configuration audit reports.
/// </summary>
/// <remarks>
/// Reporters inspect known configuration keys, provider provenance, validation diagnostics, and redacted display
/// values. Implementations should return a report with diagnostics for recoverable provider and wrapper failures
/// rather than throwing after argument validation succeeds.
/// </remarks>
public interface IConfigAuditReporter
{
    /// <summary>
    /// Builds a configuration audit report for <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The environment to audit.</param>
    /// <returns>The completed audit report.</returns>
    ConfigAuditReport GetReport(string environment);
}

/// <summary>
/// Default implementation that mirrors configuration resolution while preserving source and diagnostic metadata.
/// </summary>
/// <remarks>
/// This internal reporter treats <see cref="IEnvironmentConfigProvider"/> as the override provider, excludes
/// manager/internal provider registrations from the displayed provider list, and favors wrapper-discovered audit
/// entries when duplicate keys are registered manually. Provider failures are converted into diagnostics so one
/// broken provider does not prevent operators from seeing the rest of the report.
/// </remarks>
internal sealed class ConfigAuditReporter : IConfigAuditReporter
{
    /// <summary>
    /// Identifies the manager registration so it is not reported as a value provider.
    /// </summary>
    private static readonly Type ConfigManagerType = typeof(IConfigManager);

    /// <summary>
    /// Identifies the environment provider registration that is displayed separately as the override provider.
    /// </summary>
    private static readonly Type EnvironmentConfigProviderType = typeof(IEnvironmentConfigProvider);

    /// <summary>
    /// Gets provider implementation types that are internal to resolution orchestration and excluded from base providers.
    /// </summary>
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
                            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.OrderBy(entry => entry.ConfigType == null ? 1 : 0).First())
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

    private IReadOnlyList<ConfigAuditDiagnostic> BuildReportDiagnostics(string environment)
    {
        var diagnostics = new List<ConfigAuditDiagnostic>();
        foreach (var provider in new IConfigProvider[] { _environmentProvider }.Concat(_otherProviders))
        {
            if (provider is not IConfigDiagnosticProvider diagnosticProvider)
            {
                continue;
            }

            try
            {
                diagnostics.AddRange(diagnosticProvider.GetReportDiagnostics(environment));
            }
            catch (Exception ex)
            {
                diagnostics.Add(CreateProviderExceptionDiagnostic(
                    provider,
                    "config-provider-diagnostics-threw",
                    key: null,
                    configPath: null,
                    ex));
            }
        }

        return diagnostics;
    }

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
        var children = BuildChildren(
            knownEntry.Key,
            rawValue,
            sources,
            new HashSet<object>(ReferenceEqualityComparer.Instance));
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
        ConfigValueResolution? invalidProviderResolution = null;
        foreach (var provider in _otherProviders)
        {
            var current = ResolveProvider(provider, environment, knownEntry, ConfigAuditSourceRole.Base);
            diagnostics.AddRange(current.Diagnostics);
            if (current.State == ConfigAuditEntryState.Resolved)
            {
                providerResolution = current;
                break;
            }

            if (current.State == ConfigAuditEntryState.Invalid)
            {
                invalidProviderResolution ??= current;
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

        var resolution = providerResolution.State == ConfigAuditEntryState.Resolved
            ? providerResolution
            : invalidProviderResolution ?? providerResolution;
        return resolution with { Diagnostics = diagnostics };
    }

    private ConfigValueResolution ResolveProvider(
        IConfigProvider provider,
        string environment,
        ConfigAuditKnownEntry knownEntry,
        ConfigAuditSourceRole role)
    {
        if (provider is IConfigDiagnosticProvider diagnosticProvider)
        {
            try
            {
                return diagnosticProvider.Resolve(environment, knownEntry.Key, knownEntry.ValueType, role);
            }
            catch (Exception ex)
            {
                return CreateProviderExceptionResolution(
                    provider,
                    knownEntry.Key,
                    role,
                    "config-provider-resolve-threw",
                    ex);
            }
        }

        object? value;
        try
        {
            var method = typeof(IConfigProvider)
                .GetMethod(nameof(IConfigProvider.GetValue))!
                .MakeGenericMethod(knownEntry.ValueType);
            value = method.Invoke(provider, [environment, knownEntry.Key]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            return CreateProviderExceptionResolution(
                provider,
                knownEntry.Key,
                role,
                "config-provider-get-value-threw",
                ex.InnerException);
        }
        catch (Exception ex)
        {
            return CreateProviderExceptionResolution(
                provider,
                knownEntry.Key,
                role,
                "config-provider-get-value-threw",
                ex);
        }

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
        IReadOnlyList<ConfigAuditSourceRecord> parentSources,
        HashSet<object> visited)
    {
        if (value == null || ConfigScalarTypes.IsScalar(value.GetType()))
        {
            return [];
        }

        if (!visited.Add(value))
        {
            return [];
        }

        var valueType = value.GetType();
        var entries = new List<ConfigAuditEntry>();
        try
        {
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
                entries.Add(BuildChild(childKey, childValue, parentSources, visited));
            }

            foreach (var field in valueType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.IsInitOnly)
                {
                    continue;
                }

                var childKey = $"{key}.{field.Name}";
                entries.Add(BuildChild(childKey, field.GetValue(value), parentSources, visited));
            }
        }
        finally
        {
            visited.Remove(value);
        }

        return entries;
    }

    private ConfigAuditEntry BuildChild(
        string childKey,
        object? childValue,
        IReadOnlyList<ConfigAuditSourceRecord> parentSources,
        HashSet<object> visited)
    {
        var childSources = parentSources
            .Where(source => IsSourceForChild(source, childKey))
            .DefaultIfEmpty(parentSources.FirstOrDefault())
            .Where(source => source != null)
            .Cast<ConfigAuditSourceRecord>()
            .ToList();
        var redacted = _redactor.FormatValue(childKey, childValue, childSources);
        var children = BuildChildren(childKey, childValue, parentSources, visited);
        var state = children.Any(child => child.State == ConfigAuditEntryState.PartiallyResolved
                                          || child.Sources.Any(source => source.Role == ConfigAuditSourceRole.Patch))
            ? ConfigAuditEntryState.PartiallyResolved
            : ConfigAuditEntryState.Resolved;
        return new ConfigAuditEntry
        {
            Key = childKey,
            DeclaredType = childValue?.GetType().FullName,
            State = state,
            DisplayValue = redacted.DisplayValue,
            IsRedacted = redacted.IsRedacted,
            Sources = childSources,
            Children = children
        };
    }

    private static bool IsSourceForChild(ConfigAuditSourceRecord source, string childKey) =>
        string.Equals(source.AppliedToPath, childKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.ConfigPath, childKey, StringComparison.OrdinalIgnoreCase)
        || (source.Role == ConfigAuditSourceRole.Base
            && source.AppliedToPath != null
            && childKey.StartsWith(source.AppliedToPath, StringComparison.OrdinalIgnoreCase));

    private static ConfigValueResolution CreateProviderExceptionResolution(
        IConfigProvider provider,
        string key,
        ConfigAuditSourceRole role,
        string code,
        Exception ex)
    {
        var source = new ConfigAuditSourceRecord
        {
            Kind = ConfigAuditSourceKind.Provider,
            ProviderName = provider.Name,
            ProviderPriority = provider.Priority,
            ConfigPath = key,
            AppliedToPath = key,
            Role = role
        };
        return new ConfigValueResolution(
            key,
            ConfigAuditEntryState.Invalid,
            null,
            [source],
            [CreateProviderExceptionDiagnostic(provider, code, key, key, ex, source)]);
    }

    private static ConfigAuditDiagnostic CreateProviderExceptionDiagnostic(
        IConfigProvider provider,
        string code,
        string? key,
        string? configPath,
        Exception ex,
        ConfigAuditSourceRecord? source = null) =>
        new()
        {
            Severity = ConfigAuditDiagnosticSeverity.Error,
            Code = code,
            Key = key,
            ConfigPath = configPath,
            Source = source,
            Message = $"Configuration provider {provider.Name} threw {ex.GetType().Name}: {ex.Message}"
        };
}

/// <summary>
/// Captures a provider's value resolution result before wrapper inspection and redaction.
/// </summary>
/// <remarks>
/// The reporter uses this internal contract to keep value state, provenance, and diagnostics together while it
/// walks providers in precedence order. Missing values should be represented with <see cref="Missing(string)"/>.
/// </remarks>
/// <param name="Key">The configuration key being resolved.</param>
/// <param name="State">The provider-level resolution state.</param>
/// <param name="Value">The resolved raw value, or <see langword="null"/> when missing or invalid.</param>
/// <param name="Sources">The sources that contributed to the value.</param>
/// <param name="Diagnostics">Diagnostics emitted while resolving the value.</param>
internal sealed record ConfigValueResolution(
    string Key,
    ConfigAuditEntryState State,
    object? Value,
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics)
{
    /// <summary>
    /// Creates a missing resolution with a synthetic missing source record for <paramref name="key"/>.
    /// </summary>
    /// <param name="key">The missing configuration key.</param>
    /// <returns>A missing resolution that can still be rendered with provenance.</returns>
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

/// <summary>
/// Describes the result of tracing environment patches onto an existing provider value.
/// </summary>
/// <param name="Patched">A value indicating whether any member was patched.</param>
/// <param name="Value">The patched value, or the original/current value when unchanged.</param>
/// <param name="Sources">The environment sources that successfully patched the value.</param>
/// <param name="Diagnostics">Diagnostics produced while reading patch candidates.</param>
internal sealed record ConfigPatchDiagnosticResult(
    bool Patched,
    object? Value,
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

/// <summary>
/// Describes wrapper inspection after provider resolution.
/// </summary>
/// <param name="Value">The value to display and expand after defaulting or validation.</param>
/// <param name="State">The wrapper-adjusted state, or <see langword="null"/> to keep provider state.</param>
/// <param name="DefaultSource">The default source when the wrapper supplied a fallback value.</param>
/// <param name="Diagnostics">Validation and wrapper diagnostics.</param>
internal sealed record ConfigWrapperInspection(
    object? Value,
    ConfigAuditEntryState? State,
    ConfigAuditSourceRecord? DefaultSource,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

/// <summary>
/// Allows providers to expose audit-specific resolution details and report-level diagnostics.
/// </summary>
/// <remarks>
/// Implementations should avoid throwing for expected parse or source errors and return diagnostics instead.
/// The reporter catches unexpected exceptions and converts them into provider diagnostics.
/// </remarks>
internal interface IConfigDiagnosticProvider
{
    /// <summary>
    /// Resolves <paramref name="key"/> for audit reporting without losing source metadata.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="valueType">The expected value type.</param>
    /// <param name="role">The role the provider plays in final resolution.</param>
    /// <returns>The provider-specific resolution result.</returns>
    ConfigValueResolution Resolve(string environment, string key, Type valueType, ConfigAuditSourceRole role);

    /// <summary>
    /// Gets diagnostics that apply to the whole report rather than one key.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <returns>Report-level diagnostics. Return an empty list when there are none.</returns>
    IReadOnlyList<ConfigAuditDiagnostic> GetReportDiagnostics(string environment);
}

/// <summary>
/// Allows an override provider to trace member-level patches onto a base provider value.
/// </summary>
/// <remarks>
/// The environment provider uses this to explain mixed provenance for object values without mutating the provider
/// instance that supplied the base value.
/// </remarks>
internal interface IConfigDiagnosticPatcher
{
    /// <summary>
    /// Traces patch candidates for <paramref name="key"/> and returns a cloned patched value when possible.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="currentValue">The lower-priority provider value to patch, when any.</param>
    /// <param name="valueType">The expected value type.</param>
    /// <returns>The patch result, including successful patch sources and diagnostics.</returns>
    ConfigPatchDiagnosticResult TracePatch(string environment, string key, object? currentValue, Type valueType);
}

/// <summary>
/// Allows config wrappers to add defaults and validation diagnostics to an audit entry.
/// </summary>
/// <remarks>
/// Implementations receive the raw provider value and should return structured diagnostics rather than throwing
/// for validation failures. Unexpected exceptions are caught by the wrapper implementations.
/// </remarks>
internal interface IConfigInspectable
{
    /// <summary>
    /// Inspects a resolved value for defaults and validation.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="rawValue">The raw provider value, or <see langword="null"/> when missing.</param>
    /// <param name="resolutionState">The state determined during provider resolution.</param>
    /// <returns>The wrapper inspection result.</returns>
    ConfigWrapperInspection Inspect(string key, object? rawValue, ConfigAuditEntryState resolutionState);
}
