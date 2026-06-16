using System.Globalization;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Compares two existing <see cref="ConfigAuditReport"/> instances without resolving providers.
/// </summary>
/// <remarks>
/// The differ is intentionally pure: it does not start hosts, read files, call providers, or depend on any command
/// framework. Reports should already be sanitized by <see cref="IConfigAuditReporter"/> or by a trusted captured-report
/// workflow. Redacted and omitted values remain uncertain; the diff exposes that uncertainty through
/// <see cref="ConfigAuditDiffItem.ValueEvidence"/> instead of implying raw equality.
/// </remarks>
public sealed class ConfigAuditReportDiffer
{
    private const string SameHostEvidenceCode = "config-diff-same-host-evidence";
    private const string DuplicateEvidenceCode = "config-diff-duplicate-evidence";
    private const string ManualEvidenceCode = "config-diff-manual-report-evidence";
    private const string RedactedDictionaryCode = "config-diff-redacted-dictionary-key-uncomparable";

    /// <summary>
    /// Compares two audit reports using default options.
    /// </summary>
    /// <param name="baseline">The baseline report, often staging or the current known-good snapshot.</param>
    /// <param name="target">The target report, often production or the candidate snapshot.</param>
    /// <returns>A typed diff report.</returns>
    public ConfigAuditDiffReport Compare(ConfigAuditReport baseline, ConfigAuditReport target) =>
        Compare(baseline, target, null);

    /// <summary>
    /// Compares two audit reports using the supplied options.
    /// </summary>
    /// <param name="baseline">The baseline report, often staging or the current known-good snapshot.</param>
    /// <param name="target">The target report, often production or the candidate snapshot.</param>
    /// <param name="options">Comparison options. Defaults are used when this value is <see langword="null"/>.</param>
    /// <returns>A typed diff report whose item ordering is deterministic.</returns>
    public ConfigAuditDiffReport Compare(
        ConfigAuditReport baseline,
        ConfigAuditReport target,
        ConfigAuditDiffOptions? options)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(target);

        options ??= new ConfigAuditDiffOptions();
        var diagnostics = new List<ConfigAuditComparisonDiagnostic>();
        var items = new List<ConfigAuditDiffItem>();

        ValidateReportShape(baseline, "baseline", diagnostics);
        ValidateReportShape(target, "target", diagnostics);

        if (options.EvidenceMode == ConfigAuditDiffEvidenceMode.SameHostNamedEnvironment)
        {
            diagnostics.Add(new ConfigAuditComparisonDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = SameHostEvidenceCode,
                Message = "Same-host named-environment comparison is useful triage evidence, but it does not prove deployment parity. Prefer captured snapshots from each host for support decisions."
            });
        }

        var redactionPolicyChanged = !StringEquals(RedactionSignature(baseline.Redaction), RedactionSignature(target.Redaction));
        if (redactionPolicyChanged)
        {
            items.Add(new ConfigAuditDiffItem
            {
                Kind = ConfigAuditDiffItemKind.RedactionPolicy,
                Status = ConfigAuditDiffItemStatus.Changed,
                Significance = ConfigAuditDiffSignificance.NeedsAttention,
                Key = "Redaction",
                Description = "Redaction policy metadata differs; value comparisons that involve redacted or omitted data need review.",
                BaselineDisplayValue = RedactionSignature(baseline.Redaction),
                TargetDisplayValue = RedactionSignature(target.Redaction),
                ValueEvidence = ConfigAuditDiffValueEvidence.RedactionPolicyMismatch
            });
        }

        CompareProviders(baseline, target, items, diagnostics);
        CompareEntries(baseline, target, redactionPolicyChanged, items, diagnostics);
        CompareDiscoveredKeys(baseline, target, redactionPolicyChanged, items, diagnostics);
        CompareDiagnostics(baseline, target, items, diagnostics);

        var sortedItems = items
            .OrderByDescending(item => item.Significance)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Status)
            .ToList();

        var summary = CreateSummary(sortedItems, diagnostics.Count);
        if (!options.IncludeUnchangedItems)
        {
            sortedItems = sortedItems
                .Where(item => item.Status != ConfigAuditDiffItemStatus.Unchanged)
                .ToList();
        }

        return new ConfigAuditDiffReport
        {
            BaselineEnvironment = baseline.Environment,
            TargetEnvironment = target.Environment,
            GeneratedAt = DateTimeOffset.UtcNow,
            EvidenceMode = options.EvidenceMode,
            SourceDetail = options.SourceDetail,
            Summary = summary,
            Diagnostics = diagnostics
                .OrderByDescending(diagnostic => diagnostic.Severity)
                .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Key, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Items = sortedItems
        };
    }

    private static void CompareProviders(
        ConfigAuditReport baseline,
        ConfigAuditReport target,
        List<ConfigAuditDiffItem> items,
        List<ConfigAuditComparisonDiagnostic> diagnostics)
    {
        CompareBuckets(
            baseline.Providers,
            target.Providers,
            provider => $"provider:{provider.Name}:{provider.IsOverride}",
            ProviderSortKey,
            (provider, status) => CreateProviderItem(provider, null, status),
            (provider, status) => CreateProviderItem(null, provider, status),
            (baselineProvider, targetProvider) => CreateProviderItem(baselineProvider, targetProvider, GetStatus(ProviderSignature(baselineProvider), ProviderSignature(targetProvider))),
            items,
            diagnostics,
            ConfigAuditDiffItemKind.Provider);
    }

    private static void CompareEntries(
        ConfigAuditReport baseline,
        ConfigAuditReport target,
        bool redactionPolicyChanged,
        List<ConfigAuditDiffItem> items,
        List<ConfigAuditComparisonDiagnostic> diagnostics)
    {
        var baselineEntries = FlattenEntries(baseline.Entries).ToList();
        var targetEntries = FlattenEntries(target.Entries).ToList();

        CompareBuckets(
            baselineEntries,
            targetEntries,
            GetEntryCorrelationKey,
            EntrySortKey,
            (entry, status) => CreateEntryItem(entry, null, status, redactionPolicyChanged),
            (entry, status) => CreateEntryItem(null, entry, status, redactionPolicyChanged),
            (baselineEntry, targetEntry) => CreateEntryItem(
                baselineEntry,
                targetEntry,
                GetEntryStatus(baselineEntry, targetEntry),
                redactionPolicyChanged),
            items,
            diagnostics,
            ConfigAuditDiffItemKind.KnownEntry);
    }

    private static void CompareDiscoveredKeys(
        ConfigAuditReport baseline,
        ConfigAuditReport target,
        bool redactionPolicyChanged,
        List<ConfigAuditDiffItem> items,
        List<ConfigAuditComparisonDiagnostic> diagnostics)
    {
        CompareBuckets(
            baseline.DiscoveredKeys,
            target.DiscoveredKeys,
            key => $"discovered:{key.Key}",
            DiscoveredKeySortKey,
            (key, status) => CreateDiscoveredKeyItem(key, null, status, redactionPolicyChanged),
            (key, status) => CreateDiscoveredKeyItem(null, key, status, redactionPolicyChanged),
            (baselineKey, targetKey) => CreateDiscoveredKeyItem(
                baselineKey,
                targetKey,
                GetStatus(DiscoveredKeySignature(baselineKey), DiscoveredKeySignature(targetKey)),
                redactionPolicyChanged),
            items,
            diagnostics,
            ConfigAuditDiffItemKind.DiscoveredKey);
    }

    private static void CompareDiagnostics(
        ConfigAuditReport baseline,
        ConfigAuditReport target,
        List<ConfigAuditDiffItem> items,
        List<ConfigAuditComparisonDiagnostic> diagnostics)
    {
        CompareBuckets(
            baseline.Diagnostics,
            target.Diagnostics,
            diagnostic => $"diagnostic:{diagnostic.Severity}:{diagnostic.Code}:{diagnostic.Key}:{diagnostic.ConfigPath}",
            DiagnosticSortKey,
            (diagnostic, status) => CreateDiagnosticItem(diagnostic, null, status),
            (diagnostic, status) => CreateDiagnosticItem(null, diagnostic, status),
            (baselineDiagnostic, targetDiagnostic) => CreateDiagnosticItem(
                baselineDiagnostic,
                targetDiagnostic,
                GetStatus(DiagnosticSignature(baselineDiagnostic), DiagnosticSignature(targetDiagnostic))),
            items,
            diagnostics,
            ConfigAuditDiffItemKind.Diagnostic);
    }

    private static void CompareBuckets<T>(
        IReadOnlyList<T> baseline,
        IReadOnlyList<T> target,
        Func<T, string> getKey,
        Func<T, string> getSortKey,
        Func<T, ConfigAuditDiffItemStatus, ConfigAuditDiffItem> createBaselineOnly,
        Func<T, ConfigAuditDiffItemStatus, ConfigAuditDiffItem> createTargetOnly,
        Func<T, T, ConfigAuditDiffItem> createPaired,
        List<ConfigAuditDiffItem> items,
        List<ConfigAuditComparisonDiagnostic> diagnostics,
        ConfigAuditDiffItemKind kind)
    {
        var baselineBuckets = baseline
            .Select((item, ordinal) => new DiffInput<T>(item, ordinal))
            .GroupBy(input => getKey(input.Item), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var targetBuckets = target
            .Select((item, ordinal) => new DiffInput<T>(item, ordinal))
            .GroupBy(input => getKey(input.Item), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var keys = baselineBuckets.Keys
            .Concat(targetBuckets.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            baselineBuckets.TryGetValue(key, out var baselineGroup);
            targetBuckets.TryGetValue(key, out var targetGroup);
            baselineGroup ??= [];
            targetGroup ??= [];

            if (baselineGroup.Count > 1 || targetGroup.Count > 1)
            {
                diagnostics.Add(new ConfigAuditComparisonDiagnostic
                {
                    Severity = ConfigAuditDiagnosticSeverity.Warning,
                    Code = DuplicateEvidenceCode,
                    Key = key,
                    Message = $"Duplicate {kind} evidence was paired deterministically for '{key}'. Review each retained diff item when order matters."
                });
            }

            var orderedBaseline = baselineGroup
                .OrderBy(input => getSortKey(input.Item), StringComparer.Ordinal)
                .ThenBy(input => input.Ordinal)
                .ToList();
            var orderedTarget = targetGroup
                .OrderBy(input => getSortKey(input.Item), StringComparer.Ordinal)
                .ThenBy(input => input.Ordinal)
                .ToList();
            var pairedCount = Math.Min(orderedBaseline.Count, orderedTarget.Count);

            for (var index = 0; index < pairedCount; index++)
            {
                items.Add(createPaired(orderedBaseline[index].Item, orderedTarget[index].Item));
            }

            foreach (var removed in orderedBaseline.Skip(pairedCount))
            {
                items.Add(createBaselineOnly(removed.Item, GetMissingSideStatus(removed.Item)));
            }

            foreach (var added in orderedTarget.Skip(pairedCount))
            {
                items.Add(createTargetOnly(added.Item, GetMissingSideStatus(added.Item, targetSide: true)));
            }
        }
    }

    private static ConfigAuditDiffItemStatus GetMissingSideStatus<T>(T item, bool targetSide = false)
    {
        if (item is EntryDiffInput { IsUncomparableDictionarySubtree: true })
        {
            return ConfigAuditDiffItemStatus.Uncomparable;
        }

        return targetSide ? ConfigAuditDiffItemStatus.Added : ConfigAuditDiffItemStatus.Removed;
    }

    private static IEnumerable<EntryDiffInput> FlattenEntries(IEnumerable<ConfigAuditEntry> entries) =>
        FlattenEntries(
            entries,
            parentDisplayPath: null,
            parentCorrelationPath: null,
            parentIsUncomparableDictionarySubtree: false,
            parentUsesComparisonIdentity: false);

    private static IEnumerable<EntryDiffInput> FlattenEntries(
        IEnumerable<ConfigAuditEntry> entries,
        string? parentDisplayPath,
        string? parentCorrelationPath,
        bool parentIsUncomparableDictionarySubtree,
        bool parentUsesComparisonIdentity)
    {
        foreach (var entry in entries)
        {
            var input = CreateEntryDiffInput(
                entry,
                parentDisplayPath,
                parentCorrelationPath,
                parentIsUncomparableDictionarySubtree,
                parentUsesComparisonIdentity);
            yield return input;
            foreach (var child in FlattenEntries(
                         entry.Children,
                         entry.Key,
                         input.CorrelationPath,
                         input.IsUncomparableDictionarySubtree,
                         input.UsesComparisonIdentity))
            {
                yield return child;
            }
        }
    }

    private static EntryDiffInput CreateEntryDiffInput(
        ConfigAuditEntry entry,
        string? parentDisplayPath,
        string? parentCorrelationPath,
        bool parentIsUncomparableDictionarySubtree,
        bool parentUsesComparisonIdentity)
    {
        if (entry.Element is { Kind: ConfigAuditElementKind.DictionaryItem } element)
        {
            var fallbackParentPath = GetDictionaryParentPath(entry.Key);
            var effectiveParentCorrelationPath = parentCorrelationPath ?? fallbackParentPath;
            var hasComparisonCorrelation = !string.IsNullOrWhiteSpace(element.ComparisonKeyCorrelationId);
            var isUncomparable = parentIsUncomparableDictionarySubtree
                                 || (element.IsKeyRedacted && !hasComparisonCorrelation);
            var usesComparisonIdentity = parentUsesComparisonIdentity || hasComparisonCorrelation;
            var segment = GetDictionaryComparisonSegment(element);
            return new EntryDiffInput(
                entry,
                $"{effectiveParentCorrelationPath}{segment}",
                isUncomparable,
                usesComparisonIdentity);
        }

        if (parentCorrelationPath == null)
        {
            return new EntryDiffInput(
                entry,
                entry.Key,
                parentIsUncomparableDictionarySubtree,
                parentUsesComparisonIdentity);
        }

        var suffix = GetChildPathSuffix(parentDisplayPath, entry.Key);
        return new EntryDiffInput(
            entry,
            $"{parentCorrelationPath}{suffix}",
            parentIsUncomparableDictionarySubtree,
            parentUsesComparisonIdentity);
    }

    private static string GetDictionaryComparisonSegment(ConfigAuditElementIdentity element)
    {
        if (!string.IsNullOrWhiteSpace(element.ComparisonKeyCorrelationId))
        {
            return $"[comparison:{element.ComparisonKeyCorrelationId}]";
        }

        if (!element.IsKeyRedacted && !string.IsNullOrWhiteSpace(element.KeyLabel))
        {
            return $"[label:{element.KeyLabel}]";
        }

        return "[uncomparable-dictionary-key]";
    }

    private static string GetChildPathSuffix(string? parentDisplayPath, string key)
    {
        if (!string.IsNullOrEmpty(parentDisplayPath)
            && key.StartsWith(parentDisplayPath, StringComparison.Ordinal))
        {
            return key[parentDisplayPath.Length..];
        }

        return $"/{key}";
    }

    private static string GetEntryCorrelationKey(EntryDiffInput input)
    {
        var prefix = input.IsUncomparableDictionarySubtree
            ? "entry:dictionary:uncomparable"
            : "entry";
        return $"{prefix}:{input.CorrelationPath}";
    }

    private static ConfigAuditDiffItem CreateEntryItem(
        EntryDiffInput? baseline,
        EntryDiffInput? target,
        ConfigAuditDiffItemStatus status,
        bool redactionPolicyChanged)
    {
        var input = baseline ?? target!;
        var entry = input.Entry;
        var kind = input.IsUncomparableDictionarySubtree
            ? ConfigAuditDiffItemKind.DictionaryEntry
            : ConfigAuditDiffItemKind.KnownEntry;
        if (kind == ConfigAuditDiffItemKind.DictionaryEntry
            && (status == ConfigAuditDiffItemStatus.Unchanged || status == ConfigAuditDiffItemStatus.Changed))
        {
            status = ConfigAuditDiffItemStatus.Uncomparable;
        }

        var valueEvidence = GetEntryValueEvidence(baseline?.Entry, target?.Entry, redactionPolicyChanged);
        var significance = GetSignificance(status, kind, IsEntrySourceOnlyChange(baseline, target, status));
        var diagnostics = new List<ConfigAuditComparisonDiagnostic>();
        if (status == ConfigAuditDiffItemStatus.Uncomparable)
        {
            diagnostics.Add(new ConfigAuditComparisonDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = RedactedDictionaryCode,
                Key = entry.Key,
                Message = "Redacted dictionary key labels are report-local. Enable dictionary comparison correlation metadata to compare this item across environments."
            });
        }

        return new ConfigAuditDiffItem
        {
            Kind = kind,
            Status = status,
            Significance = significance,
            Key = entry.Key,
            Description = DescribeEntryChange(baseline, target, status, valueEvidence),
            BaselineDisplayValue = baseline?.Entry.DisplayValue,
            TargetDisplayValue = target?.Entry.DisplayValue,
            ValueEvidence = valueEvidence,
            BaselineSources = baseline?.Entry.Sources ?? [],
            TargetSources = target?.Entry.Sources ?? [],
            Diagnostics = diagnostics
        };
    }

    private static ConfigAuditDiffItem CreateDiscoveredKeyItem(
        ConfigAuditDiscoveredKey? baseline,
        ConfigAuditDiscoveredKey? target,
        ConfigAuditDiffItemStatus status,
        bool redactionPolicyChanged)
    {
        var key = baseline ?? target!;
        var valueEvidence = GetDiscoveredValueEvidence(baseline, target, redactionPolicyChanged);
        var sourceOnly = status == ConfigAuditDiffItemStatus.Changed
                         && DiscoveredKeyValueSignature(baseline) == DiscoveredKeyValueSignature(target)
                         && SourceSignature(baseline?.Sources ?? []) != SourceSignature(target?.Sources ?? []);

        return new ConfigAuditDiffItem
        {
            Kind = sourceOnly ? ConfigAuditDiffItemKind.Source : ConfigAuditDiffItemKind.DiscoveredKey,
            Status = status,
            Significance = sourceOnly ? ConfigAuditDiffSignificance.Context : GetSignificance(status, ConfigAuditDiffItemKind.DiscoveredKey),
            Key = key.Key,
            Description = DescribeDiscoveredKeyChange(baseline, target, status, valueEvidence),
            BaselineDisplayValue = baseline?.DisplayValue,
            TargetDisplayValue = target?.DisplayValue,
            ValueEvidence = valueEvidence,
            BaselineSources = baseline?.Sources ?? [],
            TargetSources = target?.Sources ?? []
        };
    }

    private static ConfigAuditDiffItem CreateProviderItem(
        ConfigAuditProvider? baseline,
        ConfigAuditProvider? target,
        ConfigAuditDiffItemStatus status)
    {
        var provider = baseline ?? target!;
        return new ConfigAuditDiffItem
        {
            Kind = ConfigAuditDiffItemKind.Provider,
            Status = status,
            Significance = GetSignificance(status, ConfigAuditDiffItemKind.Provider),
            Key = provider.Name,
            Description = status == ConfigAuditDiffItemStatus.Changed
                ? "Provider precedence, priority, or override status changed."
                : $"Provider was {status.ToString().ToLowerInvariant()}.",
            BaselineDisplayValue = baseline == null ? null : ProviderSignature(baseline),
            TargetDisplayValue = target == null ? null : ProviderSignature(target)
        };
    }

    private static ConfigAuditDiffItem CreateDiagnosticItem(
        ConfigAuditDiagnostic? baseline,
        ConfigAuditDiagnostic? target,
        ConfigAuditDiffItemStatus status)
    {
        var diagnostic = baseline ?? target!;
        var key = diagnostic.Key ?? diagnostic.ConfigPath ?? diagnostic.Code;
        return new ConfigAuditDiffItem
        {
            Kind = ConfigAuditDiffItemKind.Diagnostic,
            Status = status,
            Significance = GetDiagnosticSignificance(status, diagnostic),
            Key = key,
            Description = status == ConfigAuditDiffItemStatus.Changed
                ? "Diagnostic evidence changed."
                : $"Diagnostic was {status.ToString().ToLowerInvariant()}.",
            BaselineDisplayValue = baseline == null ? null : DiagnosticSignature(baseline),
            TargetDisplayValue = target == null ? null : DiagnosticSignature(target)
        };
    }

    private static ConfigAuditDiffItemStatus GetEntryStatus(EntryDiffInput baseline, EntryDiffInput target)
    {
        if (baseline.IsUncomparableDictionarySubtree || target.IsUncomparableDictionarySubtree)
        {
            return ConfigAuditDiffItemStatus.Uncomparable;
        }

        return GetStatus(EntrySignature(baseline), EntrySignature(target));
    }

    private static ConfigAuditDiffItemStatus GetStatus(string baselineSignature, string targetSignature) =>
        StringEquals(baselineSignature, targetSignature)
            ? ConfigAuditDiffItemStatus.Unchanged
            : ConfigAuditDiffItemStatus.Changed;

    private static ConfigAuditDiffSignificance GetSignificance(
        ConfigAuditDiffItemStatus status,
        ConfigAuditDiffItemKind kind,
        bool sourceOnly = false)
    {
        if (status == ConfigAuditDiffItemStatus.Unchanged)
        {
            return ConfigAuditDiffSignificance.Unchanged;
        }

        if (sourceOnly || kind is ConfigAuditDiffItemKind.Provider or ConfigAuditDiffItemKind.Source)
        {
            return ConfigAuditDiffSignificance.Context;
        }

        return ConfigAuditDiffSignificance.NeedsAttention;
    }

    private static ConfigAuditDiffSignificance GetDiagnosticSignificance(
        ConfigAuditDiffItemStatus status,
        ConfigAuditDiagnostic diagnostic)
    {
        if (status == ConfigAuditDiffItemStatus.Unchanged)
        {
            return ConfigAuditDiffSignificance.Unchanged;
        }

        return diagnostic.Severity == ConfigAuditDiagnosticSeverity.Info
            ? ConfigAuditDiffSignificance.Context
            : ConfigAuditDiffSignificance.NeedsAttention;
    }

    private static bool IsEntrySourceOnlyChange(
        EntryDiffInput? baseline,
        EntryDiffInput? target,
        ConfigAuditDiffItemStatus status) =>
        status == ConfigAuditDiffItemStatus.Changed
        && baseline != null
        && target != null
        && EntryValueSignature(baseline) == EntryValueSignature(target)
        && DiagnosticSignature(baseline.Entry.Diagnostics) == DiagnosticSignature(target.Entry.Diagnostics)
        && baseline.Entry.Children.Count == target.Entry.Children.Count
        && EntrySourceSignature(baseline) != EntrySourceSignature(target);

    private static ConfigAuditDiffValueEvidence GetEntryValueEvidence(
        ConfigAuditEntry? baseline,
        ConfigAuditEntry? target,
        bool redactionPolicyChanged)
    {
        if (redactionPolicyChanged)
        {
            return ConfigAuditDiffValueEvidence.RedactionPolicyMismatch;
        }

        if (baseline == null || target == null)
        {
            return ConfigAuditDiffValueEvidence.None;
        }

        if (baseline.IsRedacted && target.IsRedacted)
        {
            return ConfigAuditDiffValueEvidence.BothRedacted;
        }

        if (baseline.IsRedacted || target.IsRedacted)
        {
            return ConfigAuditDiffValueEvidence.RedactedVersusShown;
        }

        if (baseline.DisplayValue == null || target.DisplayValue == null)
        {
            return ConfigAuditDiffValueEvidence.Omitted;
        }

        return ConfigAuditDiffValueEvidence.DisplayValuesComparable;
    }

    private static ConfigAuditDiffValueEvidence GetDiscoveredValueEvidence(
        ConfigAuditDiscoveredKey? baseline,
        ConfigAuditDiscoveredKey? target,
        bool redactionPolicyChanged)
    {
        if (redactionPolicyChanged)
        {
            return ConfigAuditDiffValueEvidence.RedactionPolicyMismatch;
        }

        if (baseline == null || target == null)
        {
            return ConfigAuditDiffValueEvidence.None;
        }

        if (baseline.ValueDisplayState == ConfigAuditDiscoveredValueDisplayState.Unspecified
            || target.ValueDisplayState == ConfigAuditDiscoveredValueDisplayState.Unspecified)
        {
            return ConfigAuditDiffValueEvidence.Unspecified;
        }

        if (baseline.IsRedacted && target.IsRedacted)
        {
            return ConfigAuditDiffValueEvidence.BothRedacted;
        }

        if (baseline.IsRedacted || target.IsRedacted)
        {
            return ConfigAuditDiffValueEvidence.RedactedVersusShown;
        }

        if (baseline.DisplayValue == null || target.DisplayValue == null)
        {
            return ConfigAuditDiffValueEvidence.Omitted;
        }

        return ConfigAuditDiffValueEvidence.DisplayValuesComparable;
    }

    private static void ValidateReportShape(
        ConfigAuditReport report,
        string role,
        List<ConfigAuditComparisonDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(report.Environment))
        {
            diagnostics.Add(new ConfigAuditComparisonDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = ManualEvidenceCode,
                Message = $"The {role} report has no environment name; captured-report evidence is weaker.",
                Key = role
            });
        }

        if (report.GeneratedAt == default)
        {
            diagnostics.Add(new ConfigAuditComparisonDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = ManualEvidenceCode,
                Message = $"The {role} report has the default generated timestamp; captured-report freshness is unknown.",
                Key = role
            });
        }

        if (report.DiscoveredKeys.Any(key => key.ValueDisplayState == ConfigAuditDiscoveredValueDisplayState.Unspecified))
        {
            diagnostics.Add(new ConfigAuditComparisonDiagnostic
            {
                Severity = ConfigAuditDiagnosticSeverity.Warning,
                Code = ManualEvidenceCode,
                Message = $"The {role} report contains discovered keys with unspecified value display state; value evidence may come from a manually constructed or older report.",
                Key = role
            });
        }
    }

    private static string DescribeEntryChange(
        EntryDiffInput? baseline,
        EntryDiffInput? target,
        ConfigAuditDiffItemStatus status,
        ConfigAuditDiffValueEvidence valueEvidence) =>
        status switch
        {
            ConfigAuditDiffItemStatus.Unchanged when valueEvidence == ConfigAuditDiffValueEvidence.BothRedacted =>
                "Sanitized entry evidence is unchanged, but both values were redacted so raw equality is unknown.",
            ConfigAuditDiffItemStatus.Unchanged when valueEvidence == ConfigAuditDiffValueEvidence.Omitted =>
                "Sanitized entry evidence is unchanged, but at least one display value was omitted so raw equality is unknown.",
            ConfigAuditDiffItemStatus.Unchanged => "Entry evidence is unchanged.",
            ConfigAuditDiffItemStatus.Uncomparable => "Dictionary entry could not be safely matched across reports.",
            ConfigAuditDiffItemStatus.Added => "Entry exists only in the target report.",
            ConfigAuditDiffItemStatus.Removed => "Entry exists only in the baseline report.",
            _ when EntryValueSignature(baseline) == EntryValueSignature(target) =>
                "Entry source, state, type, child count, or diagnostic evidence changed.",
            _ => "Entry sanitized value or state changed."
        };

    private static string DescribeDiscoveredKeyChange(
        ConfigAuditDiscoveredKey? baseline,
        ConfigAuditDiscoveredKey? target,
        ConfigAuditDiffItemStatus status,
        ConfigAuditDiffValueEvidence valueEvidence) =>
        status switch
        {
            ConfigAuditDiffItemStatus.Unchanged when valueEvidence == ConfigAuditDiffValueEvidence.BothRedacted =>
                "Discovered-key evidence is unchanged, but both values were redacted so raw equality is unknown.",
            ConfigAuditDiffItemStatus.Unchanged when valueEvidence is ConfigAuditDiffValueEvidence.Omitted or ConfigAuditDiffValueEvidence.Unspecified =>
                "Discovered-key evidence is unchanged, but value display evidence is incomplete.",
            ConfigAuditDiffItemStatus.Unchanged => "Discovered-key evidence is unchanged.",
            ConfigAuditDiffItemStatus.Added => "Discovered key exists only in the target report.",
            ConfigAuditDiffItemStatus.Removed => "Discovered key exists only in the baseline report.",
            _ when DiscoveredKeyValueSignature(baseline) == DiscoveredKeyValueSignature(target) =>
                "Discovered key source or diagnostic evidence changed.",
            _ => "Discovered key sanitized value, classification, or display state changed."
        };

    private static ConfigAuditDiffSummary CreateSummary(IReadOnlyList<ConfigAuditDiffItem> items, int diagnosticCount) =>
        new()
        {
            Changed = items.Count(item => item.Status == ConfigAuditDiffItemStatus.Changed),
            Added = items.Count(item => item.Status == ConfigAuditDiffItemStatus.Added),
            Removed = items.Count(item => item.Status == ConfigAuditDiffItemStatus.Removed),
            Unchanged = items.Count(item => item.Status == ConfigAuditDiffItemStatus.Unchanged),
            Uncomparable = items.Count(item => item.Status == ConfigAuditDiffItemStatus.Uncomparable),
            Diagnostics = diagnosticCount
        };

    private static string EntrySignature(EntryDiffInput input) =>
        string.Join(
            "|",
            EntryValueSignature(input),
            EntrySourceSignature(input),
            DiagnosticSignature(input.Entry.Diagnostics),
            input.Entry.Children.Count.ToString(CultureInfo.InvariantCulture));

    private static string EntryValueSignature(EntryDiffInput? input) =>
        input == null
            ? string.Empty
            : string.Join(
                "|",
                input.CorrelationPath,
                input.Entry.State.ToString(),
                input.Entry.DeclaredType ?? string.Empty,
                input.Entry.DisplayValue ?? "<null>",
                input.Entry.IsRedacted.ToString(),
                ElementSignature(input.Entry.Element, comparisonOnly: input.UsesComparisonIdentity));

    private static string EntrySourceSignature(EntryDiffInput? input) =>
        input == null
            ? string.Empty
            : SourceSignature(input.Entry.Sources, includeConfigPaths: !input.UsesComparisonIdentity);

    private static string DiscoveredKeySignature(ConfigAuditDiscoveredKey key) =>
        string.Join(
            "|",
            DiscoveredKeyValueSignature(key),
            SourceSignature(key.Sources),
            DiagnosticSignature(key.Diagnostics));

    private static string DiscoveredKeyValueSignature(ConfigAuditDiscoveredKey? key) =>
        key == null
            ? string.Empty
            : string.Join(
                "|",
                key.Key,
                key.Classification.ToString(),
                key.DisplayValue ?? "<null>",
                key.IsRedacted.ToString(),
                key.ValueDisplayState.ToString());

    private static string ProviderSignature(ConfigAuditProvider provider) =>
        string.Join(
            "|",
            provider.Name,
            provider.Priority.ToString(CultureInfo.InvariantCulture),
            provider.Precedence.ToString(CultureInfo.InvariantCulture),
            provider.IsOverride.ToString());

    private static string DiagnosticSignature(ConfigAuditDiagnostic diagnostic) =>
        string.Join(
            "|",
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Key ?? string.Empty,
            diagnostic.ConfigPath ?? string.Empty,
            SourceSignature(diagnostic.Source));

    private static string DiagnosticSignature(IReadOnlyList<ConfigAuditDiagnostic> diagnostics) =>
        string.Join(
            "||",
            diagnostics
                .OrderBy(DiagnosticSortKey, StringComparer.Ordinal)
                .Select(DiagnosticSignature));

    private static string SourceSignature(IReadOnlyList<ConfigAuditSourceRecord> sources, bool includeConfigPaths = true) =>
        string.Join(
            "||",
            sources
                .OrderBy(source => SourceSignature(source, includeConfigPaths), StringComparer.Ordinal)
                .Select(source => SourceSignature(source, includeConfigPaths)));

    private static string SourceSignature(ConfigAuditSourceRecord? source, bool includeConfigPaths = true) =>
        source == null
            ? string.Empty
            : string.Join(
                "|",
                source.Kind.ToString(),
                source.ProviderName ?? string.Empty,
                source.ProviderPriority?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                source.FilePath ?? string.Empty,
                source.EnvironmentVariableName ?? string.Empty,
                includeConfigPaths ? source.ConfigPath ?? string.Empty : string.Empty,
                includeConfigPaths ? source.AppliedToPath ?? string.Empty : string.Empty,
                source.Role.ToString(),
                source.Sensitivity.ToString(),
                source.Location?.LineNumber.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                source.Location?.ByteColumnNumber.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string RedactionSignature(ConfigAuditRedaction redaction) =>
        string.Join(
            "|",
            redaction.Enabled.ToString(),
            redaction.Placeholder,
            redaction.DictionaryKeyCorrelationMode.ToString(),
            redaction.DictionaryKeyCorrelationKeyId ?? string.Empty,
            redaction.DictionaryKeyCorrelationApplicationScope ?? string.Empty,
            string.Join(",", redaction.MatchedFragments.OrderBy(fragment => fragment, StringComparer.Ordinal)));

    private static string ElementSignature(ConfigAuditElementIdentity? element, bool comparisonOnly = false) =>
        element == null
            ? string.Empty
            : comparisonOnly
                ? string.Join("|", element.Kind.ToString(), element.ComparisonKeyCorrelationId ?? string.Empty)
            : string.Join(
                "|",
                element.Kind.ToString(),
                element.Index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                element.KeyLabel ?? string.Empty,
                element.IsKeyRedacted.ToString(),
                element.KeyCorrelationId ?? string.Empty,
                element.ComparisonKeyCorrelationId ?? string.Empty);

    private static string EntrySortKey(EntryDiffInput input) => EntrySignature(input);

    private static string ProviderSortKey(ConfigAuditProvider provider) => ProviderSignature(provider);

    private static string DiscoveredKeySortKey(ConfigAuditDiscoveredKey key) => DiscoveredKeySignature(key);

    private static string DiagnosticSortKey(ConfigAuditDiagnostic diagnostic) => DiagnosticSignature(diagnostic);

    private static string GetDictionaryParentPath(string key)
    {
        var bracket = key.LastIndexOf("[", StringComparison.Ordinal);
        return bracket <= 0 ? key : key[..bracket];
    }

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private sealed record EntryDiffInput(
        ConfigAuditEntry Entry,
        string CorrelationPath,
        bool IsUncomparableDictionarySubtree,
        bool UsesComparisonIdentity);

    private readonly record struct DiffInput<T>(T Item, int Ordinal);
}
