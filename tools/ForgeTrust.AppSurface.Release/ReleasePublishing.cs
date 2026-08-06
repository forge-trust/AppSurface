namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Validates tag state and produces GitHub Release workflow outputs.
/// </summary>
internal sealed class ReleasePublishing
{
    private readonly ReleaseWorkspace _workspace;
    private readonly ICommandRunner _commandRunner;
    private readonly ReleaseTaggedProjectionResolver _taggedProjectionResolver;

    /// <summary>
    /// Creates release publishing validation with the standard tagged projection resolver.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="commandRunner">Process runner.</param>
    internal ReleasePublishing(ReleaseWorkspace workspace, ICommandRunner commandRunner)
        : this(workspace, commandRunner, new ReleaseTaggedProjectionResolver(workspace, commandRunner))
    {
    }

    /// <summary>
    /// Creates release publishing validation workflow.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    /// <param name="commandRunner">Process runner.</param>
    /// <param name="taggedProjectionResolver">Resolver that establishes the tag-bound release state before remote checks.</param>
    internal ReleasePublishing(
        ReleaseWorkspace workspace,
        ICommandRunner commandRunner,
        ReleaseTaggedProjectionResolver taggedProjectionResolver)
    {
        _workspace = workspace;
        _commandRunner = commandRunner;
        _taggedProjectionResolver = taggedProjectionResolver;
    }

    /// <summary>
    /// Validates an existing annotated tag and extracts release notes from the tag commit.
    /// </summary>
    /// <param name="options">Publish command options. The version and tag must match, and stable versions require protected stable package publishing proof.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Structured workflow outputs for GitHub Release creation.</returns>
    /// <remarks>
    /// <see cref="PublishAsync"/> verifies annotated tag shape, reachability from the configured base ref, package publication, draft-safe
    /// GitHub Release state, and presence of <c>releases/v{version}.md</c> in the tag commit. The tag commit must also contain the release
    /// sidecar, release manifest, and release evidence bundle; missing or invalid tag-bound artifacts fail fast before a GitHub Release is
    /// created or promoted. The method writes the tag's release note to a temporary file so workflows can pass a stable notes path to GitHub's
    /// release action.
    /// </remarks>
    internal async Task<PublishOutputs> PublishAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        var requestedTag = options.Tag ?? options.Version.TagName;
        var safeTagSegment = Path.GetFileName(requestedTag);
        if (string.IsNullOrWhiteSpace(safeTagSegment))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-invalid-temp-path",
                $"Tag '{requestedTag}' cannot be used for release output paths.",
                "The tag does not contain a file-name-safe segment for the temporary release notes path.",
                "Use the canonical release tag form `v{version}` and retry.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        var projection = await _taggedProjectionResolver.ResolveAsync(options, cancellationToken);
        var tag = projection.Tag;
        var tagCommit = projection.TagCommit;
        await ValidatePackagePublishingSucceededAsync(options.Version, tag, tagCommit, cancellationToken);
        await ValidateGitHubReleaseDraftSafeAsync(tag, cancellationToken);

        var notePathInTag = $"releases/v{options.Version}.md";
        var evidencePathInTag = $"releases/v{options.Version}.evidence.json";
        var evidenceSummary = projection.Evidence.Summary!;
        if (options.Version.IsStable && projection.Evidence.Bundle is not null && options.DocsCatalogPath is not null)
        {
            var docsEvidence = await ReleaseDocsArchiveGate.ValidateStableAsync(
                _workspace,
                options,
                projection.Evidence.Bundle,
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
        await File.WriteAllTextAsync(notesFile, projection.ReleaseNote, cancellationToken);

        return new PublishOutputs(
            options.Version.ToString(),
            tag,
            tagCommit,
            notePathInTag,
            notesFile,
            options.Version.IsStable ? "stable" : "prerelease",
            evidencePathInTag,
            evidenceSummary.SubjectSha256,
            tagCommit,
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

    private async Task ValidateGitHubReleaseDraftSafeAsync(string tag, CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandInvocation("gh", ["release", "view", tag, "--json", "isDraft,url"], _workspace.RepositoryRoot),
            cancellationToken);
        if (result.ExitCode == 0)
        {
            try
            {
                using var release = JsonDocument.Parse(result.StandardOutput);
                if (release.RootElement.TryGetProperty("isDraft", out var isDraft) && isDraft.ValueKind == JsonValueKind.True)
                {
                    return;
                }
            }
            catch (JsonException ex)
            {
                throw new ReleaseToolException(ReleaseDiagnostic.Error(
                    "release-github-release-state-invalid",
                    $"GitHub Release '{tag}' state could not be parsed.",
                    ex.Message,
                    "Inspect the release manually before retrying automated publication.",
                    "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
            }

            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-github-release-exists",
                $"GitHub Release '{tag}' already exists.",
                "Release publishing may only reuse unpublished draft releases; public release assets and notes are no-clobber by default.",
                "Recover manually or cut a fix-forward release; this tool does not mutate already-public releases.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        if (IsGitHubReleaseNotFound(result))
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = $"`gh release view {tag}` exited with code {result.ExitCode}.";
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-github-release-state-unavailable",
            $"GitHub Release '{tag}' state could not be verified.",
            detail,
            "Retry after GitHub CLI authentication, API availability, or rate-limit issues are resolved.",
            "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
    }

    private static bool IsGitHubReleaseNotFound(CommandResult result)
    {
        var output = string.Concat(result.StandardOutput, "\n", result.StandardError);
        return output.Contains("release not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("could not find a release", StringComparison.OrdinalIgnoreCase);
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
