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

/// <summary>
/// Immutable command invocation.
/// </summary>
internal sealed record CommandInvocation(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// Captured command result.
/// </summary>
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Release readiness result.
/// </summary>
internal sealed record ReleaseCheckResult(
    string Version,
    string ReleaseClassification,
    string? SourceCommit,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<ReleaseDiagnostic> Errors,
    IReadOnlyList<ReleaseDiagnostic> Warnings)
{
    /// <summary>
    /// Gets whether the report contains errors.
    /// </summary>
    internal bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Release preparation result.
/// </summary>
internal sealed record ReleasePreparationResult(
    ReleaseCheckResult Check,
    IReadOnlyList<string> PlannedOrWrittenFiles,
    bool DryRun);

/// <summary>
/// Machine-readable release manifest.
/// </summary>
internal sealed record ReleaseManifest(
    string Version,
    string Tag,
    string Date,
    string? SourceCommit,
    string ReleaseClassification,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> PublishedPackageProjects,
    IReadOnlyList<PackagePathUpdate> PackagePathUpdates,
    IReadOnlyList<ReleaseDiagnosticRecord> Diagnostics,
    IReadOnlyList<string> WarningIds);

/// <summary>
/// Package release note path update recorded in the release manifest.
/// </summary>
internal sealed record PackagePathUpdate(string Project, string PreviousReleaseNotesPath, string NextReleaseNotesPath);

/// <summary>
/// Serializable diagnostic record for release manifests.
/// </summary>
internal sealed record ReleaseDiagnosticRecord(
    string Severity,
    string Code,
    string Problem,
    string Cause,
    string Fix,
    string Docs)
{
    /// <summary>
    /// Creates a serializable diagnostic record.
    /// </summary>
    /// <param name="diagnostic">Source diagnostic.</param>
    /// <returns>Serializable record.</returns>
    internal static ReleaseDiagnosticRecord FromDiagnostic(ReleaseDiagnostic diagnostic)
    {
        return new ReleaseDiagnosticRecord(
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Problem,
            diagnostic.Cause,
            diagnostic.Fix,
            diagnostic.Docs);
    }
}

/// <summary>
/// Structured publish outputs for GitHub Actions.
/// </summary>
internal sealed record PublishOutputs(
    string Version,
    string Tag,
    string TagCommit,
    string NotePath,
    string NotesFile,
    string ReleaseClassification,
    bool Prerelease,
    bool DryRun);
