using System.Text;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Renders <see cref="ConfigAuditReport"/> instances as deterministic, human-readable text.
/// </summary>
public sealed class ConfigAuditTextRenderer
{
    private readonly int _identifierLimit;

    /// <summary>Creates a renderer with the documented resource defaults.</summary>
    public ConfigAuditTextRenderer() : this(Options.Create(new ConfigResourceOptions())) { }

    /// <summary>Creates a renderer that escapes and bounds identifiers using finalized resource options.</summary>
    /// <param name="resourceOptions">Validated configuration limits, copied when this renderer is created.</param>
    public ConfigAuditTextRenderer(IOptions<ConfigResourceOptions> resourceOptions)
    {
        ArgumentNullException.ThrowIfNull(resourceOptions);
        _identifierLimit = resourceOptions.Value.Snapshot().MaxRenderedIdentifierCharacters;
    }

    private string Identifier(string? value) => ConfigDiagnosticText.Identifier(value ?? string.Empty, _identifierLimit);

    /// <summary>
    /// Renders <paramref name="report"/> as text.
    /// </summary>
    /// <param name="report">The report to render.</param>
    /// <returns>A human-readable report.</returns>
    public string Render(ConfigAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"Environment: {Identifier(report.Environment)}");
        if (report.Mode == ConfigAuditReportMode.ExpandKnownEntryCollections)
        {
            builder.AppendLine("Mode: ExpandKnownEntryCollections");
        }

        builder.AppendLine("Providers:");
        foreach (var provider in report.Providers.OrderBy(provider => provider.Precedence))
        {
            var suffix = provider.IsOverride ? " (override)" : $" (priority {provider.Priority})";
            builder.AppendLine($"  {provider.Precedence}. {Identifier(provider.Name)}{suffix}");
        }

        builder.AppendLine();
        builder.AppendLine("Entries:");
        foreach (var entry in report.Entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            RenderEntry(builder, entry, indent: "  ");
        }

        if (report.DiscoveredKeys.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(FormatDiscoveredKeysHeading(report.DiscoveredKeys));
            foreach (var discoveredKey in report.DiscoveredKeys
                         .OrderBy(key => key.Classification)
                         .ThenBy(key => key.Key, StringComparer.OrdinalIgnoreCase).ThenBy(key => key.Key, StringComparer.Ordinal))
            {
                RenderDiscoveredKey(builder, discoveredKey);
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics:");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"  {FormatDiagnostic(diagnostic)}");
            }
        }

        return builder.ToString();
    }

    private static string FormatDiscoveredKeysHeading(IReadOnlyList<ConfigAuditDiscoveredKey> discoveredKeys)
    {
        var allSourcesAreFileBacked = discoveredKeys.All(discoveredKey =>
            discoveredKey.Sources.Count > 0
            && discoveredKey.Sources.All(source => source.Kind == ConfigAuditSourceKind.File));

        return allSourcesAreFileBacked ? "Discovered file keys:" : "Discovered keys:";
    }

    private void RenderEntry(StringBuilder builder, ConfigAuditEntry entry, string indent)
    {
        var value = entry.DisplayValue == null ? string.Empty : $" = {ConfigDiagnosticText.Prose(entry.DisplayValue)}";
        builder.AppendLine($"{indent}{Identifier(entry.Key)}{value}");
        builder.AppendLine($"{indent}  State: {entry.State}");
        foreach (var source in entry.Sources)
        {
            builder.AppendLine($"{indent}  Source: {FormatSource(source)}");
        }

        if (entry.Element?.KeyCorrelationId != null)
        {
            builder.AppendLine($"{indent}  Key correlation: {Identifier(entry.Element.KeyCorrelationId)}");
        }

        foreach (var diagnostic in entry.Diagnostics)
        {
            builder.AppendLine($"{indent}  Diagnostic: {FormatDiagnostic(diagnostic)}");
        }

        if (entry.Children.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{indent}  Children:");
        foreach (var child in OrderChildren(entry.Children))
        {
            RenderEntry(builder, child, indent + "    ");
        }
    }

    private void RenderDiscoveredKey(StringBuilder builder, ConfigAuditDiscoveredKey discoveredKey)
    {
        var value = FormatDiscoveredValue(discoveredKey);
        builder.AppendLine(
            $"  {Identifier(discoveredKey.Key)} [{FormatDiscoveredClassification(discoveredKey.Classification)}]{value}");
        if (discoveredKey.IsRedacted)
        {
            builder.AppendLine("    Redacted: true");
        }

        foreach (var source in discoveredKey.Sources)
        {
            builder.AppendLine($"    Source: {FormatSource(source)}");
        }

        foreach (var diagnostic in discoveredKey.Diagnostics)
        {
            builder.AppendLine($"    Diagnostic: {FormatDiagnostic(diagnostic)}");
        }
    }

    private static string FormatDiscoveredValue(ConfigAuditDiscoveredKey discoveredKey) =>
        discoveredKey.ValueDisplayState switch
        {
            ConfigAuditDiscoveredValueDisplayState.OmittedInventory =>
                $" (value omitted: {FormatInventoryOmissionReason(discoveredKey.Classification)})",
            ConfigAuditDiscoveredValueDisplayState.OmittedComplex => string.Empty,
            _ => discoveredKey.DisplayValue == null ? string.Empty : $" = {ConfigDiagnosticText.Prose(discoveredKey.DisplayValue)}"
        };

    private static string FormatInventoryOmissionReason(ConfigAuditDiscoveredKeyClassification classification) =>
        classification switch
        {
            ConfigAuditDiscoveredKeyClassification.KnownDescendant =>
                "descendant is not an exact audit entry; register this exact key with AddConfigAuditKey<T>() after reviewing sensitivity",
            ConfigAuditDiscoveredKeyClassification.Unknown =>
                "inventory key is not an exact audit entry; register this exact key with AddConfigAuditKey<T>() after reviewing sensitivity",
            ConfigAuditDiscoveredKeyClassification.Known =>
                "scalar display value is unavailable",
            _ => "inventory key is not an exact audit entry"
        };

    private string FormatDiagnostic(ConfigAuditDiagnostic diagnostic) =>
        $"[{diagnostic.Severity}] {Identifier(diagnostic.Code)}: {ConfigDiagnosticText.Prose(diagnostic.Message)}";

    private static IEnumerable<ConfigAuditEntry> OrderChildren(IReadOnlyList<ConfigAuditEntry> children)
    {
        if (children.Any(child => child.Element != null))
        {
            return children
                .Select((child, ordinal) => new { Child = child, Ordinal = ordinal })
                .OrderBy(item => GetElementSortGroup(item.Child))
                .ThenBy(item => item.Child.Element?.Index ?? int.MaxValue)
                .ThenBy(item => item.Child.Element?.Kind)
                .ThenBy(item => item.Child.Element?.KeyLabel ?? item.Child.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Ordinal)
                .Select(item => item.Child);
        }

        return children.OrderBy(child => child.Key, StringComparer.OrdinalIgnoreCase).ThenBy(child => child.Key, StringComparer.Ordinal);
    }

    private static int GetElementSortGroup(ConfigAuditEntry child)
    {
        var element = child.Element;
        if (element == null)
        {
            return 2;
        }

        if (element.Index != null)
        {
            return 0;
        }

        return 1;
    }

    private string FormatSource(ConfigAuditSourceRecord source) =>
        source.Kind switch
        {
            ConfigAuditSourceKind.File when source.Location != null =>
                $"{Identifier(source.ProviderName)} {Identifier(Path.GetFileName(source.FilePath))}:{source.Location.LineNumber}:{source.Location.ByteColumnNumber} :: {Identifier(source.ConfigPath)}",
            ConfigAuditSourceKind.File => $"{Identifier(source.ProviderName)} {Identifier(Path.GetFileName(source.FilePath))} :: {Identifier(source.ConfigPath)}",
            ConfigAuditSourceKind.EnvironmentVariable => $"Environment variable {Identifier(source.EnvironmentVariableName)}",
            ConfigAuditSourceKind.Default => $"Default value on {Identifier(source.ProviderName)}",
            ConfigAuditSourceKind.Missing => "none",
            _ => Identifier(source.ProviderName ?? source.Kind.ToString())
        };

    private static string FormatDiscoveredClassification(ConfigAuditDiscoveredKeyClassification classification) =>
        classification switch
        {
            ConfigAuditDiscoveredKeyClassification.Known => "Known",
            ConfigAuditDiscoveredKeyClassification.KnownDescendant => "Under known entry",
            ConfigAuditDiscoveredKeyClassification.Unknown => "Unknown to AppSurface audit registry",
            _ => classification.ToString()
        };
}
