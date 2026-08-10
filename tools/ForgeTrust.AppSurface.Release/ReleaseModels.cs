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
    ReleaseEvidenceSummary? EvidenceSummary,
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
    bool DryRun,
    ReleaseEvidenceSummary? EvidenceSummary)
{
    /// <summary>
    /// Gets the append-only unreleased entries that preparation plans to archive or archived during a real run.
    /// </summary>
    /// <remarks>
    /// These paths are intentionally separate from generated artifacts: recovery must restore them to their pre-run state,
    /// whereas generated files may be removed or restored before a retry.
    /// </remarks>
    internal IReadOnlyList<string> ArchivedUnreleasedEntryPaths { get; init; } = [];
}

/// <summary>
/// Machine-readable release manifest.
/// </summary>
internal sealed record ReleaseManifest(
    string Schema,
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
/// Machine-readable schema-v2 release manifest for frozen coordinated release links.
/// </summary>
/// <remarks>
/// V2 deliberately retains V1 as a separate type so checked-in historical manifests are never deserialized through a newer contract.
/// Its package resolutions record the tree-local alias and the immutable tagged note it resolves to at preparation time.
/// </remarks>
internal sealed record ReleaseManifestV2(
    string Schema,
    string Version,
    string Tag,
    string Date,
    string? PreparationBaseCommit,
    string ReleaseClassification,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> PublishedPackageProjects,
    IReadOnlyList<CoordinatedPackageReleaseNoteResolution> CoordinatedPackageReleaseNoteResolutions,
    IReadOnlyList<ReleaseDiagnosticRecord> Diagnostics,
    IReadOnlyList<string> WarningIds)
{
    /// <summary>
    /// Gets the append-only unreleased-entry paths composed into this release and removed during preparation.
    /// </summary>
    /// <remarks>
    /// The V2 evidence bundle digests this manifest, so this ordered list is the proof that a release-preparation
    /// pull request may delete precisely these source entries and no others.
    /// </remarks>
    public IReadOnlyList<string> ConsumedUnreleasedEntryPaths { get; init; } = [];
}

/// <summary>
/// Records how a public package's coordinated release alias resolves in the prepared documentation tree.
/// </summary>
/// <param name="Project">Repository-relative project path for the public package.</param>
/// <param name="Source">Resolution source. Schema V2 accepts only <c>coordinated</c>.</param>
/// <param name="AliasPath">Tree-local alias path used by coordinated package documentation.</param>
/// <param name="ResolvedPath">Immutable versioned release-note path selected by the alias in this documentation tree.</param>
/// <param name="ReleaseTag">Annotated release tag for the immutable note.</param>
/// <param name="PreparationBaseCommit">Preparation base commit; null only while a draft has not yet been bound to a concrete repository commit.</param>
internal sealed record CoordinatedPackageReleaseNoteResolution(
    string Project,
    string Source,
    string AliasPath,
    string ResolvedPath,
    string ReleaseTag,
    string? PreparationBaseCommit);

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
    string EvidencePath,
    string EvidenceSubjectSha256,
    string EvidenceTagCommit,
    string? DocsReleaseManifestSha256,
    bool Prerelease,
    bool DryRun);

/// <summary>
/// Maintainer-facing release evidence summary rendered in command reports and workflow outputs.
/// </summary>
/// <param name="Path">Repository-relative evidence bundle path.</param>
/// <param name="Schema">Evidence bundle schema.</param>
/// <param name="Status">Draft or tag-bound validation status.</param>
/// <param name="SubjectSha256">Stable subject digest for the evidence bundle.</param>
/// <param name="DocsReleaseManifestSha256">Optional AppSurface Docs archive manifest digest referenced by the evidence bundle.</param>
/// <param name="CatalogExactTreePath">Optional catalog exact tree path referenced by the evidence bundle.</param>
/// <param name="DocsArchiveVerificationState">Stable docs archive verification state from the checked catalog/archive inputs.</param>
/// <param name="DocsCatalogPath">Physical catalog input used to verify stable docs evidence.</param>
/// <param name="DocsTrustedReleaseRootPath">Physical trusted release root used to resolve catalog exact-tree paths.</param>
/// <param name="DocsPhysicalExactTreePath">Physical exact-tree path verified against the catalog pin.</param>
/// <param name="DocsVerifiedFileCount">Number of archive files verified from the release manifest.</param>
/// <param name="TagCommit">Optional tag commit validated at publish time.</param>
/// <param name="Attestation">Attestation requirement state.</param>
internal sealed record ReleaseEvidenceSummary(
    string Path,
    string Schema,
    string Status,
    string SubjectSha256,
    string? DocsReleaseManifestSha256,
    string? CatalogExactTreePath,
    string? DocsArchiveVerificationState,
    string? DocsCatalogPath,
    string? DocsTrustedReleaseRootPath,
    string? DocsPhysicalExactTreePath,
    int? DocsVerifiedFileCount,
    string? TagCommit,
    string Attestation);
