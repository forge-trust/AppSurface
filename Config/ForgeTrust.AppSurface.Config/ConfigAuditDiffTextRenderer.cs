using System.Text;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Renders <see cref="ConfigAuditDiffReport"/> instances as deterministic, human-readable text.
/// </summary>
/// <remarks>
/// The renderer only renders sanitized diff evidence. By default it summarizes file paths to reduce support-bundle
/// exposure; set <see cref="ConfigAuditDiffOptions.SourceDetail"/> to <see cref="ConfigAuditDiffSourceDetail.Full"/>
/// before comparing when operators need the full source details already present in the audit reports.
/// </remarks>
public sealed class ConfigAuditDiffTextRenderer
{
    /// <summary>
    /// Renders a config audit diff report.
    /// </summary>
    /// <param name="report">The diff report to render.</param>
    /// <returns>A deterministic text report.</returns>
    public string Render(ConfigAuditDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"Config audit diff: {report.BaselineEnvironment} -> {report.TargetEnvironment}");
        builder.AppendLine($"Evidence: {FormatEvidenceMode(report.EvidenceMode)}");
        if (report.EvidenceMode == ConfigAuditDiffEvidenceMode.SameHostNamedEnvironment)
        {
            builder.AppendLine("Warning: same-host named-environment comparison does not prove deployment parity; prefer captured snapshots from each host for support decisions.");
        }

        builder.AppendLine($"Summary: {report.Summary.Changed} changed, {report.Summary.Added} added, {report.Summary.Removed} removed, {report.Summary.Uncomparable} uncomparable, {report.Summary.Unchanged} unchanged");

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Comparison diagnostics:");
            foreach (var diagnostic in report.Diagnostics)
            {
                builder.AppendLine($"  [{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Items:");
        if (report.Items.Count == 0)
        {
            builder.AppendLine("  No changed, added, removed, or uncomparable items retained.");
            return builder.ToString();
        }

        foreach (var item in report.Items)
        {
            RenderItem(builder, report.SourceDetail, item);
        }

        return builder.ToString();
    }

    private static void RenderItem(
        StringBuilder builder,
        ConfigAuditDiffSourceDetail sourceDetail,
        ConfigAuditDiffItem item)
    {
        builder.AppendLine($"  [{item.Significance}] {item.Status} {item.Kind}: {item.Key}");
        builder.AppendLine($"    {item.Description}");
        if (item.ValueEvidence != ConfigAuditDiffValueEvidence.None)
        {
            builder.AppendLine($"    Value evidence: {FormatValueEvidence(item.ValueEvidence)}");
        }

        if (item.BaselineDisplayValue != null || item.TargetDisplayValue != null)
        {
            builder.AppendLine($"    Baseline value: {item.BaselineDisplayValue ?? "(omitted)"}");
            builder.AppendLine($"    Target value: {item.TargetDisplayValue ?? "(omitted)"}");
        }

        RenderSources(builder, sourceDetail, "Baseline sources", item.BaselineSources);
        RenderSources(builder, sourceDetail, "Target sources", item.TargetSources);

        foreach (var diagnostic in item.Diagnostics)
        {
            builder.AppendLine($"    Diagnostic: [{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private static void RenderSources(
        StringBuilder builder,
        ConfigAuditDiffSourceDetail sourceDetail,
        string heading,
        IReadOnlyList<ConfigAuditSourceRecord> sources)
    {
        if (sources.Count == 0)
        {
            return;
        }

        builder.AppendLine($"    {heading}:");
        foreach (var source in sources)
        {
            builder.AppendLine($"      {FormatSource(source, sourceDetail)}");
        }
    }

    private static string FormatEvidenceMode(ConfigAuditDiffEvidenceMode mode) =>
        mode switch
        {
            ConfigAuditDiffEvidenceMode.CapturedSnapshot =>
                "captured snapshots from the environments they describe; stronger evidence, still support-sensitive",
            ConfigAuditDiffEvidenceMode.SameHostNamedEnvironment =>
                "same host comparing named environments; useful triage evidence only",
            _ => mode.ToString()
        };

    private static string FormatValueEvidence(ConfigAuditDiffValueEvidence evidence) =>
        evidence switch
        {
            ConfigAuditDiffValueEvidence.DisplayValuesComparable => "sanitized display values are comparable",
            ConfigAuditDiffValueEvidence.BothRedacted => "both values were redacted; raw equality is unknown",
            ConfigAuditDiffValueEvidence.RedactedVersusShown => "one value was redacted and one was shown; raw equality is unknown",
            ConfigAuditDiffValueEvidence.Omitted => "at least one display value was omitted; raw equality is unknown",
            ConfigAuditDiffValueEvidence.RedactionPolicyMismatch => "redaction policy metadata differs; raw equality is unknown",
            ConfigAuditDiffValueEvidence.Unspecified => "manual or default value display state; raw equality is unknown",
            _ => evidence.ToString()
        };

    private static string FormatSource(ConfigAuditSourceRecord source, ConfigAuditDiffSourceDetail sourceDetail) =>
        source.Kind switch
        {
            ConfigAuditSourceKind.File when source.Location != null =>
                $"{source.ProviderName} {FormatFilePath(source.FilePath, sourceDetail)}:{source.Location.LineNumber}:{source.Location.ByteColumnNumber} :: {source.ConfigPath}",
            ConfigAuditSourceKind.File => $"{source.ProviderName} {FormatFilePath(source.FilePath, sourceDetail)} :: {source.ConfigPath}",
            ConfigAuditSourceKind.EnvironmentVariable => $"Environment variable {source.EnvironmentVariableName}",
            ConfigAuditSourceKind.Default => $"Default value on {source.ProviderName}",
            ConfigAuditSourceKind.Missing => "none",
            _ => source.ProviderName ?? source.Kind.ToString()
        };

    private static string? FormatFilePath(string? filePath, ConfigAuditDiffSourceDetail sourceDetail) =>
        sourceDetail == ConfigAuditDiffSourceDetail.Full || string.IsNullOrWhiteSpace(filePath)
            ? filePath
            : Path.GetFileName(filePath);
}
