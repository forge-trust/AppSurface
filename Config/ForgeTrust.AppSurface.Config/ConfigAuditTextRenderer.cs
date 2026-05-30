using System.Text;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Renders <see cref="ConfigAuditReport"/> instances as deterministic, human-readable text.
/// </summary>
public sealed class ConfigAuditTextRenderer
{
    /// <summary>
    /// Renders <paramref name="report"/> as text.
    /// </summary>
    /// <param name="report">The report to render.</param>
    /// <returns>A human-readable report.</returns>
    public string Render(ConfigAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"Environment: {report.Environment}");
        builder.AppendLine("Providers:");
        foreach (var provider in report.Providers.OrderBy(provider => provider.Precedence))
        {
            var suffix = provider.IsOverride ? " (override)" : $" (priority {provider.Priority})";
            builder.AppendLine($"  {provider.Precedence}. {provider.Name}{suffix}");
        }

        builder.AppendLine();
        builder.AppendLine("Entries:");
        foreach (var entry in report.Entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            RenderEntry(builder, entry, indent: "  ");
        }

        if (report.DiscoveredKeys.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(FormatDiscoveredKeysHeading(report.DiscoveredKeys));
            foreach (var discoveredKey in report.DiscoveredKeys
                         .OrderBy(key => key.Classification)
                         .ThenBy(key => key.Key, StringComparer.OrdinalIgnoreCase))
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

    private static void RenderEntry(StringBuilder builder, ConfigAuditEntry entry, string indent)
    {
        var value = entry.DisplayValue == null ? string.Empty : $" = {entry.DisplayValue}";
        builder.AppendLine($"{indent}{entry.Key}{value}");
        builder.AppendLine($"{indent}  State: {entry.State}");
        foreach (var source in entry.Sources)
        {
            builder.AppendLine($"{indent}  Source: {FormatSource(source)}");
        }

        if (entry.Element?.KeyCorrelationId != null)
        {
            builder.AppendLine($"{indent}  Key correlation: {entry.Element.KeyCorrelationId}");
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

    private static void RenderDiscoveredKey(StringBuilder builder, ConfigAuditDiscoveredKey discoveredKey)
    {
        var value = discoveredKey.DisplayValue == null ? string.Empty : $" = {discoveredKey.DisplayValue}";
        builder.AppendLine(
            $"  {discoveredKey.Key} [{FormatDiscoveredClassification(discoveredKey.Classification)}]{value}");
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

    private static string FormatDiagnostic(ConfigAuditDiagnostic diagnostic) =>
        $"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}";

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

        return children.OrderBy(child => child.Key, StringComparer.OrdinalIgnoreCase);
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

    private static string FormatSource(ConfigAuditSourceRecord source) =>
        source.Kind switch
        {
            ConfigAuditSourceKind.File when source.Location != null =>
                $"{source.ProviderName} {Path.GetFileName(source.FilePath)}:{source.Location.LineNumber}:{source.Location.ByteColumnNumber} :: {source.ConfigPath}",
            ConfigAuditSourceKind.File => $"{source.ProviderName} {Path.GetFileName(source.FilePath)} :: {source.ConfigPath}",
            ConfigAuditSourceKind.EnvironmentVariable => $"Environment variable {source.EnvironmentVariableName}",
            ConfigAuditSourceKind.Default => $"Default value on {source.ProviderName}",
            ConfigAuditSourceKind.Missing => "none",
            _ => source.ProviderName ?? source.Kind.ToString()
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
