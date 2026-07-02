using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Creates and validates checked-in release evidence bundles.
/// </summary>
/// <remarks>
/// Release evidence is repository consistency evidence, not a signature or hosted-build attestation. The bundle ties together
/// release-owned files, package release-note paths, optional docs archive catalog fields, and split commit identities so release
/// preparation can be reviewed in a pull request and publishing can validate the same bundle at the annotated tag commit.
/// </remarks>
internal static class ReleaseEvidence
{
    internal const string Schema = "appsurface-release-evidence-bundle-v1";
    private const string DocsArchiveNotConfigured = "notConfigured";
    internal const string DocsArchiveGeneratedDigest = "generated";

    /// <summary>
    /// Builds a draft release evidence bundle for release preparation.
    /// </summary>
    internal static ReleaseEvidenceBundle BuildDraft(
        ReleaseWorkspace workspace,
        SemVer version,
        string releaseClassification,
        DateOnly date,
        string? contentSourceCommit,
        string releaseNoteContent,
        string releaseSidecarContent,
        string releaseManifestContent,
        IReadOnlyList<PackagePathUpdate> packagePathUpdates)
    {
        var evidencePath = workspace.DisplayPath(workspace.ReleaseEvidencePath(version));
        var releaseNotePath = workspace.DisplayPath(workspace.ReleaseNotePath(version));
        var releaseSidecarPath = workspace.DisplayPath(workspace.ReleaseSidecarPath(version));
        var releaseManifestPath = workspace.DisplayPath(workspace.ReleaseManifestPath(version));
        var bundle = new ReleaseEvidenceBundle(
            Schema,
            version.ToString(),
            version.TagName,
            releaseClassification,
            releaseNotePath,
            releaseSidecarPath,
            releaseManifestPath,
            evidencePath,
            new ReleaseEvidenceFileDigest("sha256", ComputeSha256Hex(releaseManifestContent)),
            ReleaseArtifactDigests(
                (releaseNotePath, releaseNoteContent),
                (releaseSidecarPath, releaseSidecarContent),
                (releaseManifestPath, releaseManifestContent)),
            packagePathUpdates
                .Select(update => new ReleaseEvidencePackagePath(update.Project, update.NextReleaseNotesPath))
                .OrderBy(update => update.Project, StringComparer.Ordinal)
                .ToArray(),
            new ReleaseEvidenceDocsArchive(
                DocsArchiveNotConfigured,
                ExactTreePath: null,
                ReleaseManifestSha256: null,
                ReleaseManifestSchema: null,
                FileCount: null,
                CatalogEntry: null),
            new ReleaseEvidenceCommits(
                ContentSourceCommit: contentSourceCommit,
                ReleasePreparationCommit: null,
                TagCommit: null,
                WorkflowRunId: null),
            new ReleaseEvidenceGeneratedBy("./eng/release"),
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            new ReleaseEvidenceSubject("release-evidence", string.Empty),
            Attestation: null);

        var subject = new ReleaseEvidenceSubject(
            $"releases/v{version}.evidence.json",
            ComputeSubjectSha256(bundle));
        return bundle with { Subject = subject };
    }

    /// <summary>
    /// Validates a checked-in release evidence bundle in the current worktree.
    /// </summary>
    internal static async Task<ReleaseEvidenceValidationResult> ValidatePreparedAsync(
        ReleaseWorkspace workspace,
        SemVer version,
        string releaseClassification,
        string? contentSourceCommit,
        CancellationToken cancellationToken)
    {
        var evidencePath = workspace.ReleaseEvidencePath(version);
        var diagnostics = ValidateEvidenceFileSet(workspace, version).ToList();
        if (!File.Exists(evidencePath))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-missing",
                $"Release evidence bundle '{workspace.DisplayPath(evidencePath)}' is missing.",
                "Release-prep review must include the generated release evidence bundle alongside the release note, sidecar, and release JSON.",
                $"Run `./eng/release prepare --version {version}` and include `{workspace.DisplayPath(evidencePath)}` in the release preparation pull request.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
            return new ReleaseEvidenceValidationResult(null, diagnostics, null);
        }

        ReleaseEvidenceBundle? bundle = null;
        try
        {
            var json = await File.ReadAllTextAsync(evidencePath, cancellationToken);
            bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(json, ReleaseJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-schema-invalid",
                $"Release evidence bundle '{workspace.DisplayPath(evidencePath)}' could not be parsed.",
                ex.Message,
                "Regenerate the release evidence bundle instead of hand-editing JSON fields.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
        }

        if (bundle is not null)
        {
            if (TryValidateRequiredShape(bundle, out var issue))
            {
                diagnostics.AddRange(await ValidateBundleAsync(
                    workspace,
                    version,
                    releaseClassification,
                    contentSourceCommit,
                    bundle,
                    cancellationToken));
            }
            else
            {
                diagnostics.Add(InvalidShapeDiagnostic(issue, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
                bundle = null;
            }
        }
        else if (diagnostics.All(diagnostic => diagnostic.Code != "release-evidence-schema-invalid"))
        {
            diagnostics.Add(InvalidShapeDiagnostic(
                "Release evidence JSON must be an object, not the JSON literal `null`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
        }

        return new ReleaseEvidenceValidationResult(bundle?.ToSummary("draft evidence for release-prep review"), diagnostics, bundle);
    }

    /// <summary>
    /// Validates release evidence read from an annotated tag.
    /// </summary>
    internal static ReleaseEvidenceValidationResult ValidateTag(
        SemVer version,
        string releaseClassification,
        string tag,
        string tagCommit,
        string releaseNoteJson,
        string releaseSidecarJson,
        string releaseManifestJson,
        string evidenceJson)
    {
        var diagnostics = new List<ReleaseDiagnostic>();
        ReleaseEvidenceBundle? bundle = null;
        try
        {
            bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(evidenceJson, ReleaseJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-schema-invalid",
                "Release evidence bundle from the annotated tag could not be parsed.",
                ex.Message,
                "Regenerate release evidence from the reviewed release state before publishing.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        if (bundle is not null)
        {
            if (!TryValidateRequiredShape(bundle, out var issue))
            {
                diagnostics.Add(InvalidShapeDiagnostic(issue, "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
                bundle = null;
            }
            else
            {
                ValidateCommonFields(version, releaseClassification, bundle, diagnostics);
                if (!string.Equals(bundle.Tag, tag, StringComparison.Ordinal))
                {
                    diagnostics.Add(ReleaseDiagnostic.Error(
                        "release-evidence-version-mismatch",
                        "Release evidence tag does not match the annotated tag being published.",
                        $"Evidence tag `{bundle.Tag}` does not match `{tag}`.",
                        "Publish with matching `--version` and `--tag`, or regenerate evidence for the reviewed release.",
                        "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
                }

                if (!string.IsNullOrWhiteSpace(bundle.Commits.TagCommit)
                    && !string.Equals(bundle.Commits.TagCommit, tagCommit, StringComparison.Ordinal))
                {
                    diagnostics.Add(ReleaseDiagnostic.Error(
                        "release-evidence-tag-commit-mismatch",
                        "Release evidence does not match the annotated tag commit.",
                        $"Evidence tag commit `{bundle.Commits.TagCommit}` does not match resolved tag commit `{tagCommit}`.",
                        "Regenerate or finalize evidence from the reviewed tag commit before publishing.",
                        "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
                }

                ValidateReleaseManifestDigest(bundle, releaseManifestJson, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#publish");
                ValidateReleaseManifestSourceCommit(bundle, releaseManifestJson, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#publish");
                ValidateArtifactDigests(bundle, releaseNoteJson, releaseSidecarJson, releaseManifestJson, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#publish");
                ValidatePackagePaths(version, bundle, diagnostics);
                ValidateSubject(bundle, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#publish");
                ValidateDocsArchive(bundle, releaseClassification, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#publish");
            }
        }
        else if (diagnostics.All(diagnostic => diagnostic.Code != "release-evidence-schema-invalid"))
        {
            diagnostics.Add(InvalidShapeDiagnostic(
                "Release evidence JSON must be an object, not the JSON literal `null`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#publish"));
        }

        var summary = bundle is null
            ? null
            : bundle.ToSummary("tag-bound evidence validated for publish") with { TagCommit = tagCommit };
        return new ReleaseEvidenceValidationResult(summary, diagnostics, bundle);
    }

    internal static string Serialize(ReleaseEvidenceBundle bundle)
    {
        return JsonSerializer.Serialize(bundle, ReleaseJson.Options) + Environment.NewLine;
    }

    internal static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IEnumerable<ReleaseDiagnostic> ValidateEvidenceFileSet(ReleaseWorkspace workspace, SemVer version)
    {
        var releasesDirectory = Path.Join(workspace.RepositoryRoot, "releases");
        if (!Directory.Exists(releasesDirectory))
        {
            yield break;
        }

        var expectedPath = Path.GetFullPath(workspace.ReleaseEvidencePath(version));
        var matches = Directory.GetFiles(releasesDirectory, "v*.evidence.json", SearchOption.TopDirectoryOnly);
        var unexpectedMatches = matches
            .Select(Path.GetFullPath)
            .Where(path => EvidenceFileMatchesVersion(path, version))
            .Where(path => !string.Equals(path, expectedPath, StringComparison.Ordinal))
            .ToArray();
        if (unexpectedMatches.Length > 0)
        {
            yield return ReleaseDiagnostic.Error(
                "release-evidence-duplicate",
                "Release preparation contains more than one evidence bundle for the requested version.",
                $"Unexpected evidence files: {string.Join(", ", unexpectedMatches.Select(workspace.DisplayPath))}.",
                $"Keep exactly `{workspace.DisplayPath(expectedPath)}` for release {version}.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        }
    }

    private static async Task<IReadOnlyList<ReleaseDiagnostic>> ValidateBundleAsync(
        ReleaseWorkspace workspace,
        SemVer version,
        string releaseClassification,
        string? contentSourceCommit,
        ReleaseEvidenceBundle bundle,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<ReleaseDiagnostic>();
        ValidateCommonFields(version, releaseClassification, bundle, diagnostics);
        if (!string.IsNullOrWhiteSpace(bundle.Commits.ReleasePreparationCommit)
            && !string.Equals(bundle.Commits.ReleasePreparationCommit, contentSourceCommit, StringComparison.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-release-preparation-commit-mismatch",
                "Release evidence was finalized for a different release-preparation commit.",
                $"Evidence release-preparation commit `{bundle.Commits.ReleasePreparationCommit}` does not match current commit `{contentSourceCommit ?? "unknown"}`.",
                "Regenerate release evidence from the current reviewed release state.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
        }

        var releaseManifestPath = workspace.ReleaseManifestPath(version);
        if (File.Exists(releaseManifestPath))
        {
            var releaseManifestJson = await File.ReadAllTextAsync(releaseManifestPath, cancellationToken);
            ValidateReleaseManifestDigest(bundle, releaseManifestJson, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
            ValidateReleaseManifestSourceCommit(bundle, releaseManifestJson, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        }

        await ValidatePreparedArtifactDigestsAsync(workspace, version, bundle, diagnostics, cancellationToken);

        ValidatePackagePaths(version, bundle, diagnostics);
        ValidateSubject(bundle, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        ValidateDocsArchive(bundle, releaseClassification, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        return diagnostics;
    }

    private static void ValidateCommonFields(
        SemVer version,
        string releaseClassification,
        ReleaseEvidenceBundle bundle,
        List<ReleaseDiagnostic> diagnostics)
    {
        if (!string.Equals(bundle.Schema, Schema, StringComparison.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-schema-invalid",
                "Release evidence bundle has an unsupported schema.",
                $"Expected `{Schema}`, but found `{bundle.Schema}`.",
                "Regenerate the release evidence bundle with the current release tool.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
        }

        var expectedReleaseNotePath = $"releases/v{version}.md";
        var expectedReleaseSidecarPath = $"releases/v{version}.md.yml";
        var expectedReleaseManifestPath = $"releases/v{version}.release.json";
        var expectedEvidencePath = $"releases/v{version}.evidence.json";
        if (!string.Equals(bundle.Version, version.ToString(), StringComparison.Ordinal)
            || !string.Equals(bundle.Tag, version.TagName, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseClassification, releaseClassification, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseNotePath, expectedReleaseNotePath, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseSidecarPath, expectedReleaseSidecarPath, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseManifestPath, expectedReleaseManifestPath, StringComparison.Ordinal)
            || !string.Equals(bundle.EvidencePath, expectedEvidencePath, StringComparison.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-version-mismatch",
                "Release evidence bundle does not match the requested release identity.",
                $"Expected version `{version}`, tag `{version.TagName}`, and release paths under `releases/v{version}`.",
                "Regenerate release evidence for the requested version instead of hand-editing the bundle.",
                "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
        }
    }

    private static bool TryValidateRequiredShape(ReleaseEvidenceBundle bundle, [NotNullWhen(false)] out string? issue)
    {
        if (bundle.Schema is null
            || bundle.Version is null
            || bundle.Tag is null
            || bundle.ReleaseClassification is null
            || bundle.ReleaseNotePath is null
            || bundle.ReleaseSidecarPath is null
            || bundle.ReleaseManifestPath is null
            || bundle.EvidencePath is null
            || bundle.ReleaseManifestDigest is null
            || bundle.ReleaseArtifactDigests is null
            || bundle.PackageReleaseNotePaths is null
            || bundle.DocsArchive is null
            || bundle.Commits is null
            || bundle.GeneratedBy is null
            || bundle.GeneratedAtUtc is null
            || bundle.Subject is null)
        {
            issue = "One or more required top-level evidence fields are missing.";
            return false;
        }

        if (bundle.ReleaseArtifactDigests.Any(digest => digest is null)
            || bundle.PackageReleaseNotePaths.Any(package => package is null))
        {
            issue = "One or more evidence array entries are missing.";
            return false;
        }

        if (bundle.ReleaseManifestDigest.Algorithm is null
            || bundle.ReleaseManifestDigest.Value is null
            || bundle.ReleaseArtifactDigests.Any(digest => digest.Path is null || digest.Algorithm is null || digest.Value is null)
            || bundle.DocsArchive.Status is null
            || bundle.GeneratedBy.Tool is null
            || bundle.Subject.Name is null
            || bundle.Subject.Sha256 is null
            || bundle.PackageReleaseNotePaths.Any(package => package.Project is null || package.ReleaseNotesPath is null))
        {
            issue = "One or more required nested evidence fields are missing.";
            return false;
        }

        issue = null;
        return true;
    }

    private static ReleaseDiagnostic InvalidShapeDiagnostic(string issue, string docsPath)
    {
        return ReleaseDiagnostic.Error(
            "release-evidence-schema-invalid",
            "Release evidence bundle is missing required fields.",
            issue,
            "Regenerate the release evidence bundle with the current release tool instead of hand-editing generated JSON.",
            docsPath);
    }

    private static void ValidateReleaseManifestDigest(
        ReleaseEvidenceBundle bundle,
        string releaseManifestJson,
        List<ReleaseDiagnostic> diagnostics,
        string docsPath)
    {
        var actualDigest = ComputeSha256Hex(releaseManifestJson);
        if (!string.Equals(bundle.ReleaseManifestDigest.Algorithm, "sha256", StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseManifestDigest.Value, actualDigest, StringComparison.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-release-manifest-digest-mismatch",
                "Release evidence bundle does not match the release manifest bytes.",
                $"Evidence recorded `{bundle.ReleaseManifestDigest.Value}` but the release manifest hashes to `{actualDigest}`.",
                "Regenerate release evidence after changing release JSON or generated file lists.",
                docsPath));
        }
    }

    private static void ValidateReleaseManifestSourceCommit(
        ReleaseEvidenceBundle bundle,
        string releaseManifestJson,
        List<ReleaseDiagnostic> diagnostics,
        string docsPath)
    {
        ReleaseManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ReleaseManifest>(releaseManifestJson, ReleaseJson.Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-release-manifest-schema-invalid",
                "Release evidence could not parse the release manifest.",
                ex.Message,
                "Regenerate the release JSON with the release tool instead of hand-editing it.",
                docsPath));
            return;
        }

        if (manifest is null)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-release-manifest-schema-invalid",
                "Release evidence could not parse the release manifest.",
                "Release manifest JSON must be an object, not the JSON literal `null`.",
                "Regenerate the release JSON with the release tool instead of hand-editing it.",
                docsPath));
            return;
        }

        if (!string.Equals(bundle.Commits.ContentSourceCommit, manifest.SourceCommit, StringComparison.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-content-source-commit-mismatch",
                "Release evidence content source commit does not match the release manifest.",
                $"Evidence content source commit `{bundle.Commits.ContentSourceCommit ?? "unknown"}` does not match release manifest source commit `{manifest.SourceCommit ?? "unknown"}`.",
                "Regenerate release evidence and release JSON from the same reviewed release state.",
                docsPath));
        }
    }

    private static async Task ValidatePreparedArtifactDigestsAsync(
        ReleaseWorkspace workspace,
        SemVer version,
        ReleaseEvidenceBundle bundle,
        List<ReleaseDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var artifactContents = new Dictionary<string, string>(StringComparer.Ordinal);
        await AddFileContentAsync(artifactContents, workspace.DisplayPath(workspace.ReleaseNotePath(version)), workspace.ReleaseNotePath(version), cancellationToken);
        await AddFileContentAsync(artifactContents, workspace.DisplayPath(workspace.ReleaseSidecarPath(version)), workspace.ReleaseSidecarPath(version), cancellationToken);
        await AddFileContentAsync(artifactContents, workspace.DisplayPath(workspace.ReleaseManifestPath(version)), workspace.ReleaseManifestPath(version), cancellationToken);
        ValidateArtifactDigests(bundle, artifactContents, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
    }

    private static async Task AddFileContentAsync(
        Dictionary<string, string> artifactContents,
        string displayPath,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(absolutePath))
        {
            artifactContents[displayPath] = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        }
    }

    private static void ValidateArtifactDigests(
        ReleaseEvidenceBundle bundle,
        string releaseNoteContent,
        string releaseSidecarContent,
        string releaseManifestContent,
        List<ReleaseDiagnostic> diagnostics,
        string docsPath)
    {
        ValidateArtifactDigests(
            bundle,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [bundle.ReleaseNotePath] = releaseNoteContent,
                [bundle.ReleaseSidecarPath] = releaseSidecarContent,
                [bundle.ReleaseManifestPath] = releaseManifestContent
            },
            diagnostics,
            docsPath);
    }

    private static void ValidateArtifactDigests(
        ReleaseEvidenceBundle bundle,
        IReadOnlyDictionary<string, string> artifactContents,
        List<ReleaseDiagnostic> diagnostics,
        string docsPath)
    {
        var expectedPaths = new[] { bundle.ReleaseNotePath, bundle.ReleaseSidecarPath, bundle.ReleaseManifestPath };
        var recordedPaths = bundle.ReleaseArtifactDigests
            .Select(digest => digest.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!recordedPaths.SequenceEqual(expectedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-artifact-digest-mismatch",
                "Release evidence bundle does not describe exactly the expected release artifacts.",
                $"Expected artifact digests for: {string.Join(", ", expectedPaths)}.",
                "Regenerate release evidence so the release note, sidecar, and release JSON are all covered.",
                docsPath));
            return;
        }

        foreach (var digest in bundle.ReleaseArtifactDigests.Where(digest =>
            !string.Equals(digest.Algorithm, "sha256", StringComparison.Ordinal)
            || !artifactContents.TryGetValue(digest.Path, out var content)
            || !string.Equals(digest.Value, ComputeSha256Hex(content), StringComparison.Ordinal)))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-artifact-digest-mismatch",
                "Release evidence bundle does not match the release artifact bytes.",
                $"Artifact `{digest.Path}` recorded `{digest.Value}` but current bytes do not match.",
                "Regenerate release evidence after changing release notes, sidecars, or release JSON.",
                docsPath));
        }
    }

    private static void ValidatePackagePaths(
        SemVer version,
        ReleaseEvidenceBundle bundle,
        List<ReleaseDiagnostic> diagnostics)
    {
        var expectedReleasePath = $"releases/v{version}.md";
        var expectedPackages = bundle.PackageReleaseNotePaths
            .Where(package => !string.Equals(package.ReleaseNotesPath, expectedReleasePath, StringComparison.Ordinal))
            .ToArray();
        if (expectedPackages.Length == 0)
        {
            return;
        }

        diagnostics.Add(ReleaseDiagnostic.Error(
            "release-evidence-package-path-mismatch",
            "Release evidence bundle contains package release-note paths that do not point at the tagged release note.",
            $"Mismatched packages: {string.Join(", ", expectedPackages.Select(package => package.Project))}.",
            $"Regenerate package release-note paths and evidence so public packages point at `{expectedReleasePath}`.",
            "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"));
    }

    private static void ValidateSubject(
        ReleaseEvidenceBundle bundle,
        List<ReleaseDiagnostic> diagnostics,
        string docsPath)
    {
        var actualDigest = ComputeSubjectSha256(bundle);
        if (!string.Equals(bundle.Subject.Sha256, actualDigest, StringComparison.Ordinal))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-subject-digest-mismatch",
                "Release evidence subject digest is stale.",
                $"Evidence recorded `{bundle.Subject.Sha256}` but stable evidence fields hash to `{actualDigest}`.",
                "Regenerate release evidence after changing release artifacts instead of patching digest fields by hand.",
                docsPath));
        }
    }

    private static void ValidateDocsArchive(
        ReleaseEvidenceBundle bundle,
        string releaseClassification,
        List<ReleaseDiagnostic> diagnostics,
        string docsPath)
    {
        var isStableRelease = string.Equals(releaseClassification, "stable", StringComparison.Ordinal);
        var docs = bundle.DocsArchive;
        if (docs.CatalogEntry is not null
            && (!string.Equals(docs.CatalogEntry.ExactTreePath, docs.ExactTreePath, StringComparison.Ordinal)
                || !string.Equals(docs.CatalogEntry.ReleaseManifestSha256, docs.ReleaseManifestSha256, StringComparison.Ordinal)))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-catalog-entry-mismatch",
                "Release evidence docs catalog entry does not match the docs archive fields.",
                "The catalog entry exactTreePath and releaseManifestSha256 must mirror the evidence docsArchive values.",
                "Regenerate release evidence from the docs export output and catalog entry.",
                docsPath));
        }

        if (string.Equals(docs.Status, DocsArchiveNotConfigured, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(docs.ExactTreePath)
            && string.IsNullOrWhiteSpace(docs.ReleaseManifestSha256))
        {
            if (isStableRelease)
            {
                diagnostics.Add(ReleaseDiagnostic.Error(
                    "release-evidence-docs-archive-required",
                    "Stable release evidence must include AppSurface Docs archive fields.",
                    "The docsArchive status is `notConfigured`, so stable publish has no catalog-pinned exact tree to verify.",
                    "Regenerate release evidence from the staged AppSurface Docs export and catalog entry before publishing a stable release.",
                    docsPath));
            }

            return;
        }

        if (isStableRelease && docs.CatalogEntry is null)
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-docs-archive-incomplete",
                "Stable release evidence docs archive catalog entry is missing.",
                "Stable evidence must include docsArchive.catalogEntry.exactTreePath and docsArchive.catalogEntry.releaseManifestSha256 so maintainers can review the authored catalog pin separately from the archive summary.",
                "Regenerate stable release evidence from the staged AppSurface Docs export and version catalog.",
                docsPath));
        }

        if (string.IsNullOrWhiteSpace(docs.ExactTreePath) || string.IsNullOrWhiteSpace(docs.ReleaseManifestSha256))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-docs-archive-incomplete",
                "Release evidence docs archive fields are incomplete.",
                "A docs archive evidence entry must include both exactTreePath and releaseManifestSha256.",
                "Regenerate release evidence from a completed AppSurface Docs export, or leave docs archive evidence pending.",
                docsPath));
            return;
        }

        var normalizedExactTreePath = docs.ExactTreePath!.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedExactTreePath)
            || normalizedExactTreePath.Split('/').Any(segment => segment == ".." || (segment.StartsWith(".", StringComparison.Ordinal) && segment != ".")))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-docs-exacttreepath-unsafe",
                "Release evidence exactTreePath is unsafe.",
                $"Exact tree path `{docs.ExactTreePath}` must be trusted-release-root-relative and must not contain parent or hidden path segments.",
                "Regenerate release evidence with a trusted-root-relative AppSurface Docs catalog entry.",
                docsPath));
        }

        var isGeneratedDigest = string.Equals(docs.ReleaseManifestSha256, DocsArchiveGeneratedDigest, StringComparison.Ordinal);
        if (!(isStableRelease && isGeneratedDigest)
            && !IsSha256Hex(docs.ReleaseManifestSha256!))
        {
            diagnostics.Add(ReleaseDiagnostic.Error(
                "release-evidence-docs-manifest-digest-mismatch",
                "Release evidence docs archive manifest digest is invalid.",
                "`releaseManifestSha256` must be a 64-character SHA-256 hex digest printed by AppSurface Docs export, or `generated` when the release archive includes self-referential release evidence.",
                "Copy the digest from the matching export, use `generated` for self-referential stable archives, or regenerate release evidence.",
                docsPath));
        }
    }

    private static string ComputeSubjectSha256(ReleaseEvidenceBundle bundle)
    {
        var subjectInput = new ReleaseEvidenceSubjectInput(
            bundle.Schema,
            bundle.Version,
            bundle.Tag,
            bundle.ReleaseClassification,
            bundle.ReleaseNotePath,
            bundle.ReleaseSidecarPath,
            bundle.ReleaseManifestPath,
            bundle.EvidencePath,
            bundle.ReleaseManifestDigest,
            bundle.ReleaseArtifactDigests,
            bundle.PackageReleaseNotePaths,
            bundle.DocsArchive,
            bundle.Commits,
            bundle.GeneratedBy,
            bundle.Attestation);
        return ComputeSha256Hex(JsonSerializer.Serialize(subjectInput, ReleaseJson.Options));
    }

    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static IReadOnlyList<ReleaseEvidenceArtifactDigest> ReleaseArtifactDigests(params (string Path, string Content)[] artifacts)
    {
        return artifacts
            .Select(artifact => new ReleaseEvidenceArtifactDigest(artifact.Path, "sha256", ComputeSha256Hex(artifact.Content)))
            .OrderBy(artifact => artifact.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool EvidenceFileMatchesVersion(string path, SemVer version)
    {
        var fileName = Path.GetFileName(path);
        const string prefix = "v";
        const string suffix = ".evidence.json";
        var versionText = fileName[prefix.Length..^suffix.Length];
        try
        {
            return EqualityComparer<SemVer>.Default.Equals(SemVer.Parse(versionText), version);
        }
        catch (ReleaseToolException)
        {
            return false;
        }
    }
}

/// <summary>
/// Describes the outcome of release evidence validation.
/// </summary>
/// <param name="Summary">Structured summary when the evidence shape was readable enough to summarize; otherwise <see langword="null"/>.</param>
/// <param name="Diagnostics">Errors or warnings discovered while validating the release evidence bundle.</param>
/// <param name="Bundle">Deserialized evidence bundle when parsing and top-level shape validation reached the bundle. This can be <see langword="null"/> for unreadable evidence and can be non-null while <paramref name="Diagnostics"/> contains errors.</param>
/// <remarks>
/// Stable docs archive verification depends on <paramref name="Bundle"/> only after callers have checked diagnostics. Do not treat a non-null
/// bundle as proof that the evidence is publishable.
/// </remarks>
internal sealed record ReleaseEvidenceValidationResult(
    ReleaseEvidenceSummary? Summary,
    IReadOnlyList<ReleaseDiagnostic> Diagnostics,
    ReleaseEvidenceBundle? Bundle);

internal sealed record ReleaseEvidenceBundle(
    string Schema,
    string Version,
    string Tag,
    string ReleaseClassification,
    string ReleaseNotePath,
    string ReleaseSidecarPath,
    string ReleaseManifestPath,
    string EvidencePath,
    ReleaseEvidenceFileDigest ReleaseManifestDigest,
    IReadOnlyList<ReleaseEvidenceArtifactDigest> ReleaseArtifactDigests,
    IReadOnlyList<ReleaseEvidencePackagePath> PackageReleaseNotePaths,
    ReleaseEvidenceDocsArchive DocsArchive,
    ReleaseEvidenceCommits Commits,
    ReleaseEvidenceGeneratedBy GeneratedBy,
    string GeneratedAtUtc,
    ReleaseEvidenceSubject Subject,
    ReleaseEvidenceAttestation? Attestation)
{
    internal ReleaseEvidenceSummary ToSummary(string status)
    {
        return new ReleaseEvidenceSummary(
            EvidencePath,
            Schema,
            status,
            Subject.Sha256,
            DocsArchive.ReleaseManifestSha256,
            DocsArchive.ExactTreePath,
            null,
            null,
            null,
            null,
            null,
            Commits.TagCommit,
            Attestation is null ? "not required" : Attestation.Mode);
    }
}

internal sealed record ReleaseEvidenceFileDigest(string Algorithm, string Value);

internal sealed record ReleaseEvidenceArtifactDigest(string Path, string Algorithm, string Value);

internal sealed record ReleaseEvidencePackagePath(string Project, string ReleaseNotesPath);

internal sealed record ReleaseEvidenceDocsArchive(
    string Status,
    string? ExactTreePath,
    string? ReleaseManifestSha256,
    string? ReleaseManifestSchema,
    int? FileCount,
    ReleaseEvidenceCatalogEntry? CatalogEntry);

internal sealed record ReleaseEvidenceCatalogEntry(string ExactTreePath, string ReleaseManifestSha256);

internal sealed record ReleaseEvidenceCommits(
    string? ContentSourceCommit,
    string? ReleasePreparationCommit,
    string? TagCommit,
    string? WorkflowRunId);

internal sealed record ReleaseEvidenceGeneratedBy(string Tool);

internal sealed record ReleaseEvidenceSubject(string Name, string Sha256);

internal sealed record ReleaseEvidenceAttestation(string Mode, string? SubjectName, string? SubjectSha256);

internal sealed record ReleaseEvidenceSubjectInput(
    string Schema,
    string Version,
    string Tag,
    string ReleaseClassification,
    string ReleaseNotePath,
    string ReleaseSidecarPath,
    string ReleaseManifestPath,
    string EvidencePath,
    ReleaseEvidenceFileDigest ReleaseManifestDigest,
    IReadOnlyList<ReleaseEvidenceArtifactDigest> ReleaseArtifactDigests,
    IReadOnlyList<ReleaseEvidencePackagePath> PackageReleaseNotePaths,
    ReleaseEvidenceDocsArchive DocsArchive,
    ReleaseEvidenceCommits Commits,
    ReleaseEvidenceGeneratedBy GeneratedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ReleaseEvidenceAttestation? Attestation);
