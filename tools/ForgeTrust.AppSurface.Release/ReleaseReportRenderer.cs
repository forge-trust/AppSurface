namespace ForgeTrust.AppSurface.Release;

internal static class ReleaseReportRenderer
{
    /// <summary>
    /// Renders a check report.
    /// </summary>
    /// <param name="result">Check result.</param>
    /// <returns>Markdown report.</returns>
    /// <remarks>
    /// The report shape is stable for workflow comments and maintainer review: <c># Release readiness report</c>, a summary bullet list,
    /// <c>## Generated files</c>, <c>## Errors</c>, then <c>## Warnings</c>. Empty diagnostics render as <c>- None</c>. Generated
    /// file paths and diagnostic codes are wrapped in inline code; diagnostic text is not escaped beyond normal Markdown rendering.
    /// Consumers should key off headings and diagnostic codes rather than line numbers.
    /// </remarks>
    internal static string RenderCheck(ReleaseCheckResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Release readiness report");
        builder.AppendLine();
        builder.AppendLine($"- Version: `{result.Version}`");
        builder.AppendLine($"- Classification: `{result.ReleaseClassification}`");
        builder.AppendLine($"- Source commit: `{result.SourceCommit ?? "unknown"}`");
        builder.AppendLine($"- Errors: `{result.Errors.Count}`");
        builder.AppendLine($"- Warnings: `{result.Warnings.Count}`");
        builder.AppendLine();
        builder.AppendLine("## Generated files");
        foreach (var path in result.GeneratedFiles)
        {
            builder.AppendLine($"- `{path}`");
        }

        AppendEvidenceSummary(builder, result.EvidenceSummary);
        AppendDiagnostics(builder, "Errors", result.Errors);
        AppendDiagnostics(builder, "Warnings", result.Warnings);
        return builder.ToString();
    }

    /// <summary>
    /// Renders a prepare report.
    /// </summary>
    /// <param name="result">Preparation result.</param>
    /// <returns>Markdown report.</returns>
    /// <remarks>
    /// Preparation reports begin with the check report contract, then append a manual review gate and either <c>## Dry-run plan</c> or
    /// <c>## Files written</c> based on <see cref="ReleasePreparationResult.DryRun"/>. Paths are repository-relative bullets. This
    /// distinction is the only dry-run marker in the report, so callers that publish the report should preserve that heading.
    /// </remarks>
    internal static string RenderPreparation(ReleasePreparationResult result)
    {
        var builder = new StringBuilder(RenderCheck(result.Check));
        builder.AppendLine();
        builder.AppendLine("## Manual review gate");
        builder.AppendLine("- Stop at this release pull request for maintainer review and manual merge.");
        builder.AppendLine("- Do not create the annotated tag or start publish workflows until a maintainer gives an explicit post-review instruction.");
        builder.AppendLine();
        AppendEvidenceSummary(builder, result.EvidenceSummary ?? result.Check.EvidenceSummary);
        builder.AppendLine(result.DryRun ? "## Dry-run plan" : "## Files written");
        foreach (var path in result.PlannedOrWrittenFiles)
        {
            builder.AppendLine($"- `{path}`");
        }

        return builder.ToString();
    }

    private static void AppendEvidenceSummary(StringBuilder builder, ReleaseEvidenceSummary? summary)
    {
        if (summary is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Release evidence bundle");
        builder.AppendLine($"- Path: `{summary.Path}`");
        builder.AppendLine($"- Schema: `{summary.Schema}`");
        builder.AppendLine($"- Status: {summary.Status}");
        builder.AppendLine($"- Subject SHA-256: `{summary.SubjectSha256}`");
        builder.AppendLine($"- Docs archive manifest SHA-256: `{summary.DocsReleaseManifestSha256 ?? "pending"}`");
        builder.AppendLine($"- Catalog exact tree path: `{summary.CatalogExactTreePath ?? "pending"}`");
        builder.AppendLine($"- Tag commit: `{summary.TagCommit ?? "pending until publish validation"}`");
        builder.AppendLine($"- Attestation: {summary.Attestation}");
    }

    private static void AppendDiagnostics(StringBuilder builder, string heading, IReadOnlyList<ReleaseDiagnostic> diagnostics)
    {
        builder.AppendLine();
        builder.AppendLine($"## {heading}");
        if (diagnostics.Count == 0)
        {
            builder.AppendLine("- None");
            return;
        }

        foreach (var diagnostic in diagnostics)
        {
            builder.AppendLine($"- `{diagnostic.Code}`: {diagnostic.Problem}");
        }
    }
}
