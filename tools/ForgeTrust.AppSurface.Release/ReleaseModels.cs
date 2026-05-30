namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Immutable command invocation for release-owned external processes.
/// </summary>
/// <param name="Executable">Executable name or absolute path.</param>
/// <param name="Arguments">Argument list passed without shell evaluation.</param>
/// <param name="WorkingDirectory">Working directory used for the process.</param>
/// <param name="Timeout">Optional wall-clock timeout. When omitted, release commands use the default bounded timeout.</param>
internal sealed record CommandInvocation(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan? Timeout = null);

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
