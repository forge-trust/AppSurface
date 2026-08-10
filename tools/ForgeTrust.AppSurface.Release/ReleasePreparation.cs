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
    private readonly Func<UnreleasedEntrySnapshot, CancellationToken, Task>? _beforeArchiveEntryAsync;
    private readonly Func<UnreleasedEntrySnapshot, string, CancellationToken, Task>? _afterArchiveEntryHandoffAsync;

    /// <summary>
    /// Creates release preparation workflow.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="checker">Release readiness checker.</param>
    /// <param name="clock">Clock for default dates.</param>
    /// <param name="beforeWriteAsync">Optional test seam invoked after release content is rendered and before pointer-digest revalidation.</param>
    /// <param name="beforeArchiveEntryAsync">Optional test seam invoked after an entry's final digest check and immediately before its guarded archive handoff.</param>
    /// <param name="afterArchiveEntryHandoffAsync">Optional test seam invoked after a source entry moves to private recovery and before the moved bytes are verified.</param>
    internal ReleasePreparation(
        ReleaseWorkspace workspace,
        ReleaseChecker checker,
        IReleaseClock clock,
        Func<CancellationToken, Task>? beforeWriteAsync = null,
        Func<UnreleasedEntrySnapshot, CancellationToken, Task>? beforeArchiveEntryAsync = null,
        Func<UnreleasedEntrySnapshot, string, CancellationToken, Task>? afterArchiveEntryHandoffAsync = null)
    {
        _workspace = workspace;
        _checker = checker;
        _clock = clock;
        _beforeWriteAsync = beforeWriteAsync;
        _beforeArchiveEntryAsync = beforeArchiveEntryAsync;
        _afterArchiveEntryHandoffAsync = afterArchiveEntryHandoffAsync;
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
    /// files, removes consumed append-only unreleased entries, and records diagnostics in the release manifest. Coordinated package rows are intentionally not rewritten: each docs export
    /// freezes the current pointer that was generated for its release. Dry-run mode performs all reads and rendering but does not write files.
    /// The method does not create git branches, tags, commits, package artifacts, or GitHub Releases; workflows own those operations.
    /// Callers should treat any readiness errors as blocking and should avoid running against a dirty or concurrently modified tree.
    /// Writes are sequential rather than transactional. The current pointer is written last, so a partial write cannot advance the
    /// visible coordinated alias before the matching versioned artifacts and living-note roll-forward exist. If the local process fails,
    /// rerun <c>git status</c> and remove or revert the partial generated artifacts before retrying so create-only target checks do not
    /// stop the next run.
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
        UnreleasedEntrySet unreleasedEntries;
        string unreleased;
        try
        {
            var unreleasedTemplate = await File.ReadAllTextAsync(_workspace.UnreleasedPath, cancellationToken);
            unreleasedEntries = await UnreleasedEntryComposer.LoadAsync(_workspace.UnreleasedEntriesDirectory, cancellationToken);
            unreleased = UnreleasedEntryComposer.Compose(unreleasedTemplate, unreleasedEntries.Entries);
        }
        catch (UnreleasedEntryException ex)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.InvalidUnreleasedEntry(ex.Message));
        }

        var sidecar = await ReleaseSidecar.LoadAsync(_workspace.UnreleasedSidecarPath, cancellationToken);
        var currentReleaseSidecar = await File.ReadAllTextAsync(_workspace.CurrentReleaseSidecarPath, cancellationToken);
        var packageSummary = await PackageIndexSummary.LoadAsync(_workspace.PackageIndexPath, cancellationToken);
        var generatedPaths = new List<string>();
        var archivedEntryPaths = new List<string>();
        var releaseNotePath = _workspace.ReleaseNotePath(options.Version);
        var releaseSidecarPath = _workspace.ReleaseSidecarPath(options.Version);
        var releaseManifestPath = _workspace.ReleaseManifestPath(options.Version);
        var releaseEvidencePath = _workspace.ReleaseEvidencePath(options.Version);
        var releasePath = $"releases/v{options.Version}.md";
        var currentReleasePath = _workspace.CurrentReleasePath;

        var releaseNote = ReleaseNoteBuilder.Build(options.Version, date, unreleased);
        var releaseSidecar = sidecar.ToPreparedRelease(options.Version, date);
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
            ReleaseManifestV2Validator.Schema,
            options.Version.ToString(),
            options.Version.TagName,
            date.ToString("yyyy-MM-dd"),
            check.SourceCommit,
            check.ReleaseClassification,
            check.GeneratedFiles,
            packageSummary.PublicPublishedPackages.Select(package => package.Project).OrderBy(project => project, StringComparer.Ordinal).ToArray(),
            coordinatedResolutions,
            check.Errors.Concat(check.Warnings).Select(ReleaseDiagnosticRecord.FromDiagnostic).ToArray(),
            check.Warnings.Select(warning => warning.Code).ToArray())
        {
            ConsumedUnreleasedEntryPaths = unreleasedEntries.Paths
                .Select(_workspace.DisplayPath)
                .ToArray()
        };
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

        var writes = new List<KeyValuePair<string, string>>
        {
            new(releaseNotePath, releaseNote),
            new(releaseSidecarPath, releaseSidecar),
            new(releaseManifestPath, releaseManifestContent),
            new(releaseEvidencePath, releaseEvidenceContent),
            new(_workspace.ChangelogPath, ChangelogEditor.RollForward(changelog, options.Version, date, releasePath)),
            new(_workspace.UnreleasedPath, nextUnreleased),
            new(_workspace.UnreleasedSidecarPath, ReleaseSidecar.UnreleasedTemplate()),
            new(currentReleasePath, currentRelease)
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
            foreach (var entrySnapshot in unreleasedEntries.Snapshots)
            {
                await EnsureUnreleasedEntryDigestUnchangedAsync(entrySnapshot, cancellationToken);
            }

            foreach (var (path, _) in writes)
            {
                EnsureSafeWriteTarget(path);
            }

            foreach (var (path, content) in writes.Take(writes.Count - 1))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, cancellationToken);
                generatedPaths.Add(_workspace.DisplayPath(path));
            }

            foreach (var entrySnapshot in unreleasedEntries.Snapshots)
            {
                await ArchiveUnreleasedEntryAsync(entrySnapshot, cancellationToken);
                archivedEntryPaths.Add(_workspace.DisplayPath(entrySnapshot.Path));
            }

            var (currentPointerPath, currentPointerContent) = writes[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(currentPointerPath)!);
            await File.WriteAllTextAsync(currentPointerPath, currentPointerContent, cancellationToken);
            generatedPaths.Add(_workspace.DisplayPath(currentPointerPath));
        }
        else
        {
            generatedPaths.AddRange(writes.Select(write => _workspace.DisplayPath(write.Key)));
            archivedEntryPaths.AddRange(unreleasedEntries.Paths.Select(_workspace.DisplayPath));
        }

        return new ReleasePreparationResult(check, generatedPaths, options.DryRun, evidence.ToSummary("draft evidence for release-prep review"))
        {
            ArchivedUnreleasedEntryPaths = archivedEntryPaths
        };
    }

    private void EnsureSafeWriteTarget(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!ReleaseDocsArchiveGate.TryValidateNoReparseSegments(_workspace.RepositoryRoot, directory, out var directoryIssue))
        {
            throw UnsafePreparationOutput(path, directoryIssue ?? "The output directory is outside the repository or includes a reparse point.");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw UnsafePreparationOutput(path, "The existing output is a directory.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw UnsafePreparationOutput(path, "The existing output is a symlink, junction, or reparse point.");
            }
        }
        catch (ReleaseToolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw UnsafePreparationOutput(path, $"The existing output could not be inspected: {ex.Message}");
        }
    }

    private ReleaseToolException UnsafePreparationOutput(string path, string cause) =>
        new(ReleaseDiagnostic.Error(
            "release-preparation-output-path-unsafe",
            $"Release preparation cannot write generated output '{_workspace.DisplayPath(path)}'.",
            cause,
            "Restore ordinary repository directories and files for the generated release paths, then rerun release preparation.",
            "tools/ForgeTrust.AppSurface.Release/README.md#prepare"));

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

    private async Task EnsureUnreleasedEntryDigestUnchangedAsync(UnreleasedEntrySnapshot snapshot, CancellationToken cancellationToken)
    {
        var currentDigest = await ComputeFileDigestAsync(snapshot.Path, cancellationToken);
        if (string.Equals(snapshot.Sha256, currentDigest, StringComparison.Ordinal))
        {
            return;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-unreleased-entry-concurrent-update",
            "An unreleased entry changed while release preparation was running.",
            $"`{_workspace.DisplayPath(snapshot.Path)}` no longer matches the digest used to build the tagged release note.",
            "Review the concurrent entry update, remove only partial generated release artifacts if any were written, and rerun ./eng/release prepare. Do not delete the entry by hand.",
            "releases/README.md#append-only-unreleased-entries"));
    }

    /// <summary>
    /// Removes one consumed entry without deleting a concurrently replaced file.
    /// </summary>
    /// <remarks>
    /// Filesystem deletion is pathname-based, so a digest check immediately followed by <see cref="File.Delete(string)"/>
    /// could delete a replacement written in the intervening window. The guarded handoff atomically moves the current
    /// pathname to a private recovery location, verifies the moved bytes, and deletes only that verified private file.
    /// When the bytes differ, it restores the candidate without overwrite; if another writer already recreated the source
    /// pathname, the changed candidate remains in the recovery location for manual reconciliation instead of being lost.
    /// </remarks>
    private async Task ArchiveUnreleasedEntryAsync(UnreleasedEntrySnapshot snapshot, CancellationToken cancellationToken)
    {
        await EnsureUnreleasedEntryDigestUnchangedAsync(snapshot, cancellationToken);
        if (_beforeArchiveEntryAsync is not null)
        {
            await _beforeArchiveEntryAsync(snapshot, cancellationToken);
        }

        EnsureSafeWriteTarget(snapshot.Path);
        var recoveryPath = CreateUnreleasedEntryRecoveryPath(snapshot.Path);
        EnsureSafeRecoveryDirectory(Path.GetDirectoryName(recoveryPath)!);
        EnsureSafeWriteTarget(recoveryPath);

        try
        {
            File.Move(snapshot.Path, recoveryPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ConcurrentUnreleasedEntryUpdate(snapshot, recoveryPath: null, $"The guarded archive handoff could not move the entry: {ex.Message}");
        }

        if (_afterArchiveEntryHandoffAsync is not null)
        {
            await _afterArchiveEntryHandoffAsync(snapshot, recoveryPath, cancellationToken);
        }

        var movedDigest = await ComputeFileDigestAsync(recoveryPath, cancellationToken);
        if (string.Equals(snapshot.Sha256, movedDigest, StringComparison.Ordinal))
        {
            EnsureSafeWriteTarget(recoveryPath);
            File.Delete(recoveryPath);
            return;
        }

        if (TryRestoreUnreleasedEntry(recoveryPath, snapshot.Path))
        {
            throw ConcurrentUnreleasedEntryUpdate(snapshot, recoveryPath: null, "The entry changed after release preparation's final digest check and was restored without deletion.");
        }

        throw ConcurrentUnreleasedEntryUpdate(
            snapshot,
            recoveryPath,
            "The entry changed after release preparation's final digest check and another writer recreated its source path before the changed candidate could be restored.");
    }

    private string CreateUnreleasedEntryRecoveryPath(string entryPath)
    {
        var recoveryDirectory = _workspace.PathFor("releases/.release-prep-recovery");
        var recoveryFileName = $"{Guid.NewGuid():N}-{Path.GetFileName(entryPath)}.recovery";
        return Path.Join(recoveryDirectory, recoveryFileName);
    }

    private void EnsureSafeRecoveryDirectory(string recoveryDirectory)
    {
        var releasesDirectory = Path.GetDirectoryName(recoveryDirectory)!;
        if (!ReleaseDocsArchiveGate.TryValidateNoReparseSegments(_workspace.RepositoryRoot, releasesDirectory, out var releasesDirectoryIssue))
        {
            throw UnsafePreparationOutput(recoveryDirectory, releasesDirectoryIssue ?? "The releases directory is outside the repository or includes a reparse point.");
        }

        Directory.CreateDirectory(recoveryDirectory);
        if (!ReleaseDocsArchiveGate.TryValidateNoReparseSegments(_workspace.RepositoryRoot, recoveryDirectory, out var recoveryDirectoryIssue))
        {
            throw UnsafePreparationOutput(recoveryDirectory, recoveryDirectoryIssue ?? "The entry recovery directory is outside the repository or includes a reparse point.");
        }
    }

    private static bool TryRestoreUnreleasedEntry(string recoveryPath, string entryPath)
    {
        try
        {
            File.Move(recoveryPath, entryPath, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ReleaseToolException ConcurrentUnreleasedEntryUpdate(
        UnreleasedEntrySnapshot snapshot,
        string? recoveryPath,
        string cause)
    {
        var recoveryGuidance = recoveryPath is null
            ? "The changed entry remains at or was restored to its original path."
            : $"The changed entry was preserved at '{_workspace.DisplayPath(recoveryPath)}' because its original path was recreated.";
        return new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-unreleased-entry-concurrent-update",
            "An unreleased entry changed while release preparation was running.",
            $"{cause} {recoveryGuidance}",
            "Review the concurrent entry update, preserve any recovery file, remove only partial generated release artifacts if any were written, and rerun ./eng/release prepare. Do not delete the entry by hand.",
            "releases/README.md#append-only-unreleased-entries"));
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
