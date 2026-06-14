using System.Text;

namespace ProductReadinessLab;

/// <summary>
/// Status values emitted by product-readiness report rows.
/// </summary>
internal enum ReadinessStatus
{
    /// <summary>
    /// The capability was proven in the local lab.
    /// </summary>
    ProvenLocally,

    /// <summary>
    /// The host application must own this production concern.
    /// </summary>
    HostOwned,

    /// <summary>
    /// The capability is intentionally deferred from the local lab.
    /// </summary>
    Deferred,

    /// <summary>
    /// The row is useful proof scaffolding but must not be copied into production.
    /// </summary>
    UnsafeToCopy,

    /// <summary>
    /// A local proof could not run because setup or infrastructure is missing.
    /// </summary>
    Blocked,
}

/// <summary>
/// One evidence row in the product-readiness report.
/// </summary>
/// <param name="Area">Stable area id.</param>
/// <param name="Status">Readiness status.</param>
/// <param name="Evidence">What the lab observed or proved.</param>
/// <param name="Problem">Problem statement for blocked or delegated work.</param>
/// <param name="Cause">Likely cause or boundary explanation.</param>
/// <param name="Fix">Next action for an evaluator.</param>
/// <param name="CopyGuidance">What can or cannot be copied into a product app.</param>
internal sealed record ReadinessRow(
    string Area,
    ReadinessStatus Status,
    string Evidence,
    string Problem,
    string Cause,
    string Fix,
    string CopyGuidance);

/// <summary>
/// Complete product-readiness report.
/// </summary>
/// <param name="GeneratedAtUtc">Report generation timestamp.</param>
/// <param name="Rows">Evidence rows in display order.</param>
internal sealed record ReadinessReport(DateTimeOffset GeneratedAtUtc, IReadOnlyList<ReadinessRow> Rows);

/// <summary>
/// JSON response shape for product-readiness reports.
/// </summary>
/// <param name="GeneratedAtUtc">Report generation timestamp.</param>
/// <param name="Rows">Report rows with stable status wire names.</param>
internal sealed record ReadinessReportResponse(DateTimeOffset GeneratedAtUtc, IReadOnlyList<ReadinessRowResponse> Rows)
{
    /// <summary>
    /// Creates a JSON response from an internal report.
    /// </summary>
    /// <param name="report">Report to convert.</param>
    /// <returns>JSON-safe response DTO.</returns>
    public static ReadinessReportResponse FromReport(ReadinessReport report) =>
        new(
            report.GeneratedAtUtc,
            report.Rows
                .Select(row => new ReadinessRowResponse(
                    row.Area,
                    ReadinessReportMarkdownRenderer.ToWireName(row.Status),
                    row.Evidence,
                    row.Problem,
                    row.Cause,
                    row.Fix,
                    row.CopyGuidance))
                .ToArray());
}

/// <summary>
/// JSON response row with a stable string status.
/// </summary>
/// <param name="Area">Stable area id.</param>
/// <param name="Status">Kebab-case readiness status.</param>
/// <param name="Evidence">What the lab observed or proved.</param>
/// <param name="Problem">Problem statement for blocked or delegated work.</param>
/// <param name="Cause">Likely cause or boundary explanation.</param>
/// <param name="Fix">Next action for an evaluator.</param>
/// <param name="CopyGuidance">What can or cannot be copied into a product app.</param>
internal sealed record ReadinessRowResponse(
    string Area,
    string Status,
    string Evidence,
    string Problem,
    string Cause,
    string Fix,
    string CopyGuidance);

/// <summary>
/// Renders the product-readiness report as Markdown.
/// </summary>
internal static class ReadinessReportMarkdownRenderer
{
    /// <summary>
    /// Renders the supplied report.
    /// </summary>
    /// <param name="report">Report to render.</param>
    /// <returns>Markdown text.</returns>
    public static string Render(ReadinessReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AppSurface Product Readiness Lab Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAtUtc:O}`");
        builder.AppendLine();
        builder.AppendLine("| Area | Status | Evidence | Problem | Cause | Fix | Copy guidance |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach (var row in report.Rows)
        {
            builder
                .Append("| ")
                .Append(Escape(row.Area))
                .Append(" | `")
                .Append(ToWireName(row.Status))
                .Append("` | ")
                .Append(Escape(row.Evidence))
                .Append(" | ")
                .Append(Escape(row.Problem))
                .Append(" | ")
                .Append(Escape(row.Cause))
                .Append(" | ")
                .Append(Escape(row.Fix))
                .Append(" | ")
                .Append(Escape(row.CopyGuidance))
                .AppendLine(" |");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts a status to its report wire name.
    /// </summary>
    /// <param name="status">Status to convert.</param>
    /// <returns>Kebab-case status name.</returns>
    public static string ToWireName(ReadinessStatus status) =>
        status switch
        {
            ReadinessStatus.ProvenLocally => "proven-locally",
            ReadinessStatus.HostOwned => "host-owned",
            ReadinessStatus.Deferred => "deferred",
            ReadinessStatus.UnsafeToCopy => "unsafe-to-copy",
            ReadinessStatus.Blocked => "blocked",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown readiness status."),
        };

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}

/// <summary>
/// Exit-code policy for readiness reports.
/// </summary>
internal static class ReadinessReportExitCodePolicy
{
    /// <summary>
    /// Returns the process exit code for a report.
    /// </summary>
    /// <param name="report">Report to evaluate.</param>
    /// <returns>Zero when local proofs are complete; otherwise one.</returns>
    public static int GetExitCode(ReadinessReport report) =>
        report.Rows.Any(row => row.Status == ReadinessStatus.Blocked) ? 1 : 0;
}
