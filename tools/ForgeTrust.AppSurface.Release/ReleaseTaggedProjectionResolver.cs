using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Resolves prepared release artifacts into a tagged projection bound to an annotated Git tag.
/// </summary>
/// <remarks>
/// This is the single authority for the prepared-to-tagged transition. It reads tag objects and artifact blobs through Git,
/// validates the checked-in evidence bundle and canonical tag trailers, then returns an in-memory sidecar projection. It never
/// mutates repository files, creates tags, or calls GitHub.
/// </remarks>
internal sealed class ReleaseTaggedProjectionResolver
{
    private const string DocsPath = "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state";
    private readonly ReleaseWorkspace _workspace;
    private readonly ICommandRunner _commandRunner;

    internal ReleaseTaggedProjectionResolver(ReleaseWorkspace workspace, ICommandRunner commandRunner)
    {
        _workspace = workspace;
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Generates canonical annotated-tag trailers from the prepared artifacts at HEAD.
    /// </summary>
    /// <param name="version">Release version whose prepared artifacts are read from HEAD.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Canonical trailer block with a trailing newline.</returns>
    internal async Task<string> GenerateTagMessageAsync(SemVer version, CancellationToken cancellationToken)
    {
        var head = await RequireCommandOutputAsync(
            "git",
            ["rev-parse", "HEAD"],
            "release-tag-message-head-missing",
            "Tag-message requires a resolved HEAD commit.",
            cancellationToken);
        var commit = head.Trim();
        var artifacts = await ReadArtifactsAsync(commit, version, "release-tag-message-artifact-missing", cancellationToken);
        var evidence = await ValidateEvidenceAsync(version, version.TagName, commit, artifacts, cancellationToken);
        var sidecar = ReleaseSidecar.Parse(artifacts.Sidecar, $"{commit}:releases/v{version}.md.yml");
        sidecar.EnsurePrepared(version, $"{commit}:releases/v{version}.md.yml");
        var binding = CreateBinding(version, artifacts, evidence);
        return binding.Render();
    }

    /// <summary>
    /// Resolves and validates the tag-bound tagged projection for ordinary inspect and publish.
    /// </summary>
    /// <param name="options">Release command options containing the canonical version, tag, and base ref.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated immutable tag details and transient tagged sidecar YAML.</returns>
    internal Task<ReleaseTaggedProjection> ResolveAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        return ResolveCoreAsync(options, cancellationToken, captureTagObjectIdentity: false);
    }

    /// <summary>
    /// Resolves the tagged projection and pins all tag-related reads to one captured annotated-tag object ID.
    /// </summary>
    /// <param name="options">Release command options containing the canonical version, tag, and base ref.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validated immutable tag details and transient tagged sidecar YAML.</returns>
    internal Task<ReleaseTaggedProjection> ResolveMachineInspectAsync(ReleaseOptions options, CancellationToken cancellationToken)
    {
        return ResolveCoreAsync(options, cancellationToken, captureTagObjectIdentity: true);
    }

    private async Task<ReleaseTaggedProjection> ResolveCoreAsync(
        ReleaseOptions options,
        CancellationToken cancellationToken,
        bool captureTagObjectIdentity)
    {
        var tag = options.Tag ?? options.Version.TagName;
        string? tagObjectId = null;
        string tagObject;
        string tagCommit;
        string artifactRevision;
        if (captureTagObjectIdentity)
        {
            tagObjectId = await ResolveAnnotatedTagObjectIdAsync(tag, cancellationToken);
            tagObject = await RequireCommandOutputAsync(
                "git",
                ["cat-file", "-p", tagObjectId],
                "release-tag-object-missing",
                $"Annotated tag {tag} could not be read.",
                cancellationToken);
            tagCommit = (await RequireCommandOutputAsync(
                "git",
                ["rev-parse", $"{tagObjectId}^{{commit}}"],
                "release-tag-commit-missing",
                $"Annotated tag {tag} does not resolve to a commit.",
                cancellationToken)).Trim();
            if (!IsCanonicalGitObjectId(tagCommit) || tagCommit.Length != tagObjectId.Length)
            {
                throw new ReleaseToolException(ReleaseDiagnostic.Error(
                    "release-tag-commit-id-invalid",
                    $"Annotated tag {tag} resolved to an invalid peeled commit ID.",
                    "The captured tag object and peeled commit must be full lowercase IDs using the same Git object format.",
                    "Check the local tag object and repository object format, then retry inspect.",
                    DocsPath));
            }

            ValidateCapturedTagObjectHeaders(tag, tagObject, tagCommit);

            artifactRevision = tagCommit;
        }
        else
        {
            await RequireAnnotatedTagAsync(tag, cancellationToken);
            tagObject = await RequireCommandOutputAsync(
                "git",
                ["cat-file", "-p", $"refs/tags/{tag}"],
                "release-tag-object-missing",
                $"Annotated tag {tag} could not be read.",
                cancellationToken);
            tagCommit = (await RequireCommandOutputAsync(
                "git",
                ["rev-parse", $"refs/tags/{tag}^{{commit}}"],
                "release-tag-commit-missing",
                $"Annotated tag {tag} does not resolve to a commit.",
                cancellationToken)).Trim();
            artifactRevision = tag;
        }

        var taggerTimestamp = ReleaseTagBinding.ParseTaggerTimestamp(tag, tagObject);
        await RequireReachableFromBaseAsync(tag, tagCommit, options.BaseRef, cancellationToken);

        var artifacts = await ReadArtifactsAsync(artifactRevision, options.Version, "release-note-missing-from-tag", cancellationToken);
        var evidence = await ValidateEvidenceAsync(options.Version, tag, tagCommit, artifacts, cancellationToken);
        var evidenceBundle = RequireCompleteValidationResult(evidence);
        var evidenceSchema = ReleaseEvidence.IsV2(artifacts.Evidence)
            ? ReleaseEvidenceV2.Schema
            : ReleaseEvidence.Schema;
        var preparationBaseCommit = string.Equals(evidenceSchema, ReleaseEvidenceV2.Schema, StringComparison.Ordinal)
            ? evidenceBundle.Commits.ContentSourceCommit
            : null;
        var releaseArtifactDigests = evidenceBundle.ReleaseArtifactDigests;
        var evidenceBundleSha256 = ReleaseEvidence.ComputeSha256Hex(artifacts.Evidence);
        var sidecar = ReleaseSidecar.Parse(artifacts.Sidecar, $"{tag}:releases/v{options.Version}.md.yml");
        sidecar.EnsurePrepared(options.Version, $"{tag}:releases/v{options.Version}.md.yml");
        var expectedBinding = CreateBinding(options.Version, artifacts, evidence);
        ReleaseTagBinding.ParseAndValidate(tag, tagObject, expectedBinding);
        if (tagObjectId is not null)
        {
            await RequireCapturedTagRefUnchangedAsync(tag, tagObjectId, cancellationToken);
        }

        var projectedYaml = sidecar.ToTaggedProjection(
            options.Version,
            taggerTimestamp,
            $"{tag}:releases/v{options.Version}.md.yml");

        return new ReleaseTaggedProjection(
            tag,
            tagObjectId,
            tagCommit,
            taggerTimestamp,
            artifacts.Note,
            projectedYaml,
            evidence,
            evidenceSchema,
            preparationBaseCommit,
            evidenceBundle.Subject.Sha256,
            evidenceBundleSha256,
            releaseArtifactDigests);
    }

    private async Task RequireCapturedTagRefUnchangedAsync(string tag, string capturedTagObjectId, CancellationToken cancellationToken)
    {
        var result = await RunAsync("git", ["rev-parse", "--verify", $"refs/tags/{tag}"], cancellationToken);
        var currentTagObjectId = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
        if (string.Equals(currentTagObjectId, capturedTagObjectId, StringComparison.Ordinal))
        {
            return;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-tag-moved-during-inspect",
            $"Release tag {tag} changed while inspect was validating its artifacts.",
            result.ExitCode == 0
                ? $"Captured tag object {capturedTagObjectId}, but the tag ref now resolves to {currentTagObjectId}."
                : string.IsNullOrWhiteSpace(result.StandardError) ? "The tag ref could not be re-read after artifact validation." : result.StandardError.Trim(),
            "Restore the protected annotated tag identity and rerun inspect; do not accept a result produced across a tag move.",
            DocsPath));
    }

    private static void ValidateCapturedTagObjectHeaders(string tag, string tagObject, string peeledCommit)
    {
        var normalized = tagObject.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var header = separator < 0 ? normalized : normalized[..separator];
        var lines = header.Split('\n');
        var fields = lines.Length >= 3
            && lines[0].StartsWith("object ", StringComparison.Ordinal)
            && lines[1] == "type commit"
            && lines[2].StartsWith("tag ", StringComparison.Ordinal);
        if (fields
            && string.Equals(lines[0]["object ".Length..], peeledCommit, StringComparison.Ordinal)
            && string.Equals(lines[2]["tag ".Length..], tag, StringComparison.Ordinal))
        {
            return;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-tag-object-binding-invalid",
            $"Annotated tag {tag} has headers that do not match its captured tag and peeled commit.",
            "The captured object must directly name the requested tag and the exact commit returned by Git peeling.",
            "Create a canonical annotated release tag directly on the validated release commit and rerun inspect.",
            DocsPath));
    }

    private async Task<string> ResolveAnnotatedTagObjectIdAsync(string tag, CancellationToken cancellationToken)
    {
        var tagObjectResult = await RunAsync("git", ["rev-parse", "--verify", $"refs/tags/{tag}"], cancellationToken);
        if (tagObjectResult.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-missing",
                $"Annotated tag {tag} could not be found.",
                string.IsNullOrWhiteSpace(tagObjectResult.StandardError)
                    ? (string.IsNullOrWhiteSpace(tagObjectResult.StandardOutput) ? "Git did not resolve the requested tag." : tagObjectResult.StandardOutput.Trim())
                    : tagObjectResult.StandardError.Trim(),
                "Create the annotated tag locally, run inspect, then push it only after validation succeeds.",
                DocsPath));
        }

        var tagObjectId = tagObjectResult.StandardOutput.Trim();
        if (!IsCanonicalGitObjectId(tagObjectId))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-object-id-invalid",
                $"Annotated tag {tag} resolved to an invalid Git object ID.",
                "Git must return one full lowercase 40- or 64-character object ID.",
                "Check the local tag ref and Git repository object format, then retry inspect.",
                DocsPath));
        }

        var typeResult = await RunAsync("git", ["cat-file", "-t", tagObjectId], cancellationToken);
        if (typeResult.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-object-missing",
                $"Annotated tag {tag} object {tagObjectId} could not be read.",
                string.IsNullOrWhiteSpace(typeResult.StandardError)
                    ? "Git could not inspect the captured tag object."
                    : typeResult.StandardError.Trim(),
                "Check that the captured local tag object is present, then retry inspect.",
                DocsPath));
        }

        if (!string.Equals(typeResult.StandardOutput.Trim(), "tag", StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-lightweight",
                $"Release tag {tag} is not an annotated tag.",
                $"Git reported object type {typeResult.StandardOutput.Trim()}.",
                "Delete and recreate the unpushed local tag with git tag -a, then run inspect again.",
                DocsPath));
        }

        return tagObjectId;
    }

    private async Task RequireAnnotatedTagAsync(string tag, CancellationToken cancellationToken)
    {
        var result = await RunAsync("git", ["cat-file", "-t", $"refs/tags/{tag}"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-missing",
                $"Annotated tag {tag} could not be found.",
                string.IsNullOrWhiteSpace(result.StandardError) ? "Git did not resolve the requested tag." : result.StandardError.Trim(),
                "Create the annotated tag locally, run inspect, then push it only after validation succeeds.",
                DocsPath));
        }

        if (!string.Equals(result.StandardOutput.Trim(), "tag", StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-lightweight",
                $"Release tag {tag} is not an annotated tag.",
                $"Git reported object type {result.StandardOutput.Trim()}.",
                "Delete and recreate the unpushed local tag with git tag -a, then run inspect again.",
                DocsPath));
        }
    }

    private static bool IsCanonicalGitObjectId(string value) =>
        (value.Length is 40 or 64)
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task RequireReachableFromBaseAsync(string tag, string tagCommit, string baseRef, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "git",
            ["merge-base", "--is-ancestor", tagCommit, $"origin/{baseRef}"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-unreachable-from-base-ref",
                $"Release tag {tag} is not reachable from origin/{baseRef}.",
                $"Resolved tag commit {tagCommit} is outside the configured protected release branch.",
                "Checkout the merged release commit, fetch origin, create the tag there, and pass the matching --base-ref.",
                DocsPath));
        }
    }

    private async Task<ReleaseTagArtifacts> ReadArtifactsAsync(
        string revision,
        SemVer version,
        string missingCode,
        CancellationToken cancellationToken)
    {
        var note = await RequireGitBlobOutputAsync(revision, $"releases/v{version}.md", missingCode, cancellationToken);
        var sidecar = await RequireGitBlobOutputAsync(revision, $"releases/v{version}.md.yml", missingCode, cancellationToken);
        var manifest = await RequireGitBlobOutputAsync(revision, $"releases/v{version}.release.json", missingCode, cancellationToken);
        var evidence = await RequireGitBlobOutputAsync(revision, $"releases/v{version}.evidence.json", missingCode, cancellationToken);
        if (!ReleaseEvidence.IsV2(evidence))
        {
            return new ReleaseTagArtifacts(note, sidecar, manifest, evidence, null, null, null);
        }

        var currentRelease = await RequireGitBlobOutputAsync(
            revision,
            PackageReleaseLink.CoordinatedReleaseNotesPath,
            "release-current-pointer-missing-from-tag",
            cancellationToken);
        var currentReleaseSidecar = await RequireGitBlobOutputAsync(
            revision,
            PackageReleaseLink.CoordinatedReleaseSidecarPath,
            "release-current-pointer-sidecar-missing-from-tag",
            cancellationToken);
        var packageIndex = await RequireGitBlobOutputAsync(
            revision,
            "packages/package-index.yml",
            "release-package-index-missing-from-tag",
            cancellationToken);
        return new ReleaseTagArtifacts(note, sidecar, manifest, evidence, currentRelease, currentReleaseSidecar, packageIndex);
    }

    private async Task<ReleaseEvidenceValidationResult> ValidateEvidenceAsync(
        SemVer version,
        string tag,
        string tagCommit,
        ReleaseTagArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        var evidence = ReleaseEvidence.ValidateTag(
            version,
            version.IsStable ? "stable" : "prerelease",
            tag,
            tagCommit,
            artifacts.Note,
            artifacts.Sidecar,
            artifacts.Manifest,
            artifacts.Evidence,
            artifacts.CurrentRelease,
            artifacts.CurrentReleaseSidecar);
        if (evidence.Diagnostics.Count > 0)
        {
            throw new ReleaseToolException(evidence.Diagnostics[0]);
        }

        var evidenceBundle = RequireCompleteValidationResult(evidence);

        if (ReleaseEvidence.IsV2(artifacts.Evidence))
        {
            var packageSummary = PackageIndexSummary.Load(artifacts.PackageIndex!);
            if (!ReleaseManifestV2Validator.TryDeserialize(artifacts.Manifest, out var manifest, out var issue)
                || !ReleaseManifestV2Validator.TryValidatePackageSet(manifest, packageSummary.PublicPublishedPackages, out issue))
            {
                throw new ReleaseToolException(ReleaseDiagnostic.Error(
                    "release-evidence-package-set-mismatch",
                    "V2 release evidence does not attest to the tagged package-index release surface.",
                    issue,
                    "Regenerate the release manifest and evidence from the package index in the tagged release tree.",
                    DocsPath));
            }

            await ValidatePreparationBaseContainedByTagAsync(
                tag,
                evidenceBundle.Commits.ContentSourceCommit,
                tagCommit,
                cancellationToken);
        }

        return evidence;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "ReleaseEvidence validates that a diagnostic-free result includes both bundle and summary; this defensive invariant cannot be reached through the public release contracts.")]
    private static ReleaseEvidenceBundle RequireCompleteValidationResult(ReleaseEvidenceValidationResult evidence)
    {
        if (evidence.Bundle is null || evidence.Summary is null)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-evidence-schema-invalid",
                "Release evidence did not produce a complete validation result.",
                "The evidence bundle could not be used to establish the tag binding.",
                "Regenerate the prepared release artifacts and evidence before creating the annotated tag.",
                DocsPath));
        }

        return evidence.Bundle;
    }

    private async Task ValidatePreparationBaseContainedByTagAsync(
        string tag,
        string? preparationBaseCommit,
        string tagCommit,
        CancellationToken cancellationToken)
    {
        var canonicalPreparationBaseCommit = preparationBaseCommit ?? string.Empty;
        if (!IsCanonicalCommitId(canonicalPreparationBaseCommit))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-preparation-base-commit-invalid",
                $"Tag {tag} has V2 release evidence with an invalid preparation base commit.",
                "V2 evidence must preserve the exact 40-character lowercase Git commit captured before release preparation wrote artifacts.",
                "Regenerate the release preparation artifacts from the reviewed source commit before creating the tag.",
                DocsPath));
        }

        var result = await RunAsync(
            "git",
            ["merge-base", "--is-ancestor", canonicalPreparationBaseCommit, tagCommit],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-preparation-base-commit-not-contained-by-tag",
                $"Tag {tag} does not contain the V2 evidence preparation base commit.",
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"Preparation base commit {canonicalPreparationBaseCommit} is not an ancestor of tag commit {tagCommit}."
                    : result.StandardError.Trim(),
                "Create the annotated tag from a commit that contains the reviewed release preparation artifacts and their captured source commit.",
                DocsPath));
        }
    }

    private static bool IsCanonicalCommitId(string value) =>
        value.Length == 40
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ReleaseTagBinding CreateBinding(
        SemVer version,
        ReleaseTagArtifacts artifacts,
        ReleaseEvidenceValidationResult evidence)
    {
        return new ReleaseTagBinding(
            version.TagName,
            ReleaseEvidence.ComputeSha256Hex(artifacts.Sidecar),
            ReleaseEvidence.ComputeSha256Hex(artifacts.Manifest),
            evidence.Bundle!.Subject.Sha256);
    }

    private async Task<string> RequireGitBlobOutputAsync(
        string revision,
        string path,
        string missingCode,
        CancellationToken cancellationToken)
    {
        return await RequireCommandOutputAsync(
            "git",
            ["show", $"{revision}:{path}"],
            missingCode,
            $"Release artifact {path} could not be read from {revision}.",
            cancellationToken);
    }

    private async Task<string> RequireCommandOutputAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string code,
        string problem,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(executable, arguments, cancellationToken);
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardOutput;
        }

        throw new ReleaseToolException(ReleaseDiagnostic.Error(
            code,
            problem,
            string.IsNullOrWhiteSpace(result.StandardError)
                ? (string.IsNullOrWhiteSpace(result.StandardOutput) ? "The command did not produce the required output." : result.StandardOutput.Trim())
                : result.StandardError.Trim(),
            "Check the tag, release artifacts, and local Git refs, then retry inspect before pushing the tag.",
            DocsPath));
    }

    private Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        return _commandRunner.RunAsync(
            new CommandInvocation(executable, arguments, _workspace.RepositoryRoot),
            cancellationToken);
    }
}

/// <summary>
/// Exact release artifacts read from one Git revision.
/// </summary>
internal sealed record ReleaseTagArtifacts(
    string Note,
    string Sidecar,
    string Manifest,
    string Evidence,
    string? CurrentRelease,
    string? CurrentReleaseSidecar,
    string? PackageIndex);

/// <summary>
/// Result of a successful prepared-to-tagged projection resolution.
/// </summary>
internal sealed record ReleaseTaggedProjection(
    string Tag,
    string? TagObjectId,
    string TagCommit,
    DateTimeOffset TaggerTimestamp,
    string ReleaseNote,
    string SidecarYaml,
    ReleaseEvidenceValidationResult Evidence,
    string EvidenceSchema,
    string? PreparationBaseCommit,
    string EvidenceSubjectSha256,
    string EvidenceBundleSha256,
    IReadOnlyList<ReleaseEvidenceArtifactDigest> ReleaseArtifactDigests);
