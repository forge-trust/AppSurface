using System.Globalization;
using System.Text;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Keeps report display paths separate from provider source paths while child entries are built.
/// </summary>
/// <param name="DisplayPath">The dotted/bracketed user-facing label; never a provider lookup key.</param>
/// <param name="SourcePath">The typed identity of the deepest display-safe source ancestor.</param>
/// <param name="Element">Collection identity metadata, with dictionary labels already redacted when necessary.</param>
/// <param name="CollectionDepth">The number of collection boundaries traversed from the root.</param>
/// <param name="RequiresInheritedSource">Whether an unsafe dictionary ancestor prevents publishing an exact source identity.</param>
internal sealed record ConfigAuditPath(
    string DisplayPath,
    AppSurfaceConfigKey SourcePath,
    ConfigAuditElementIdentity? Element = null,
    int CollectionDepth = 0,
    bool RequiresInheritedSource = false)
{
    private const int MaxDictionaryKeyLabelLength = 128;

    /// <summary>Starts a traversal from a strict colon-delimited source path.</summary>
    public static ConfigAuditPath Root(string key) => Root(AppSurfaceConfigKey.Parse(key));

    /// <summary>Starts traversal with the original typed identity; presentation is never reparsed as hierarchy.</summary>
    public static ConfigAuditPath Root(AppSurfaceConfigKey key) => new(key.Value, key);

    /// <summary>Appends one member segment while retaining dotted display and inherited privacy state.</summary>
    public ConfigAuditPath AppendMember(string name) =>
        new(
            $"{DisplayPath}.{name}",
            AppSurfaceConfigKey.FromSegments([.. SourcePath.Segments, name]),
            Element: null,
            CollectionDepth,
            RequiresInheritedSource);

    /// <summary>Appends an invariant numeric source segment and a bracketed display index.</summary>
    public ConfigAuditPath AppendIndex(int index, ConfigAuditElementKind kind) =>
        new(
            $"{DisplayPath}[{index.ToString(CultureInfo.InvariantCulture)}]",
            AppSurfaceConfigKey.FromSegments([.. SourcePath.Segments, index.ToString(CultureInfo.InvariantCulture)]),
            new ConfigAuditElementIdentity
            {
                Kind = kind,
                Index = index
            },
            CollectionDepth + 1,
            RequiresInheritedSource);

    /// <summary>Builds a bounded display label and retains exact provenance only for a visible valid segment.</summary>
    /// <remarks>Literal dots, slashes, and brackets remain part of one segment. Hidden or invalid keys never enter public source paths.</remarks>
    public ConfigAuditPath AppendDictionaryKey(
        object? key,
        ConfigAuditEntryOptions options,
        ConfigAuditDictionaryLabelSet labels,
        ConfigAuditDictionaryKeyCorrelationContext correlation)
    {
        var rawLabel = ConvertDictionaryKeyToLabel(key, out var conversionFailed, out var truncated);
        var rawDisplayLabel = truncated
            ? $"{rawLabel[..MaxDictionaryKeyLabelLength]}..."
            : rawLabel;
        var displayLabel = EscapeDictionaryLabel(rawDisplayLabel);
        var keyIsSensitive = ConfigAuditRedactor.ContainsSensitiveFragment(rawLabel);
        var normalizedSensitivity = ConfigAuditEntryOptions.NormalizeSensitivity(options.Sensitivity);
        var entryIsSensitive = normalizedSensitivity == ConfigAuditSensitivity.Sensitive;
        var parentIsSensitive = ConfigAuditRedactor.ContainsSensitiveFragment(SourcePath.Value)
                                || HasRedactedDictionaryLabel(Element);
        var suppressLabel = !options.DisplayDictionaryKeys;
        var redactLabel = keyIsSensitive || entryIsSensitive || parentIsSensitive;
        var isRedacted = redactLabel || suppressLabel || conversionFailed;
        var canCreateCorrelationId = key != null && !conversionFailed;
        var label = redactLabel
            ? labels.GetRedactedLabel(conversionFailed ? $"unprintable:{key?.GetType().FullName}" : rawLabel)
            : suppressLabel || conversionFailed ? "[key]" : displayLabel;
        var displayKey = isRedacted
            ? $"{DisplayPath}[{label}]"
            : $"{DisplayPath}[\"{label}\"]";
        var canUseExactSource = !isRedacted && !truncated && IsPlainSourceSegment(rawLabel);

        return new ConfigAuditPath(
            displayKey,
            canUseExactSource ? AppSurfaceConfigKey.FromSegments([.. SourcePath.Segments, rawLabel]) : SourcePath,
            new ConfigAuditElementIdentity
            {
                Kind = ConfigAuditElementKind.DictionaryItem,
                KeyLabel = label,
                IsKeyRedacted = isRedacted,
                KeyCorrelationId = options.DictionaryKeyCorrelationMode == ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac && canCreateCorrelationId
                    ? correlation.CreateCorrelationId(rawLabel)
                    : null,
                ComparisonKeyCorrelationId = options.DictionaryKeyCorrelationMode == ConfigAuditDictionaryKeyCorrelationMode.ScopedHmac && canCreateCorrelationId
                    ? correlation.CreateComparisonCorrelationId(rawLabel)
                    : null
            },
            CollectionDepth + 1,
            RequiresInheritedSource || !canUseExactSource);
    }

    private static string EscapeDictionaryLabel(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static bool HasRedactedDictionaryLabel(ConfigAuditElementIdentity? element) =>
        element is
        {
            Kind: ConfigAuditElementKind.DictionaryItem,
            IsKeyRedacted: true,
            KeyLabel: not null
        }
        && !string.Equals(element.KeyLabel, "[key]", StringComparison.Ordinal);

    private static string ConvertDictionaryKeyToLabel(object? key, out bool conversionFailed, out bool truncated)
    {
        conversionFailed = false;
        truncated = false;
        if (key == null)
        {
            return string.Empty;
        }

        if (key is string stringKey)
        {
            truncated = stringKey.Length > MaxDictionaryKeyLabelLength;
            return stringKey;
        }

        if (key is not IFormattable and not IConvertible)
        {
            conversionFailed = true;
            return string.Empty;
        }

        string label;
        try
        {
            label = key is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : ((IConvertible)key).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            conversionFailed = true;
            return string.Empty;
        }

        if (label == null)
        {
            conversionFailed = true;
            return string.Empty;
        }

        truncated = label.Length > MaxDictionaryKeyLabelLength;
        return label;
    }

    private static bool IsPlainSourceSegment(string value) =>
        AppSurfaceConfigKey.TryParse(value, out var key) && key.Segments.Length == 1;
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
