using System.Globalization;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Keeps report display paths separate from provider source paths while child entries are built.
/// </summary>
internal sealed record ConfigAuditPath(
    string DisplayPath,
    string SourcePath,
    ConfigAuditElementIdentity? Element = null,
    int CollectionDepth = 0,
    bool RequiresInheritedSource = false)
{
    private const int MaxDictionaryKeyLabelLength = 128;

    public static ConfigAuditPath Root(string key) => new(key, key);

    public ConfigAuditPath AppendMember(string name) =>
        new(
            $"{DisplayPath}.{name}",
            $"{SourcePath}.{name}",
            Element: null,
            CollectionDepth,
            RequiresInheritedSource);

    public ConfigAuditPath AppendIndex(int index, ConfigAuditElementKind kind) =>
        new(
            $"{DisplayPath}[{index.ToString(CultureInfo.InvariantCulture)}]",
            $"{SourcePath}.{index.ToString(CultureInfo.InvariantCulture)}",
            new ConfigAuditElementIdentity
            {
                Kind = kind,
                Index = index
            },
            CollectionDepth + 1,
            RequiresInheritedSource);

    public ConfigAuditPath AppendDictionaryKey(
        object? key,
        ConfigAuditEntryOptions options,
        ConfigAuditDictionaryLabelSet labels)
    {
        var rawLabel = ConvertDictionaryKeyToLabel(key, out var conversionFailed, out var truncated);
        var displayLabel = truncated
            ? $"{rawLabel[..MaxDictionaryKeyLabelLength]}..."
            : rawLabel;
        var keyIsSensitive = ConfigAuditRedactor.ContainsSensitiveFragment(rawLabel);
        var entryIsSensitive = options.Sensitivity == ConfigAuditSensitivity.Sensitive;
        var parentIsSensitive = ConfigAuditRedactor.ContainsSensitiveFragment(DisplayPath)
                                || ConfigAuditRedactor.ContainsSensitiveFragment(SourcePath);
        var suppressLabel = !options.DisplayDictionaryKeys;
        var redactLabel = keyIsSensitive || entryIsSensitive || parentIsSensitive;
        var isRedacted = redactLabel || suppressLabel || conversionFailed;
        var label = redactLabel
            ? labels.GetRedactedLabel(conversionFailed ? $"unprintable:{key?.GetType().FullName}" : rawLabel)
            : suppressLabel || conversionFailed ? "[key]" : displayLabel;
        var displayKey = isRedacted
            ? $"{DisplayPath}[{label}]"
            : $"{DisplayPath}[\"{EscapeDictionaryLabel(label)}\"]";
        var canUseExactSource = !isRedacted && !truncated && IsPlainSourceSegment(rawLabel);

        return new ConfigAuditPath(
            displayKey,
            canUseExactSource ? $"{SourcePath}.{rawLabel}" : SourcePath,
            new ConfigAuditElementIdentity
            {
                Kind = ConfigAuditElementKind.DictionaryItem,
                KeyLabel = label,
                IsKeyRedacted = isRedacted
            },
            CollectionDepth + 1,
            RequiresInheritedSource || !canUseExactSource);
    }

    private static string EscapeDictionaryLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ConvertDictionaryKeyToLabel(object? key, out bool conversionFailed, out bool truncated)
    {
        conversionFailed = false;
        truncated = false;
        if (key == null)
        {
            return string.Empty;
        }

        string label;
        try
        {
            label = Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch (Exception)
        {
            conversionFailed = true;
            return string.Empty;
        }

        truncated = label.Length > MaxDictionaryKeyLabelLength;
        return label;
    }

    private static bool IsPlainSourceSegment(string value) =>
        value.Length > 0
        && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
}

internal sealed class ConfigAuditDictionaryLabelSet
{
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);

    public string GetRedactedLabel(string rawKey)
    {
        if (_labels.TryGetValue(rawKey, out var label))
        {
            return label;
        }

        label = $"[redacted-key-{(_labels.Count + 1).ToString(CultureInfo.InvariantCulture)}]";
        _labels[rawKey] = label;
        return label;
    }
}
