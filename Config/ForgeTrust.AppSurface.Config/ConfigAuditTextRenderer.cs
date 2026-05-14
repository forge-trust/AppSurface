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

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics:");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"  [{diagnostic.Severity}] {diagnostic.Message}");
            }
        }

        return builder.ToString();
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

        foreach (var diagnostic in entry.Diagnostics)
        {
            builder.AppendLine($"{indent}  Diagnostic: {diagnostic.Message}");
        }

        if (entry.Children.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{indent}  Children:");
        foreach (var child in entry.Children.OrderBy(child => child.Key, StringComparer.OrdinalIgnoreCase))
        {
            RenderEntry(builder, child, indent + "    ");
        }
    }

    private static string FormatSource(ConfigAuditSourceRecord source) =>
        source.Kind switch
        {
            ConfigAuditSourceKind.File => $"{source.ProviderName} {Path.GetFileName(source.FilePath)} :: {source.ConfigPath}",
            ConfigAuditSourceKind.EnvironmentVariable => $"Environment variable {source.EnvironmentVariableName}",
            ConfigAuditSourceKind.Default => $"Default value on {source.ProviderName}",
            ConfigAuditSourceKind.Missing => "none",
            _ => source.ProviderName ?? source.Kind.ToString()
        };
}
