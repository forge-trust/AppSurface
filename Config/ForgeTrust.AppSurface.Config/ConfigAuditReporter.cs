using System.Reflection;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Config;

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
/// entries when duplicate keys are registered manually. Manual audit option assignments override wrapper-discovered
/// options per property so callers can intentionally reset a wrapper default for one key. Provider failures are
/// converted into diagnostics so one broken provider does not prevent operators from seeing the rest of the report.
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
    private readonly ConfigAuditValueTraverser _traverser;

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
                            .Select(MergeKnownEntries)
                            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                        ?? [];
        _serviceProvider = serviceProvider;
        _redactor = redactor;
        _traverser = new ConfigAuditValueTraverser(redactor);
    }

    public ConfigAuditReport GetReport(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var entries = _knownEntries.Select(entry => BuildEntry(environment, entry)).ToList();
        var diagnostics = BuildReportDiagnostics(environment).ToList();
        RemoveEntryLevelDiagnostics(diagnostics, entries);
        var discoveredKeys = BuildDiscoveredKeys(environment, diagnostics);
        return new ConfigAuditReport
        {
            Environment = environment,
            GeneratedAt = DateTimeOffset.UtcNow,
            Providers = BuildProviderList(),
            Entries = entries,
            DiscoveredKeys = discoveredKeys,
            Diagnostics = diagnostics,
            Redaction = _redactor.CreatePolicy()
        };
    }

    private IReadOnlyList<ConfigAuditDiscoveredKey> BuildDiscoveredKeys(
        string environment,
        List<ConfigAuditDiagnostic> reportDiagnostics)
    {
        var discoveredKeys = new List<ConfigAuditDiscoveredKey>();
        foreach (var provider in new IConfigProvider[] { _environmentProvider }.Concat(_otherProviders))
        {
            if (provider is not IConfigAuditKeyEnumerator keyEnumerator)
            {
                continue;
            }

            IReadOnlyList<ConfigAuditProviderDiscoveredKey> providerKeys;
            try
            {
                providerKeys = keyEnumerator.EnumerateKeys(environment);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException)
            {
                reportDiagnostics.Add(CreateProviderExceptionDiagnostic(
                    provider,
                    "config-provider-enumerate-keys-threw",
                    key: null,
                    configPath: null,
                    ex));
                continue;
            }

            foreach (var providerKey in providerKeys)
            {
                var classification = ClassifyDiscoveredKey(providerKey.Key);
                var entrySensitivity = GetDiscoveredKeyEntrySensitivity(providerKey.Key);
                var redacted = _redactor.FormatValue(
                    providerKey.Key,
                    providerKey.RawValue,
                    providerKey.Sources,
                    entrySensitivity);
                discoveredKeys.Add(new ConfigAuditDiscoveredKey
                {
                    Key = providerKey.Key,
                    Classification = classification,
                    DisplayValue = redacted.DisplayValue,
                    IsRedacted = redacted.IsRedacted,
                    Sources = providerKey.Sources,
                    Diagnostics = providerKey.Diagnostics
                });
            }
        }

        return discoveredKeys
            .OrderBy(key => key.Classification)
            .ThenBy(key => key.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ConfigAuditSensitivity GetDiscoveredKeyEntrySensitivity(string key)
    {
        var exact = _knownEntries.FirstOrDefault(
            knownEntry => string.Equals(knownEntry.Key, key, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact.OptionsSnapshot.Sensitivity;
        }

        return _knownEntries
            .Where(knownEntry => key.StartsWith($"{knownEntry.Key}.", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(knownEntry => knownEntry.Key.Length)
            .Select(knownEntry => knownEntry.OptionsSnapshot.Sensitivity)
            .FirstOrDefault();
    }

    private ConfigAuditDiscoveredKeyClassification ClassifyDiscoveredKey(string key)
    {
        if (_knownEntries.Any(knownEntry => string.Equals(knownEntry.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return ConfigAuditDiscoveredKeyClassification.Known;
        }

        if (_knownEntries.Any(knownEntry => key.StartsWith($"{knownEntry.Key}.", StringComparison.OrdinalIgnoreCase)))
        {
            return ConfigAuditDiscoveredKeyClassification.KnownDescendant;
        }

        return ConfigAuditDiscoveredKeyClassification.Unknown;
    }

    private static void RemoveEntryLevelDiagnostics(
        List<ConfigAuditDiagnostic> reportDiagnostics,
        IReadOnlyList<ConfigAuditEntry> entries)
    {
        var entryDiagnostics = entries.SelectMany(FlattenEntryDiagnostics).ToList();
        reportDiagnostics.RemoveAll(reportDiagnostic =>
            entryDiagnostics.Any(entryDiagnostic => IsSameDiagnostic(entryDiagnostic, reportDiagnostic)));
    }

    private static IEnumerable<ConfigAuditDiagnostic> FlattenEntryDiagnostics(ConfigAuditEntry entry)
    {
        foreach (var diagnostic in entry.Diagnostics)
        {
            yield return diagnostic;
        }

        foreach (var child in entry.Children)
        {
            foreach (var diagnostic in FlattenEntryDiagnostics(child))
            {
                yield return diagnostic;
            }
        }
    }

    private static bool IsSameDiagnostic(ConfigAuditDiagnostic left, ConfigAuditDiagnostic right) =>
        left.Severity == right.Severity
        && string.Equals(left.Code, right.Code, StringComparison.Ordinal)
        && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
        && string.Equals(left.ConfigPath, right.ConfigPath, StringComparison.Ordinal)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

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
        var traversalSources = inspection.DefaultSource != null || resolution.AuditSources.Count == 0
            ? sources
            : resolution.AuditSources;
        var optionsDiagnostics = knownEntry.OptionsSnapshot.Validate(knownEntry.Key);
        var options = optionsDiagnostics.Count == 0 ? knownEntry.OptionsSnapshot : knownEntry.OptionsSnapshot.Normalize();
        var traversal = _traverser.BuildChildren(
            ConfigAuditPath.Root(knownEntry.Key),
            rawValue,
            traversalSources,
            options,
            new HashSet<object>(ReferenceEqualityComparer.Instance),
            new ConfigAuditDictionaryLabelSet());
        var diagnostics = resolution.Diagnostics
            .Concat(inspection.Diagnostics)
            .Concat(optionsDiagnostics)
            .Concat(traversal.Diagnostics)
            .ToList();
        if (traversal.Children.Any(ConfigAuditEntryStateHelpers.IsPartiallyResolved)
            && state == ConfigAuditEntryState.Resolved)
        {
            state = ConfigAuditEntryState.PartiallyResolved;
        }

        var redacted = _redactor.FormatValue(knownEntry.Key, rawValue, sources, options.Sensitivity);
        return new ConfigAuditEntry
        {
            Key = knownEntry.Key,
            DeclaredType = knownEntry.ValueType.FullName,
            State = state,
            DisplayValue = redacted.DisplayValue,
            IsRedacted = redacted.IsRedacted,
            Sources = sources,
            Children = traversal.Children,
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
            ConfigPatchDiagnosticResult? patch = null;
            try
            {
                patch = patcher.TracePatch(environment, knownEntry.Key, providerResolution.Value, knownEntry.ValueType);
            }
            catch (Exception ex)
            {
                diagnostics.Add(CreateProviderExceptionDiagnostic(
                    _environmentProvider,
                    "config-provider-patch-threw",
                    knownEntry.Key,
                    knownEntry.Key,
                    ex));
            }

            if (patch != null)
            {
                diagnostics.AddRange(patch.Diagnostics);
                if (patch.Patched)
                {
                    var sourceRecords = providerResolution.Sources.Concat(patch.Sources).ToList();
                    return new ConfigValueResolution(
                        knownEntry.Key,
                        ConfigAuditEntryState.PartiallyResolved,
                        patch.Value,
                        sourceRecords,
                        diagnostics)
                    {
                        AuditSources = providerResolution.AuditSources.Concat(patch.Sources).ToList()
                    };
                }
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
                        Message = $"Could not create config wrapper {knownEntry.ConfigType.Name}; constructor threw {ex.GetType().Name}."
                    }
                ]);
        }

        if (wrapper is not IConfigInspectable inspectable)
        {
            return new ConfigWrapperInspection(resolution.Value, resolution.State, null, []);
        }

        return inspectable.Inspect(knownEntry.Key, resolution.Value, resolution.State);
    }

    private static ConfigAuditKnownEntry MergeKnownEntries(IGrouping<string, ConfigAuditKnownEntry> group)
    {
        var selected = group.OrderBy(entry => entry.ConfigType == null ? 1 : 0).First();
        var mergedOptions = new ConfigAuditEntryOptions(selected.OptionsSnapshot);
        foreach (var entry in group.Where(entry => entry.ConfigType == null))
        {
            mergedOptions = mergedOptions.ApplyAssignedOverrides(entry.OptionsSnapshot);
        }

        return selected.WithOptions(mergedOptions);
    }

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
            Message = $"Configuration provider {provider.Name} threw {ex.GetType().Name}."
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
    public IReadOnlyList<ConfigAuditSourceRecord> AuditSources { get; init; } = Sources;

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
/// Allows providers to expose their effective keys for audit reporting.
/// </summary>
/// <remarks>
/// This contract is internal in v1 so provider-specific enumeration semantics can settle before AppSurface exposes a
/// public extension API. Implementations should enumerate the provider's effective view for the requested environment
/// and return diagnostics instead of throwing for expected source problems.
/// </remarks>
internal interface IConfigAuditKeyEnumerator
{
    /// <summary>
    /// Enumerates effective provider-discovered configuration keys for <paramref name="environment"/>.
    /// </summary>
    /// <param name="environment">The environment being audited.</param>
    /// <returns>Discovered provider keys with redaction-ready scalar values and source metadata.</returns>
    IReadOnlyList<ConfigAuditProviderDiscoveredKey> EnumerateKeys(string environment);
}

/// <summary>
/// Describes one provider-discovered key before public classification and redaction.
/// </summary>
/// <param name="Key">The discovered configuration key.</param>
/// <param name="RawValue">
/// The scalar CLR value used for display redaction, or <see langword="null"/> for object/array parents.
/// </param>
/// <param name="ValueKind">The provider value shape.</param>
/// <param name="Sources">Sources associated with the effective key.</param>
/// <param name="Diagnostics">Diagnostics specific to this discovered key.</param>
internal sealed record ConfigAuditProviderDiscoveredKey(
    string Key,
    object? RawValue,
    ConfigAuditDiscoveredValueKind ValueKind,
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

/// <summary>
/// Identifies the provider value shape used while building discovered-key reports.
/// </summary>
internal enum ConfigAuditDiscoveredValueKind
{
    /// <summary>A scalar JSON value.</summary>
    Scalar = 0,

    /// <summary>A JSON object parent.</summary>
    Object = 1,

    /// <summary>A JSON array parent.</summary>
    Array = 2
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
