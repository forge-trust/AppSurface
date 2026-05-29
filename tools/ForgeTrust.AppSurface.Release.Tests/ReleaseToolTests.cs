using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Release;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseToolTests : IDisposable
{
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
        Assert.Contains("## Dry-run plan", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("releases/v0.1.0-preview.1.release.json", result.Stdout, StringComparison.Ordinal);
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
        Assert.Contains("# Release readiness report", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
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

        var releaseNote = await ReadFileAsync("releases/v0.1.0-preview.1.md");
        Assert.Contains("# Release 0.1.0-preview.1", releaseNote, StringComparison.Ordinal);

        var sidecar = await ReadFileAsync("releases/v0.1.0-preview.1.md.yml");
        Assert.Contains("title: Release 0.1.0-preview.1", sidecar, StringComparison.Ordinal);

        var manifestJson = await ReadFileAsync("releases/v0.1.0-preview.1.release.json");
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal("0.1.0-preview.1", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("prerelease", manifest.RootElement.GetProperty("releaseClassification").GetString());
        Assert.Equal("abc123", manifest.RootElement.GetProperty("sourceCommit").GetString());

        var packageIndex = await ReadFileAsync("packages/package-index.yml");
        Assert.Contains("release_notes_path: releases/v0.1.0-preview.1.md", packageIndex, StringComparison.Ordinal);
        Assert.Contains("classification: support", packageIndex, StringComparison.Ordinal);
        Assert.Contains("release_notes_path: releases/unreleased.md", packageIndex, StringComparison.Ordinal);

        var changelog = await ReadFileAsync("CHANGELOG.md");
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", changelog, StringComparison.Ordinal);
        Assert.Contains("- Release manifest: `releases/v0.1.0-preview.1.release.json`", changelog, StringComparison.Ordinal);
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
        await WriteFileAsync("releases/v0.1.0-preview.1.md", "# Existing\n");
        await WriteFileAsync("releases/v0.1.0-preview.1.md.yml", "title: Existing\n");
        await WriteFileAsync("releases/v0.1.0-preview.1.release.json", "{}\n");

        var result = await RunAsync(
            ["check", "--version", "0.1.0-preview.1", "--fail-on-warnings", "--allow-existing-targets"],
            FakeCommandRunner.WithSourceCommit("abc123"));

        Assert.Equal(0, result.ExitCode);
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
        Assert.DoesNotContain("release-target-exists", result.Stdout, StringComparison.Ordinal);
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
    public async Task PublishCanReturnJsonWithoutGithubOutputFile()
    {
        await SeedRepositoryAsync();

        var result = await RunAsync(
            ["publish", "--version", "0.1.0-preview.1", "--tag", "v0.1.0-preview.1", "--dry-run"],
            CreateSuccessfulPublishRunner());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"releaseClassification\": \"prerelease\"", result.Stdout, StringComparison.Ordinal);
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
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", appendedChangelog, StringComparison.Ordinal);

        var terminalUnreleasedChangelog = ChangelogEditor.RollForward(
            "# Changelog\n\n## Unreleased\n\n- Current work.\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
        Assert.Contains("- Current work.", terminalUnreleasedChangelog, StringComparison.Ordinal);
        Assert.Contains("## 0.1.0-preview.1 - 2026-05-25", terminalUnreleasedChangelog, StringComparison.Ordinal);

        var multiReleaseChangelog = ChangelogEditor.RollForward(
            "# Changelog\n\n## Unreleased\n\n## 0.0.1 - 2026-01-01\n\n- Previous work.\n",
            version,
            new DateOnly(2026, 5, 25),
            "releases/v0.1.0-preview.1.md");
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
        runner.Add("git show /:releases/v0.1.0-preview.1.md", new CommandResult(0, "# Release 0.1.0-preview.1\n", ""));
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
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new ArgumentException("Test repository paths must be relative.", nameof(relativePath));
        }

        return Path.Join(_repositoryRoot, normalizedRelativePath);
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
        runner.Add("git cat-file -t refs/tags/v0.1.0-preview.1", new CommandResult(0, "tag\n", ""));
        runner.Add("git rev-parse refs/tags/v0.1.0-preview.1^{commit}", new CommandResult(0, "abc123\n", ""));
        runner.Add("git merge-base --is-ancestor abc123 origin/main", new CommandResult(0, "", ""));
        runner.Add("gh run list --workflow nuget-prerelease-publish.yml --commit abc123 --json conclusion,headBranch,status,url --jq [.[] | select(.headBranch == \"v0.1.0-preview.1\" and .status == \"completed\" and .conclusion == \"success\")][0].url // \"\"", new CommandResult(0, "https://github.com/example/actions/runs/1\n", ""));
        runner.Add("gh release view v0.1.0-preview.1 --json url", new CommandResult(1, "", "not found"));
        runner.Add("git show v0.1.0-preview.1:releases/v0.1.0-preview.1.md", new CommandResult(0, "# Release 0.1.0-preview.1\n", ""));
        return runner;
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
