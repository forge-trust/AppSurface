using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
    /// Preparation reports begin with the check report contract, then append either <c>## Dry-run plan</c> or <c>## Files written</c>
    /// based on <see cref="ReleasePreparationResult.DryRun"/>. Paths are repository-relative bullets. This distinction is the only
    /// dry-run marker in the report, so callers that publish the report should preserve that heading.
    /// </remarks>
    internal static string RenderPreparation(ReleasePreparationResult result)
    {
        var builder = new StringBuilder(RenderCheck(result.Check));
        builder.AppendLine();
        builder.AppendLine(result.DryRun ? "## Dry-run plan" : "## Files written");
        foreach (var path in result.PlannedOrWrittenFiles)
        {
            builder.AppendLine($"- `{path}`");
        }

        return builder.ToString();
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
