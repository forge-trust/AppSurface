using System.Collections;
using System.Reflection;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Builds safe child audit entries from object and opt-in collection values.
/// </summary>
internal sealed class ConfigAuditValueTraverser
{
    private readonly ConfigAuditRedactor _redactor;

    public ConfigAuditValueTraverser(ConfigAuditRedactor redactor)
    {
        _redactor = redactor;
    }

    /// <summary>
    /// Builds child audit entries for <paramref name="value"/>.
    /// </summary>
    /// <param name="path">The root path being traversed.</param>
    /// <param name="value">The value to expand into child entries.</param>
    /// <param name="sources">Candidate sources used for child provenance and source selection.</param>
    /// <param name="factContext">
    /// Proof-limited provenance facts used to attach fact-derived child diagnostics. Pass
    /// <see cref="ConfigAuditFactContext.Empty"/> when resolution produced no facts or when child diagnostics must not
    /// infer collection element creation from patch evidence. Non-empty contexts can add diagnostics to the returned
    /// <see cref="ConfigAuditTraversalResult"/> without changing labels, correlations, traversal limits, or the child value
    /// shape.
    /// </param>
    /// <param name="options">Traversal limits, collection opt-ins, and redaction options.</param>
    /// <param name="visited">Reference-tracking set used to avoid cycles.</param>
    /// <param name="labels">Dictionary label state carried through traversal.</param>
    /// <param name="correlation">Dictionary key correlation context.</param>
    /// <returns>The traversed child entries and traversal diagnostics.</returns>
    public ConfigAuditTraversalResult BuildChildren(
        ConfigAuditPath path,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation)
    {
        var budget = options.MaxReportNodes;
        return BuildChildren(path, value, sources, factContext, options, visited, labels, correlation, ref budget);
    }

    /// <summary>
    /// Builds child audit entries for <paramref name="value"/> while sharing the caller-owned traversal budget.
    /// </summary>
    /// <param name="path">The root path being traversed.</param>
    /// <param name="value">The value to expand into child entries.</param>
    /// <param name="sources">Candidate sources used for child provenance and source selection.</param>
    /// <param name="factContext">
    /// Proof-limited provenance facts used to attach fact-derived child diagnostics. Pass
    /// <see cref="ConfigAuditFactContext.Empty"/> when there are no facts; non-empty contexts can mark environment-created
    /// or unknown-base collection elements in the returned <see cref="ConfigAuditTraversalResult"/>.
    /// </param>
    /// <param name="options">Traversal limits, collection opt-ins, and redaction options.</param>
    /// <param name="visited">Reference-tracking set used to avoid cycles.</param>
    /// <param name="labels">Dictionary label state carried through traversal.</param>
    /// <param name="correlation">Dictionary key correlation context.</param>
    /// <param name="budget">Remaining report-node budget shared across recursive traversal.</param>
    /// <returns>The traversed child entries and traversal diagnostics.</returns>
    private ConfigAuditTraversalResult BuildChildren(
        ConfigAuditPath path,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        if (value == null || ConfigScalarTypes.IsScalar(value.GetType()))
        {
            return ConfigAuditTraversalResult.Empty;
        }

        if (ShouldTrack(value) && !visited.Add(value))
        {
            return ConfigAuditTraversalResult.Empty;
        }

        try
        {
            if (value is Array array)
            {
                return BuildArrayChildren(path, array, sources, factContext, options, visited, labels, correlation, ref budget);
            }

            if (value is IDictionary dictionary)
            {
                return BuildDictionaryChildren(path, dictionary, sources, factContext, options, visited, labels, correlation, ref budget);
            }

            if (value is IList list)
            {
                return BuildListChildren(path, list, sources, factContext, options, visited, labels, correlation, ref budget);
            }

            if (TryCreateReadOnlyListAccessor(value, out var readOnlyList))
            {
                return BuildReadOnlyListChildren(path, readOnlyList, sources, factContext, options, visited, labels, correlation, ref budget);
            }

            if (value is IEnumerable)
            {
                return options.TraverseCollectionElements
                    ? ConfigAuditTraversalResult.DiagnosticsOnly(
                        CreateDiagnostic(
                            path,
                            "config-audit-collection-kind-unsupported",
                            ConfigAuditDiagnosticSeverity.Warning,
                            $"Collection traversal for '{path.DisplayPath}' does not support {value.GetType().Name}."))
                    : ConfigAuditTraversalResult.Empty;
            }

            return BuildObjectChildren(path, value, sources, factContext, options, visited, labels, correlation, ref budget);
        }
        finally
        {
            if (ShouldTrack(value))
            {
                visited.Remove(value);
            }
        }
    }

    private ConfigAuditTraversalResult BuildArrayChildren(
        ConfigAuditPath path,
        Array array,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        if (!options.TraverseCollectionElements)
        {
            return ConfigAuditTraversalResult.Empty;
        }

        if (array.Rank != 1)
        {
            return ConfigAuditTraversalResult.DiagnosticsOnly(
                CreateDiagnostic(
                    path,
                    "config-audit-collection-kind-unsupported",
                    ConfigAuditDiagnosticSeverity.Warning,
                    $"Collection traversal for '{path.DisplayPath}' supports one-dimensional arrays only."));
        }

        if (path.CollectionDepth >= options.MaxCollectionDepth)
        {
            return ConfigAuditTraversalResult.DiagnosticsOnly(CreateDepthLimitDiagnostic(path));
        }

        var entries = new List<ConfigAuditEntry>();
        var diagnostics = new List<ConfigAuditDiagnostic>();
        for (var i = 0; i < array.Length; i++)
        {
            if (!CanVisitElement(path, options, entries.Count, ref budget, diagnostics))
            {
                break;
            }

            var childPath = path.AppendIndex(i, ConfigAuditElementKind.ArrayItem);
            entries.Add(BuildChild(childPath, array.GetValue(i), sources, factContext, options, visited, labels, correlation, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildListChildren(
        ConfigAuditPath path,
        IList list,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        if (!options.TraverseCollectionElements)
        {
            return ConfigAuditTraversalResult.Empty;
        }

        if (path.CollectionDepth >= options.MaxCollectionDepth)
        {
            return ConfigAuditTraversalResult.DiagnosticsOnly(CreateDepthLimitDiagnostic(path));
        }

        var entries = new List<ConfigAuditEntry>();
        var diagnostics = new List<ConfigAuditDiagnostic>();
        for (var i = 0; i < list.Count; i++)
        {
            if (!CanVisitElement(path, options, entries.Count, ref budget, diagnostics))
            {
                break;
            }

            var childPath = path.AppendIndex(i, ConfigAuditElementKind.ListItem);
            entries.Add(BuildChild(childPath, list[i], sources, factContext, options, visited, labels, correlation, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildReadOnlyListChildren(
        ConfigAuditPath path,
        ReadOnlyListAccessor list,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        if (!options.TraverseCollectionElements)
        {
            return ConfigAuditTraversalResult.Empty;
        }

        if (path.CollectionDepth >= options.MaxCollectionDepth)
        {
            return ConfigAuditTraversalResult.DiagnosticsOnly(CreateDepthLimitDiagnostic(path));
        }

        var entries = new List<ConfigAuditEntry>();
        var diagnostics = new List<ConfigAuditDiagnostic>();
        for (var i = 0; i < list.Count; i++)
        {
            if (!CanVisitElement(path, options, entries.Count, ref budget, diagnostics))
            {
                break;
            }

            var childPath = path.AppendIndex(i, ConfigAuditElementKind.ListItem);
            entries.Add(BuildChild(childPath, list.GetValue(i), sources, factContext, options, visited, labels, correlation, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildDictionaryChildren(
        ConfigAuditPath path,
        IDictionary dictionary,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        if (!options.TraverseCollectionElements)
        {
            return ConfigAuditTraversalResult.Empty;
        }

        if (path.CollectionDepth >= options.MaxCollectionDepth)
        {
            return ConfigAuditTraversalResult.DiagnosticsOnly(CreateDepthLimitDiagnostic(path));
        }

        var entries = new List<ConfigAuditEntry>();
        var diagnostics = new List<ConfigAuditDiagnostic>();
        if (options.DictionaryKeyCorrelationMode == ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac
            && !correlation.IsAvailable)
        {
            diagnostics.Add(correlation.CreateUnavailableDiagnostic(path));
        }

        foreach (DictionaryEntry item in dictionary)
        {
            if (!CanVisitElement(path, options, entries.Count, ref budget, diagnostics))
            {
                break;
            }

            var childPath = path.AppendDictionaryKey(item.Key, options, labels, correlation);
            entries.Add(BuildChild(childPath, item.Value, sources, factContext, options, visited, labels, correlation, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildObjectChildren(
        ConfigAuditPath path,
        object value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        var entries = new List<ConfigAuditEntry>();
        var diagnostics = new List<ConfigAuditDiagnostic>();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
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
            catch (Exception ex) when (IsPropertyReadException(ex))
            {
                continue;
            }

            if (!TryConsumeNode(path, ref budget, diagnostics))
            {
                break;
            }

            entries.Add(BuildChild(path.AppendMember(property.Name), childValue, sources, factContext, options, visited, labels, correlation, ref budget));
        }

        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            // Public readonly fields are immutable/non-configurable for audit and patch operations.
            if (field.IsInitOnly)
            {
                continue;
            }

            if (!TryConsumeNode(path, ref budget, diagnostics))
            {
                break;
            }

            entries.Add(BuildChild(path.AppendMember(field.Name), field.GetValue(value), sources, factContext, options, visited, labels, correlation, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditEntry BuildChild(
        ConfigAuditPath path,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditFactContext factContext,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation,
        ref int budget)
    {
        var selectedSources = SelectChildSources(sources, path);
        var redactionSources = path.RequiresInheritedSource ? sources : selectedSources.Sources;
        var redacted = _redactor.FormatValue(path.DisplayPath, value, redactionSources, options.Sensitivity);
        var traversal = BuildChildren(path, value, sources, factContext, options, visited, labels, correlation, ref budget);
        var diagnostics = selectedSources.Diagnostics
            .Concat(CreateProvenanceDiagnostics(path, factContext))
            .Concat(traversal.Diagnostics)
            .ToList();
        var state = traversal.Children.Any(ConfigAuditEntryStateHelpers.IsPartiallyResolved)
                    || redactionSources.Any(source => source.Role == ConfigAuditSourceRole.Patch)
            ? ConfigAuditEntryState.PartiallyResolved
            : ConfigAuditEntryState.Resolved;

        return new ConfigAuditEntry
        {
            Key = path.DisplayPath,
            DeclaredType = value?.GetType().FullName,
            State = state,
            DisplayValue = redacted.DisplayValue,
            IsRedacted = redacted.IsRedacted,
            Element = path.Element,
            Sources = selectedSources.Sources,
            Children = traversal.Children,
            Diagnostics = diagnostics
        };
    }

    private bool CanVisitElement(
        ConfigAuditPath path,
        ConfigAuditEntryOptions options,
        int visitedElements,
        ref int budget,
        List<ConfigAuditDiagnostic> diagnostics)
    {
        if (visitedElements >= options.MaxCollectionElements)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    path,
                    "config-audit-collection-element-limit",
                    ConfigAuditDiagnosticSeverity.Warning,
                    $"Collection traversal for '{path.DisplayPath}' stopped after {options.MaxCollectionElements} element(s)."));
            return false;
        }

        return TryConsumeNode(path, ref budget, diagnostics);
    }

    private static bool TryConsumeNode(
        ConfigAuditPath path,
        ref int budget,
        ICollection<ConfigAuditDiagnostic> diagnostics)
    {
        if (budget <= 0)
        {
            diagnostics.Add(
                CreateDiagnostic(
                    path,
                    "config-audit-report-node-limit",
                    ConfigAuditDiagnosticSeverity.Warning,
                    $"Config audit traversal for '{path.DisplayPath}' stopped at the node budget."));
            return false;
        }

        budget--;
        return true;
    }

    private static ConfigAuditSourceSelection SelectChildSources(
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditPath path)
    {
        if (path.RequiresInheritedSource)
        {
            return CreateInheritedSelection(sources, path, unknownWhenEmpty: true);
        }

        var matches = sources
            .Select(source => new { Source = source, Specificity = GetSourceSpecificity(source, path.SourcePath) })
            .Where(match => match.Specificity >= 0)
            .ToList();
        if (matches.Count == 0)
        {
            return CreateInheritedSelection(sources, path, unknownWhenEmpty: true);
        }

        var maxSpecificity = matches.Max(match => match.Specificity);
        return new ConfigAuditSourceSelection(
            matches
                .Where(match => match.Specificity == maxSpecificity)
                .Select(match => PrepareChildSource(match.Source, path.SourcePath))
                .ToList(),
            []);
    }

    private static ConfigAuditSourceSelection CreateInheritedSelection(
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditPath path,
        bool unknownWhenEmpty = false)
    {
        if (sources.FirstOrDefault() is not { } fallback)
        {
            var code = unknownWhenEmpty ? "config-audit-source-unavailable" : "config-audit-source-inherited";
            return new ConfigAuditSourceSelection(
                [],
                [CreateSourceDiagnostic(path, code, "No exact source was available for this collection element.")]);
        }

        if (path.Element == null)
        {
            return new ConfigAuditSourceSelection([PrepareChildSource(fallback, path.SourcePath)], []);
        }

        return new ConfigAuditSourceSelection(
            [PrepareChildSource(fallback, path.SourcePath)],
            [
                CreateSourceDiagnostic(
                    path,
                    "config-audit-source-inherited",
                    "This collection element inherits provenance from its parent because an exact display-safe source path was not available.")
            ]);
    }

    private static ConfigAuditSourceRecord PrepareChildSource(ConfigAuditSourceRecord source, string childSourcePath)
    {
        if (source.Location == null || SourceRepresentsExactPath(source, childSourcePath))
        {
            return source;
        }

        return new ConfigAuditSourceRecord
        {
            Kind = source.Kind,
            ProviderName = source.ProviderName,
            ProviderPriority = source.ProviderPriority,
            FilePath = source.FilePath,
            EnvironmentVariableName = source.EnvironmentVariableName,
            ConfigPath = source.ConfigPath,
            AppliedToPath = source.AppliedToPath,
            Role = source.Role,
            Sensitivity = source.Sensitivity
        };
    }

    private static bool SourceRepresentsExactPath(ConfigAuditSourceRecord source, string childSourcePath) =>
        string.Equals(source.AppliedToPath, childSourcePath, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.ConfigPath, childSourcePath, StringComparison.OrdinalIgnoreCase);

    private static int GetSourceSpecificity(ConfigAuditSourceRecord source, string childSourcePath) =>
        Math.Max(
            GetPathSpecificity(source.AppliedToPath, childSourcePath, allowDescendant: source.Role is ConfigAuditSourceRole.Base or ConfigAuditSourceRole.Patch),
            GetPathSpecificity(source.ConfigPath, childSourcePath, allowDescendant: false));

    private static int GetPathSpecificity(string? sourcePath, string childSourcePath, bool allowDescendant)
    {
        if (sourcePath == null)
        {
            return -1;
        }

        if (string.Equals(sourcePath, childSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath.Length;
        }

        return allowDescendant
               && childSourcePath.StartsWith($"{sourcePath}.", StringComparison.OrdinalIgnoreCase)
            ? sourcePath.Length
            : -1;
    }

    private static IEnumerable<ConfigAuditDiagnostic> CreateProvenanceDiagnostics(
        ConfigAuditPath path,
        ConfigAuditFactContext factContext)
    {
        if (path.Element?.Index == null)
        {
            yield break;
        }

        foreach (var fact in factContext.GetFacts(path.SourcePath))
        {
            if (fact.Source.Kind != ConfigAuditSourceKind.EnvironmentVariable)
            {
                continue;
            }

            if (fact.PriorPresence == ConfigAuditPriorPresence.Missing)
            {
                yield return CreateDiagnostic(
                    path,
                    "config-audit-environment-created-element",
                    ConfigAuditDiagnosticSeverity.Info,
                    "Environment variable created this collection element; audit provenance found no prior element.",
                    fact.Source);
            }
            else if (fact.PriorPresence == ConfigAuditPriorPresence.Unknown)
            {
                yield return CreateDiagnostic(
                    path,
                    "config-audit-environment-element-base-unknown",
                    ConfigAuditDiagnosticSeverity.Info,
                    "Environment variable supplied this collection element; audit provenance could not prove whether a lower-priority element existed.",
                    fact.Source);
            }
        }
    }

    private static ConfigAuditDiagnostic CreateDepthLimitDiagnostic(ConfigAuditPath path) =>
        CreateDiagnostic(
            path,
            "config-audit-collection-depth-limit",
            ConfigAuditDiagnosticSeverity.Warning,
            $"Collection traversal for '{path.DisplayPath}' stopped at the configured depth limit.");

    private static ConfigAuditDiagnostic CreateSourceDiagnostic(ConfigAuditPath path, string code, string message) =>
        CreateDiagnostic(path, code, ConfigAuditDiagnosticSeverity.Info, message);

    private static ConfigAuditDiagnostic CreateDiagnostic(
        ConfigAuditPath path,
        string code,
        ConfigAuditDiagnosticSeverity severity,
        string message,
        ConfigAuditSourceRecord? source = null) =>
        new()
        {
            Severity = severity,
            Code = code,
            Key = path.DisplayPath,
            ConfigPath = path.DisplayPath,
            Source = source,
            Message = message
        };

    private static bool IsPropertyReadException(Exception ex) =>
        ex is TargetInvocationException
            or TargetParameterCountException
            or MethodAccessException
            or ArgumentException;

    private static bool ShouldTrack(object value)
    {
        var type = value.GetType();
        return !type.IsValueType && !ConfigScalarTypes.IsScalar(type);
    }

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
            ?.GetProperty(nameof(IReadOnlyCollection<object>.Count));
        var itemProperty = interfaceType.GetProperty("Item");
        if (countProperty == null || itemProperty == null)
        {
            accessor = default;
            return false;
        }

        accessor = new ReadOnlyListAccessor(
            (int)(countProperty.GetValue(value) ?? 0),
            index => itemProperty.GetValue(value, [index]));
        return true;
    }
}

internal sealed record ConfigAuditTraversalResult(
    IReadOnlyList<ConfigAuditEntry> Children,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics)
{
    public static ConfigAuditTraversalResult Empty { get; } = new([], []);

    public static ConfigAuditTraversalResult DiagnosticsOnly(ConfigAuditDiagnostic diagnostic) =>
        new([], [diagnostic]);
}

internal sealed record ConfigAuditSourceSelection(
    IReadOnlyList<ConfigAuditSourceRecord> Sources,
    IReadOnlyList<ConfigAuditDiagnostic> Diagnostics);

internal readonly record struct ReadOnlyListAccessor(int Count, Func<int, object?> GetValue);
