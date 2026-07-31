using System.Security.Cryptography;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Creates release artifacts from the living unreleased note.
/// </summary>
internal sealed class ReleasePreparation
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ReleaseChecker _checker;
    private readonly IReleaseClock _clock;
    private readonly Func<CancellationToken, Task>? _beforeWriteAsync;

    /// <summary>
    /// Creates release preparation workflow.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="checker">Release readiness checker.</param>
    /// <param name="clock">Clock for default dates.</param>
    /// <param name="beforeWriteAsync">Optional test seam invoked after release content is rendered and before pointer-digest revalidation.</param>
    internal ReleasePreparation(
        ReleaseWorkspace workspace,
        ReleaseChecker checker,
        IReleaseClock clock,
        Func<CancellationToken, Task>? beforeWriteAsync = null)
    {
        _workspace = workspace;
        _checker = checker;
        _clock = clock;
        _beforeWriteAsync = beforeWriteAsync;
    }

    /// <summary>
    /// Generates release files or, in dry-run mode, returns the planned edits.
    /// </summary>
    /// <param name="options">Release command options. Date defaults to the injected clock when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preparation result containing readiness diagnostics and planned or written repository-relative paths.</returns>
    /// <remarks>
    /// Preparation is a deterministic repository-file rewrite: it runs readiness checks, reads the unreleased note and sidecar,
    /// builds versioned release artifacts, refreshes the frozen tree-local current pointer, rolls <c>CHANGELOG.md</c>, resets unreleased
    /// files, and records diagnostics in the release manifest. Coordinated package rows are intentionally not rewritten: each docs export
    /// freezes the current pointer that was generated for its release. Dry-run mode performs all reads and rendering but does not write files.
    /// The method does not create git branches, tags, commits, package artifacts, or GitHub Releases; workflows own those operations.
    /// Callers should treat any readiness errors as blocking and should avoid running against a dirty or concurrently modified tree.
    /// Writes are sequential rather than transactional. If the local process fails after some files are written, rerun <c>git status</c>
    /// and remove or revert the partial generated artifacts before retrying so create-only target checks do not stop the next run.
    /// </remarks>
    internal async Task<ReleasePreparationResult> PrepareAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        var check = await _checker.CheckAsync(options, cancellationToken);
        if (check.HasErrors)
        {
            return new ReleasePreparationResult(check, [], options.DryRun, EvidenceSummary: null);
        }

        var date = options.Date ?? _clock.TodayUtc();
        var currentPointerSnapshot = await CaptureFileDigestAsync(_workspace.CurrentReleasePath, cancellationToken);
        var currentPointerSidecarSnapshot = await CaptureFileDigestAsync(_workspace.CurrentReleaseSidecarPath, cancellationToken);
        var unreleased = await File.ReadAllTextAsync(_workspace.UnreleasedPath, cancellationToken);
        var sidecar = await ReleaseSidecar.LoadAsync(_workspace.UnreleasedSidecarPath, cancellationToken);
        var currentReleaseSidecar = await File.ReadAllTextAsync(_workspace.CurrentReleaseSidecarPath, cancellationToken);
        var packageSummary = await PackageIndexSummary.LoadAsync(_workspace.PackageIndexPath, cancellationToken);
        var generatedPaths = new List<string>();
        var releaseNotePath = _workspace.ReleaseNotePath(options.Version);
        var releaseSidecarPath = _workspace.ReleaseSidecarPath(options.Version);
        var releaseManifestPath = _workspace.ReleaseManifestPath(options.Version);
        var releaseEvidencePath = _workspace.ReleaseEvidencePath(options.Version);
        var releasePath = $"releases/v{options.Version}.md";
        var currentReleasePath = _workspace.CurrentReleasePath;

        var releaseNote = ReleaseNoteBuilder.Build(options.Version, date, unreleased);
        var releaseSidecar = sidecar.ToTaggedRelease(options.Version, date);
        var currentRelease = ReleaseNoteBuilder.BuildCurrentReleasePointer(options.Version);
        var coordinatedResolutions = packageSummary.PublicPublishedPackages
            .Where(package => package.ReleaseLink?.Track == PackageReleaseTrack.Coordinated)
            .Select(package => new CoordinatedPackageReleaseNoteResolution(
                package.Project,
                "coordinated",
                PackageReleaseLink.CoordinatedReleaseNotesPath,
                releasePath,
                options.Version.TagName,
                check.SourceCommit))
            .OrderBy(resolution => resolution.Project, StringComparer.Ordinal)
            .ToArray();
        var manifest = new ReleaseManifestV2(
            "appsurface-release-manifest-v2",
            options.Version.ToString(),
            options.Version.TagName,
            date.ToString("yyyy-MM-dd"),
            check.SourceCommit,
            check.ReleaseClassification,
            check.GeneratedFiles,
            packageSummary.PublicPublishedPackages.Select(package => package.Project).OrderBy(project => project, StringComparer.Ordinal).ToArray(),
            coordinatedResolutions,
            check.Errors.Concat(check.Warnings).Select(ReleaseDiagnosticRecord.FromDiagnostic).ToArray(),
            check.Warnings.Select(warning => warning.Code).ToArray());
        var releaseManifestContent = JsonSerializer.Serialize(manifest, ReleaseJson.Options) + Environment.NewLine;
        var evidence = ReleaseEvidence.BuildDraftV2(
            _workspace,
            options.Version,
            check.ReleaseClassification,
            date,
            check.SourceCommit,
            releaseNote,
            releaseSidecar,
            releaseManifestContent,
            currentRelease,
            currentReleaseSidecar,
            coordinatedResolutions);
        var releaseEvidenceContent = ReleaseEvidence.Serialize(evidence);

        var changelog = await File.ReadAllTextAsync(_workspace.ChangelogPath, cancellationToken);
        var nextUnreleased = ReleaseNoteBuilder.ResetUnreleased(options.Version);

        var writes = new Dictionary<string, string>
        {
            [releaseNotePath] = releaseNote,
            [releaseSidecarPath] = releaseSidecar,
            [releaseManifestPath] = releaseManifestContent,
            [releaseEvidencePath] = releaseEvidenceContent,
            [currentReleasePath] = currentRelease,
            [_workspace.ChangelogPath] = ChangelogEditor.RollForward(changelog, options.Version, date, releasePath),
            [_workspace.UnreleasedPath] = nextUnreleased,
            [_workspace.UnreleasedSidecarPath] = ReleaseSidecar.UnreleasedTemplate()
        };

        if (!options.DryRun)
        {
            if (_beforeWriteAsync is not null)
            {
                await _beforeWriteAsync(cancellationToken);
            }

            await EnsurePreparationBaseCommitUnchangedAsync(check.SourceCommit, cancellationToken);
            await EnsureFileDigestUnchangedAsync(currentPointerSnapshot, cancellationToken);
            await EnsureFileDigestUnchangedAsync(currentPointerSidecarSnapshot, cancellationToken);
            foreach (var (path, content) in writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, cancellationToken);
                generatedPaths.Add(_workspace.DisplayPath(path));
            }
        }
        else
        {
            generatedPaths.AddRange(writes.Keys.Select(_workspace.DisplayPath));
        }

        return new ReleasePreparationResult(check, generatedPaths, options.DryRun, evidence.ToSummary("draft evidence for release-prep review"));
    }

    private static async Task<ReleaseFileDigestSnapshot> CaptureFileDigestAsync(string path, CancellationToken cancellationToken)
    {
        return new ReleaseFileDigestSnapshot(path, await ComputeFileDigestAsync(path, cancellationToken));
    }

    private static async Task EnsureFileDigestUnchangedAsync(ReleaseFileDigestSnapshot snapshot, CancellationToken cancellationToken)
    {
        var currentDigest = await ComputeFileDigestAsync(snapshot.Path, cancellationToken);
        if (string.Equals(snapshot.Sha256, currentDigest, StringComparison.Ordinal))
        {
            return;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-current-pointer-concurrent-update",
            "The coordinated current release pointer changed while release preparation was running.",
            $"`{snapshot.Path}` no longer matches the digest captured before generated release files were written.",
            "Review the concurrent change, rerun ./eng/release prepare, and commit only the newly generated release artifacts. Do not manually overwrite the pointer.",
            "releases/coordinated-release-links.md"));
    }

    private static async Task<string?> ComputeFileDigestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task EnsurePreparationBaseCommitUnchangedAsync(string? preparationBaseCommit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(preparationBaseCommit))
        {
            return;
        }

        var current = await _checker.GetSourceCommitAsync(cancellationToken);
        if (string.Equals(preparationBaseCommit, current, StringComparison.Ordinal))
        {
            return;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-preparation-base-commit-concurrent-update",
            "HEAD changed while release preparation was rendering artifacts.",
            $"Preparation started from `{preparationBaseCommit}` but the repository now resolves HEAD to `{current ?? "unknown"}`.",
            "Review the concurrent change, reset only the partial generated release artifacts, and rerun ./eng/release prepare.",
            "releases/release-authoring-checklist.md"));
    }
}

internal sealed record ReleaseFileDigestSnapshot(string Path, string? Sha256);
