using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    private readonly ConfigAuditDictionaryKeyCorrelationOptions _correlationOptions;
    private readonly ConfigAuditValueTraverser _traverser;

    public ConfigAuditReporter(
        IEnvironmentConfigProvider environmentProvider,
        IEnumerable<IConfigProvider>? providers,
        IEnumerable<ConfigAuditKnownEntry>? knownEntries,
        IServiceProvider serviceProvider,
        ConfigAuditRedactor redactor,
        IOptions<ConfigAuditDictionaryKeyCorrelationOptions> correlationOptions)
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
        _correlationOptions = correlationOptions.Value;
        _traverser = new ConfigAuditValueTraverser(redactor);
    }

    public ConfigAuditReport GetReport(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var entries = _knownEntries.Select(entry => BuildEntry(environment, entry)).ToList();
        var dictionaryKeyCorrelationRequested = _knownEntries.Any(entry =>
            GetEffectiveOptions(entry, out _).DictionaryKeyCorrelationMode == ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac);
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
            Redaction = _redactor.CreatePolicy(_correlationOptions, dictionaryKeyCorrelationRequested)
        };
    }

    private IReadOnlyList<ConfigAuditDiscoveredKey> BuildDiscoveredKeys(
        string environment,
        List<ConfigAuditDiagnostic> reportDiagnostics)
    {
        var discoveredKeys = new List<ConfigAuditDiscoveredKey>();
        foreach (var provider in new IConfigProvider[] { _environmentProvider }.Concat(_otherProviders))
        {
            if (provider is IConfigAuditKeyEnumerator keyEnumerator)
            {
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

                AddDiscoveredKeys(providerKeys);
                continue;
            }

            if (provider is not IConfigProviderAuditKeyEnumerator publicKeyEnumerator)
            {
                continue;
            }

            IReadOnlyList<ConfigProviderAuditDiscoveredKey> publicProviderKeys;
            try
            {
                publicProviderKeys = publicKeyEnumerator.EnumerateKeys(environment);
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

            AddDiscoveredKeys(publicProviderKeys.Select(providerKey => new ConfigAuditProviderDiscoveredKey(
                providerKey.Key,
                providerKey.RawValue,
                providerKey.ValueKind,
                providerKey.Sources,
                providerKey.Diagnostics)).ToList());
        }

        void AddDiscoveredKeys(IReadOnlyList<ConfigAuditProviderDiscoveredKey> providerKeys)
        {
            foreach (var providerKey in providerKeys)
            {
                var classification = ClassifyDiscoveredKey(providerKey.Key);
                var entrySensitivity = GetDiscoveredKeyEntrySensitivity(providerKey.Key);
                var redacted = _redactor.FormatValue(
                    providerKey.Key,
                    providerKey.RawValue,
                    providerKey.Sources,
                    entrySensitivity);
                var display = ResolveDiscoveredValueDisplay(providerKey, classification, redacted);
                discoveredKeys.Add(new ConfigAuditDiscoveredKey
                {
                    Key = providerKey.Key,
                    Classification = classification,
                    DisplayValue = display.DisplayValue,
                    IsRedacted = display.IsRedacted,
                    ValueDisplayState = display.DisplayState,
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
        ConfigAuditSensitivity? exactSensitivity = null;
        ConfigAuditSensitivity? nearestSpecifiedParentSensitivity = null;
        var nearestSpecifiedParentLength = -1;
        var hasSensitiveEntryOrAncestor = false;
        foreach (var knownEntry in _knownEntries)
        {
            var sensitivity = knownEntry.OptionsSnapshot.Sensitivity;
            var normalizedSensitivity = ConfigAuditEntryOptions.NormalizeSensitivity(sensitivity);
            if (string.Equals(knownEntry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (normalizedSensitivity == ConfigAuditSensitivity.Sensitive)
                {
                    hasSensitiveEntryOrAncestor = true;
                }

                if (normalizedSensitivity != ConfigAuditSensitivity.Unknown)
                {
                    exactSensitivity = normalizedSensitivity;
                }

                continue;
            }

            var isDescendant = IsKnownDescendantKey(key, knownEntry.Key);
            if (isDescendant
                && normalizedSensitivity != ConfigAuditSensitivity.Unknown
                && knownEntry.Key.Length > nearestSpecifiedParentLength)
            {
                nearestSpecifiedParentSensitivity = normalizedSensitivity;
                nearestSpecifiedParentLength = knownEntry.Key.Length;
            }

            if (isDescendant && normalizedSensitivity == ConfigAuditSensitivity.Sensitive)
            {
                hasSensitiveEntryOrAncestor = true;
            }
        }

        if (hasSensitiveEntryOrAncestor)
        {
            return ConfigAuditSensitivity.Sensitive;
        }

        return nearestSpecifiedParentSensitivity
            ?? exactSensitivity
            ?? ConfigAuditSensitivity.Unknown;
    }

    private static DiscoveredValueDisplay ResolveDiscoveredValueDisplay(
        ConfigAuditProviderDiscoveredKey providerKey,
        ConfigAuditDiscoveredKeyClassification classification,
        RedactedValue redacted)
    {
        if (redacted.IsRedacted)
        {
            return new DiscoveredValueDisplay(
                redacted.DisplayValue,
                IsRedacted: true,
                ConfigAuditDiscoveredValueDisplayState.Redacted);
        }

        if (providerKey.ValueKind != ConfigAuditDiscoveredValueKind.Scalar)
        {
            return new DiscoveredValueDisplay(
                DisplayValue: null,
                IsRedacted: false,
                ConfigAuditDiscoveredValueDisplayState.OmittedComplex);
        }

        if (classification == ConfigAuditDiscoveredKeyClassification.Known && redacted.DisplayValue != null)
        {
            return new DiscoveredValueDisplay(
                redacted.DisplayValue,
                IsRedacted: false,
                ConfigAuditDiscoveredValueDisplayState.Shown);
        }

        return new DiscoveredValueDisplay(
            DisplayValue: null,
            IsRedacted: false,
            ConfigAuditDiscoveredValueDisplayState.OmittedInventory);
    }

    private ConfigAuditDiscoveredKeyClassification ClassifyDiscoveredKey(string key)
    {
        if (_knownEntries.Any(knownEntry => string.Equals(knownEntry.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return ConfigAuditDiscoveredKeyClassification.Known;
        }

        if (_knownEntries.Any(knownEntry => IsKnownDescendantKey(key, knownEntry.Key)))
        {
            return ConfigAuditDiscoveredKeyClassification.KnownDescendant;
        }

        return ConfigAuditDiscoveredKeyClassification.Unknown;
    }

    private static bool IsKnownDescendantKey(string key, string knownKey) =>
        key.Length > knownKey.Length
        && key[knownKey.Length] == '.'
        && key.StartsWith(knownKey, StringComparison.OrdinalIgnoreCase);

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
            if (provider is IConfigDiagnosticProvider diagnosticProvider)
            {
                try
                {
                    diagnostics.AddRange(diagnosticProvider.GetReportDiagnostics(environment));
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null && !IsRecoverableProviderException(ex.InnerException))
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
                catch (Exception ex) when (IsRecoverableProviderException(ex))
                {
                    diagnostics.Add(CreateProviderExceptionDiagnostic(
                        provider,
                        "config-provider-diagnostics-threw",
                        key: null,
                        configPath: null,
                        ex));
                }

                continue;
            }

            if (provider is not IConfigProviderAuditDiagnostics publicDiagnosticProvider)
            {
                continue;
            }

            try
            {
                diagnostics.AddRange(publicDiagnosticProvider.GetReportDiagnostics(environment));
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null && !IsRecoverableProviderException(ex.InnerException))
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (Exception ex) when (IsRecoverableProviderException(ex))
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

    private static bool IsRecoverableProviderException(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

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
        var options = GetEffectiveOptions(knownEntry, out var optionsDiagnostics);
        var correlation = options.DictionaryKeyCorrelationMode == ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
            ? new ConfigAuditDictionaryKeyCorrelator(_correlationOptions).CreateContext(environment, knownEntry.Key)
            : ConfigAuditDictionaryKeyCorrelationContext.Unavailable("dictionary key correlation was not requested");
        var traversal = _traverser.BuildChildren(
            ConfigAuditPath.Root(knownEntry.Key),
            rawValue,
            traversalSources,
            resolution.AuditFacts,
            options,
            new HashSet<object>(ReferenceEqualityComparer.Instance),
            new ConfigAuditDictionaryLabelSet(),
            correlation);
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

    private static ConfigAuditEntryOptions GetEffectiveOptions(
        ConfigAuditKnownEntry knownEntry,
        out IReadOnlyList<ConfigAuditDiagnostic> diagnostics)
    {
        diagnostics = knownEntry.OptionsSnapshot.Validate(knownEntry.Key);
        return diagnostics.Count == 0 ? knownEntry.OptionsSnapshot : knownEntry.OptionsSnapshot.Normalize();
    }

    private ConfigValueResolution Resolve(string environment, ConfigAuditKnownEntry knownEntry)
    {
        var envResolution = ResolveProvider(_environmentProvider, environment, knownEntry, ConfigAuditSourceRole.Override);
        if (envResolution.State == ConfigAuditEntryState.Resolved)
        {
            var baseResolution = ResolveBaseProviders(environment, knownEntry, out var baseDiagnostics, out var invalidBaseResolution);
            var provenanceBaseResolution = SelectProvenanceBaseResolution(baseResolution, invalidBaseResolution);
            return envResolution with
            {
                Diagnostics = envResolution.Diagnostics.Concat(baseDiagnostics).ToList(),
                AuditFacts = BuildEnvironmentOverrideFacts(knownEntry.Key, envResolution, provenanceBaseResolution)
            };
        }

        var diagnostics = envResolution.Diagnostics.ToList();
        var providerResolution = ResolveBaseProviders(environment, knownEntry, out var providerDiagnostics, out var invalidProviderResolution);
        diagnostics.AddRange(providerDiagnostics);

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
                        AuditSources = providerResolution.AuditSources.Concat(patch.Sources).ToList(),
                        AuditFacts = BuildPatchFactContext(patch.Facts, invalidProviderResolution)
                    };
                }
            }
        }

        var resolution = providerResolution.State == ConfigAuditEntryState.Resolved
            ? providerResolution
            : invalidProviderResolution ?? providerResolution;
        return resolution with { Diagnostics = diagnostics };
    }

    private ConfigValueResolution ResolveBaseProviders(
        string environment,
        ConfigAuditKnownEntry knownEntry,
        out IReadOnlyList<ConfigAuditDiagnostic> diagnostics,
        out ConfigValueResolution? invalidProviderResolution)
    {
        var collectedDiagnostics = new List<ConfigAuditDiagnostic>();
        var providerResolution = ConfigValueResolution.Missing(knownEntry.Key);
        invalidProviderResolution = null;

        foreach (var provider in _otherProviders)
        {
            var current = ResolveProvider(provider, environment, knownEntry, ConfigAuditSourceRole.Base);
            collectedDiagnostics.AddRange(current.Diagnostics);
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

        diagnostics = collectedDiagnostics;
        return providerResolution;
    }

    private static ConfigValueResolution SelectProvenanceBaseResolution(
        ConfigValueResolution baseResolution,
        ConfigValueResolution? invalidBaseResolution) =>
        invalidBaseResolution ?? baseResolution;

    private static ConfigAuditFactContext BuildPatchFactContext(
        IReadOnlyList<ConfigPatchProvenanceFact> facts,
        ConfigValueResolution? invalidProviderResolution)
    {
        if (invalidProviderResolution == null)
        {
            return new ConfigAuditFactContext(facts);
        }

        return new ConfigAuditFactContext(
            facts.Select(fact => fact with { PriorPresence = ConfigAuditPriorPresence.Unknown }));
    }

    private static ConfigAuditFactContext BuildEnvironmentOverrideFacts(
        string rootKey,
        ConfigValueResolution envResolution,
        ConfigValueResolution baseResolution)
    {
        var facts = envResolution.AuditSources
            .Where(source => source.Kind == ConfigAuditSourceKind.EnvironmentVariable
                             && source.ConfigPath != null
                             && IsCollectionElementPath(source.ConfigPath))
            .Select(source => new ConfigPatchProvenanceFact(
                source.ConfigPath!,
                source,
                ConfigPatchProvenanceAction.EnvironmentOverrideElement,
                ClassifyBasePresence(rootKey, source.ConfigPath!, baseResolution)))
            .ToList();

        return new ConfigAuditFactContext(facts);
    }

    private static bool IsCollectionElementPath(string configPath)
    {
        var lastSeparator = configPath.LastIndexOf('.');
        if (lastSeparator < 0 || lastSeparator == configPath.Length - 1)
        {
            return false;
        }

        return int.TryParse(configPath[(lastSeparator + 1)..], out _);
    }

    private static ConfigAuditPriorPresence ClassifyBasePresence(
        string rootKey,
        string configPath,
        ConfigValueResolution baseResolution)
    {
        if (baseResolution.State == ConfigAuditEntryState.Missing)
        {
            return ConfigAuditPriorPresence.Missing;
        }

        if (baseResolution.State != ConfigAuditEntryState.Resolved || !HasUsableBaseEvidence(rootKey, configPath, baseResolution))
        {
            return ConfigAuditPriorPresence.Unknown;
        }

        return TryPathExists(rootKey, configPath, baseResolution.Value, out var exists)
            ? exists ? ConfigAuditPriorPresence.Present : ConfigAuditPriorPresence.Missing
            : ConfigAuditPriorPresence.Unknown;
    }

    private static bool HasUsableBaseEvidence(
        string rootKey,
        string configPath,
        ConfigValueResolution baseResolution) =>
        baseResolution.AuditSources.Any(source =>
            SourcePathMatches(source.ConfigPath, rootKey, configPath)
            || SourcePathMatches(source.AppliedToPath, rootKey, configPath));

    private static bool SourcePathMatches(string? sourcePath, string rootKey, string configPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        return string.Equals(sourcePath, configPath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(sourcePath, rootKey, StringComparison.OrdinalIgnoreCase)
               || configPath.StartsWith($"{sourcePath}.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryPathExists(string rootKey, string configPath, object? value, out bool exists)
    {
        exists = false;
        if (value == null)
        {
            return true;
        }

        if (!configPath.StartsWith($"{rootKey}.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        object? current = value;
        foreach (var segment in configPath[(rootKey.Length + 1)..].Split('.'))
        {
            if (current == null)
            {
                exists = false;
                return true;
            }

            if (int.TryParse(segment, out var index))
            {
                if (!TryGetIndexedValue(current, index, out current))
                {
                    exists = false;
                    return true;
                }
            }
            else if (!TryGetMemberValue(current, segment, out current))
            {
                exists = false;
                return true;
            }
        }

        exists = true;
        return true;
    }

    private static bool TryGetIndexedValue(object value, int index, out object? item)
    {
        item = null;
        if (value is Array array)
        {
            if (array.Rank != 1 || index < 0 || index >= array.Length)
            {
                return false;
            }

            item = array.GetValue(index);
            return true;
        }

        if (value is IList list)
        {
            if (index < 0 || index >= list.Count)
            {
                return false;
            }

            item = list[index];
            return true;
        }

        if (TryCreateReadOnlyListAccessor(value, out var accessor))
        {
            if (index < 0 || index >= accessor.Count)
            {
                return false;
            }

            item = accessor.GetValue(index);
            return true;
        }

        return false;
    }

    private static bool TryGetMemberValue(object value, string memberName, out object? memberValue)
    {
        memberValue = null;
        var type = value.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property is { GetMethod: not null } && property.GetIndexParameters().Length == 0)
        {
            memberValue = property.GetValue(value);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (field == null || field.IsInitOnly)
        {
            return false;
        }

        memberValue = field.GetValue(value);
        return true;
    }

    [ExcludeFromCodeCoverage(Justification = "Defensive reflection helper; public traversal tests cover supported IReadOnlyList shapes.")]
    private static bool TryCreateReadOnlyListAccessor(object value, out ReadOnlyListAccessor accessor)
    {
        var interfaceType = value.GetType()
            .GetInterfaces()
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
        if (interfaceType == null)
        {
            accessor = default;
            return false;
        }

        var countProperty = interfaceType.GetInterfaces()
            .Append(interfaceType)
            .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))
            ?.GetProperty(nameof(IReadOnlyCollection<object>.Count))!;
        var itemProperty = interfaceType.GetProperty("Item")!;

        accessor = new ReadOnlyListAccessor(
            (int)(countProperty.GetValue(value) ?? 0),
            index => itemProperty.GetValue(value, [index]));
        return true;
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
            catch (TargetInvocationException ex) when (ex.InnerException != null && !IsRecoverableProviderException(ex.InnerException))
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (Exception ex) when (IsRecoverableProviderException(ex))
            {
                return CreateProviderExceptionResolution(
                    provider,
                    knownEntry.Key,
                    role,
                    "config-provider-resolve-threw",
                ex);
            }
        }

        if (provider is IConfigProviderAuditDiagnostics publicDiagnosticProvider)
        {
            try
            {
                var resolution = publicDiagnosticProvider.ResolveForAudit(
                    environment,
                    knownEntry.Key,
                    knownEntry.ValueType,
                    role);

                if (!string.Equals(resolution.Key, knownEntry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return CreateProviderExceptionResolution(
                        provider,
                        knownEntry.Key,
                        role,
                        "config-provider-resolve-threw",
                        new InvalidOperationException("ResolveForAudit returned a mismatched key."));
                }

                return new ConfigValueResolution(
                    resolution.Key,
                    resolution.State,
                    resolution.Value,
                    resolution.Sources,
                    resolution.Diagnostics);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null && !IsRecoverableProviderException(ex.InnerException))
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            catch (Exception ex) when (IsRecoverableProviderException(ex))
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
    /// <summary>
    /// Gets the sources used for child-entry source selection.
    /// </summary>
    public IReadOnlyList<ConfigAuditSourceRecord> AuditSources { get; init; } = Sources;

    /// <summary>
    /// Gets internal provenance facts used to attach proof-limited diagnostics while traversing child entries.
    /// </summary>
    public ConfigAuditFactContext AuditFacts { get; init; } = ConfigAuditFactContext.Empty;

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
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets internal patch-time provenance facts for child audit diagnostics.
    /// </summary>
    public IReadOnlyList<ConfigPatchProvenanceFact> Facts { get; init; } = [];
}

/// <summary>
/// Describes one environment patch fact before it is converted into public audit diagnostics.
/// </summary>
/// <param name="ConfigPath">The source-style config path affected by the environment source.</param>
/// <param name="Source">The environment source that supplied the value.</param>
/// <param name="Action">The patch action that produced the final value.</param>
/// <param name="PriorPresence">The lower-priority/provider presence evidence for the same config path.</param>
internal sealed record ConfigPatchProvenanceFact(
    string ConfigPath,
    ConfigAuditSourceRecord Source,
    ConfigPatchProvenanceAction Action,
    ConfigAuditPriorPresence PriorPresence);

/// <summary>
/// Identifies how an environment source changed the final audited value.
/// </summary>
internal enum ConfigPatchProvenanceAction
{
    /// <summary>An environment variable set a scalar or object member.</summary>
    SetMember = 0,

    /// <summary>An environment-indexed collection replaced a settable collection member.</summary>
    ReplacedCollection = 1,

    /// <summary>An environment-indexed collection patched an existing getter-only mutable collection.</summary>
    PatchedExistingCollection = 2,

    /// <summary>An environment-indexed collection supplied an element for a root override value.</summary>
    EnvironmentOverrideElement = 3
}

/// <summary>
/// Describes whether lower-priority provider evidence proved a path existed before the environment override.
/// </summary>
internal enum ConfigAuditPriorPresence
{
    /// <summary>Provider evidence was pathless, generic, invalid, or otherwise unable to prove presence.</summary>
    Unknown = 0,

    /// <summary>Provider evidence proved the path was absent.</summary>
    Missing = 1,

    /// <summary>Provider evidence proved the path was present.</summary>
    Present = 2
}

/// <summary>
/// Indexes internal audit provenance facts by source-style config path for traversal.
/// </summary>
internal sealed class ConfigAuditFactContext
{
    private readonly Dictionary<string, IReadOnlyList<ConfigPatchProvenanceFact>> _factsByPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAuditFactContext"/> class.
    /// </summary>
    /// <param name="facts">
    /// Provenance facts emitted by provider patching or environment overrides. Each fact must carry a source-style
    /// <see cref="ConfigPatchProvenanceFact.ConfigPath"/>, the source that created the fact, the patch action, and the
    /// proven prior presence for that path; optional source metadata can stay unset when the provider could not prove it.
    /// Facts are grouped case-insensitively by config path and copied into per-path <see cref="IReadOnlyList{T}"/> buckets,
    /// so constructed contexts are immutable and safe to share between traversal calls as long as the supplied fact records
    /// are treated as immutable.
    /// </param>
    public ConfigAuditFactContext(IEnumerable<ConfigPatchProvenanceFact> facts)
    {
        _factsByPath = facts
            .GroupBy(fact => fact.ConfigPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ConfigPatchProvenanceFact>)group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the shared immutable empty fact context for callers that have no provenance facts to report.
    /// </summary>
    /// <remarks>
    /// Use this instance to avoid allocations when traversal should emit no fact-derived child diagnostics. The context
    /// contains no path entries and is never modified after construction.
    /// </remarks>
    public static ConfigAuditFactContext Empty { get; } = new([]);

    /// <summary>
    /// Gets all facts associated with <paramref name="configPath"/>.
    /// </summary>
    /// <param name="configPath">The source-style config path to inspect.</param>
    /// <returns>Facts for the path, or an empty list when none are known.</returns>
    public IReadOnlyList<ConfigPatchProvenanceFact> GetFacts(string configPath) =>
        _factsByPath.TryGetValue(configPath, out var facts) ? facts : [];
}

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
/// Carries the discovered-value display decision used while projecting provider-discovered keys.
/// </summary>
/// <remarks>
/// This helper keeps the formatted value, redaction flag, and
/// <see cref="ConfigAuditDiscoveredValueDisplayState"/> synchronized before they enter
/// <see cref="ConfigAuditDiscoveredKey"/>. <see cref="DisplayValue"/> may be <see langword="null"/> for omitted
/// states, so callers must branch on <see cref="DisplayState"/> instead of inferring intent from nullability.
/// Pitfall: a null display value is not an error, and renderers or logs must preserve the redaction and display-state
/// decision ordering rather than recomputing value visibility.
/// </remarks>
/// <param name="DisplayValue">The formatted scalar value, or <see langword="null"/> when the value is omitted.</param>
/// <param name="IsRedacted">Whether the formatted value came from a redaction decision.</param>
/// <param name="DisplayState">Why the value is shown, redacted, or omitted.</param>
internal sealed record DiscoveredValueDisplay(
    string? DisplayValue,
    bool IsRedacted,
    ConfigAuditDiscoveredValueDisplayState DisplayState);

/// <summary>
/// Identifies the provider value shape used while building discovered-key reports.
/// </summary>
/// <remarks>
/// Values are explicit and append-only so external provider audit enumerators can report inventory shape without exposing
/// provider-private parser details.
/// </remarks>
public enum ConfigAuditDiscoveredValueKind
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
