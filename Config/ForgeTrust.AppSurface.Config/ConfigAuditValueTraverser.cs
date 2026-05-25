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

    public ConfigAuditTraversalResult BuildChildren(
        ConfigAuditPath path,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels)
    {
        var budget = options.MaxReportNodes;
        return BuildChildren(path, value, sources, options, visited, labels, ref budget);
    }

    private ConfigAuditTraversalResult BuildChildren(
        ConfigAuditPath path,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
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
                return BuildArrayChildren(path, array, sources, options, visited, labels, ref budget);
            }

            if (value is IDictionary dictionary)
            {
                return BuildDictionaryChildren(path, dictionary, sources, options, visited, labels, ref budget);
            }

            if (value is IList list)
            {
                return BuildListChildren(path, list, sources, options, visited, labels, ref budget);
            }

            if (TryCreateReadOnlyListAccessor(value, out var readOnlyList))
            {
                return BuildReadOnlyListChildren(path, readOnlyList, sources, options, visited, labels, ref budget);
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

            return BuildObjectChildren(path, value, sources, options, visited, labels, ref budget);
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
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
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
            entries.Add(BuildChild(childPath, array.GetValue(i), sources, options, visited, labels, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildListChildren(
        ConfigAuditPath path,
        IList list,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
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
            entries.Add(BuildChild(childPath, list[i], sources, options, visited, labels, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildReadOnlyListChildren(
        ConfigAuditPath path,
        ReadOnlyListAccessor list,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
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
            entries.Add(BuildChild(childPath, list.GetValue(i), sources, options, visited, labels, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildDictionaryChildren(
        ConfigAuditPath path,
        IDictionary dictionary,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
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
        foreach (DictionaryEntry item in dictionary)
        {
            if (!CanVisitElement(path, options, entries.Count, ref budget, diagnostics))
            {
                break;
            }

            var childPath = path.AppendDictionaryKey(item.Key, options, labels);
            entries.Add(BuildChild(childPath, item.Value, sources, options, visited, labels, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditTraversalResult BuildObjectChildren(
        ConfigAuditPath path,
        object value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
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

            entries.Add(BuildChild(path.AppendMember(property.Name), childValue, sources, options, visited, labels, ref budget));
        }

        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (field.IsInitOnly)
            {
                continue;
            }

            if (!TryConsumeNode(path, ref budget, diagnostics))
            {
                break;
            }

            entries.Add(BuildChild(path.AppendMember(field.Name), field.GetValue(value), sources, options, visited, labels, ref budget));
        }

        return new ConfigAuditTraversalResult(entries, diagnostics);
    }

    private ConfigAuditEntry BuildChild(
        ConfigAuditPath path,
        object? value,
        IReadOnlyList<ConfigAuditSourceRecord> sources,
        ConfigAuditEntryOptions options,
        HashSet<object> visited,
        ConfigAuditDictionaryLabelSet labels,
        ref int budget)
    {
        var selectedSources = SelectChildSources(sources, path);
        var redacted = _redactor.FormatValue(path.DisplayPath, value, selectedSources.Sources);
        var traversal = BuildChildren(path, value, sources, options, visited, labels, ref budget);
        var diagnostics = selectedSources.Diagnostics.Concat(traversal.Diagnostics).ToList();
        var state = traversal.Children.Any(IsPartiallyResolved)
                    || selectedSources.Sources.Any(source => source.Role == ConfigAuditSourceRole.Patch)
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
            return CreateInheritedSelection(sources, path);
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
                .Select(match => match.Source)
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
            return new ConfigAuditSourceSelection([fallback], []);
        }

        return new ConfigAuditSourceSelection(
            [fallback],
            [
                CreateSourceDiagnostic(
                    path,
                    "config-audit-source-inherited",
                    "This collection element inherits provenance from its parent because an exact display-safe source path was not available.")
            ]);
    }

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
        string message) =>
        new()
        {
            Severity = severity,
            Code = code,
            Key = path.DisplayPath,
            ConfigPath = path.DisplayPath,
            Message = message
        };

    private static bool IsPartiallyResolved(ConfigAuditEntry entry) =>
        entry.State == ConfigAuditEntryState.PartiallyResolved
        || entry.Sources.Any(source => source.Role == ConfigAuditSourceRole.Patch)
        || entry.Children.Any(IsPartiallyResolved);

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
