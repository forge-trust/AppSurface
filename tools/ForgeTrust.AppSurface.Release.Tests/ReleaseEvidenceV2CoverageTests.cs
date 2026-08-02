using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseEvidenceV2CoverageTests
{
    [Fact]
    public void ValidateTagRejectsMalformedRootJson()
    {
        var fixture = CreateFixture();

        var result = ValidateTag(fixture, "{");

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task ValidatePreparedRejectsMalformedRootJson()
    {
        var fixture = CreateFixture();

        var result = await ReleaseEvidenceV2.ValidatePreparedAsync(
            fixture.Workspace,
            fixture.Version,
            fixture.Classification,
            fixture.BaseCommit,
            "{",
            CancellationToken.None);

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Fact]
    public void ValidateTagRejectsNonObjectRootJson()
    {
        var fixture = CreateFixture();

        var result = ValidateTag(fixture, "[]");

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Fact]
    public void ValidateTagRejectsMissingRootProperty()
    {
        var fixture = CreateFixture();
        var root = JsonNode.Parse(ReleaseEvidenceV2.Serialize(fixture.Bundle))!.AsObject();
        root.Remove("subject");

        var result = ValidateTag(fixture, root.ToJsonString(ReleaseJson.Options));

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Fact]
    public void ValidateTagRejectsUnknownRootProperty()
    {
        var fixture = CreateFixture();
        var root = JsonNode.Parse(ReleaseEvidenceV2.Serialize(fixture.Bundle))!.AsObject();
        root["unknown"] = true;

        var result = ValidateTag(fixture, root.ToJsonString(ReleaseJson.Options));

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("version")]
    [InlineData("classification")]
    [InlineData("path")]
    public void ValidateTagRejectsIdentityMismatches(string mismatch)
    {
        var fixture = CreateFixture();
        var bundle = mismatch switch
        {
            "tag" => fixture.Bundle with { Tag = "v9.9.9" },
            "version" => fixture.Bundle with { Version = "9.9.9" },
            "classification" => fixture.Bundle with { ReleaseClassification = "stable" },
            "path" => fixture.Bundle with { ReleaseNotePath = "releases/other.md" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(ReleaseEvidenceV2.RefreshSubject(bundle)));

        AssertDiagnostic(result, "release-evidence-version-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsMismatchedTagCommit()
    {
        var fixture = CreateFixture();
        var bundle = fixture.Bundle with
        {
            Commits = fixture.Bundle.Commits with { TagCommit = new string('c', 40) }
        };

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(ReleaseEvidenceV2.RefreshSubject(bundle)));

        AssertDiagnostic(result, "release-evidence-tag-commit-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsInvalidNestedJson()
    {
        var fixture = CreateFixture();
        var root = JsonNode.Parse(ReleaseEvidenceV2.Serialize(fixture.Bundle))!.AsObject();
        root["commits"] = new JsonArray();

        var result = ValidateTag(fixture, root.ToJsonString(ReleaseJson.Options));

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Fact]
    public void ValidateTagRejectsNullRequiredTopLevelValue()
    {
        var fixture = CreateFixture();
        var root = JsonNode.Parse(ReleaseEvidenceV2.Serialize(fixture.Bundle))!.AsObject();
        root["subject"] = null;

        var result = ValidateTag(fixture, root.ToJsonString(ReleaseJson.Options));

        AssertDiagnostic(result, "release-evidence-schema-invalid");
        Assert.Null(result.Summary);
    }

    [Fact]
    public void ValidateTagRejectsUnsupportedV2Schema()
    {
        var fixture = CreateFixture();
        var bundle = ReleaseEvidenceV2.RefreshSubject(fixture.Bundle with { Schema = "unsupported" });

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(bundle));

        AssertDiagnostic(result, "release-evidence-schema-invalid");
    }

    [Fact]
    public void ValidateTagRejectsDuplicateCoordinatedResolutions()
    {
        var fixture = CreateFixture();
        var resolution = fixture.Bundle.CoordinatedPackageReleaseNoteResolutions.Single();
        var bundle = fixture.Bundle with
        {
            CoordinatedPackageReleaseNoteResolutions = [resolution, resolution]
        };

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(ReleaseEvidenceV2.RefreshSubject(bundle)));

        AssertDiagnostic(result, "release-evidence-package-link-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsUnorderedCoordinatedResolutions()
    {
        var fixture = CreateFixture(
            new[]
            {
                CreateResolution("Alpha/ForgeTrust.AppSurface.Alpha.csproj", "1.2.3-preview.1", fixtureBaseCommit: null),
                CreateResolution("Zeta/ForgeTrust.AppSurface.Zeta.csproj", "1.2.3-preview.1", fixtureBaseCommit: null)
            });
        var resolutions = fixture.Bundle.CoordinatedPackageReleaseNoteResolutions
            .Select(resolution => resolution with { PreparationBaseCommit = fixture.BaseCommit })
            .Reverse()
            .ToArray();
        var bundle = fixture.Bundle with { CoordinatedPackageReleaseNoteResolutions = resolutions };

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(ReleaseEvidenceV2.RefreshSubject(bundle)));

        AssertDiagnostic(result, "release-evidence-package-link-mismatch");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("alias")]
    [InlineData("resolved")]
    [InlineData("tag")]
    [InlineData("preparation")]
    public void ValidateTagRejectsInvalidCoordinatedResolution(string mismatch)
    {
        var fixture = CreateFixture();
        var original = fixture.Bundle.CoordinatedPackageReleaseNoteResolutions.Single();
        var invalid = mismatch switch
        {
            "source" => original with { Source = "package" },
            "alias" => original with { AliasPath = "releases/v1.2.3-preview.1.md" },
            "resolved" => original with { ResolvedPath = PackageReleaseLink.CoordinatedReleaseNotesPath },
            "tag" => original with { ReleaseTag = "v9.9.9" },
            "preparation" => original with { PreparationBaseCommit = new string('b', 40) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var bundle = fixture.Bundle with
        {
            CoordinatedPackageReleaseNoteResolutions = [invalid]
        };

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(ReleaseEvidenceV2.RefreshSubject(bundle)));

        AssertDiagnostic(result, "release-evidence-package-link-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsArtifactDigestMismatch()
    {
        var fixture = CreateFixture();
        var bundle = ReplaceArtifact(
            fixture.Bundle,
            fixture.Bundle.ReleaseNotePath,
            "changed release note bytes");

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(bundle));

        AssertDiagnostic(result, "release-evidence-artifact-digest-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsInvalidCurrentPointer()
    {
        var fixture = CreateFixture();
        const string invalidPointer = "# Current coordinated release\n";
        var bundle = ReplaceArtifact(
            fixture.Bundle,
            PackageReleaseLink.CoordinatedReleaseNotesPath,
            invalidPointer);

        var result = ValidateTag(
            fixture,
            ReleaseEvidenceV2.Serialize(bundle),
            currentReleaseContent: invalidPointer);

        AssertDiagnostic(result, "release-evidence-current-pointer-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
    }

    [Fact]
    public void ValidateTagRejectsBadReleaseManifest()
    {
        var fixture = CreateFixture();
        const string invalidManifest = "{}";
        var bundle = ReplaceArtifact(
            fixture.Bundle with
            {
                ReleaseManifestDigest = new ReleaseEvidenceFileDigest(
                    "sha256",
                    ReleaseEvidence.ComputeSha256Hex(invalidManifest))
            },
            fixture.Bundle.ReleaseManifestPath,
            invalidManifest);

        var result = ValidateTag(
            fixture,
            ReleaseEvidenceV2.Serialize(bundle),
            releaseManifestContent: invalidManifest);

        AssertDiagnostic(result, "release-evidence-release-manifest-schema-invalid");
    }

    [Fact]
    public void ValidateTagRejectsMismatchedReleaseManifestDigest()
    {
        var fixture = CreateFixture();
        var bundle = ReleaseEvidenceV2.RefreshSubject(fixture.Bundle with
        {
            ReleaseManifestDigest = new ReleaseEvidenceFileDigest("sha256", new string('b', 64))
        });

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(bundle));

        AssertDiagnostic(result, "release-evidence-release-manifest-digest-mismatch");
    }

    [Fact]
    public async Task ValidatePreparedRejectsMissingReleaseManifest()
    {
        var fixture = CreateFixture();

        try
        {
            await WritePreparedFilesAsync(fixture, includeManifest: false);

            var result = await ReleaseEvidenceV2.ValidatePreparedAsync(
                fixture.Workspace,
                fixture.Version,
                fixture.Classification,
                fixture.BaseCommit,
                ReleaseEvidenceV2.Serialize(fixture.Bundle),
                CancellationToken.None);

            AssertDiagnostic(result, "release-evidence-artifact-digest-mismatch");
        }
        finally
        {
            DeleteWorkspace(fixture.Workspace);
        }
    }

    [Fact]
    public void ValidateTagRejectsMissingPreparationBaseCommit()
    {
        var fixture = CreateFixture();
        var root = JsonNode.Parse(ReleaseEvidenceV2.Serialize(fixture.Bundle))!.AsObject();
        root["commits"]!["preparationBaseCommit"] = null;

        var result = ValidateTag(fixture, root.ToJsonString(ReleaseJson.Options));

        AssertDiagnostic(result, "release-evidence-schema-invalid");
    }

    [Fact]
    public async Task ValidatePreparedRejectsMismatchedReleasePreparationCommit()
    {
        var fixture = CreateFixture();
        var bundle = fixture.Bundle with
        {
            Commits = fixture.Bundle.Commits with
            {
                ReleasePreparationCommit = new string('b', 40)
            }
        };

        try
        {
            await WritePreparedFilesAsync(fixture);

            var result = await ReleaseEvidenceV2.ValidatePreparedAsync(
                fixture.Workspace,
                fixture.Version,
                fixture.Classification,
                new string('c', 40),
                ReleaseEvidenceV2.Serialize(bundle),
                CancellationToken.None);

            AssertDiagnostic(result, "release-evidence-release-preparation-commit-mismatch");
        }
        finally
        {
            DeleteWorkspace(fixture.Workspace);
        }
    }

    [Fact]
    public void ValidateTagRejectsStaleSubjectDigest()
    {
        var fixture = CreateFixture();
        var bundle = fixture.Bundle with
        {
            Subject = fixture.Bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(bundle));

        AssertDiagnostic(result, "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void StableEvidenceRequiresDocsArchiveWhenNotConfigured()
    {
        var fixture = CreateFixture("stable");

        var result = ValidateTag(fixture);

        AssertDiagnostic(result, "release-evidence-docs-archive-required");
    }

    [Theory]
    [InlineData(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("releases/1.2.3", null)]
    public void StableEvidenceRequiresCompleteDocsArchiveProof(string? exactTreePath, string? manifestDigest)
    {
        var fixture = CreateFixture("stable");
        var bundle = ReleaseEvidenceV2.RefreshSubject(fixture.Bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                exactTreePath,
                manifestDigest,
                "appsurface-docs-release-manifest-v1",
                1,
                null)
        });

        var result = ValidateTag(fixture, ReleaseEvidenceV2.Serialize(bundle));

        AssertDiagnostic(result, "release-evidence-docs-archive-required");
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsMissingCatalogForV2Evidence()
    {
        var fixture = CreateFixture("stable");
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "ReleaseEvidenceV2Coverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var options = CreateDocsOptions(fixture, root, catalogPath: TestPathUtils.PathUnder(root, "missing-versions.json"));
            var result = await ReleaseDocsArchiveGate.ValidateStableAsync(
                fixture.Workspace,
                options,
                fixture.Bundle.ToCompatibilityBundle(),
                CancellationToken.None);

            AssertDiagnostic(result, "release-docs-catalog-input-missing");
        }
        finally
        {
            DeleteWorkspace(new ReleaseWorkspace(root));
        }
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsInvalidCatalogBindingForV2Evidence()
    {
        var fixture = CreateFixture("stable");
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "ReleaseEvidenceV2Coverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalogPath = TestPathUtils.PathUnder(root, "versions.json");
        await File.WriteAllTextAsync(
            catalogPath,
            $$"""{"versions":[{"version":"1.2.3","exactTreePath":"releases/1.2.3","releaseManifestSha256":"{{new string('b', 64)}}","visibility":"Public"}]}""");

        try
        {
            var options = CreateDocsOptions(fixture, root, catalogPath);
            var result = await ReleaseDocsArchiveGate.ValidateStableAsync(
                fixture.Workspace,
                options,
                fixture.Bundle.ToCompatibilityBundle(),
                CancellationToken.None);

            AssertDiagnostic(result, "release-evidence-catalog-entry-mismatch");
        }
        finally
        {
            DeleteWorkspace(new ReleaseWorkspace(root));
        }
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsUnsafeCatalogPathForV2Evidence()
    {
        var fixture = CreateFixture("stable");
        var root = TestPathUtils.PathUnder(Path.GetTempPath(), "ReleaseEvidenceV2Coverage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalogPath = TestPathUtils.PathUnder(root, "versions.json");
        var digest = new string('a', 64);
        await File.WriteAllTextAsync(
            catalogPath,
            $$"""{"versions":[{"version":"1.2.3","exactTreePath":"../outside","releaseManifestSha256":"{{digest}}","visibility":"Public"}]}""");
        var bundle = ReleaseEvidenceV2.RefreshSubject(fixture.Bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                "../outside",
                digest,
                "appsurface-docs-release-manifest-v1",
                1,
                new ReleaseEvidenceCatalogEntry("../outside", digest))
        });

        try
        {
            var options = CreateDocsOptions(fixture, root, catalogPath);
            var result = await ReleaseDocsArchiveGate.ValidateStableAsync(
                fixture.Workspace,
                options,
                bundle.ToCompatibilityBundle(),
                CancellationToken.None);

            AssertDiagnostic(result, "release-evidence-docs-exacttreepath-unsafe");
        }
        finally
        {
            DeleteWorkspace(new ReleaseWorkspace(root));
        }
    }

    private static V2Fixture CreateFixture(string classification = "prerelease")
    {
        var version = SemVer.Parse(classification == "stable" ? "1.2.3" : "1.2.3-preview.1");
        var baseCommit = new string('a', 40);
        var resolutions = new[]
        {
            CreateResolution(
                "Core/ForgeTrust.AppSurface.Core.csproj",
                version.ToString(),
                baseCommit)
        };
        return CreateFixture(resolutions, classification, version, baseCommit);
    }

    private static V2Fixture CreateFixture(
        IReadOnlyList<CoordinatedPackageReleaseNoteResolution> resolutions)
    {
        var version = SemVer.Parse("1.2.3-preview.1");
        var baseCommit = new string('a', 40);
        return CreateFixture(resolutions, "prerelease", version, baseCommit);
    }

    private static V2Fixture CreateFixture(
        IReadOnlyList<CoordinatedPackageReleaseNoteResolution> resolutions,
        string classification,
        SemVer version,
        string baseCommit)
    {
        var workspace = new ReleaseWorkspace(
            TestPathUtils.PathUnder(Path.GetTempPath(), "ReleaseEvidenceV2Coverage", Guid.NewGuid().ToString("N")));
        var releaseNote = $"# Release {version}\n";
        var releaseSidecar = $"title: Release {version}\n";
        var currentRelease = ReleaseCurrentPointer.Build(version);
        var currentReleaseSidecar = "title: Current coordinated release\n";
        var manifest = new ReleaseManifestV2(
            ReleaseManifestV2Validator.Schema,
            version.ToString(),
            version.TagName,
            "2026-07-30",
            baseCommit,
            classification,
            [],
            resolutions.Select(resolution => resolution.Project).ToArray(),
            resolutions,
            [],
            []);
        var releaseManifest = JsonSerializer.Serialize(manifest, ReleaseJson.Options) + Environment.NewLine;
        var bundle = ReleaseEvidenceV2.BuildDraft(
            workspace,
            version,
            classification,
            new DateOnly(2026, 7, 30),
            baseCommit,
            releaseNote,
            releaseSidecar,
            releaseManifest,
            currentRelease,
            currentReleaseSidecar,
            resolutions);

        return new V2Fixture(
            workspace,
            version,
            classification,
            baseCommit,
            releaseNote,
            releaseSidecar,
            releaseManifest,
            currentRelease,
            currentReleaseSidecar,
            bundle);
    }

    private static CoordinatedPackageReleaseNoteResolution CreateResolution(
        string project,
        string version,
        string? fixtureBaseCommit)
    {
        var baseCommit = fixtureBaseCommit ?? new string('a', 40);
        return new CoordinatedPackageReleaseNoteResolution(
            project,
            "coordinated",
            PackageReleaseLink.CoordinatedReleaseNotesPath,
            $"releases/v{version}.md",
            $"v{version}",
            baseCommit);
    }

    private static ReleaseEvidenceBundleV2 ReplaceArtifact(
        ReleaseEvidenceBundleV2 bundle,
        string path,
        string content)
    {
        var digests = bundle.ReleaseArtifactDigests
            .Select(digest => digest.Path == path
                ? digest with { Value = ReleaseEvidence.ComputeSha256Hex(content) }
                : digest)
            .ToArray();
        return ReleaseEvidenceV2.RefreshSubject(bundle with { ReleaseArtifactDigests = digests });
    }

    private static ReleaseEvidenceValidationResult ValidateTag(
        V2Fixture fixture,
        string? evidenceJson = null,
        string? currentReleaseContent = null,
        string? releaseManifestContent = null)
    {
        return ReleaseEvidenceV2.ValidateTag(
            fixture.Version,
            fixture.Classification,
            fixture.Version.TagName,
            new string('b', 40),
            fixture.ReleaseNote,
            fixture.ReleaseSidecar,
            releaseManifestContent ?? fixture.ReleaseManifest,
            currentReleaseContent ?? fixture.CurrentRelease,
            fixture.CurrentReleaseSidecar,
            evidenceJson ?? ReleaseEvidenceV2.Serialize(fixture.Bundle));
    }

    private static async Task WritePreparedFilesAsync(V2Fixture fixture, bool includeManifest = true)
    {
        await WriteFileAsync(fixture.Workspace.ReleaseNotePath(fixture.Version), fixture.ReleaseNote);
        await WriteFileAsync(fixture.Workspace.ReleaseSidecarPath(fixture.Version), fixture.ReleaseSidecar);
        if (includeManifest)
        {
            await WriteFileAsync(fixture.Workspace.ReleaseManifestPath(fixture.Version), fixture.ReleaseManifest);
        }

        await WriteFileAsync(fixture.Workspace.CurrentReleasePath, fixture.CurrentRelease);
        await WriteFileAsync(fixture.Workspace.CurrentReleaseSidecarPath, fixture.CurrentReleaseSidecar);
    }

    private static async Task WriteFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static ReleaseOptions CreateDocsOptions(V2Fixture fixture, string root, string catalogPath) =>
        new(
            "publish",
            fixture.Workspace.RepositoryRoot,
            fixture.Version,
            fixture.Version.TagName,
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false,
            BaseRef: "main",
            DocsCatalogPath: catalogPath,
            DocsTrustedReleaseRootPath: root);

    private static void AssertDiagnostic(
        ReleaseEvidenceValidationResult result,
        string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static void AssertDiagnostic(
        ReleaseDocsArchiveGateResult result,
        string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    private static void DeleteWorkspace(ReleaseWorkspace workspace)
    {
        if (Directory.Exists(workspace.RepositoryRoot))
        {
            Directory.Delete(workspace.RepositoryRoot, recursive: true);
        }
    }

    private sealed record V2Fixture(
        ReleaseWorkspace Workspace,
        SemVer Version,
        string Classification,
        string BaseCommit,
        string ReleaseNote,
        string ReleaseSidecar,
        string ReleaseManifest,
        string CurrentRelease,
        string CurrentReleaseSidecar,
        ReleaseEvidenceBundleV2 Bundle);
}
