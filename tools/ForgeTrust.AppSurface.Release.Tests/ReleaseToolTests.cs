using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

        var prepareWithDocs = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--docs-catalog", "dist/docs/versions.json"],
            new FakeCommandRunner());

        Assert.Equal(1, prepareWithDocs.ExitCode);
        Assert.Contains("--docs-catalog", prepareWithDocs.Stderr, StringComparison.Ordinal);
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
    public async Task CheckDoesNotWarnForStableReleaseWhenStableWorkflowExists()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--fail-on-warnings"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-stable-package-policy-missing", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-prerelease-label-unprotected", result.Stdout, StringComparison.Ordinal);
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
    public async Task CheckRejectsPreparedReleaseEvidenceWithMismatchedContentSourceCommit()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseNote = await ReadFileAsync("releases/v0.1.0-preview.1.md");
        var releaseSidecar = await ReadFileAsync("releases/v0.1.0-preview.1.md.yml");
        var releaseManifest = (await ReadFileAsync("releases/v0.1.0-preview.1.release.json")).Replace(
            "\"sourceCommit\": \"abc123\"",
            "\"sourceCommit\": \"other-content-source\"",
            StringComparison.Ordinal);
        await WriteFileAsync("releases/v0.1.0-preview.1.release.json", releaseManifest);
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(_repositoryRoot),
            version,
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            releaseNote,
            releaseSidecar,
            releaseManifest,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")]);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", ReleaseEvidence.Serialize(evidence));

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("release-evidence-content-source-commit-mismatch", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("release-evidence-subject-digest-mismatch", result.Stdout, StringComparison.Ordinal);
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
    public async Task PreparedEvidenceValidationHandlesMissingReleasesDirectory()
    {
        var result = await ReleaseEvidence.ValidatePreparedAsync(
            new ReleaseWorkspace(_repositoryRoot),
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "abc123",
            CancellationToken.None);

        Assert.Null(result.Summary);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-missing");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-duplicate");
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
    public async Task CheckRejectsNullPreparedReleaseEvidence()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.1.evidence.json", "null\n");

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
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/v0.1.0-preview.2.evidence.json", "{}\n");
        await WriteFileAsync("releases/v0.1.00.evidence.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-evidence-duplicate", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckIgnoresMalformedNeighboringEvidenceFileNames()
    {
        await SeedRepositoryAsync();
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0-preview.1", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);
        await WriteFileAsync("releases/vnot-semver.evidence.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("release-prep-commit"));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("release-evidence-duplicate", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseEvidenceBuildDraftAllowsEmptyPackageUpdates()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
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
        Assert.NotEqual("2026-05-25T00:00:00Z", evidence.GeneratedAtUtc);

        var generatedAt = DateTimeOffset.Parse(evidence.GeneratedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.InRange(generatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void ReleaseEvidenceBuildDraftOrdersPackageUpdatesByProject()
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
            [
                new PackagePathUpdate("Web/ForgeTrust.AppSurface.Web.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md"),
                new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")
            ]);

        Assert.Collection(
            evidence.PackageReleaseNotePaths,
            package => Assert.Equal("Core/ForgeTrust.AppSurface.Core.csproj", package.Project),
            package => Assert.Equal("Web/ForgeTrust.AppSurface.Web.csproj", package.Project));
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
    public void ReleaseEvidenceValidationRejectsDocsCatalogDigestMismatch()
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
                CatalogEntry: new ReleaseEvidenceCatalogEntry("releases/0.1.0-preview.1", new string('c', 64))),
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
    public void ReleaseEvidenceValidationRejectsNullTagEvidence()
    {
        var result = ReleaseEvidence.ValidateTag(
            SemVer.Parse("0.1.0-preview.1"),
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            CreateReleaseManifestJson(),
            "null\n");

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
    public void ReleaseEvidenceValidationRejectsContentSourceCommitMismatch()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson("other-content-source");
        var evidence = ReleaseEvidence.BuildDraft(
            new ReleaseWorkspace(Path.Join(Path.GetTempPath(), "ReleaseToolEvidenceFixtures")),
            version,
            "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", "releases/v0.1.0-preview.1.md")]);

        var result = ReleaseEvidence.ValidateTag(
            version,
            "prerelease",
            "v0.1.0-preview.1",
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifest,
            ReleaseEvidence.Serialize(evidence));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-content-source-commit-mismatch");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-archive-incomplete");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-subject-digest-mismatch");
    }

    [Fact]
    public void ReleaseEvidenceValidationRejectsDocsArchiveMissingExactTreePath()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var releaseManifest = CreateReleaseManifestJson();
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(CreateReleaseEvidenceJson(releaseManifest), ReleaseJson.Options)!;
        var mismatched = bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                ExactTreePath: null,
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-archive-incomplete");
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

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-exacttreepath-unsafe");
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
                "availableVerified",
                "dist/docs/versions.json",
                "dist/docs",
                "dist/docs/releases/0.1.0-preview.1",
                3,
                "tag-commit",
                "not required"),
            [],
            []);

        var report = ReleaseReportRenderer.RenderCheck(result);

        Assert.Contains("- Docs archive manifest SHA-256: `" + new string('b', 64) + "`", report, StringComparison.Ordinal);
        Assert.Contains("- Catalog exact tree path: `releases/0.1.0-preview.1`", report, StringComparison.Ordinal);
        Assert.Contains("- Docs archive verification: `availableVerified`", report, StringComparison.Ordinal);
        Assert.Contains("- Docs catalog input: `dist/docs/versions.json`", report, StringComparison.Ordinal);
        Assert.Contains("- Docs verified file count: `3`", report, StringComparison.Ordinal);
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
    public async Task PublishRejectsStableReleaseWithoutStablePackageProof()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner();
        runner.Add("gh run list --workflow nuget-stable-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-stable-packages-not-published", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishStableReleaseValidatesStablePackageWorkflow()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutDocsCatalogInput()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-input-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutDocsArchiveEvidence()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner();

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-archive-required", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWithoutDocsArchiveCatalogEntry()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs, includeDocsCatalogEntry: false);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-archive-incomplete", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogEntryDoesNotMatchEvidence()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);
        await File.WriteAllTextAsync(
            docs.CatalogPath,
            JsonSerializer.Serialize(
                new
                {
                    versions = new[]
                    {
                        new
                        {
                            version = "0.1.0",
                            exactTreePath = "releases/other",
                            releaseManifestSha256 = docs.ReleaseManifestSha256,
                            visibility = "Public"
                        }
                    }
                },
                ReleaseJson.Options) + Environment.NewLine);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-catalog-entry-mismatch", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenArchiveBytesChangeAfterCatalogPin()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);
        await File.WriteAllTextAsync(
            Path.Join(docs.TrustedReleaseRootPath, "releases", "0.1.0", "index.html"),
            "<!doctype html><title>changed</title>");

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenRouteManifestIsMalformed()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0", routeManifestJson: "{");
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(".appsurface-docs-route-manifest.json", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenRouteManifestRoutesAreInvalid()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "../outside",
                  "recoveryAliases": [],
                  "declaredAliases": []
                }
              ]
            }
            """);
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsafe canonical route", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenManifestOmitsRuntimeServeableAsset()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllBytesAsync(
            TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath, "favicon.ico"),
            [0, 1, 2, 3]);
        var runner = CreateSuccessfulStablePublishRunner(docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("favicon.ico", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogContainsDuplicateSelectedVersion()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            },
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("appears more than once", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogVersionIsHidden()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "hidden"
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("hidden", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid-visibility")]
    [InlineData("missing-exact-tree")]
    [InlineData("non-string-exact-tree")]
    [InlineData("invalid-digest")]
    public async Task PublishRejectsStableReleaseWhenCatalogEntryShapeIsInvalid(string shape)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        object entry = shape switch
        {
            "invalid-visibility" => new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = 2
            },
            "missing-exact-tree" => new
            {
                version = "0.1.0",
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            },
            "non-string-exact-tree" => new
            {
                version = "0.1.0",
                exactTreePath = 5,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility = "Public"
            },
            _ => new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = "not-a-sha256",
                visibility = "Public"
            }
        };
        await WriteDocsCatalogAsync(docs, entry);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("internal")]
    [InlineData(null)]
    public async Task PublishRejectsStableReleaseWhenCatalogStringFieldsAreInvalid(string? visibility)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = 5,
                visibility
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogVisibilityIsMissing()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-catalog-version-unavailable", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("public visibility", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task PublishHandlesNumericCatalogVisibility(int visibility, int expectedExitCode)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            new
            {
                version = "0.1.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256,
                visibility
            });

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(expectedExitCode, result.ExitCode);
        if (expectedExitCode == 0)
        {
            Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("hidden", result.Stderr, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/tmp/appsurface-docs")]
    [InlineData("releases/.hidden")]
    public async Task PublishRejectsStableReleaseWhenCatalogExactTreePathIsUnsafe(string exactTreePath)
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = exactTreePath };
        await WriteDocsCatalogAsync(docs);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-docs-exacttreepath-unsafe", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("exactTreePath is unsafe", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenCatalogExactTreeIsMissing()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = "releases/missing" };
        await WriteDocsCatalogAsync(docs);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsUnreadableCatalogShape()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(docs.CatalogPath, "{}\n");

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("versions", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsMalformedCatalogPayload()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(docs.CatalogPath, "{");

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("could not be read", result.Diagnostics[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsMissingCheckFallbackCatalog()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.Delete(docs.CatalogPath);

        var result = await ValidateStableDocsArchiveGateAsync(
            docs,
            command: "check",
            docsCatalogPath: null);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-input-missing");
        Assert.Contains("local review fallback", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsCatalogWithoutSelectedVersion()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await WriteDocsCatalogAsync(
            docs,
            "ignored",
            new
            {
                version = "0.2.0",
                exactTreePath = docs.ExactTreePath,
                releaseManifestSha256 = docs.ReleaseManifestSha256
            });

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("not present", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsFileTrustedReleaseRoot()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var trustedRootFile = RepositoryPath("docs-root.txt");
        await File.WriteAllTextAsync(trustedRootFile, "not a directory");

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: trustedRootFile);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("not a directory", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsSymlinkTrustedReleaseRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var symlinkRoot = RepositoryPath("docs-root-link");
        Directory.CreateSymbolicLink(symlinkRoot, docs.TrustedReleaseRootPath);

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: symlinkRoot);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("symlink", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/appsurface-docs")]
    [InlineData("releases/.hidden")]
    public async Task StableDocsArchiveGateRejectsUnsafeCatalogExactTreePath(string exactTreePath)
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = exactTreePath };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-exacttreepath-unsafe");
        Assert.Contains("exactTreePath", result.Diagnostics[0].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsBlankCatalogExactTreePath()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = " " };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-catalog-version-unavailable");
        Assert.Contains("missing exactTreePath", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsInvalidCatalogExactTreePathCharacters()
    {
        await SeedRepositoryAsync();
        var docs = (await SeedDocsArchiveAsync("0.1.0")) with { ExactTreePath = "bad\u0000path" };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-evidence-docs-exacttreepath-unsafe");
        Assert.Contains("is invalid", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsSymlinkExactTreeSegment()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var releasesPath = Path.Join(docs.TrustedReleaseRootPath, "releases");
        var realReleasesParent = RepositoryPath("real-docs");
        Directory.CreateDirectory(realReleasesParent);
        var realReleasesPath = Path.Join(realReleasesParent, "releases");
        Directory.Move(releasesPath, realReleasesPath);
        Directory.CreateSymbolicLink(releasesPath, realReleasesPath);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("symlink", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsCandidateOutsideTrustedRoot()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        var outsideRoot = RepositoryPath("dist-other/docs");
        Directory.CreateDirectory(trustedRoot);
        Directory.CreateDirectory(outsideRoot);

        var result = ReleaseDocsArchiveGate.TryValidateNoReparseSegments(
            trustedRoot,
            outsideRoot,
            out var detail);

        Assert.False(result);
        Assert.Contains("outside trusted root", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsEmptyExactTreePath()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        Directory.CreateDirectory(trustedRoot);

        var result = ReleaseDocsArchiveGate.TryResolveExactTreePath(
            trustedRoot,
            " ",
            out var physicalExactTreePath,
            out var issue);

        Assert.False(result);
        Assert.Null(physicalExactTreePath);
        Assert.Contains("empty", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void StableDocsArchiveGateRejectsExactTreePathEscapingUnnormalizedTrustedRoot()
    {
        var trustedRoot = RepositoryPath("dist/docs");
        Directory.CreateDirectory(trustedRoot);

        var result = ReleaseDocsArchiveGate.TryResolveExactTreePath(
            trustedRoot + Path.DirectorySeparatorChar,
            "releases/0.1.0",
            out var physicalExactTreePath,
            out var issue);

        Assert.False(result);
        Assert.NotNull(physicalExactTreePath);
        Assert.Contains("escapes the trusted release root", issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateDefaultsTrustedRootToCatalogDirectory()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: null);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsExactTreeAtTrustedRoot()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        foreach (var file in Directory.EnumerateFiles(TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath)))
        {
            var target = TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        Directory.Delete(TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, "releases"), recursive: true);
        File.Delete(docs.CatalogPath);
        docs = docs with
        {
            CatalogPath = RepositoryPath("versions-root.json"),
            ExactTreePath = "."
        };
        await WriteDocsCatalogAsync(docs);

        var result = await ValidateStableDocsArchiveGateAsync(docs, trustedRootPath: docs.TrustedReleaseRootPath);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsRouteManifestWithRecoveryAliasesOnly()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": ["old-packages"]
                }
              ]
            }
            """);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
        var proof = result.Proof;
        Assert.Equal(ReleaseDocsArchiveGate.VerifiedState, proof.State);
        Assert.Equal(docs.CatalogPath, proof.CatalogPath);
        Assert.Equal(docs.TrustedReleaseRootPath, proof.TrustedReleaseRootPath);
        Assert.Equal(docs.ExactTreePath, proof.CatalogExactTreePath);
        Assert.Equal(docs.ReleaseManifestSha256, proof.CatalogReleaseManifestSha256);
        Assert.Equal(TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath), proof.PhysicalExactTreePath);
        Assert.Equal(docs.FileCount, proof.VerifiedFileCount);
    }

    [Fact]
    public async Task StableDocsArchiveGateAcceptsListedServeableAssetExtensions()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var files = new List<IReadOnlyDictionary<string, object?>>();
        var indexBytes = await ReadDocsArchiveFileAsync(docs, "index.html");
        files.Add(CreateDocsManifestFile("index.html", indexBytes.Length, Sha256(indexBytes)));
        var routeManifestBytes = await ReadDocsArchiveFileAsync(docs, ".appsurface-docs-route-manifest.json");
        files.Add(CreateDocsManifestFile(
            ".appsurface-docs-route-manifest.json",
            routeManifestBytes.Length,
            Sha256(routeManifestBytes)));

        foreach (var relativePath in new[]
                 {
                     "app.js",
                     "site.css",
                     "icon.svg",
                     "image.png",
                     "photo.jpg",
                     "photo-alt.jpeg",
                     "spinner.gif",
                     "hero.webp",
                     "favicon.ico",
                     "font.woff",
                     "font.woff2",
                     "font.ttf",
                     "font.eot"
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(relativePath);
            await File.WriteAllBytesAsync(DocsArchivePath(docs, relativePath), bytes);
            files.Add(CreateDocsManifestFile(relativePath, bytes.Length, Sha256(bytes)));
        }

        docs = await RewriteDocsReleaseManifestAsync(docs, "appsurface-docs-release-manifest-v1", files);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Proof);
        Assert.Equal(files.Count, result.Proof.VerifiedFileCount);

        static string Sha256(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestSchemaIsInvalid()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(docs, "wrong-schema", []);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("schema", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestIsMissing()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.Delete(DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"));

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("is missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestDigestMismatchesCatalogPin()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        await File.WriteAllTextAsync(
            DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"),
            "{}");

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("digest does not match", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestIsSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var manifestPath = DocsArchivePath(docs, ".appsurface-docs-release-manifest.json");
        var realManifestPath = DocsArchivePath(docs, "real-release-manifest.json");
        File.Move(manifestPath, realManifestPath);
        File.CreateSymbolicLink(manifestPath, realManifestPath);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.SetUnixFileMode(DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"), UnixFileMode.None);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestPayloadIsUnreadable()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, "{");

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("payload is unreadable", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestPayloadIsNull()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, "null");

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("schema", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestOmitsFiles()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var manifestJson = JsonSerializer.Serialize(
            new
            {
                schema = "appsurface-docs-release-manifest-v1"
            },
            ReleaseJson.Options) + Environment.NewLine;
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, manifestJson);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("not listed", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestEntryIsInvalid()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                new Dictionary<string, object?>
                {
                    ["path"] = "index.html",
                    ["length"] = 1,
                    ["contentType"] = "text/html",
                    ["hashAlgorithm"] = "md5",
                    ["sha256"] = new string('a', 64)
                }
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid file entry", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-path")]
    [InlineData("negative-length")]
    [InlineData("blank-digest")]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestEntryShapeIsInvalid(string shape)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var entry = CreateDocsManifestFile("index.html", 1, new string('a', 64)).ToDictionary();
        switch (shape)
        {
            case "missing-path":
                entry["path"] = null;
                break;
            case "negative-length":
                entry["length"] = -1;
                break;
            case "blank-digest":
                entry["sha256"] = " ";
                break;
        }

        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [entry]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid file entry", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableDocsArchiveGateRejectsBlankReleaseManifestContentType()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var indexBytes = await ReadDocsArchiveFileAsync(docs, "index.html");
        var entry = CreateDocsManifestFile(
            "index.html",
            indexBytes.Length,
            Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant()).ToDictionary();
        entry["contentType"] = " ";
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [entry]);

        var result = await ValidateStableDocsArchiveGateAsync(docs);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-docs-archive-verification-failed");
        Assert.Contains("invalid contentType", result.Diagnostics[0].Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestContainsNullEntry()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var manifestJson = """
            {
              "schema": "appsurface-docs-release-manifest-v1",
              "files": [null]
            }
            """;
        docs = await RewriteDocsReleaseManifestPayloadAsync(docs, manifestJson);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid file entry", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestContainsUnsafeFilePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("assets/.secret.json", 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsafe file path", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/assets/app.css")]
    [InlineData("assets\\app.css")]
    [InlineData("assets:app.css")]
    [InlineData("assets/app.css?cache=1")]
    [InlineData("assets//app.css")]
    [InlineData("assets/../app.css")]
    [InlineData("assets/./app.css")]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestPathShapeIsUnsafe(string path)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile(path, 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("unsafe file path", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestListsMissingFile()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("missing.css", 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("lists missing file", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestContainsDuplicatePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var index = await ReadDocsArchiveFileAsync(docs, "index.html");
        var indexSha256 = Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant();
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("index.html", index.Length, indexSha256),
                CreateDocsManifestFile("index.html", index.Length, indexSha256)
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("duplicate path", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestLengthMismatches()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var index = await ReadDocsArchiveFileAsync(docs, "index.html");
        var indexSha256 = Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant();
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("index.html", index.Length + 1, indexSha256)
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("different byte length", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestListsSymlinkFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.CreateSymbolicLink(DocsArchivePath(docs, "linked.css"), DocsArchivePath(docs, "index.html"));
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("linked.css", 0, new string('a', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestFileUsesSymlinkAncestor()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var realAssetsPath = RepositoryPath("real-doc-assets");
        Directory.CreateDirectory(realAssetsPath);
        var assetBytes = Encoding.UTF8.GetBytes("body{}");
        await File.WriteAllBytesAsync(Path.Join(realAssetsPath, "app.css"), assetBytes);
        Directory.CreateSymbolicLink(DocsArchivePath(docs, "assets"), realAssetsPath);
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("assets/app.css", assetBytes.Length, Convert.ToHexString(SHA256.HashData(assetBytes)).ToLowerInvariant())
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("symlink", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestFileDigestMismatches()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var index = await ReadDocsArchiveFileAsync(docs, "index.html");
        docs = await RewriteDocsReleaseManifestAsync(
            docs,
            "appsurface-docs-release-manifest-v1",
            [
                CreateDocsManifestFile("index.html", index.Length, new string('b', 64))
            ]);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("different SHA-256 digest", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenReleaseManifestListedFileCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        File.SetUnixFileMode(DocsArchivePath(docs, "index.html"), UnixFileMode.None);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsStableReleaseWhenRouteManifestAliasCollidesWithCanonicalRoute()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages",
                  "recoveryAliases": [],
                  "declaredAliases": []
                },
                {
                  "sourcePath": "cli.html",
                  "canonicalRoutePath": "cli",
                  "recoveryAliases": ["packages"],
                  "declaredAliases": []
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("collides with a canonical route", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """
        {
          "schema": "wrong",
          "entries": []
        }
        """,
        "is malformed")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "recoveryAliases": [],
              "declaredAliases": []
            }
          ]
        }
        """,
        "require canonicalRoutePath")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": [],
              "declaredAliases": []
            },
            {
              "sourcePath": "packages.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": [],
              "declaredAliases": []
            }
          ]
        }
        """,
        "duplicate canonical route")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "matches its canonical route")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["../outside"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "alias '../outside' is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages?preview=true",
              "recoveryAliases": [],
              "declaredAliases": []
            }
          ]
        }
        """,
        "unsafe canonical route")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old\\\\packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old//packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old/ /packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is unsafe")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["old-packages", "old-packages"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "is duplicated")]
    [InlineData(
        """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": [
            {
              "sourcePath": "index.html",
              "canonicalRoutePath": "packages",
              "recoveryAliases": ["install"],
              "declaredAliases": []
            },
            {
              "sourcePath": "cli.html",
              "canonicalRoutePath": "cli",
              "recoveryAliases": ["install"],
              "declaredAliases": []
            }
          ]
        }
        """,
        "points at multiple canonical routes")]
    public async Task PublishRejectsStableReleaseWhenRouteManifestShapeIsInvalid(
        string routeManifestJson,
        string expectedMessage)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0", routeManifestJson);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-docs-archive-verification-failed", result.Stderr, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestUsesEmptyRoutePath()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": " ",
                  "recoveryAliases": [],
                  "declaredAliases": []
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestUsesDeclaredAliasAndFragment()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "index.html",
                  "canonicalRoutePath": "packages#intro",
                  "declaredAliases": ["old-packages#intro"]
                }
              ]
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishAcceptsStableReleaseWhenRouteManifestOmitsEntries()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync(
            "0.1.0",
            routeManifestJson: """
            {
              "schema": "appsurface-docs-route-manifest-v1"
            }
            """);

        var result = await RunStablePublishWithDocsAsync(docs);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"stable\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStablePreparedReleaseVerifiesFallbackDocsCatalog()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(evidenceJson, ReleaseJson.Options)!;
        await WriteFileAsync("releases/v0.1.0.evidence.json", ReleaseEvidence.Serialize(PinDocsArchive(bundle, docs)));

        var result = await RunAsync(
            ["check", "--version", "0.1.0", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("- Docs archive verification: `availableVerified`", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("- Docs verified file count: `2`", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckStablePreparedReleaseVerifiesConfiguredDocsCatalog()
    {
        await SeedRepositoryAsync();
        await WriteFileAsync(".github/workflows/nuget-stable-publish.yml", "name: NuGet Stable Publish\n");
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var prepare = await RunAsync(
            ["prepare", "--version", "0.1.0", "--date", "2026-05-25"],
            FakeCommandRunner.WithSourceCommit("abc123"));
        Assert.Equal(0, prepare.ExitCode);

        var evidenceJson = await ReadFileAsync("releases/v0.1.0.evidence.json");
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(evidenceJson, ReleaseJson.Options)!;
        await WriteFileAsync("releases/v0.1.0.evidence.json", ReleaseEvidence.Serialize(PinDocsArchive(bundle, docs)));

        var result = await RunAsync(
            [
                "check",
                "--version",
                "0.1.0",
                "--allow-existing-targets",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("- Docs archive verification: `availableVerified`", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishUsesConfiguredBaseRefForReachability()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(baseRef: "release/0.1.0", docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--base-ref",
                "release/0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("origin/release/0.1.0")]
    [InlineData("refs/heads/release/0.1.0")]
    [InlineData("refs/remotes/origin/release/0.1.0")]
    public async Task PublishNormalizesBranchishBaseRefForReachability(string baseRef)
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var runner = CreateSuccessfulStablePublishRunner(baseRef: "release/0.1.0", docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--base-ref",
                baseRef,
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task PublishAllowsObjectIdLengthBranchNameWhenItIsNotAFullObjectId()
    {
        await SeedRepositoryAsync();
        var docs = await SeedDocsArchiveAsync("0.1.0");
        var baseRef = "0123456789abcdef0123456789abcdef0123456g";
        var runner = CreateSuccessfulStablePublishRunner(baseRef: baseRef, docs: docs);

        var result = await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--base-ref",
                baseRef,
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            runner);

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("origin/")]
    [InlineData("origin/-release/0.1.0")]
    [InlineData("refs/heads/")]
    [InlineData("refs/heads/-release/0.1.0")]
    [InlineData("refs/remotes/origin/")]
    [InlineData("refs/tags/v0.1.0")]
    [InlineData("refs/remotes/upstream/release/0.1.0")]
    [InlineData("/release/0.1.0")]
    [InlineData(".release/0.1.0")]
    [InlineData("release..0.1.0")]
    [InlineData("release//0.1.0")]
    [InlineData("release/.hidden")]
    [InlineData("release.lock")]
    [InlineData("topic@{1}")]
    [InlineData("release 0.1.0")]
    [InlineData("release\\0.1.0")]
    [InlineData("release~0.1.0")]
    [InlineData("release^0.1.0")]
    [InlineData("release:0.1.0")]
    [InlineData("qa?hotfix")]
    [InlineData("qa*hotfix")]
    [InlineData("release[hotfix]")]
    [InlineData("@")]
    [InlineData("release.")]
    [InlineData("release/")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task PublishRejectsUnsupportedBaseRefShapes(string baseRef)
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulStablePublishRunner(baseRef: "release/0.1.0");

        var result = await RunAsync(
            ["publish", "--version", "0.1.0", "--tag", "v0.1.0", "--base-ref", baseRef, "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-base-ref-invalid", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git cat-file -t refs/tags/v0.1.0-preview.1", "commit", "release-tag-lightweight")]
    [InlineData("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", "stdout failure", "release-tag-commit-missing")]
    [InlineData("git merge-base --is-ancestor abc123 origin/main", "", "release-tag-unreachable-from-base-ref")]
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
    public async Task PublishReportsGitBlobCommandStdoutWhenStderrIsEmpty()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json", new CommandResult(1, "blob stdout failure", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("blob stdout failure", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishRejectsNullTagEvidenceWithStructuredDiagnostic()
    {
        await SeedRepositoryAsync();
        var runner = CreateSuccessfulPublishRunner();
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.evidence.json", new CommandResult(0, "null\n", ""));

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            runner);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Code: release-evidence-schema-invalid", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("NullReferenceException", result.Stderr, StringComparison.Ordinal);
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

    private static FakeCommandRunner CreateSuccessfulStablePublishRunner(
        string baseRef = "main",
        DocsArchiveFixture? docs = null,
        bool includeDocsCatalogEntry = true)
    {
        var runner = new FakeCommandRunner();
        var releaseManifest = CreateReleaseManifestJson(versionText: "0.1.0");
        var releaseEvidence = CreateReleaseEvidenceJson(
            releaseManifest,
            "0.1.0",
            docs?.ExactTreePath,
            docs?.ReleaseManifestSha256,
            docs?.FileCount,
            includeDocsCatalogEntry);
        runner.Add("git cat-file -t refs/tags/v0.1.0", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v0.1.0^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add($"git merge-base --is-ancestor abc123 origin/{baseRef}", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-stable-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/2\n", ""));
        runner.Add("gh release view v0.1.0 --json url", new CommandResult(1, "", "not found"));
        runner.Add("git show v0.1.0:releases/v0.1.0.md", new CommandResult(0, TaggedReleaseNoteContent, ""));
        runner.Add("git show v0.1.0:releases/v0.1.0.md.yml", new CommandResult(0, TaggedReleaseSidecarContent, ""));
        runner.Add("git show v0.1.0:releases/v0.1.0.release.json", new CommandResult(0, releaseManifest, ""));
        runner.Add("git show v0.1.0:releases/v0.1.0.evidence.json", new CommandResult(0, releaseEvidence, ""));
        return runner;
    }

    private async Task<CliResult> RunStablePublishWithDocsAsync(DocsArchiveFixture docs)
    {
        return await RunAsync(
            [
                "publish",
                "--version",
                "0.1.0",
                "--tag",
                "v0.1.0",
                "--dry-run",
                "--docs-catalog",
                docs.CatalogPath,
                "--docs-trusted-release-root",
                docs.TrustedReleaseRootPath
            ],
            CreateSuccessfulStablePublishRunner(docs: docs));
    }

    private async Task<ReleaseDocsArchiveGateResult> ValidateStableDocsArchiveGateAsync(
        DocsArchiveFixture docs,
        string? trustedRootPath = "__fixture__",
        string command = "publish",
        string? docsCatalogPath = "__fixture__")
    {
        var options = new ReleaseOptions(
            command,
            _repositoryRoot,
            SemVer.Parse("0.1.0"),
            "v0.1.0",
            Date: null,
            DryRun: true,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false,
            DocsCatalogPath: string.Equals(docsCatalogPath, "__fixture__", StringComparison.Ordinal)
                ? docs.CatalogPath
                : docsCatalogPath,
            DocsTrustedReleaseRootPath: string.Equals(trustedRootPath, "__fixture__", StringComparison.Ordinal)
                ? docs.TrustedReleaseRootPath
                : trustedRootPath);
        var bundle = JsonSerializer.Deserialize<ReleaseEvidenceBundle>(
            CreateReleaseEvidenceJson(
                CreateReleaseManifestJson(versionText: "0.1.0"),
                "0.1.0",
                docs.ExactTreePath,
                docs.ReleaseManifestSha256,
                docs.FileCount),
            ReleaseJson.Options)!;

        return await ReleaseDocsArchiveGate.ValidateStableAsync(
            new ReleaseWorkspace(_repositoryRoot),
            options,
            bundle,
            CancellationToken.None);
    }

    private static string CreateReleaseManifestJson(string? sourceCommit = "abc123", string versionText = "0.1.0-preview.1")
    {
        var version = SemVer.Parse(versionText);
        var releasePath = $"releases/v{version}.md";
        var manifest = new ReleaseManifest(
            "appsurface-release-manifest-v1",
            version.ToString(),
            version.TagName,
            "2026-05-25",
            sourceCommit,
            version.IsStable ? "stable" : "prerelease",
            [
                releasePath,
                $"releases/v{version}.md.yml",
                $"releases/v{version}.release.json",
                $"releases/v{version}.evidence.json"
            ],
            ["Core/ForgeTrust.AppSurface.Core.csproj"],
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", releasePath)],
            [],
            []);
        return JsonSerializer.Serialize(manifest, ReleaseJson.Options) + Environment.NewLine;
    }

    private static string CreateReleaseEvidenceJson(
        string releaseManifestJson,
        string versionText = "0.1.0-preview.1",
        string? docsExactTreePath = null,
        string? docsReleaseManifestSha256 = null,
        int? docsFileCount = null,
        bool includeDocsCatalogEntry = true)
    {
        var version = SemVer.Parse(versionText);
        var releasePath = $"releases/v{version}.md";
        var workspace = new ReleaseWorkspace(Path.Join(Path.GetTempPath(), "ReleaseToolEvidenceFixtures"));
        var evidence = ReleaseEvidence.BuildDraft(
            workspace,
            version,
            version.IsStable ? "stable" : "prerelease",
            new DateOnly(2026, 5, 25),
            "abc123",
            TaggedReleaseNoteContent,
            TaggedReleaseSidecarContent,
            releaseManifestJson,
            [new PackagePathUpdate("Core/ForgeTrust.AppSurface.Core.csproj", "releases/unreleased.md", releasePath)]);
        if (!string.IsNullOrWhiteSpace(docsExactTreePath) && !string.IsNullOrWhiteSpace(docsReleaseManifestSha256))
        {
            evidence = RefreshSubject(evidence with
            {
                DocsArchive = new ReleaseEvidenceDocsArchive(
                    "catalogPinned",
                    docsExactTreePath,
                    docsReleaseManifestSha256,
                    "appsurface-docs-release-manifest-v1",
                    docsFileCount,
                    includeDocsCatalogEntry
                        ? new ReleaseEvidenceCatalogEntry(docsExactTreePath, docsReleaseManifestSha256)
                        : null)
            });
        }

        return ReleaseEvidence.Serialize(evidence);
    }

    private async Task<DocsArchiveFixture> SeedDocsArchiveAsync(
        string versionText,
        string routeManifestJson = """
        {
          "schema": "appsurface-docs-route-manifest-v1",
          "entries": []
        }
        """)
    {
        var exactTreePath = $"releases/{versionText}";
        var trustedReleaseRootPath = RepositoryPath("dist/docs");
        var exactTreePhysicalPath = Path.Join(trustedReleaseRootPath, "releases", versionText);
        Directory.CreateDirectory(exactTreePhysicalPath);

        var indexBytes = Encoding.UTF8.GetBytes("<!doctype html><title>AppSurface Docs</title>");
        var indexPath = Path.Join(exactTreePhysicalPath, "index.html");
        await File.WriteAllBytesAsync(indexPath, indexBytes);
        var indexSha256 = Convert.ToHexString(SHA256.HashData(indexBytes)).ToLowerInvariant();
        var routeManifestBytes = Encoding.UTF8.GetBytes(routeManifestJson);
        var routeManifestPath = Path.Join(exactTreePhysicalPath, ".appsurface-docs-route-manifest.json");
        await File.WriteAllBytesAsync(routeManifestPath, routeManifestBytes);
        var routeManifestSha256 = Convert.ToHexString(SHA256.HashData(routeManifestBytes)).ToLowerInvariant();
        var manifestJson = JsonSerializer.Serialize(
            new
            {
                schema = "appsurface-docs-release-manifest-v1",
                files = new[]
                {
                    new
                    {
                        path = "index.html",
                        length = indexBytes.Length,
                        contentType = "text/html",
                        hashAlgorithm = "sha256",
                        sha256 = indexSha256
                    },
                    new
                    {
                        path = ".appsurface-docs-route-manifest.json",
                        length = routeManifestBytes.Length,
                        contentType = "application/json",
                        hashAlgorithm = "sha256",
                        sha256 = routeManifestSha256
                    }
                }
            },
            ReleaseJson.Options) + Environment.NewLine;
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        await File.WriteAllBytesAsync(Path.Join(exactTreePhysicalPath, ".appsurface-docs-release-manifest.json"), manifestBytes);
        var releaseManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var catalogPath = Path.Join(trustedReleaseRootPath, "versions.json");
        var catalogJson = JsonSerializer.Serialize(
            new
            {
                versions = new[]
                {
                    new
                    {
                        version = versionText,
                        label = versionText,
                        exactTreePath,
                        releaseManifestSha256,
                        visibility = "Public"
                    }
                }
            },
            ReleaseJson.Options) + Environment.NewLine;
        await File.WriteAllTextAsync(catalogPath, catalogJson);
        return new DocsArchiveFixture(
            catalogPath,
            trustedReleaseRootPath,
            exactTreePath,
            releaseManifestSha256,
            FileCount: 2,
            versionText);
    }

    private async Task WriteDocsCatalogAsync(DocsArchiveFixture docs, params object[] entries)
    {
        var versions = entries.Length > 0
            ? entries
            :
            [
                new
                {
                    version = docs.VersionText,
                    label = docs.VersionText,
                    exactTreePath = docs.ExactTreePath,
                    releaseManifestSha256 = docs.ReleaseManifestSha256,
                    visibility = "Public"
                }
            ];
        var catalogJson = JsonSerializer.Serialize(new { versions }, ReleaseJson.Options) + Environment.NewLine;
        await File.WriteAllTextAsync(docs.CatalogPath, catalogJson);
    }

    private async Task<DocsArchiveFixture> RewriteDocsReleaseManifestAsync(
        DocsArchiveFixture docs,
        string schema,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> files)
    {
        var manifestJson = JsonSerializer.Serialize(
            new
            {
                schema,
                files
            },
            ReleaseJson.Options) + Environment.NewLine;
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        await File.WriteAllBytesAsync(
            DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"),
            manifestBytes);
        var updatedDocs = docs with
        {
            ReleaseManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            FileCount = files.Count
        };
        await WriteDocsCatalogAsync(updatedDocs);
        return updatedDocs;
    }

    private async Task<DocsArchiveFixture> RewriteDocsReleaseManifestPayloadAsync(DocsArchiveFixture docs, string payload)
    {
        var manifestBytes = Encoding.UTF8.GetBytes(payload);
        await File.WriteAllBytesAsync(
            DocsArchivePath(docs, ".appsurface-docs-release-manifest.json"),
            manifestBytes);
        var updatedDocs = docs with
        {
            ReleaseManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()
        };
        await WriteDocsCatalogAsync(updatedDocs);
        return updatedDocs;
    }

    private static IReadOnlyDictionary<string, object?> CreateDocsManifestFile(string path, long length, string sha256)
    {
        return new Dictionary<string, object?>
        {
            ["path"] = path,
            ["length"] = length,
            ["contentType"] = "text/plain",
            ["hashAlgorithm"] = "sha256",
            ["sha256"] = sha256
        };
    }

    private async Task<byte[]> ReadDocsArchiveFileAsync(DocsArchiveFixture docs, string relativePath)
    {
        return await File.ReadAllBytesAsync(DocsArchivePath(docs, relativePath));
    }

    private static string DocsArchivePath(DocsArchiveFixture docs, string relativePath)
    {
        return TestPathUtils.PathUnder(docs.TrustedReleaseRootPath, docs.ExactTreePath, relativePath);
    }

    private static ReleaseEvidenceBundle PinDocsArchive(ReleaseEvidenceBundle bundle, DocsArchiveFixture docs)
    {
        return RefreshSubject(bundle with
        {
            DocsArchive = new ReleaseEvidenceDocsArchive(
                "catalogPinned",
                docs.ExactTreePath,
                docs.ReleaseManifestSha256,
                "appsurface-docs-release-manifest-v1",
                docs.FileCount,
                new ReleaseEvidenceCatalogEntry(docs.ExactTreePath, docs.ReleaseManifestSha256))
        });
    }

    private static ReleaseEvidenceBundle RefreshSubject(ReleaseEvidenceBundle bundle)
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
        return bundle with
        {
            Subject = bundle.Subject with { Sha256 = subjectDigest }
        };
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

    private sealed record DocsArchiveFixture(
        string CatalogPath,
        string TrustedReleaseRootPath,
        string ExactTreePath,
        string ReleaseManifestSha256,
        int FileCount,
        string VersionText);

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
