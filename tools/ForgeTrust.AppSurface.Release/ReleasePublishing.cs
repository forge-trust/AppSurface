namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Validates tag state and produces GitHub Release workflow outputs.
/// </summary>
internal sealed class ReleasePublishing
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ICommandRunner _commandRunner;

    /// <summary>
    /// Creates release publishing validation workflow.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="commandRunner">Process runner.</param>
    internal ReleasePublishing(ReleaseWorkspace workspace, ICommandRunner commandRunner)
    {
        _workspace = workspace;
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Validates an existing annotated tag and extracts release notes from the tag commit.
    /// </summary>
    /// <param name="options">Publish command options. The version and tag must match, and stable versions require protected stable package publishing proof.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured workflow outputs for GitHub Release creation.</returns>
    /// <remarks>
    /// <see cref="PublishAsync"/> is create-only: it verifies annotated tag shape, reachability from the configured base ref, package
    /// publication, absence of an existing GitHub Release, and presence of <c>releases/v{version}.md</c> in the tag commit. The tag commit
    /// must also contain the release sidecar, release manifest, and release evidence bundle; missing or invalid tag-bound artifacts fail fast
    /// before a GitHub Release is created. The method writes the tag's release note to a temporary file so workflows can pass a stable notes
    /// path to GitHub's release action.
    /// </remarks>
    internal async Task<PublishOutputs> PublishAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        var tag = options.Tag ?? options.Version.TagName;
        await ValidateAnnotatedTagAsync(tag, cancellationToken);
        var tagCommit = await RequireCommandOutputAsync("git", ["rev-parse", $"refs/tags/{tag}^{{commit}}"], "release-tag-commit-missing", cancellationToken);
        await ValidateReachableFromBaseAsync(tag, tagCommit, options.BaseRef, cancellationToken);
        await ValidatePackagePublishingSucceededAsync(options.Version, tag, tagCommit, cancellationToken);
        await ValidateGitHubReleaseDoesNotExistAsync(tag, cancellationToken);

        var safeTagSegment = Path.GetFileName(tag);
        if (string.IsNullOrWhiteSpace(safeTagSegment))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-invalid-temp-path",
                $"Tag '{tag}' cannot be used for release output paths.",
                "The tag does not contain a file-name-safe segment for the temporary release notes path.",
                "Use the canonical release tag form `v{version}` and retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        var notePathInTag = $"releases/v{options.Version}.md";
        var note = await RequireGitBlobOutputAsync(tag, notePathInTag, "release-note-missing-from-tag", cancellationToken);
        var sidecarPathInTag = $"releases/v{options.Version}.md.yml";
        var sidecar = await RequireGitBlobOutputAsync(tag, sidecarPathInTag, "release-sidecar-missing-from-tag", cancellationToken);
        var releaseManifestPathInTag = $"releases/v{options.Version}.release.json";
        var releaseManifest = await RequireGitBlobOutputAsync(tag, releaseManifestPathInTag, "release-manifest-missing-from-tag", cancellationToken);
        var evidencePathInTag = $"releases/v{options.Version}.evidence.json";
        var evidenceJson = await RequireGitBlobOutputAsync(tag, evidencePathInTag, "release-evidence-missing", cancellationToken);
        var evidence = ReleaseEvidence.ValidateTag(
            options.Version,
            options.Version.IsStable ? "stable" : "prerelease",
            tag,
            tagCommit.Trim(),
            note,
            sidecar,
            releaseManifest,
            evidenceJson);
        if (evidence.Diagnostics.Count > 0)
        {
            throw new ReleaseToolException(evidence.Diagnostics[0]);
        }

        if (evidence.Summary is null)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-evidence-schema-invalid",
                "Release evidence did not produce a validation summary.",
                "Publish requires tag-bound evidence to deserialize to a complete release evidence bundle.",
                "Regenerate release evidence from the reviewed release state before publishing.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        var evidenceSummary = evidence.Summary;
        if (options.Version.IsStable && evidence.Bundle is not null)
        {
            var docsEvidence = await ReleaseDocsArchiveGate.ValidateStableAsync(
                _workspace,
                options,
                evidence.Bundle,
                cancellationToken);
            if (docsEvidence.Diagnostics.Count > 0)
            {
                throw new ReleaseToolException(docsEvidence.Diagnostics[0]);
            }

            if (docsEvidence.Proof is not null)
            {
                evidenceSummary = evidenceSummary with
                {
                    DocsArchiveVerificationState = docsEvidence.Proof.State,
                    DocsCatalogPath = docsEvidence.Proof.CatalogPath,
                    DocsTrustedReleaseRootPath = docsEvidence.Proof.TrustedReleaseRootPath,
                    DocsPhysicalExactTreePath = docsEvidence.Proof.PhysicalExactTreePath,
                    DocsVerifiedFileCount = docsEvidence.Proof.VerifiedFileCount
                };
            }
        }

        var tempDirectory = Path.Join(Path.GetTempPath(), "appsurface-release", safeTagSegment);
        Directory.CreateDirectory(tempDirectory);
        var notesFile = Path.Join(tempDirectory, "release-notes.md");
        await File.WriteAllTextAsync(notesFile, note, cancellationToken);

        return new PublishOutputs(
            options.Version.ToString(),
            tag,
            tagCommit,
            notePathInTag,
            notesFile,
            options.Version.IsStable ? "stable" : "prerelease",
            evidencePathInTag,
            evidenceSummary.SubjectSha256,
            tagCommit.Trim(),
            evidenceSummary.DocsReleaseManifestSha256,
            !options.Version.IsStable,
            options.DryRun);
    }

    /// <summary>
    /// Writes publish outputs to a GitHub Actions output file when requested.
    /// </summary>
    /// <param name="outputs">Publish outputs.</param>
    /// <param name="options">Release command options. <see cref="ReleaseOptions.GitHubOutputPath"/> must be a file path, not a root directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Scalar outputs use <c>name=value</c>. Multiline outputs use GitHub's delimiter form. Existing files are appended to match
    /// <c>GITHUB_OUTPUT</c> behavior.
    /// </remarks>
    internal async Task WriteOutputsAsync(PublishOutputs outputs, ReleaseOptions options, CancellationToken cancellationToken)
    {
        if (options.GitHubOutputPath is null)
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(options.GitHubOutputPath);
        if (string.IsNullOrEmpty(outputDirectory))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-github-output-path-invalid",
                "The GitHub output path must be a file path, not a root directory.",
                $"`--github-output {options.GitHubOutputPath}` does not include a parent directory.",
                "Pass a file path such as `$GITHUB_OUTPUT` or `artifacts/release-output.txt`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        Directory.CreateDirectory(outputDirectory);
        var builder = new StringBuilder();
        AppendOutput(builder, "version", outputs.Version);
        AppendOutput(builder, "tag", outputs.Tag);
        AppendOutput(builder, "tag_commit", outputs.TagCommit);
        AppendOutput(builder, "note_path", outputs.NotePath);
        AppendOutput(builder, "notes_file", outputs.NotesFile);
        AppendOutput(builder, "release_classification", outputs.ReleaseClassification);
        AppendOutput(builder, "evidence_path", outputs.EvidencePath);
        AppendOutput(builder, "evidence_subject_sha256", outputs.EvidenceSubjectSha256);
        AppendOutput(builder, "evidence_tag_commit", outputs.EvidenceTagCommit);
        AppendOutput(builder, "docs_release_manifest_sha256", outputs.DocsReleaseManifestSha256 ?? "");
        AppendOutput(builder, "prerelease", outputs.Prerelease ? "true" : "false");
        await File.AppendAllTextAsync(options.GitHubOutputPath, builder.ToString(), cancellationToken);
    }

    private async Task ValidateAnnotatedTagAsync(string tag, CancellationToken cancellationToken)
    {
        var tagType = await RequireCommandOutputAsync("git", ["cat-file", "-t", $"refs/tags/{tag}"], "release-tag-missing", cancellationToken);
        if (!string.Equals(tagType.Trim(), "tag", StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-lightweight",
                $"Tag '{tag}' is not annotated.",
                $"`git cat-file -t refs/tags/{tag}` returned `{tagType.Trim()}`.",
                "Create an annotated tag with `git tag -a` so the release has stable provenance.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }
    }

    private async Task ValidateReachableFromBaseAsync(string tag, string tagCommit, string baseRef, CancellationToken cancellationToken)
    {
        var remoteBaseRef = $"origin/{baseRef}";
        var result = await _commandRunner.RunAsync(
            new CommandInvocation("git", ["merge-base", "--is-ancestor", tagCommit.Trim(), remoteBaseRef], _workspace.RepositoryRoot),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-unreachable-from-base-ref",
                $"Tag '{tag}' does not point at a commit reachable from {remoteBaseRef}.",
                result.StandardError.Length == 0 ? $"Commit {tagCommit.Trim()} is not an ancestor of {remoteBaseRef}." : result.StandardError.Trim(),
                $"Fetch `{remoteBaseRef}`, move the tag only through the normal protected process, and retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }
    }

    private async Task ValidateGitHubReleaseDoesNotExistAsync(string tag, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandInvocation("gh", ["release", "view", tag, "--json", "url"], _workspace.RepositoryRoot),
            cancellationToken);
        if (result.ExitCode == 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-github-release-exists",
                $"GitHub Release '{tag}' already exists.",
                "Release publishing is create-only in v1 to avoid changing already-public notes by accident.",
                "Delete the incorrect draft/release manually or cut a new tag; this tool does not update existing releases.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }
    }

    private async Task ValidatePackagePublishingSucceededAsync(SemVer version, string tag, string tagCommit, CancellationToken cancellationToken)
    {
        var workflow = version.IsStable ? "nuget-stable-publish.yml" : "nuget-prerelease-publish.yml";
        var classification = version.IsStable ? "Stable" : "Prerelease";
        var code = version.IsStable ? "release-stable-packages-not-published" : "release-prerelease-packages-not-published";
        var result = await _commandRunner.RunAsync(
            new CommandInvocation(
                "gh",
                [
                    "run",
                    "list",
                    "--workflow",
                    workflow,
                    "--commit",
                    tagCommit.Trim(),
                    "--json",
                    "conclusion,headBranch,status,url",
                    "--jq",
                    $"[.[] | select(.headBranch == \"{tag}\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\""
                ],
                _workspace.RepositoryRoot),
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                code,
                $"{classification} packages have not been published for tag '{tag}'.",
                result.ExitCode == 0
                    ? $"No successful `{workflow}` run was found for {tag} at {tagCommit.Trim()}."
                    : (string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim()),
                $"Wait for the protected NuGet {classification.ToLowerInvariant()} publish workflow for this tag to complete successfully, then retry GitHub Release publishing.",
                "tools/ForgeTrust.AppSurface.Release/README.md#stable-release-policy"));
        }
    }

    private async Task<string> RequireCommandOutputAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string code,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(new CommandInvocation(executable, arguments, _workspace.RepositoryRoot), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                code,
                $"Command `{executable} {string.Join(' ', arguments)}` failed.",
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim(),
                "Fetch tags and the configured base ref, verify the release artifact exists at the tag commit, then retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    private async Task<string> RequireGitBlobOutputAsync(
        string tag,
        string pathInTag,
        string code,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(new CommandInvocation("git", ["show", $"{tag}:{pathInTag}"], _workspace.RepositoryRoot), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                code,
                $"Command `git show {tag}:{pathInTag}` failed.",
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim(),
                "Fetch tags and the configured base ref, verify the release artifact exists at the tag commit, then retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        return result.StandardOutput;
    }

    private static void AppendOutput(StringBuilder builder, string name, string value)
    {
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            var delimiter = $"EOF_{Guid.NewGuid():N}";
            builder.AppendLine($"{name}<<{delimiter}");
            builder.AppendLine(value);
            builder.AppendLine(delimiter);
            return;
        }

        builder.AppendLine($"{name}={value}");
    }
}
