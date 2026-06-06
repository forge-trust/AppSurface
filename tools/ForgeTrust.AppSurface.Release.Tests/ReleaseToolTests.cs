using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Release;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseToolTests : IDisposable
{
    private const string TaggedReleaseNoteContent = "# Release 0.1.0-preview.1\n";
    private const string TaggedReleaseSidecarContent = "title: Release 0.1.0-preview.1\n";

    private readonly string _repositoryRoot;

    public ReleaseToolTests()
    {
        _repositoryRoot = Path.Join(Path.GetTempPath(), "ReleaseToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
    }

    [Fact]
    public async Task HelpAndUnknownCommandUseDocumentedUsagePaths()
    {
        var help = await RunAsync(["--help"], new FakeCommandRunner());
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("USAGE", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("check", help.Stdout, StringComparison.Ordinal);

        var unknown = await RunAsync(["frobnicate"], new FakeCommandRunner());
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("frobnicate", unknown.Stderr, StringComparison.Ordinal);

        var unknownWithReleaseVersion = await RunAsync(["frobnicate", "--version", "0.1.0"], new FakeCommandRunner());
        Assert.Equal(1, unknownWithReleaseVersion.ExitCode);
        Assert.Contains("Unrecognized command 'frobnicate'.", unknownWithReleaseVersion.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("System.FormatException", unknownWithReleaseVersion.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsMissingRequiredSources()
    {
        await WriteFileAsync(
            "CHANGELOG.md",
            "# Changelog\n");

        var result = await RunAsync(["check", "--version", "0.1.0-preview.1"], new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-required-file-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/unreleased.md", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsVersionWithLeadingTagPrefix()
    {
        var result = await RunAsync(["check", "--version", "v0.1.0"], new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-version-leading-v", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Problem:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Cause:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Fix:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Docs:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseFailuresUseDiagnosticEnvelope()
    {
        var missingVersion = await RunAsync(["check"], new FakeCommandRunner());
        Assert.Equal(1, missingVersion.ExitCode);
        Assert.Contains("Code: release-version-required", missingVersion.Stderr, StringComparison.Ordinal);

        var missingOptionValue = await RunAsync(["check", "--version"], new FakeCommandRunner());
        Assert.Equal(1, missingOptionValue.ExitCode);
        Assert.Contains("Code: release-version-required", missingOptionValue.Stderr, StringComparison.Ordinal);

        var invalidDate = await RunAsync(["prepare", "--version", "0.1.0-preview.1", "--date", "05/25/2026"], new FakeCommandRunner());
        Assert.Equal(1, invalidDate.ExitCode);
        Assert.Contains("Code: release-date-invalid", invalidDate.Stderr, StringComparison.Ordinal);

        var unknownOption = await RunAsync(["check", "--version", "0.1.0-preview.1", "--bogus"], new FakeCommandRunner());
        Assert.Equal(1, unknownOption.ExitCode);
        Assert.Contains("--bogus", unknownOption.Stderr, StringComparison.Ordinal);

        var invalidVersion = await RunAsync(["check", "--version", "01.0.0"], new FakeCommandRunner());
        Assert.Equal(1, invalidVersion.ExitCode);
        Assert.Contains("Code: release-version-invalid", invalidVersion.Stderr, StringComparison.Ordinal);
        Assert.Contains("Severity: error", invalidVersion.Stderr, StringComparison.Ordinal);

        var overflowingVersion = await RunAsync(["check", "--version", "999999999999999999.0.0"], new FakeCommandRunner());
        Assert.Equal(1, overflowingVersion.ExitCode);
        Assert.Contains("Code: release-version-invalid", overflowingVersion.Stderr, StringComparison.Ordinal);

        var missingTag = await RunAsync(["publish", "--version", "0.1.0-preview.1"], new FakeCommandRunner());
        Assert.Equal(1, missingTag.ExitCode);
        Assert.Contains("Code: release-tag-required", missingTag.Stderr, StringComparison.Ordinal);

        var mismatchedTag = await RunAsync(["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.2"], new FakeCommandRunner());
        Assert.Equal(1, mismatchedTag.ExitCode);
        Assert.Contains("Code: release-tag-version-mismatch", mismatchedTag.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckOnlyOptionsAreRejectedByOtherCommands()
    {
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--fail-on-warnings"],
            new FakeCommandRunner());

        Assert.Equal(1, prepare.ExitCode);
        Assert.Contains("--fail-on-warnings", prepare.Stderr, StringComparison.Ordinal);

        var publish = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--allow-existing-targets"],
            new FakeCommandRunner());

        Assert.Equal(1, publish.ExitCode);
        Assert.Contains("--allow-existing-targets", publish.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareDryRunDoesNotWriteGeneratedFiles()
    {
        await SeedRepositoryAsync();
        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25", "--dry-run"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Join(_repositoryRoot, "releases", "v0.1.0-preview.1.md")));
        Assert.Contains("## Manual review gate", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("## Release evidence bundle", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("## Dry-run plan", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.release.json", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.evidence.json", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareWritesExternalReportDuringDryRun()
    {
        await SeedRepositoryAsync();
        var reportPath = Path.Join(Path.GetTempPath(), "ReleaseToolReports", Guid.NewGuid().ToString("N"), "prepare-report.md");

        var result = await RunAsync(
            [
                "prepare",
                "--version",
                "0.1.0-preview.1",
                "--date",
                "2026-05-25",
                "--dry-run",
                "--report",
                reportPath
            ],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(reportPath));
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("# Release readiness report", report, StringComparison.Ordinal);
        Assert.Contains("## Manual review gate", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareWritesReleaseArtifactsAndUpdatesPublicPublishedPackagePaths()
    {
        await SeedRepositoryAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            stdout,
            stderr,
            _repositoryRoot,
            commandRunner: FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, exitCode);
        var report = stdout.ToString();
        Assert.Contains("## Manual review gate", report, StringComparison.Ordinal);
        Assert.Contains("## Release evidence bundle", report, StringComparison.Ordinal);
        Assert.Contains("## Files written", report, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.md", report, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.evidence.json", report, StringComparison.Ordinal);
        Assert.Contains("CHANGELOG.md", report, StringComparison.Ordinal);

        var releaseNote = await ReadFileAsync("releases/v0.1.0-preview.1.md");
        Assert.Contains("# Release 0.1.0-preview.1", releaseNote, StringComparison.Ordinal);

        var sidecar = await ReadFileAsync("releases/v0.1.0-preview.1.md.yml");
        Assert.Contains("title: Release 0.1.0-preview.1", sidecar, StringComparison.Ordinal);

        var manifestJson = await ReadFileAsync("releases/v0.1.0-preview.1.release.json");
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal("appsurface-release-manifest-v1", manifest.RootElement.GetProperty("schema").GetString());
        Assert.Equal("0.1.0-preview.1", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("prerelease", manifest.RootElement.GetProperty("releaseClassification").GetString());
        Assert.Equal("abc123", manifest.RootElement.GetProperty("sourceCommit").GetString());
        Assert.Contains(
            manifest.RootElement.GetProperty("generatedFiles").EnumerateArray(),
            path => string.Equals(path.GetString(), "releases/v0.1.0-preview.1.evidence.json", StringComparison.Ordinal));

        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        using var evidence = JsonDocument.Parse(evidenceJson);
        Assert.Equal("appsurface-release-evidence-bundle-v1", evidence.RootElement.GetProperty("schema").GetString());
        Assert.Equal("0.1.0-preview.1", evidence.RootElement.GetProperty("version").GetString());
        Assert.Equal("releases/v0.1.0-preview.1.release.json", evidence.RootElement.GetProperty("releaseManifestPath").GetString());
        var artifactDigests = evidence.RootElement.GetProperty("releaseArtifactDigests").EnumerateArray().ToArray();
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/v0.1.0-preview.1.md", StringComparison.Ordinal));
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/v0.1.0-preview.1.md.yml", StringComparison.Ordinal));
        Assert.Contains(artifactDigests, digest => string.Equals(digest.GetProperty("path").GetString(), "releases/v0.1.0-preview.1.release.json", StringComparison.Ordinal));
        Assert.Equal("notConfigured", evidence.RootElement.GetProperty("docsArchive").GetProperty("status").GetString());
        Assert.NotEmpty(evidence.RootElement.GetProperty("subject").GetProperty("sha256").GetString()!);

        var packageIndex = await ReadFileAsync("packages/package-index.yml");
        Assert.Contains("release_notes_path: releases/v0.1.0-preview.1.md", packageIndex, StringComparison.Ordinal);
        Assert.Contains("classification: support", packageIndex, StringComparison.Ordinal);
        Assert.Contains("release_notes_path: releases/unreleased.md", packageIndex, StringComparison.Ordinal);

        var changelog = await ReadFileAsync("CHANGELOG.md");
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", changelog, StringComparison.Ordinal);
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release manifest: `releases/v0.1.0-preview.1.release.json`", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release evidence bundle: `releases/v0.1.0-preview.1.evidence.json`", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("- Current work.", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("[v0.1.0-preview.1.release.json]", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("## No tagged releases yet", changelog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareReportsInvalidSidecarThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/unreleased.md.yml", "title: [\n");

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-sidecar-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Problem:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Cause:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Fix:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("Docs:", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareStableReleaseRecordsPolicyWarningInManifest()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        var manifestJson = await ReadFileAsync("releases/v0.1.0.release.json");
        Assert.Contains("\"warningIds\": [", manifestJson, StringComparison.Ordinal);
        Assert.Contains("release-stable-package-policy-missing", manifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckWritesReportWhenReportPathIsRequested()
    {
        await SeedRepositoryAsync();
        var reportPath = Path.Join(_repositoryRoot, "artifacts", "release-report.md");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--report", reportPath],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(reportPath));
        Assert.Contains("# Release readiness report", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsReportWriteFailuresThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--report", _repositoryRoot],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.True(
            result.Stderr.Contains("Code: release-io-failure", StringComparison.Ordinal)
                || result.Stderr.Contains("Code: release-path-permission-denied", StringComparison.Ordinal),
            result.Stderr);
    }

    [Fact]
    public async Task PrepareRejectsExistingVersionedTargets()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/v0.1.0-preview.1.md", "# Existing\n");

        var result = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-target-exists", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCanFailOnStablePolicyWarnings()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--fail-on-warnings"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-stable-package-policy-missing", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckWarnsWhenPrereleaseLabelCannotTriggerProtectedPublishing()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["check", "--version", "0.1.0-foo.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release-prerelease-label-unprotected", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("will not trigger protected NuGet prerelease publishing", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsTargetAndNarrativeWarningsWithoutFailingByDefault()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/v0.1.0-preview.1.md", "# Existing\n");
        await WriteFileAsync(
            "releases/unreleased.md",
            """
            # Unreleased

            TODO: replace this placeholder before release.
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release-target-exists", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-migration-guidance-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-placeholder-copy", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCanAllowExistingTargetsForPreparedReleaseReview()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--fail-on-warnings", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("## Release evidence bundle", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-target-exists", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRequiresCompleteGeneratedTargetsForPreparedReleaseReview()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("releases/v0.1.0-preview.1.release.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--fail-on-warnings", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-generated-target-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.md", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.md.yml", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.evidence.json", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-target-exists", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsStalePreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidencePath = RepositoryPath("releases/v0.1.0-preview.1.evidence.json");
        var staleEvidence = (await File.ReadAllTextAsync(evidencePath)).Replace(
            "\"version\": \"0.1.0-preview.1\"",
            "\"version\": \"0.1.0-preview.2\"",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(evidencePath, staleEvidence);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-version-mismatch", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-evidence-subject-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsStalePreparedReleaseArtifactBytes()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await File.AppendAllTextAsync(RepositoryPath("releases/v0.1.0-preview.1.md"), "\nLate edit.\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-artifact-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsMissingPreparedReleaseArtifactBytes()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        File.Delete(RepositoryPath("releases/v0.1.0-preview.1.md.yml"));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-artifact-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsMissingPreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        File.Delete(RepositoryPath("releases/v0.1.0-preview.1.evidence.json"));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-missing", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsMalformedPreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", "{\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-schema-invalid", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPreparedReleaseEvidenceWithMissingNestedFields()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(evidenceJson, ReleaseJson.Options)!;
        var malformed = bundle with
        {
            ReleaseManifestDigest = new ReleaseEvidenceFileDigest(null!, bundle.ReleaseManifestDigest.Value)
        };
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(malformed));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-schema-invalid", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRejectsPreparedReleaseEvidenceFinalizedForDifferentCommit()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        var evidenceJson = await ReadFileAsync("releases/v0.1.0-preview.1.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(evidenceJson, ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            Commits = bundle.Commits with { ReleasePreparationCommit = "old-release-prep-commit" },
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(mismatched));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-release-preparation-commit-mismatch", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-evidence-subject-digest-mismatch", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAllowsNeighboringHistoricalEvidenceVersions()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", "{}\n");
        await WriteFileAsync("releases/v0.1.00.evidence.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-evidence-duplicate", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckIgnoresMalformedNeighboringEvidenceFileNames()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/vnot-semver.evidence.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-evidence-duplicate", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceBuildDraftAllowsEmptyPackageUpdates()
    {
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(_repositoryRoot),
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            []);

        Assert.Empty(evidence.PackageReleaseNotePaths);
        Assert.Equal("releases/v0.1.0-preview.1.evidence.json", evidence.Subject.Name);
        Assert.NotEmpty(evidence.Subject.Sha256);
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsDocsCatalogMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var manifestDigest = new string('a', 64);
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: manifestDigest,
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: new ReleaseEvidenceCatalogEntry("releases/other", manifestDigest)),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-catalog-entry-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsMalformedTagEvidence()
    {
        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            "{");

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsUnsupportedSchema()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            Schema = "appsurface-release-evidence-bundle-v0",
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsMissingTopLevelFields()
    {
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJson(releaseManifest).Replace(
            "\"schema\": \"appsurface-release-evidence-bundle-v1\"",
            "\"schema\": null",
            StringComparison.Ordinal);

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("version")]
    [InlineData("tag")]
    [InlineData("releaseClassification")]
    [InlineData("releaseNotePath")]
    [InlineData("releaseSidecarPath")]
    [InlineData("releaseManifestPath")]
    [InlineData("evidencePath")]
    [InlineData("releaseManifestDigest")]
    [InlineData("releaseArtifactDigests")]
    [InlineData("packageReleaseNotePaths")]
    [InlineData("docsArchive")]
    [InlineData("commits")]
    [InlineData("generatedBy")]
    [InlineData("generatedAtUtc")]
    [InlineData("subject")]
    public void ReleaseEvidenceValidationRejectsEveryMissingTopLevelField(string propertyName)
    {
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJsonWithNull(releaseManifest, propertyName);

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Theory]
    [InlineData("releaseManifestDigest.algorithm")]
    [InlineData("releaseManifestDigest.value")]
    [InlineData("releaseArtifactDigests.0.path")]
    [InlineData("releaseArtifactDigests.0.algorithm")]
    [InlineData("releaseArtifactDigests.0.value")]
    [InlineData("docsArchive.status")]
    [InlineData("generatedBy.tool")]
    [InlineData("subject.name")]
    [InlineData("subject.sha256")]
    [InlineData("packageReleaseNotePaths.0.project")]
    [InlineData("packageReleaseNotePaths.0.releaseNotesPath")]
    public void ReleaseEvidenceValidationRejectsEveryMissingNestedField(string path)
    {
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJsonWithNull(releaseManifest, path.Split('.'));

        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsTagAndTagCommitMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            Tag = "v0.1.0-preview.2",
            Commits = bundle.Commits with { TagCommit = "other-tag-commit" },
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-version-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-tag-commit-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsReleaseManifestDigestMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            ReleaseManifestDigest = new ReleaseEvidenceFileDigest("sha512", new string('a', 64)),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-release-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsMissingReleaseArtifactDigest()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            ReleaseArtifactDigests = bundle.ReleaseArtifactDigests
                .Where(digest => !string.Equals(digest.Path, "releases/v0.1.0-preview.1.md.yml", StringComparison.Ordinal))
                .ToArray(),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsInvalidReleaseArtifactDigest()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            ReleaseArtifactDigests = bundle.ReleaseArtifactDigests
                .Select(digest => string.Equals(digest.Path, "releases/v0.1.0-preview.1.md", StringComparison.Ordinal)
                    ? digest with { Algorithm = "sha512" }
                    : digest)
                .ToArray(),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-artifact-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsIncompleteDocsArchiveFields()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: null,
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsUnsafeDocsArchivePathAndDigest()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "../outside",
                ReleaseManifestSha256: "not-a-sha",
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-catalog-entry-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationAcceptsCompleteDocsArchiveFieldsWithoutCatalogEntry()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: "releases/0.1.0-preview.1",
                ReleaseManifestSha256: new string('a', 64),
                ReleaseManifestSchema: "appsurface-docs-release-manifest-v1",
                FileCount: 1,
                CatalogEntry: null),
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-catalog-entry-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-manifest-digest-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceSummaryReportsOptionalAttestationMode()
    {
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;

        var summary = (bundle with
        {
            Attestation = new ReleaseEvidenceAttestation("github-artifact-attestation", "subject", new string('a', 64))
        }).ToSummary("validated");

        Assert.Equal("github-artifact-attestation", summary.Attestation);
    }

    [Fact]
    public void ReleaseReportRendererPrintsNonPendingEvidenceSummaryFields()
    {
        var result = new ReleaseCheckResult(
            "0.1.0-preview.1",
            "prerelease",
            "abc123",
            ["releases/v0.1.0-preview.1.evidence.json"],
            new ReleaseEvidenceSummary(
                "releases/v0.1.0-preview.1.evidence.json",
                "appsurface-release-evidence-bundle-v1",
                "tag-bound evidence validated for publish",
                new string('a', 64),
                new string('b', 64),
                "releases/0.1.0-preview.1",
                "tag-commit",
                "not required"),
            [],
            []);

        var report = ReleaseReportRenderer.RenderCheck(result);

        Assert.Contains("- Docs archive manifest SHA-256: `" + new string('b', 64) + "`", report, StringComparison.Ordinal);
        Assert.Contains("- Catalog exact tree path: `releases/0.1.0-preview.1`", report, StringComparison.Ordinal);
        Assert.Contains("- Tag commit: `tag-commit`", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsTagBoundPackagePathMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            PackageReleaseNotePaths =
            [
                new ReleaseEvidencePackagePath("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md")
            ],
            Subject = bundle.Subject with { Sha256 = new string('b', 64) }
        };

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(mismatched));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-package-path-mismatch");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Theory]
    [InlineData("\"releaseArtifactDigests\": [", "\"releaseArtifactDigests\": [ null,")]
    [InlineData("\"packageReleaseNotePaths\": [", "\"packageReleaseNotePaths\": [ null,")]
    public void ReleaseEvidenceValidationRejectsNullArrayEntries(string oldValue, string newValue)
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var malformedEvidence = CreateReleaseEvidenceJson(releaseManifest).Replace(oldValue, newValue, StringComparison.Ordinal);

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            malformedEvidence);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-schema-invalid");
    }

    [Fact]
    public async Task CheckReportsMissingPackagePolicyAndEmptyPublicPackageSet()
    {
        await SeedRepositoryAsync();
        File.Delete(Path.Join(_repositoryRoot, ".github", "workflows", "nuget-prerelease-publish.yml"));
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Web/Support.csproj
                classification: support
                publish_decision: support_publish
                release_notes_path: releases/unreleased.md
                order: 10
            """);

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-prerelease-package-path-missing", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("release-no-public-packages", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckReportsInvalidPackageManifestThroughDiagnosticEnvelope()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync("packages/package-index.yml", "packages: [\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-package-index-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutStablePackageWorkflow()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-stable-package-policy-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishKeepsStableReleaseBlockedEvenWhenStableWorkflowFileExists()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(
            ".github/workflows/nuget-release-publish.yml",
            "name: Placeholder Stable Publish\n");

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            new FakeCommandRunner());

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-stable-package-policy-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git cat-file -t refs/tags/v0.1.0-preview.1", "commit", "release-tag-lightweight")]
    [InlineData("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", "stdout failure", "release-tag-commit-missing")]
    [InlineData("git merge-base --is-ancestor abc123 origin/main", "", "release-tag-unreachable-from-main")]
    [InlineData("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", "", "release-prerelease-packages-not-published")]
    [InlineData("gh release view v0.1.0-preview.1 --json url", "{\"url\":\"https://example.test\"}", "release-github-release-exists")]
    [InlineData("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md", "", "release-note-missing-from-tag")]
    public async Task PublishReportsTagAndGitHubValidationFailures(string failingCommand, string stdout, string expectedCode)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var failingResult = expectedCode switch
        {
            "release-tag-lightweight" => new CommandResult(0, stdout, ""),
            "release-github-release-exists" => new CommandResult(0, stdout, ""),
            "release-prerelease-packages-not-published" => new CommandResult(0, stdout, ""),
            _ => new CommandResult(1, stdout, "validation failed")
        };
        runner.Add(failingCommand, failingResult);

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Code: {expectedCode}", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsTagBoundEvidenceDiagnosticsBeforeReleaseCreation()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        runner.Add(
            "git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json",
            new CommandResult(0, ReleaseEvidence.Serialize(bundle with { Schema = "unsupported" }), ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-schema-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishCanReturnJsonWithoutGithubOutputFile()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            CreateSuccessfulPublishRunner());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"prerelease\"", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"evidencePath\": \"releases/v0.1.0-preview.1.evidence.json\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishEmitsStructuredOutputsForAnnotatedPrereleaseTag()
    {
        await SeedRepositoryAsync();
        var githubOutput = Path.Join(_repositoryRoot, "github-output.txt");
        var runner = CreateSuccessfulPublishRunner();

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0-preview.1",
                "--tag",
                "v0.1.0-preview.1",
                "--dry-run",
                "--github-output",
                githubOutput
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"tag\": \"v0.1.0-preview.1\"", result.Stdout, StringComparison.Ordinal);

        var output = await File.ReadAllTextAsync(githubOutput);
        Assert.Contains("tag=v0.1.0-preview.1", output, StringComparison.Ordinal);
        Assert.Contains("tag_commit=abc123", output, StringComparison.Ordinal);
        Assert.Contains("evidence_path=releases/v0.1.0-preview.1.evidence.json", output, StringComparison.Ordinal);
        Assert.Contains("evidence_subject_sha256=", output, StringComparison.Ordinal);
        Assert.Contains("evidence_tag_commit=abc123", output, StringComparison.Ordinal);
        Assert.Contains("prerelease=true", output, StringComparison.Ordinal);
        Assert.Contains("notes_file=", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorsHandleFallbackShapes()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var workspace = new ReleaseWorkspace(_repositoryRoot);

        Assert.Equal(Path.Join(_repositoryRoot, "CHANGELOG.md"), workspace.ChangelogPath);
        Assert.Equal(Path.Join(_repositoryRoot, "releases", "v0.1.0-preview.1.md"), workspace.ReleaseNotePath(version));
        Assert.True(ReleaseWorkspace.IsUnderPath(_repositoryRoot, Path.Join(_repositoryRoot, "releases")));
        Assert.False(ReleaseWorkspace.IsUnderPath(_repositoryRoot, Path.GetTempPath()));

        var changelog = ChangelogEditor.RollForward(
            """
            # Changelog

            ## Unreleased

            ## No tagged releases yet

            AppSurface is still defining its first release boundary.
            """,
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release manifest: `releases/v0.1.0-preview.1.release.json`", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release evidence bundle: `releases/v0.1.0-preview.1.evidence.json`", changelog, StringComparison.Ordinal);
        Assert.DoesNotContain("## No tagged releases yet", changelog, StringComparison.Ordinal);

        var packageIndex = PackageIndexEditor.UpdatePublicPublishedReleaseNotes(
            """
            packages:
              - project: Core.csproj
                classification: public
                publish_decision: publish
                order: 10
            """,
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("release_notes_path: releases/v0.1.0-preview.1.md", packageIndex, StringComparison.Ordinal);

        var appendedChangelog = ChangelogEditor.RollForward(
            "# Changelog\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", appendedChangelog, StringComparison.Ordinal);
        Assert.Contains("- Release evidence bundle: `releases/v0.1.0-preview.1.evidence.json`", appendedChangelog, StringComparison.Ordinal);
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", appendedChangelog, StringComparison.Ordinal);

        var terminalUnreleasedChangelog = ChangelogEditor.RollForward(
            "# Changelog\n\n## Unreleased\n\n- Current work.\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.DoesNotContain("- Current work.", terminalUnreleasedChangelog, StringComparison.Ordinal);
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", terminalUnreleasedChangelog, StringComparison.Ordinal);
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", terminalUnreleasedChangelog, StringComparison.Ordinal);

        var multiReleaseChangelog = ChangelogEditor.RollForward(
            "# Changelog\n\n## Unreleased\n\n## 0.0.1 - 2026-01-01\n\n- Previous work.\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("- Narrative release note: [Upcoming release note](./releases/unreleased.md)", multiReleaseChangelog, StringComparison.Ordinal);
        Assert.Matches("## Unreleased[\\s\\S]*## 0\\.1\\.0-preview\\.1 - 2026-05-25[\\s\\S]*## 0\\.0\\.1 - 2026-01-01", multiReleaseChangelog);

        var packageIndexWithoutOrder = PackageIndexEditor.UpdatePublicPublishedReleaseNotes(
            """
            packages:
              - project: Core.csproj
                classification: public
                publish_decision: publish
            """,
            "releases/v0.1.0-preview.1.md");
        Assert.EndsWith("    release_notes_path: releases/v0.1.0-preview.1.md\n", packageIndexWithoutOrder, StringComparison.Ordinal);

        var placeholderWithFollowingRelease = ChangelogEditor.RollForward(
            """
            # Changelog

            ## Unreleased

            ## No tagged releases yet

            Placeholder.

            ## 0.0.1 - 2026-01-01

            - Previous work.
            """,
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.DoesNotContain("Placeholder.", placeholderWithFollowingRelease, StringComparison.Ordinal);
        Assert.Contains("## 0.0.1 - 2026-01-01", placeholderWithFollowingRelease, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleRegistersReleaseServicesAndNoOpHooks()
    {
        var module = new ReleaseCliModule();
        var context = new StartupContext([], module);
        var services = new ServiceCollection();

        module.ConfigureServices(context, services);
        module.ConfigureHostBeforeServices(context, Host.CreateDefaultBuilder());
        module.ConfigureHostAfterServices(context, Host.CreateDefaultBuilder());
        module.RegisterDependentModules(new ModuleDependencyBuilder());

        using var provider = services.BuildServiceProvider();
        Assert.Equal(Directory.GetCurrentDirectory(), provider.GetRequiredService<ReleaseExecutionContext>().CurrentDirectory);
        Assert.IsType<ProcessCommandRunner>(provider.GetRequiredService<ICommandRunner>());
        Assert.IsType<SystemReleaseClock>(provider.GetRequiredService<IReleaseClock>());
    }

    [Fact]
    public void WorkspaceRejectsRootedRepositoryRelativePaths()
    {
        var workspace = new ReleaseWorkspace(_repositoryRoot);

        var exception = Assert.Throws<ArgumentException>(() => workspace.PathFor(Path.GetTempPath()));

        Assert.Equal("relativePath", exception.ParamName);
    }

    [Fact]
    public void WorkspaceRejectsTraversalRepositoryRelativePaths()
    {
        var workspace = new ReleaseWorkspace(_repositoryRoot);

        var exception = Assert.Throws<ArgumentException>(() => workspace.PathFor("../outside.md"));

        Assert.Equal("relativePath", exception.ParamName);
    }

    [Fact]
    public async Task ProcessCommandRunnerTimesOutStuckCommands()
    {
        var runner = new ProcessCommandRunner();
        var invocation = CreateSlowCommandInvocation();

        var result = await runner.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(124, result.ExitCode);
        Assert.Contains("timed out", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReportsPrereleasePackageWorkflowErrors()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add(
            "gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"",
            new CommandResult(1, "", "workflow query failed"));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-prerelease-packages-not-published", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("workflow query failed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReportsCommandStdoutWhenStderrIsEmpty()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", new CommandResult(1, "stdout failure", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("stdout failure", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsTagsThatCannotCreateTempPath()
    {
        await SeedRepositoryAsync();
        var runner = new FakeCommandRunner();
        runner.Add("git cat-file -t refs/tags//", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags//^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add("git merge-base --is-ancestor abc123 origin/main", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"/\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/1\n", ""));
        runner.Add("gh release view / --json url", new CommandResult(1, "", "not found"));
        var publishing = new ReleasePublishing(new ReleaseWorkspace(_repositoryRoot), runner);
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            "/",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => publishing.PublishAsync(options, CancellationToken.None));

        Assert.Equal("release-tag-invalid-temp-path", exception.Diagnostic.Code);
    }

    [Fact]
    public async Task PublishWritesMultilineGithubOutputs()
    {
        await SeedRepositoryAsync();
        var githubOutput = Path.Join(_repositoryRoot, "artifacts", "github-output.txt");
        var publishing = new ReleasePublishing(new ReleaseWorkspace(_repositoryRoot), new FakeCommandRunner());
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            "v0.1.0-preview.1",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: githubOutput,
            FailOnWarnings: false,
            AllowExistingTargets: false);
        var outputs = new PublishOutputs(
            "0.1.0-preview.1",
            "v0.1.0-preview.1",
            "abc123",
            "releases/v0.1.0-preview.1.md",
            "first\nsecond",
            "prerelease",
            "releases/v0.1.0-preview.1.evidence.json",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "abc123",
            null,
            Prerelease: true,
            DryRun: true);

        await publishing.WriteOutputsAsync(outputs, options, CancellationToken.None);

        var output = await File.ReadAllTextAsync(githubOutput);
        Assert.Contains("notes_file<<EOF_", output, StringComparison.Ordinal);
        Assert.Contains("first\nsecond", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsGithubOutputRootPath()
    {
        var publishing = new ReleasePublishing(new ReleaseWorkspace(_repositoryRoot), new FakeCommandRunner());
        var options = new ReleaseOptions(
            "publish",
            _repositoryRoot,
            SemVer.Parse("0.1.0-preview.1"),
            "v0.1.0-preview.1",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: Path.GetPathRoot(_repositoryRoot),
            FailOnWarnings: false,
            AllowExistingTargets: false);
        var outputs = new PublishOutputs(
            "0.1.0-preview.1",
            "v0.1.0-preview.1",
            "abc123",
            "releases/v0.1.0-preview.1.md",
            "notes.md",
            "prerelease",
            "releases/v0.1.0-preview.1.evidence.json",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "abc123",
            null,
            Prerelease: true,
            DryRun: true);

        var exception = await Assert.ThrowsAsync<ReleaseToolException>(() => publishing.WriteOutputsAsync(outputs, options, CancellationToken.None));

        Assert.Equal("release-github-output-path-invalid", exception.Diagnostic.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repositoryRoot))
        {
            Directory.Delete(_repositoryRoot, recursive: true);
        }
    }

    private async Task SeedRepositoryAsync()
    {
        await WriteFileAsync(
            ".github/workflows/nuget-prerelease-publish.yml",
            "name: NuGet Prerelease Publish\n");
        await WriteFileAsync(
            "CHANGELOG.md",
            """
            # Changelog

            ## Unreleased

            ### Added

            - Current work.

            ## No tagged releases yet

            AppSurface is still defining its first release boundary.
            """);
        await WriteFileAsync(
            "releases/unreleased.md",
            """
            # Unreleased

            This is the living release note for the next coordinated AppSurface version.

            ## What is taking shape

            - The release story is almost ready.

            ## Included in the next coordinated version

            ### Release and docs surface

            - The release cockpit prepares release pull requests.

            ## Migration watch

            - No migration steps are required.
            """);
        await WriteFileAsync(
            "releases/unreleased.md.yml",
            """
            title: Unreleased
            summary: Living proof artifact.
            page_type: release-note
            nav_group: Releases
            order: 15
            """);
        await WriteFileAsync(
            "releases/templates/tagged-release-template.md",
            "# Release x.y.z\n");
        await WriteFileAsync(
            "packages/package-index.yml",
            """
            packages:
              - project: Core/ForgeTrust.AppSurface.Core.csproj
                classification: public
                publish_decision: publish
                release_notes_path: releases/unreleased.md
                order: 10
              - project: Web/ForgeTrust.AppSurface.Web.Tailwind.Runtime.linux-x64.csproj
                classification: support
                publish_decision: support_publish
                release_notes_path: releases/unreleased.md
                order: 20
            """);
    }

    private async Task WriteFileAsync(string relativePath, string content)
    {
        var path = RepositoryPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<string> ReadFileAsync(string relativePath)
    {
        return await File.ReadAllTextAsync(RepositoryPath(relativePath));
    }

    private string RepositoryPath(string relativePath)
    {
        return TestPathUtils.PathUnder(_repositoryRoot, relativePath);
    }

    private async Task<CliResult> RunAsync(string[] args, FakeCommandRunner runner)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunAsync(
            args,
            stdout,
            stderr,
            _repositoryRoot,
            commandRunner: runner);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private CommandInvocation CreateSlowCommandInvocation()
    {
        if (OperatingSystem.IsWindows())
        {
            return new CommandInvocation(
                "cmd.exe",
                ["/c", "ping -n 6 127.0.0.1 > nul"],
                _repositoryRoot,
                TimeSpan.FromMilliseconds(50));
        }

        return new CommandInvocation(
            "/bin/sh",
            ["-c", "sleep 5"],
            _repositoryRoot,
            TimeSpan.FromMilliseconds(50));
    }

    private static FakeCommandRunner CreateSuccessfulPublishRunner()
    {
        var runner = new FakeCommandRunner();
        var releaseManifest = CreateReleaseManifestJson();
        runner.Add("git cat-file -t refs/tags/v0.1.0-preview.1", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add("git merge-base --is-ancestor abc123 origin/main", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/1\n", ""));
        runner.Add("gh release view v0.1.0-preview.1 --json url", new CommandResult(1, "", "not found"));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md", new CommandResult(0, TaggedReleaseNoteContent, ""));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md.yml", new CommandResult(0, TaggedReleaseSidecarContent, ""));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.release.json", new CommandResult(0, releaseManifest, ""));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json", new CommandResult(0, CreateReleaseEvidenceJson(releaseManifest), ""));
        return runner;
    }

    private static string CreateReleaseManifestJson()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var manifest = new ReleaseManifest(
            "appsurface-release-manifest-v1",
            version.ToString(),
            version.TagName,
            "2026-05-25",
            "abc123",
            "prerelease",
            [
                "releases/v0.1.0-preview.1.md",
                "releases/v0.1.0-preview.1.md.yml",
                "releases/v0.1.0-preview.1.release.json",
                "releases/v0.1.0-preview.1.evidence.json"
            ],
            ["Core/ForgeTrust.AppSurface.Core.csproj"],
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")],
            [],
            []);
        return JsonSerializer.Serialize(manifest, ReleaseJson.Options) + Environment.NewLine;
    }

    private static string CreateReleaseEvidenceJson(string releaseManifestJson)
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var workspace = new ReleaseWorkspace(Path.Join(Path.GetTempPath(), "ReleaseToolEvidenceFixtures"));
        var evidence = ReleaseEvidence.BuildDraft(
            workspace,
            version,
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifestJson,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")]);
        return ReleaseEvidence.Serialize(evidence);
    }

    private static string CreateReleaseEvidenceJsonWithNull(string releaseManifestJson, params string[] path)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(CreateReleaseEvidenceJson(releaseManifestJson))!.AsObject();
        System.Text.Json.Nodes.JsonNode current = root;
        for (var index = 0; index < path.Length - 1; index++)
        {
            current = int.TryParse(path[index], out var arrayIndex)
                ? current.AsArray()[arrayIndex]!
                : current[path[index]]!;
        }

        current.AsObject()[path[^1]] = null;
        return root.ToJsonString(ReleaseJson.Options) + Environment.NewLine;
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly Dictionary<string, CommandResult> _results = new(StringComparer.Ordinal);

        internal static FakeCommandRunner WithSourceCommit(string sourceCommit)
        {
            var runner = new FakeCommandRunner();
            runner.Add("git rev-parse HEAD", new CommandResult(0, sourceCommit + "\n", ""));
            return runner;
        }

        internal void Add(string command, CommandResult result)
        {
            _results[command] = result;
        }

        public Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            var command = invocation.Executable + " " + string.Join(' ', invocation.Arguments);
            return Task.FromResult(_results.TryGetValue(command, out var result)
                ? result
                : new CommandResult(1, "", "command not configured"));
        }
    }
}
