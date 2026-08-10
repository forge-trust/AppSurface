using System.Globalization;

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
    /// <c>## Generated files</c>, optional <c>## Release evidence bundle</c>, <c>## Errors</c>, then <c>## Warnings</c>.
    /// Empty diagnostics render as <c>- None</c>. Each diagnostic renders its complete severity/code/problem/cause/fix/docs envelope.
    /// Generated file paths and diagnostic codes are wrapped in inline code; diagnostic text is not escaped beyond normal Markdown
    /// rendering. Consumers should key off headings and diagnostic codes rather than line numbers.
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
    /// Preparation reports begin with the check report contract, then append a manual review gate, optional evidence summary, either
    /// <c>## Dry-run plan</c> or <c>## Files written</c> based on <see cref="ReleasePreparationResult.DryRun"/>, a separate
    /// append-only entry archive section, and structured recovery guidance. Paths are repository-relative bullets. This distinction is the
    /// only dry-run marker in the report, so callers that publish the report should preserve that heading.
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

        AppendArchivedUnreleasedEntries(builder, result);
        AppendPreparationRecovery(builder, result);
        return builder.ToString();
    }

    private static void AppendArchivedUnreleasedEntries(StringBuilder builder, ReleasePreparationResult result)
    {
        builder.AppendLine();
        builder.AppendLine(result.DryRun ? "## Planned unreleased entry archives" : "## Archived unreleased entries");
        if (result.ArchivedUnreleasedEntryPaths.Count == 0)
        {
            builder.AppendLine("- None");
            return;
        }

        foreach (var path in result.ArchivedUnreleasedEntryPaths)
        {
            builder.AppendLine($"- `{path}`");
        }
    }

    private static void AppendPreparationRecovery(StringBuilder builder, ReleasePreparationResult result)
    {
        builder.AppendLine();
        builder.AppendLine("## Preparation recovery");
        if (result.DryRun)
        {
            builder.AppendLine("- State: dry run; no preparation artifacts were written.");
        }
        else
        {
            builder.AppendLine("- State: preparation writes artifacts sequentially; a failed run may leave a partial generated set.");
        }

        builder.AppendLine("- Generated artifacts:");
        foreach (var path in result.PlannedOrWrittenFiles)
        {
            builder.AppendLine($"  - `{path}`");
        }

        builder.AppendLine("- Archived unreleased entries:");
        if (result.ArchivedUnreleasedEntryPaths.Count == 0)
        {
            builder.AppendLine("  - None");
        }
        else
        {
            foreach (var path in result.ArchivedUnreleasedEntryPaths)
            {
                builder.AppendLine($"  - `{path}`");
            }
        }

        builder.AppendLine("- Recovery: stop, inspect `git status --short`, and preserve unrelated work. Remove or restore only the generated artifacts listed above, and restore each archived unreleased entry, before retrying.");
        builder.AppendLine("- Safe rollback validation: run `git diff --check`, confirm generated artifacts are absent or match the pre-run state, confirm archived unreleased entries are restored to the pre-run state, then rerun `./eng/release check --version " + result.Check.Version + " --allow-existing-targets` before another prepare attempt.");
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
        builder.AppendLine($"- Docs archive verification: `{summary.DocsArchiveVerificationState ?? "pending"}`");
        builder.AppendLine($"- Docs catalog input: `{summary.DocsCatalogPath ?? "pending"}`");
        builder.AppendLine($"- Docs trusted release root: `{summary.DocsTrustedReleaseRootPath ?? "pending"}`");
        builder.AppendLine($"- Docs physical exact tree: `{summary.DocsPhysicalExactTreePath ?? "pending"}`");
        builder.AppendLine($"- Docs verified file count: `{summary.DocsVerifiedFileCount?.ToString(CultureInfo.InvariantCulture) ?? "pending"}`");
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
            builder.AppendLine($"- Severity: `{diagnostic.Severity}`");
            builder.AppendLine($"  - Code: `{diagnostic.Code}`");
            builder.AppendLine($"  - Problem: {diagnostic.Problem}");
            builder.AppendLine($"  - Cause: {diagnostic.Cause}");
            builder.AppendLine($"  - Fix: {diagnostic.Fix}");
            builder.AppendLine($"  - Docs: {diagnostic.Docs}");
        }
    }
}
