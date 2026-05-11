using System.Text;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Exception thrown by strict RazorDocs startup preflight when every configured harvester fails.
/// </summary>
/// <remarks>
/// The exception message and <see cref="Summary"/> intentionally contain only redacted harvest-health fields. Repository
/// roots, raw exception messages, stack traces, and diagnostic cause text stay in host logs and are not copied into this
/// public failure contract.
/// </remarks>
public sealed class RazorDocsHarvestFailedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of <see cref="RazorDocsHarvestFailedException"/> from a harvest-health snapshot.
    /// </summary>
    /// <param name="health">The failed harvest-health snapshot to summarize.</param>
    public RazorDocsHarvestFailedException(DocHarvestHealthSnapshot health)
        : this(DocHarvestFailureSummary.FromSnapshot(health))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="RazorDocsHarvestFailedException"/> from a redacted failure summary.
    /// </summary>
    /// <param name="summary">The redacted strict-harvest failure summary.</param>
    public RazorDocsHarvestFailedException(DocHarvestFailureSummary summary)
        : base(CreateMessage(summary))
    {
        Summary = summary;
    }

    /// <summary>
    /// Gets the redacted strict-harvest failure summary.
    /// </summary>
    public DocHarvestFailureSummary Summary { get; }

    private static string CreateMessage(DocHarvestFailureSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();
        builder.Append("RazorDocs strict harvest failed because every configured harvester failed, timed out, or canceled.");
        builder.Append(
            $" Status={summary.Status}; SuccessfulHarvesters={summary.SuccessfulHarvesters}/{summary.TotalHarvesters}; FailedHarvesters={summary.FailedHarvesters}; TotalDocs={summary.TotalDocs}.");

        var diagnostics = summary.Diagnostics ?? [];
        if (diagnostics.Count > 0)
        {
            builder.Append(" Diagnostics:");
            foreach (var diagnostic in diagnostics)
            {
                builder.Append(' ');
                builder.Append(diagnostic.Code);
                builder.Append(" (");
                builder.Append(diagnostic.Severity);
                builder.Append("): ");
                builder.Append(diagnostic.Problem);

                if (!string.IsNullOrWhiteSpace(diagnostic.Fix))
                {
                    builder.Append(" Fix: ");
                    builder.Append(diagnostic.Fix);
                }
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Redacted strict-harvest failure summary safe for public exception messages and export output.
/// </summary>
/// <param name="Status">The aggregate harvest-health status that triggered strict failure.</param>
/// <param name="GeneratedUtc">The timestamp for the cached harvest snapshot generation.</param>
/// <param name="TotalHarvesters">Number of configured harvesters in the failed snapshot.</param>
/// <param name="SuccessfulHarvesters">Number of harvesters that completed with docs or an intentional empty result.</param>
/// <param name="FailedHarvesters">Number of harvesters that failed, timed out, or canceled.</param>
/// <param name="TotalDocs">Number of final docs in the failed snapshot.</param>
/// <param name="Diagnostics">Redacted diagnostic entries copied from the failed snapshot.</param>
public sealed record DocHarvestFailureSummary(
    DocHarvestHealthStatus Status,
    DateTimeOffset GeneratedUtc,
    int TotalHarvesters,
    int SuccessfulHarvesters,
    int FailedHarvesters,
    int TotalDocs,
    IReadOnlyList<DocHarvestFailureDiagnostic> Diagnostics)
{
    /// <summary>
    /// Creates a redacted strict-harvest failure summary from a harvest-health snapshot.
    /// </summary>
    /// <param name="health">The harvest-health snapshot to summarize.</param>
    /// <returns>A redacted summary that omits repository roots, raw exception details, and diagnostic cause text.</returns>
    public static DocHarvestFailureSummary FromSnapshot(DocHarvestHealthSnapshot health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new DocHarvestFailureSummary(
            health.Status,
            health.GeneratedUtc,
            health.TotalHarvesters,
            health.SuccessfulHarvesters,
            health.FailedHarvesters,
            health.TotalDocs,
            health.Diagnostics
                .Select(DocHarvestFailureDiagnostic.FromDiagnostic)
                .ToArray());
    }
}

/// <summary>
/// Redacted strict-harvest diagnostic safe for public exception messages and export output.
/// </summary>
/// <param name="Code">Stable diagnostic code for machine-readable branching and tests.</param>
/// <param name="Severity">Diagnostic severity copied from the source diagnostic.</param>
/// <param name="HarvesterType">Concrete harvester type when the diagnostic belongs to one harvester.</param>
/// <param name="Problem">Operator-facing problem statement.</param>
/// <param name="Fix">Suggested recovery action.</param>
public sealed record DocHarvestFailureDiagnostic(
    string Code,
    DocHarvestDiagnosticSeverity Severity,
    string? HarvesterType,
    string Problem,
    string Fix)
{
    /// <summary>
    /// Creates a redacted strict-harvest diagnostic from a harvest-health diagnostic.
    /// </summary>
    /// <param name="diagnostic">The source diagnostic to redact.</param>
    /// <returns>A diagnostic that omits cause text and raw exception details.</returns>
    public static DocHarvestFailureDiagnostic FromDiagnostic(DocHarvestDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new DocHarvestFailureDiagnostic(
            diagnostic.Code,
            diagnostic.Severity,
            diagnostic.HarvesterType,
            diagnostic.Problem,
            diagnostic.Fix);
    }
}
