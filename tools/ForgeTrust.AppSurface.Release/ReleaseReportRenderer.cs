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
