using System.Globalization;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// The receipt, diagnostic, outcome mapping and operator output of a scoped patch coverage producer.
/// </summary>
/// <remarks>
/// <para>
/// The receipt is <c>coverage-scope/scoped-coverage-receipt.json</c>, schema version 1, holding totals only: the
/// declared projects, each source with whether its assembly was reported, the thresholds and mode, both metrics, the
/// in-scope and out-of-scope file counts, up to 50 unknown files, <c>overallGateEvaluated: false</c> and the reason.
/// Per-line detail stays in <c>coverage-patch-targets.json</c> and <c>.md</c> beside the merged report, which the
/// diagnostic names.
/// </para>
/// <para>
/// Every reason writes a receipt. When no report was classified, both metrics are <c>unverified</c> with null counts,
/// every <c>assemblyReported</c> is null, and the counts are null.
/// </para>
/// </remarks>
internal static class ScopedCoverageReceipt
{
    /// <summary>The receipt path inside the producer's artifact directory.</summary>
    public const string ReceiptRelativePath = EvidenceScopedCoverageContract.ReceiptRelativeRoot + "/" + EvidenceScopedCoverageContract.ReceiptFileName;

    /// <summary>Returns whether a declaration is a scoped coverage producer.</summary>
    /// <param name="producer">The declaration.</param>
    /// <returns>True when it is a <c>coverage</c> producer with a <c>coverageScope</c> block.</returns>
    public static bool IsScopedCoverageProducer(EvidenceProducerDeclaration producer) =>
        producer.CoverageScope is not null
        && string.Equals(producer.Kind, EvidenceScopedCoverageContract.Kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a reason to the producer outcome the manifest records.</summary>
    /// <param name="reason">The scoped verdict or run reason.</param>
    /// <returns>Passed only for <see cref="ScopedCoverageReason.Complete"/>.</returns>
    public static EvidenceProducerOutcome ToOutcome(ScopedCoverageReason reason) => reason switch
    {
        ScopedCoverageReason.Complete => EvidenceProducerOutcome.Passed,
        ScopedCoverageReason.SolutionUnavailable or ScopedCoverageReason.DiffUnavailable => EvidenceProducerOutcome.Unavailable,
        ScopedCoverageReason.DeadlineExceeded => EvidenceProducerOutcome.TimedOut,
        _ => EvidenceProducerOutcome.Failed,
    };

    /// <summary>Serializes receipt schema version 1.</summary>
    /// <param name="scope">The declared scope.</param>
    /// <param name="classification">The classification, or the unclassified result for a run reason.</param>
    /// <returns>UTF-8 JSON bytes, with non-ASCII characters escaped so the planner's size bound holds.</returns>
    public static byte[] Serialize(EvidenceCoverageScope scope, ScopedPatchClassification classification)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(classification);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("reason", classification.Reason.ToString());
            writer.WriteBoolean("overallGateEvaluated", false);
            writer.WriteStartArray("projects");
            foreach (var project in scope.Projects)
            {
                writer.WriteStringValue(project);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("sources");
            foreach (var source in classification.Sources)
            {
                writer.WriteStartObject();
                writer.WriteString("pattern", source.Pattern);
                writer.WriteString("assembly", source.Assembly);
                WriteNullable(writer, "assemblyReported", source.AssemblyReported);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("thresholds");
            WriteNullable(writer, "minPatchLinePercent", scope.MinPatchLinePercent);
            WriteNullable(writer, "minPatchBranchPercent", scope.MinPatchBranchPercent);
            writer.WriteString("patchLineMode", scope.PatchLineMode.ToLowerInvariant());
            writer.WriteNumber("tolerancePercent", scope.TolerancePercent);
            writer.WriteEndObject();
            WriteMetric(writer, "line", classification.Line);
            WriteMetric(writer, "branch", classification.Branch);
            WriteNullable(writer, "inScopeFiles", classification.InScopeFiles);
            WriteNullable(writer, "outOfScopeFiles", classification.OutOfScopeFiles);
            writer.WriteStartArray("unknownFiles");
            foreach (var file in classification.UnknownFiles)
            {
                writer.WriteStringValue(file);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("unknownFilesTruncated", classification.UnknownFilesTruncated);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Formats the one-line manifest diagnostic.</summary>
    /// <param name="scope">The declared scope.</param>
    /// <param name="classification">The classification.</param>
    /// <param name="detail">Optional run detail, such as the failing step or the patch-targets path.</param>
    /// <returns>For example <c>Scoped patch coverage Complete, 1 project, 1 source: line 97.2% (35/36), branch not applicable. The repository-wide gate was not evaluated.</c></returns>
    public static string FormatDiagnostic(EvidenceCoverageScope scope, ScopedPatchClassification classification, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(classification);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Scoped patch coverage {classification.Reason}, {Plural(scope.Projects.Count, "project")}, {Plural(scope.Sources.Count, "source")}: line {FormatMetric(classification.Line)}, branch {FormatMetric(classification.Branch)}.");
        if (classification.OutOfScopeFiles is > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" Changed files outside the scope, not scored: {classification.OutOfScopeFiles.Value}.");
        }

        var missing = classification.Sources.Where(static source => source.AssemblyReported == false).Select(static source => source.Assembly).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            text += $" Not in the report: {string.Join(", ", missing)}; declare a test project that references it, or correct the assembly name.";
        }

        if (classification.Reason == ScopedCoverageReason.AssemblyMismatch)
        {
            text += " A changed file has coverage rows only under another assembly; correct the source entry's assembly name.";
        }

        if (classification.Reason == ScopedCoverageReason.NothingInScope)
        {
            text += " No changed file is under a declared source pattern or test project directory; check the routing rule and the source patterns.";
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            text += " " + detail;
        }

        return text + " The repository-wide gate was not evaluated.";
    }

    /// <summary>Formats the <c>explain</c> lines for the plan's scoped coverage producers; empty when there is none.</summary>
    /// <param name="plan">The resolved plan.</param>
    /// <returns>One heading per producer, then one line per project and per source.</returns>
    public static IReadOnlyList<string> FormatPlan(EvidencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<string>();
        foreach (var producer in plan.Profile.Producers.Where(IsScopedCoverageProducer))
        {
            var scope = producer.CoverageScope!;
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Scoped coverage producer '{producer.Id}' (timeout {producer.TimeoutSeconds}s, patch line {FormatThreshold(scope.MinPatchLinePercent)}, patch branch {FormatThreshold(scope.MinPatchBranchPercent)}, mode {scope.PatchLineMode.ToLowerInvariant()}, tolerance {scope.TolerancePercent}%):"));
            lines.AddRange(scope.Projects.Select(static project => $"  project {project}"));
            lines.AddRange(scope.Sources.Select(static source => $"  source {source.Pattern} -> assembly {source.Assembly}"));
            lines.Add("  The repository-wide gate is not evaluated; see " + TestEvidenceRunSupport.DocumentationPage + "#scoped-patch-coverage");
        }

        return lines;
    }

    /// <summary>Describes the plan's scoped coverage producers for <c>evidence-summary.json</c>.</summary>
    /// <param name="plan">The resolved plan.</param>
    /// <returns>One object per scoped producer with its projects, sources and thresholds.</returns>
    public static object[] DescribePlan(EvidencePlan plan) =>
    [
        .. plan.Profile.Producers.Where(IsScopedCoverageProducer).Select(static producer => new
        {
            id = producer.Id,
            projects = producer.CoverageScope!.Projects,
            sources = producer.CoverageScope.Sources.Select(static source => new { pattern = source.Pattern, assembly = source.Assembly }).ToArray(),
            minPatchLinePercent = producer.CoverageScope.MinPatchLinePercent,
            minPatchBranchPercent = producer.CoverageScope.MinPatchBranchPercent,
            patchLineMode = producer.CoverageScope.PatchLineMode.ToLowerInvariant(),
            tolerancePercent = producer.CoverageScope.TolerancePercent,
            overallGateEvaluated = false,
        }),
    ];

    private static void WriteMetric(Utf8JsonWriter writer, string name, ScopedPatchMetric metric)
    {
        writer.WriteStartObject(name);
        writer.WriteString("outcome", metric.Outcome);
        writer.WriteString("reason", metric.Reason);
        WriteNullable(writer, "changedLines", metric.ChangedLines);
        WriteNullable(writer, "measurable", metric.Measurable);
        WriteNullable(writer, "covered", metric.Covered);
        WriteNullable(writer, "percent", metric.Percent);
        writer.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string FormatMetric(ScopedPatchMetric metric) => metric.Outcome switch
    {
        "measured" => string.Create(CultureInfo.InvariantCulture, $"{metric.Percent:0.##}% ({metric.Covered}/{metric.Measurable})"),
        "notApplicable" => "not applicable",
        _ => "unverified",
    };

    private static string FormatThreshold(decimal? threshold) =>
        threshold is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value}%") : "not declared";

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");
}
