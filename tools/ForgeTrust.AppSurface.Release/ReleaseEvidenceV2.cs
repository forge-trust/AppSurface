using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Schema-v2 release evidence implementation for frozen coordinated release pointers.
/// </summary>
/// <remarks>
/// This type deliberately has its own JSON shape and reader. A v2 bundle must never be deserialized as the v1 shape: v1
/// could silently omit the current-pointer fields that make a versioned documentation tree historically honest.
/// </remarks>
internal static class ReleaseEvidenceV2
{
    internal const string Schema = "appsurface-release-evidence-bundle-v2";

    internal static ReleaseEvidenceBundleV2 BuildDraft(
        ReleaseWorkspace workspace,
        SemVer version,
        string releaseClassification,
        DateOnly date,
        string? contentSourceCommit,
        string releaseNoteContent,
        string releaseSidecarContent,
        string releaseManifestContent,
        string currentReleaseContent,
        string currentReleaseSidecarContent,
        IReadOnlyList<PackageIndexEntry> packages)
    {
        var releaseNotePath = workspace.DisplayPath(workspace.ReleaseNotePath(version));
        var releaseSidecarPath = workspace.DisplayPath(workspace.ReleaseSidecarPath(version));
        var releaseManifestPath = workspace.DisplayPath(workspace.ReleaseManifestPath(version));
        var evidencePath = workspace.DisplayPath(workspace.ReleaseEvidencePath(version));
        var currentReleasePath = workspace.DisplayPath(workspace.CurrentReleasePath);
        var currentReleaseSidecarPath = workspace.DisplayPath(workspace.CurrentReleaseSidecarPath);
        var bundle = new ReleaseEvidenceBundleV2(
            Schema,
            version.ToString(),
            version.TagName,
            releaseClassification,
            releaseNotePath,
            releaseSidecarPath,
            releaseManifestPath,
            evidencePath,
            new ReleaseEvidenceFileDigest("sha256", ReleaseEvidence.ComputeSha256Hex(releaseManifestContent)),
            ArtifactDigests(
                (releaseNotePath, releaseNoteContent),
                (releaseSidecarPath, releaseSidecarContent),
                (releaseManifestPath, releaseManifestContent),
                (currentReleasePath, currentReleaseContent),
                (currentReleaseSidecarPath, currentReleaseSidecarContent)),
            packages
                .Select(package => new ReleaseEvidencePackageReleaseLink(
                    package.Project,
                    package.ReleaseLink?.Track == PackageReleaseTrack.Coordinated ? "coordinated" : "explicit",
                    package.ReleaseLink?.ReleaseNotesPath ?? string.Empty))
                .OrderBy(package => package.Project, StringComparer.Ordinal)
                .ToArray(),
            new ReleaseEvidenceCurrentRelease(currentReleasePath, currentReleaseSidecarPath, releaseNotePath),
            new ReleaseEvidenceDocsArchive("notConfigured", null, null, null, null, null),
            new ReleaseEvidenceCommits(contentSourceCommit, null, null, null),
            new ReleaseEvidenceGeneratedBy("./eng/release"),
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            new ReleaseEvidenceSubject("release-evidence", string.Empty),
            Attestation: null);
        return bundle with
        {
            Subject = new ReleaseEvidenceSubject(
                evidencePath,
                ComputeSubjectSha256(bundle))
        };
    }

    internal static async Task<ReleaseEvidenceValidationResult> ValidatePreparedAsync(
        ReleaseWorkspace workspace,
        SemVer version,
        string releaseClassification,
        string? contentSourceCommit,
        string evidenceJson,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<ReleaseDiagnostic>();
        var bundle = Deserialize(evidenceJson, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        if (bundle is null)
        {
            return new ReleaseEvidenceValidationResult(null, diagnostics, null);
        }

        if (!ValidateShape(bundle, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle"))
        {
            return new ReleaseEvidenceValidationResult(null, diagnostics, null);
        }

        var identityIsValid = ValidateIdentity(bundle, version, releaseClassification, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        if (identityIsValid)
        {
            await ValidatePreparedContentsAsync(workspace, version, bundle, diagnostics, cancellationToken);
        }
        ValidateCommitAndManifest(bundle, contentSourceCommit, await ReadOptionalAsync(workspace.ReleaseManifestPath(version), cancellationToken), diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        ValidateSubject(bundle, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        ValidateDocsArchive(bundle, releaseClassification, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");

        return new ReleaseEvidenceValidationResult(
            bundle.ToSummary("draft evidence for release-prep review"),
            diagnostics,
            bundle.ToCompatibilityBundle());
    }

    internal static ReleaseEvidenceValidationResult ValidateTag(
        SemVer version,
        string releaseClassification,
        string tag,
        string tagCommit,
        string releaseNoteContent,
        string releaseSidecarContent,
        string releaseManifestContent,
        string currentReleaseContent,
        string currentReleaseSidecarContent,
        string evidenceJson)
    {
        const string docsPath = "tools/ForgeTrust.AppSurface.Release/README.md#publish";
        var diagnostics = new List<ReleaseDiagnostic>();
        var bundle = Deserialize(evidenceJson, diagnostics, docsPath);
        if (bundle is null)
        {
            return new ReleaseEvidenceValidationResult(null, diagnostics, null);
        }

        if (!ValidateShape(bundle, diagnostics, docsPath))
        {
            return new ReleaseEvidenceValidationResult(null, diagnostics, null);
        }

        var identityIsValid = ValidateIdentity(bundle, version, releaseClassification, diagnostics, docsPath);
        if (!string.Equals(bundle.Tag, tag, StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-version-mismatch", "Release evidence tag does not match the annotated tag being published.", $"Evidence tag `{bundle.Tag}` does not match `{tag}`.", "Regenerate evidence for the tagged release.", docsPath);
        }

        if (!string.IsNullOrWhiteSpace(bundle.Commits.TagCommit)
            && !string.Equals(bundle.Commits.TagCommit, tagCommit, StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-tag-commit-mismatch", "Release evidence does not match the annotated tag commit.", $"Evidence tag commit `{bundle.Commits.TagCommit}` does not match `{tagCommit}`.", "Regenerate evidence from the reviewed tag commit.", docsPath);
        }

        if (identityIsValid)
        {
            ValidateArtifactDigests(
                bundle,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [bundle.ReleaseNotePath] = releaseNoteContent,
                    [bundle.ReleaseSidecarPath] = releaseSidecarContent,
                    [bundle.ReleaseManifestPath] = releaseManifestContent,
                    [bundle.CurrentRelease.ReleaseNotePath] = currentReleaseContent,
                    [bundle.CurrentRelease.ReleaseSidecarPath] = currentReleaseSidecarContent
                },
                diagnostics,
                docsPath);
            ValidateCurrentPointer(bundle, version, currentReleaseContent, diagnostics, docsPath);
        }
        ValidateCommitAndManifest(bundle, ContentSourceCommit: null, releaseManifestContent, diagnostics, docsPath);
        ValidateSubject(bundle, diagnostics, docsPath);
        ValidateDocsArchive(bundle, releaseClassification, diagnostics, docsPath);

        return new ReleaseEvidenceValidationResult(
            bundle.ToSummary("tag-bound evidence validated for publish") with { TagCommit = tagCommit },
            diagnostics,
            bundle.ToCompatibilityBundle());
    }

    internal static string Serialize(ReleaseEvidenceBundleV2 bundle) => JsonSerializer.Serialize(bundle, ReleaseJson.Options) + Environment.NewLine;

    /// <summary>
    /// Recomputes the v2 subject after a maintainer or workflow supplies catalog-bound docs archive fields.
    /// </summary>
    /// <remarks>
    /// The release-preparation draft intentionally leaves stable docs archive proof unconfigured until the staged exact tree exists.
    /// Callers that add that proof must refresh the subject rather than hand-editing its digest.
    /// </remarks>
    internal static ReleaseEvidenceBundleV2 RefreshSubject(ReleaseEvidenceBundleV2 bundle) =>
        bundle with { Subject = bundle.Subject with { Sha256 = ComputeSubjectSha256(bundle) } };

    private static ReleaseEvidenceBundleV2? Deserialize(string json, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        try
        {
            return JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(json, ReleaseJson.Options);
        }
        catch (JsonException ex)
        {
            AddError(diagnostics, "release-evidence-schema-invalid", "Release evidence bundle could not be parsed as v2 evidence.", ex.Message, "Regenerate release evidence instead of hand-editing JSON.", docsPath);
            return null;
        }
    }

    private static bool ValidateShape(ReleaseEvidenceBundleV2 bundle, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        if (bundle.Schema is null || bundle.Version is null || bundle.Tag is null || bundle.ReleaseClassification is null
            || bundle.ReleaseNotePath is null || bundle.ReleaseSidecarPath is null || bundle.ReleaseManifestPath is null
            || bundle.EvidencePath is null || bundle.ReleaseManifestDigest is null || bundle.ReleaseArtifactDigests is null
            || bundle.PackageReleaseLinks is null || bundle.CurrentRelease is null || bundle.DocsArchive is null || bundle.Commits is null
            || bundle.GeneratedBy is null || bundle.GeneratedAtUtc is null || bundle.Subject is null
            || bundle.ReleaseArtifactDigests.Any(item => item is null) || bundle.PackageReleaseLinks.Any(item => item is null))
        {
            AddError(diagnostics, "release-evidence-schema-invalid", "Release evidence bundle is missing required v2 fields.", "One or more required top-level or array fields are missing.", "Regenerate release evidence with the current release tool.", docsPath);
            return false;
        }

        if (bundle.ReleaseManifestDigest.Algorithm is null || bundle.ReleaseManifestDigest.Value is null
            || bundle.ReleaseArtifactDigests.Any(item => item.Path is null || item.Algorithm is null || item.Value is null)
            || bundle.PackageReleaseLinks.Any(item => item.Project is null || item.Track is null || item.ReleaseNotesPath is null)
            || bundle.CurrentRelease.ReleaseNotePath is null || bundle.CurrentRelease.ReleaseSidecarPath is null || bundle.CurrentRelease.TargetReleaseNotePath is null
            || bundle.DocsArchive.Status is null || bundle.GeneratedBy.Tool is null || bundle.Subject.Name is null || bundle.Subject.Sha256 is null)
        {
            AddError(diagnostics, "release-evidence-schema-invalid", "Release evidence bundle is missing required nested v2 fields.", "One or more digest, package-link, current-pointer, or metadata fields are missing.", "Regenerate release evidence with the current release tool.", docsPath);
            return false;
        }

        return true;
    }

    private static bool ValidateIdentity(ReleaseEvidenceBundleV2 bundle, SemVer version, string releaseClassification, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        var isValid = true;
        if (!string.Equals(bundle.Schema, Schema, StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-schema-invalid", "Release evidence bundle has an unsupported schema.", $"Expected `{Schema}`, but found `{bundle.Schema}`.", "Regenerate release evidence with the current release tool.", docsPath);
            isValid = false;
        }

        var expectedNote = $"releases/v{version}.md";
        var expectedSidecar = $"releases/v{version}.md.yml";
        var expectedManifest = $"releases/v{version}.release.json";
        var expectedEvidence = $"releases/v{version}.evidence.json";
        if (!string.Equals(bundle.Version, version.ToString(), StringComparison.Ordinal)
            || !string.Equals(bundle.Tag, version.TagName, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseClassification, releaseClassification, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseNotePath, expectedNote, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseSidecarPath, expectedSidecar, StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseManifestPath, expectedManifest, StringComparison.Ordinal)
            || !string.Equals(bundle.EvidencePath, expectedEvidence, StringComparison.Ordinal)
            || !string.Equals(bundle.CurrentRelease.ReleaseNotePath, PackageReleaseLink.CoordinatedReleaseNotesPath, StringComparison.Ordinal)
            || !string.Equals(bundle.CurrentRelease.ReleaseSidecarPath, "releases/current.md.yml", StringComparison.Ordinal)
            || !string.Equals(bundle.CurrentRelease.TargetReleaseNotePath, expectedNote, StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-version-mismatch", "Release evidence bundle does not match the requested release identity.", $"Expected tagged paths under `releases/v{version}` and a current pointer to `{expectedNote}`.", "Regenerate release evidence for the requested version.", docsPath);
            isValid = false;
        }

        var invalidLinks = bundle.PackageReleaseLinks
            .Where(link => string.IsNullOrWhiteSpace(link.Project)
                           || (string.Equals(link.Track, "coordinated", StringComparison.Ordinal)
                               && !string.Equals(link.ReleaseNotesPath, PackageReleaseLink.CoordinatedReleaseNotesPath, StringComparison.Ordinal))
                           || (string.Equals(link.Track, "explicit", StringComparison.Ordinal)
                               && string.IsNullOrWhiteSpace(link.ReleaseNotesPath))
                           || (!string.Equals(link.Track, "coordinated", StringComparison.Ordinal)
                               && !string.Equals(link.Track, "explicit", StringComparison.Ordinal)))
            .Select(link => link.Project)
            .ToArray();
        if (invalidLinks.Length > 0 || bundle.PackageReleaseLinks.Select(link => link.Project).Distinct(StringComparer.Ordinal).Count() != bundle.PackageReleaseLinks.Count)
        {
            AddError(diagnostics, "release-evidence-package-link-mismatch", "V2 release evidence contains invalid package release links.", $"Invalid or duplicate package links: {string.Join(", ", invalidLinks)}.", "Use a coordinated releases/current.md link or a non-empty explicit historical release_notes_path, then regenerate evidence.", docsPath);
            isValid = false;
        }

        return isValid;
    }

    private static async Task ValidatePreparedContentsAsync(ReleaseWorkspace workspace, SemVer version, ReleaseEvidenceBundleV2 bundle, List<ReleaseDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        await AddIfExistsAsync(content, workspace.DisplayPath(workspace.ReleaseNotePath(version)), workspace.ReleaseNotePath(version), cancellationToken);
        await AddIfExistsAsync(content, workspace.DisplayPath(workspace.ReleaseSidecarPath(version)), workspace.ReleaseSidecarPath(version), cancellationToken);
        await AddIfExistsAsync(content, workspace.DisplayPath(workspace.ReleaseManifestPath(version)), workspace.ReleaseManifestPath(version), cancellationToken);
        await AddIfExistsAsync(content, workspace.DisplayPath(workspace.CurrentReleasePath), workspace.CurrentReleasePath, cancellationToken);
        await AddIfExistsAsync(content, workspace.DisplayPath(workspace.CurrentReleaseSidecarPath), workspace.CurrentReleaseSidecarPath, cancellationToken);
        ValidateArtifactDigests(bundle, content, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        if (content.TryGetValue(bundle.CurrentRelease.ReleaseNotePath, out var current))
        {
            ValidateCurrentPointer(bundle, version, current, diagnostics, "tools/ForgeTrust.AppSurface.Release/README.md#release-evidence-bundle");
        }
    }

    private static async Task AddIfExistsAsync(Dictionary<string, string> contents, string displayPath, string physicalPath, CancellationToken cancellationToken)
    {
        if (File.Exists(physicalPath))
        {
            contents[displayPath] = await File.ReadAllTextAsync(physicalPath, cancellationToken);
        }
    }

    private static async Task<string?> ReadOptionalAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

    private static void ValidateArtifactDigests(ReleaseEvidenceBundleV2 bundle, IReadOnlyDictionary<string, string> contents, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        var expectedPaths = new[]
        {
            bundle.ReleaseNotePath,
            bundle.ReleaseSidecarPath,
            bundle.ReleaseManifestPath,
            bundle.CurrentRelease.ReleaseNotePath,
            bundle.CurrentRelease.ReleaseSidecarPath
        };
        var actualPaths = bundle.ReleaseArtifactDigests.Select(item => item.Path).Order(StringComparer.Ordinal).ToArray();
        if (!actualPaths.SequenceEqual(expectedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            AddError(diagnostics, "release-evidence-artifact-digest-mismatch", "V2 evidence does not cover exactly the tagged artifacts and frozen current pointer.", $"Expected digests for: {string.Join(", ", expectedPaths)}.", "Regenerate evidence after changing release artifacts or the current pointer.", docsPath);
            return;
        }

        foreach (var digest in bundle.ReleaseArtifactDigests)
        {
            if (!string.Equals(digest.Algorithm, "sha256", StringComparison.Ordinal)
                || !contents.TryGetValue(digest.Path, out var content)
                || !string.Equals(digest.Value, ReleaseEvidence.ComputeSha256Hex(content), StringComparison.Ordinal))
            {
                AddError(diagnostics, "release-evidence-artifact-digest-mismatch", "Release evidence bundle does not match the release artifact bytes.", $"Artifact `{digest.Path}` recorded `{digest.Value}` but current bytes do not match.", "Regenerate evidence after changing release artifacts or the current pointer.", docsPath);
            }
        }
    }

    private static void ValidateCurrentPointer(ReleaseEvidenceBundleV2 bundle, SemVer version, string currentReleaseContent, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        var expectedLink = $"[Read the AppSurface {version} release note](./v{version}.md)";
        if (!currentReleaseContent.Contains(expectedLink, StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-current-pointer-mismatch", "The frozen current release pointer does not link to this tagged release.", $"Expected `{bundle.CurrentRelease.ReleaseNotePath}` to contain `{expectedLink}`.", "Regenerate releases/current.md through ./eng/release prepare.", docsPath);
        }
    }

    private static void ValidateCommitAndManifest(ReleaseEvidenceBundleV2 bundle, string? ContentSourceCommit, string? releaseManifestContent, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        if (releaseManifestContent is null)
        {
            return;
        }

        if (!string.Equals(bundle.ReleaseManifestDigest.Algorithm, "sha256", StringComparison.Ordinal)
            || !string.Equals(bundle.ReleaseManifestDigest.Value, ReleaseEvidence.ComputeSha256Hex(releaseManifestContent), StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-release-manifest-digest-mismatch", "Release evidence bundle does not match the release manifest bytes.", "The stored release manifest digest differs from the current release manifest.", "Regenerate evidence after changing release JSON.", docsPath);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(releaseManifestContent, ReleaseJson.Options);
            if (manifest is null || !string.Equals(bundle.Commits.ContentSourceCommit, manifest.SourceCommit, StringComparison.Ordinal))
            {
                AddError(diagnostics, "release-evidence-content-source-commit-mismatch", "Release evidence content source commit does not match the release manifest.", "The evidence and manifest were not generated from the same reviewed release state.", "Regenerate evidence and release JSON together.", docsPath);
            }
        }
        catch (JsonException ex)
        {
            AddError(diagnostics, "release-evidence-release-manifest-schema-invalid", "Release evidence could not parse the release manifest.", ex.Message, "Regenerate release JSON with the release tool.", docsPath);
        }

        if (!string.IsNullOrWhiteSpace(ContentSourceCommit)
            && !string.IsNullOrWhiteSpace(bundle.Commits.ReleasePreparationCommit)
            && !string.Equals(bundle.Commits.ReleasePreparationCommit, ContentSourceCommit, StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-release-preparation-commit-mismatch", "Release evidence was finalized for a different release-preparation commit.", $"Evidence commit `{bundle.Commits.ReleasePreparationCommit}` does not match `{ContentSourceCommit}`.", "Regenerate evidence from the current reviewed release state.", docsPath);
        }
    }

    private static void ValidateSubject(ReleaseEvidenceBundleV2 bundle, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        if (!string.Equals(bundle.Subject.Sha256, ComputeSubjectSha256(bundle), StringComparison.Ordinal))
        {
            AddError(diagnostics, "release-evidence-subject-digest-mismatch", "Release evidence subject digest is stale.", "The digest no longer covers the current v2 release-link evidence.", "Regenerate evidence after changing release artifacts or package links.", docsPath);
        }
    }

    private static void ValidateDocsArchive(ReleaseEvidenceBundleV2 bundle, string releaseClassification, List<ReleaseDiagnostic> diagnostics, string docsPath)
    {
        if (!string.Equals(releaseClassification, "stable", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(bundle.DocsArchive.Status, "notConfigured", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(bundle.DocsArchive.ExactTreePath)
            || string.IsNullOrWhiteSpace(bundle.DocsArchive.ReleaseManifestSha256))
        {
            AddError(diagnostics, "release-evidence-docs-archive-required", "Stable v2 release evidence is missing docs archive proof.", "Stable release evidence must bind the exported docs exact tree and manifest digest.", "Update the generated evidence from the staged docs catalog before stable release review.", docsPath);
        }
    }

    private static IReadOnlyList<ReleaseEvidenceArtifactDigest> ArtifactDigests(params (string Path, string Content)[] artifacts) =>
        artifacts.Select(item => new ReleaseEvidenceArtifactDigest(item.Path, "sha256", ReleaseEvidence.ComputeSha256Hex(item.Content)))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

    private static string ComputeSubjectSha256(ReleaseEvidenceBundleV2 bundle)
    {
        var input = new ReleaseEvidenceSubjectInputV2(
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
            bundle.PackageReleaseLinks,
            bundle.CurrentRelease,
            bundle.DocsArchive,
            bundle.Commits,
            bundle.GeneratedBy,
            bundle.Attestation);
        return ReleaseEvidence.ComputeSha256Hex(JsonSerializer.Serialize(input, ReleaseJson.Options));
    }

    private static void AddError(List<ReleaseDiagnostic> diagnostics, string code, string problem, string cause, string fix, string docsPath) =>
        diagnostics.Add(ReleaseDiagnostic.Error(code, problem, cause, fix, docsPath));
}

internal sealed record ReleaseEvidenceBundleV2(
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
    IReadOnlyList<ReleaseEvidencePackageReleaseLink> PackageReleaseLinks,
    ReleaseEvidenceCurrentRelease CurrentRelease,
    ReleaseEvidenceDocsArchive DocsArchive,
    ReleaseEvidenceCommits Commits,
    ReleaseEvidenceGeneratedBy GeneratedBy,
    string GeneratedAtUtc,
    ReleaseEvidenceSubject Subject,
    ReleaseEvidenceAttestation? Attestation)
{
    internal ReleaseEvidenceSummary ToSummary(string status) =>
        new(EvidencePath, Schema, status, Subject.Sha256, DocsArchive.ReleaseManifestSha256, DocsArchive.ExactTreePath, null, null, null, null, null, Commits.TagCommit, Attestation is null ? "not required" : Attestation.Mode);

    /// <summary>
    /// Adapts already-parsed v2 docs archive fields for the existing archive gate; this is not JSON deserialization.
    /// </summary>
    internal ReleaseEvidenceBundle ToCompatibilityBundle() =>
        new("appsurface-release-evidence-bundle-v1", Version, Tag, ReleaseClassification, ReleaseNotePath, ReleaseSidecarPath, ReleaseManifestPath, EvidencePath, ReleaseManifestDigest, ReleaseArtifactDigests, PackageReleaseLinks.Select(link => new ReleaseEvidencePackagePath(link.Project, link.ReleaseNotesPath)).ToArray(), DocsArchive, Commits, GeneratedBy, GeneratedAtUtc, Subject, Attestation);
}

internal sealed record ReleaseEvidencePackageReleaseLink(string Project, string Track, string ReleaseNotesPath);

internal sealed record ReleaseEvidenceCurrentRelease(string ReleaseNotePath, string ReleaseSidecarPath, string TargetReleaseNotePath);

internal sealed record ReleaseEvidenceSubjectInputV2(
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
    IReadOnlyList<ReleaseEvidencePackageReleaseLink> PackageReleaseLinks,
    ReleaseEvidenceCurrentRelease CurrentRelease,
    ReleaseEvidenceDocsArchive DocsArchive,
    ReleaseEvidenceCommits Commits,
    ReleaseEvidenceGeneratedBy GeneratedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReleaseEvidenceAttestation? Attestation);
