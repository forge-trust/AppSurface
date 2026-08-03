using System.Text.Json;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseEvidenceCoverageTests
{
    private static readonly SemVer Version = SemVer.Parse("0.1.0-preview.1");
    private const string ReleaseClassification = "prerelease";
    private const string PreparationBaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TagCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ReleaseNote = "# Release 0.1.0-preview.1\n";
    private const string ReleaseSidecar = "title: Release 0.1.0-preview.1\n";
    private const string CurrentReleaseSidecar = "title: Current coordinated release\n";

    [Fact]
    public void ValidateTagRejectsUnsupportedEvidenceSchema()
    {
        var manifest = CreateV1Manifest("abc123");
        var bundle = CreateV1Bundle(manifest) with { Schema = "unsupported" };

        var result = ReleaseEvidence.ValidateTag(
            Version,
            ReleaseClassification,
            Version.TagName,
            TagCommit,
            ReleaseNote,
            ReleaseSidecar,
            manifest,
            ReleaseEvidence.Serialize(bundle));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
        Assert.Null(result.Bundle);
    }

    [Fact]
    public async Task ValidatePreparedRejectsUnsupportedEvidenceSchema()
    {
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "ReleaseEvidenceCoverage", Guid.NewGuid().ToString("N"));
        var workspace = new ReleaseWorkspace(root);
        var manifest = CreateV1Manifest("abc123");
        var bundle = CreateV1Bundle(manifest) with { Schema = "unsupported" };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspace.ReleaseEvidencePath(Version))!);
            await File.WriteAllTextAsync(workspace.ReleaseEvidencePath(Version), ReleaseEvidence.Serialize(bundle));

            var result = await ReleaseEvidence.ValidatePreparedAsync(
                workspace,
                Version,
                ReleaseClassification,
                "abc123",
                CancellationToken.None);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
            Assert.Null(result.Bundle);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidatePreparedReportsMissingPreparedSidecarAfterReadingEvidence()
    {
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "ReleaseEvidenceCoverage", Guid.NewGuid().ToString("N"));
        var workspace = new ReleaseWorkspace(root);
        var manifest = CreateV1Manifest("abc123");
        var bundle = CreateV1Bundle(manifest);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspace.ReleaseEvidencePath(Version))!);
            await File.WriteAllTextAsync(workspace.ReleaseEvidencePath(Version), ReleaseEvidence.Serialize(bundle));
            await File.WriteAllTextAsync(workspace.ReleaseNotePath(Version), ReleaseNote);
            await File.WriteAllTextAsync(workspace.ReleaseManifestPath(Version), manifest);

            var result = await ReleaseEvidence.ValidatePreparedAsync(
                workspace,
                Version,
                ReleaseClassification,
                "abc123",
                CancellationToken.None);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ValidateTagRejectsMalformedV1ReleaseManifestJson()
    {
        const string malformedManifest = "{";
        var validManifest = CreateV1Manifest("abc123");
        var bundle = CreateV1Bundle(validManifest);
        var manifestDigest = ReleaseEvidence.ComputeSha256Hex(malformedManifest);
        bundle = RefreshV1Subject(bundle with
        {
            ReleaseManifestDigest = new ReleaseEvidenceFileDigest("sha256", manifestDigest),
            ReleaseArtifactDigests = bundle.ReleaseArtifactDigests
                .Select(digest => digest.Path == bundle.ReleaseManifestPath
                    ? digest with { Value = manifestDigest }
                    : digest)
                .ToArray()
        });

        var result = ReleaseEvidence.ValidateTag(
            Version,
            ReleaseClassification,
            Version.TagName,
            TagCommit,
            ReleaseNote,
            ReleaseSidecar,
            malformedManifest,
            ReleaseEvidence.Serialize(bundle));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-release-manifest-schema-invalid");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-release-manifest-digest-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateTagRejectsV2EvidenceWhenEitherFrozenCurrentPointerArtifactIsMissing(bool omitCurrentPointer)
    {
        var fixture = CreateV2Fixture();

        var result = ReleaseEvidence.ValidateTag(
            Version,
            ReleaseClassification,
            Version.TagName,
            TagCommit,
            fixture.ReleaseNote,
            fixture.ReleaseSidecar,
            fixture.ReleaseManifest,
            ReleaseEvidence.Serialize(fixture.Bundle),
            omitCurrentPointer ? null : fixture.CurrentRelease,
            omitCurrentPointer ? fixture.CurrentReleaseSidecar : null);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-current-pointer-missing");
        Assert.Null(result.Bundle);
    }

    [Fact]
    public void ValidateTagRejectsV2EvidenceWithReleaseArtifactDigestPathMismatch()
    {
        var fixture = CreateV2Fixture();
        var mutatedBundle = ReleaseEvidenceV2.RefreshSubject(fixture.Bundle with
        {
            ReleaseArtifactDigests = fixture.Bundle.ReleaseArtifactDigests
                .Select(digest => string.Equals(digest.Path, fixture.Bundle.ReleaseNotePath, StringComparison.Ordinal)
                    ? digest with { Path = "releases/not-the-versioned-note.md" }
                    : digest)
                .ToArray()
        });

        var result = ValidateV2Tag(fixture, mutatedBundle);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsV2EvidenceWithReleaseArtifactDigestContentMismatch()
    {
        var fixture = CreateV2Fixture();

        var result = ReleaseEvidence.ValidateTag(
            Version,
            ReleaseClassification,
            Version.TagName,
            TagCommit,
            fixture.ReleaseNote + "tampered",
            fixture.ReleaseSidecar,
            fixture.ReleaseManifest,
            ReleaseEvidence.Serialize(fixture.Bundle),
            fixture.CurrentRelease,
            fixture.CurrentReleaseSidecar);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
    }

    [Fact]
    public async Task PublishRejectsV2EvidenceWithInvalidPreparationBaseCommit()
    {
        var fixture = CreateV2Fixture("HEAD");
        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(fixture, preparationBaseMergeBaseResult: null));

        Assert.Equal("release-preparation-base-commit-invalid", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task PublishRejectsV2EvidenceWithNonAncestorPreparationBaseCommit()
    {
        var fixture = CreateV2Fixture();
        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(fixture, preparationBaseMergeBaseResult: new CommandResult(1, string.Empty, "not an ancestor")));

        Assert.Equal("release-preparation-base-commit-not-contained-by-tag", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task PublishRejectsV2EvidenceWhoseManifestOmitsTaggedPublicPackage()
    {
        var fixture = CreateV2Fixture();
        var packageIndex = """
            packages:
              - project: Core/ForgeTrust.AppSurface.Core.csproj
                classification: public
                publish_decision: publish
                release_track: coordinated
            """;

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() =>
            PublishAsync(
                fixture,
                preparationBaseMergeBaseResult: new CommandResult(0, string.Empty, string.Empty),
                packageIndex: packageIndex));

        Assert.Equal("release-evidence-package-set-mismatch", exception.Diagnostic.Code);
    }

    private static ReleaseEvidenceBundle CreateV1Bundle(string manifest)
    {
        var workspace = new ReleaseWorkspace(Path.GetTempPath());
        return ReleaseEvidence.BuildDraft(
            workspace,
            Version,
            ReleaseClassification,
            new DateOnly(2026, 5, 25),
            "abc123",
            ReleaseNote,
            ReleaseSidecar,
            manifest,
            Array.Empty<PackagePathUpdate>());
    }

    private static string CreateV1Manifest(string? sourceCommit)
    {
        return JsonSerializer.Serialize(
            new ReleaseManifest(
                "appsurface-release-manifest-v1",
                Version.ToString(),
                Version.TagName,
                "2026-05-25",
                sourceCommit,
                ReleaseClassification,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<PackagePathUpdate>(),
                Array.Empty<ReleaseDiagnosticRecord>(),
                Array.Empty<string>()),
            ReleaseJson.Options);
    }

    private static ReleaseEvidenceBundle RefreshV1Subject(ReleaseEvidenceBundle bundle)
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
        var subjectDigest = ReleaseEvidence.ComputeSha256Hex(JsonSerializer.Serialize(subjectInput, ReleaseJson.Options));
        return bundle with { Subject = bundle.Subject with { Sha256 = subjectDigest } };
    }

    private static V2Fixture CreateV2Fixture(string preparationBaseCommit = PreparationBaseCommit)
    {
        var releaseManifest = JsonSerializer.Serialize(
            new ReleaseManifestV2(
                ReleaseManifestV2Validator.Schema,
                Version.ToString(),
                Version.TagName,
                "2026-05-25",
                preparationBaseCommit,
                ReleaseClassification,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<CoordinatedPackageReleaseNoteResolution>(),
                Array.Empty<ReleaseDiagnosticRecord>(),
                Array.Empty<string>()),
            ReleaseJson.Options);
        var currentRelease = ReleaseCurrentPointer.Build(Version);
        var bundle = ReleaseEvidence.BuildDraftV2(
            new ReleaseWorkspace(Path.GetTempPath()),
            Version,
            ReleaseClassification,
            new DateOnly(2026, 5, 25),
            preparationBaseCommit,
            ReleaseNote,
            ReleaseSidecar,
            releaseManifest,
            currentRelease,
            CurrentReleaseSidecar,
            Array.Empty<CoordinatedPackageReleaseNoteResolution>());
        return new V2Fixture(bundle, releaseManifest, currentRelease, CurrentReleaseSidecar, ReleaseNote, ReleaseSidecar);
    }

    private static ReleaseEvidenceValidationResult ValidateV2Tag(V2Fixture fixture, ReleaseEvidenceBundleV2 bundle)
    {
        return ReleaseEvidence.ValidateTag(
            Version,
            ReleaseClassification,
            Version.TagName,
            TagCommit,
            fixture.ReleaseNote,
            fixture.ReleaseSidecar,
            fixture.ReleaseManifest,
            ReleaseEvidence.Serialize(bundle),
            fixture.CurrentRelease,
            fixture.CurrentReleaseSidecar);
    }

    private static async Task<PublishOutputs> PublishAsync(
        V2Fixture fixture,
        CommandResult? preparationBaseMergeBaseResult,
        string packageIndex = "packages: []\n")
    {
        var runner = new FakeCommandRunner();
        var tag = Version.TagName;
        runner.Add($"git cat-file -t refs/tags/{tag}", new CommandResult(0, "tag\n", string.Empty));
        runner.Add($"git rev-parse refs/tags/{tag}^{{commit}}", new CommandResult(0, $"{TagCommit}\n", string.Empty));
        runner.Add($"git merge-base --is-ancestor {TagCommit} origin/main", new CommandResult(0, string.Empty, string.Empty));
        runner.Add(
            $"gh run list --workflow nuget-prerelease-publish.yml --commit {TagCommit} --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"{tag}\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"",
            new CommandResult(0, "https://github.com/example/actions/runs/1\n", string.Empty));
        runner.Add($"gh release view {tag} --json isDraft,url", new CommandResult(1, string.Empty, "release not found"));
        runner.Add($"git show {tag}:releases/v{Version}.md", new CommandResult(0, fixture.ReleaseNote, string.Empty));
        runner.Add($"git show {tag}:releases/v{Version}.md.yml", new CommandResult(0, fixture.ReleaseSidecar, string.Empty));
        runner.Add($"git show {tag}:releases/v{Version}.release.json", new CommandResult(0, fixture.ReleaseManifest, string.Empty));
        runner.Add(
            $"git show {tag}:releases/v{Version}.evidence.json",
            new CommandResult(0, ReleaseEvidence.Serialize(fixture.Bundle), string.Empty));
        runner.Add($"git show {tag}:{PackageReleaseLink.CoordinatedReleaseNotesPath}", new CommandResult(0, fixture.CurrentRelease, string.Empty));
        runner.Add($"git show {tag}:{PackageReleaseLink.CoordinatedReleaseSidecarPath}", new CommandResult(0, fixture.CurrentReleaseSidecar, string.Empty));
        runner.Add($"git show {tag}:packages/package-index.yml", new CommandResult(0, packageIndex, string.Empty));
        if (preparationBaseMergeBaseResult is not null)
        {
            runner.Add(
                $"git merge-base --is-ancestor {fixture.Bundle.Commits.PreparationBaseCommit} {TagCommit}",
                preparationBaseMergeBaseResult);
        }

        var options = new ReleaseOptions(
            "publish",
            Path.GetTempPath(),
            Version,
            tag,
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);
        return await new ReleasePublishing(new ReleaseWorkspace(options.RepositoryRoot), runner)
            .PublishAsync(options, CancellationToken.None);
    }

    private sealed record V2Fixture(
        ReleaseEvidenceBundleV2 Bundle,
        string ReleaseManifest,
        string CurrentRelease,
        string CurrentReleaseSidecar,
        string ReleaseNote,
        string ReleaseSidecar);

}
