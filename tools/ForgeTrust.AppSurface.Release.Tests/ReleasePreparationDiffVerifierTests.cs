using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

/// <summary>
/// Regression coverage for strict release-preparation diff and witness parsing.
/// </summary>
public sealed class ReleasePreparationDiffVerifierTests
{
    [Fact]
    public void NameStatusParserPreservesNulDelimitedPathsAndRenameDirection()
    {
        var parsed = ReleasePreparationDiffVerifier.TryParseNameStatus(
            "M\0packages/README.md\0R100\0old name.md\0new name.md\0",
            out var changes,
            out var issue);

        Assert.True(parsed, issue);
        Assert.Collection(
            changes,
            change =>
            {
                Assert.Equal("M", change.Status);
                Assert.Equal("packages/README.md", change.Path);
                Assert.Null(change.OriginalPath);
            },
            change =>
            {
                Assert.Equal("R100", change.Status);
                Assert.Equal("new name.md", change.Path);
                Assert.Equal("old name.md", change.OriginalPath);
            });
    }

    [Theory]
    [InlineData("M\0packages/README.md", "does not have the required path")]
    [InlineData("R100\0old.md\0", "does not have the required path")]
    [InlineData("\0", "unsupported status")]
    [InlineData("Q\0unexpected.md\0", "unsupported status")]
    public void NameStatusParserRejectsMalformedStreams(string output, string expectedIssue)
    {
        var parsed = ReleasePreparationDiffVerifier.TryParseNameStatus(output, out _, out var issue);

        Assert.False(parsed);
        Assert.Contains(expectedIssue, issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NameStatusParserRejectsAStreamWithTrailingNonNulData()
    {
        var parsed = ReleasePreparationDiffVerifier.TryParseNameStatus("M\0README.md\0trailing", out _, out var issue);

        Assert.False(parsed);
        Assert.Contains("not NUL terminated", issue, StringComparison.Ordinal);
    }

    [Fact]
    public void WitnessParserRequiresExactOrderedAndLowercaseContract()
    {
        var json = """
            {
              "schema": "forge-trust.appsurface.release-prep-witness/v1",
              "baseRef": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "baseTipCommit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "mergeBaseCommit": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "headCommit": "cccccccccccccccccccccccccccccccccccccccc",
              "verification": "verified",
              "changedInputs": [
                {
                  "kind": "package-index-manifest",
                  "path": "packages/package-index.yml",
                  "surfaces": ["packages/README.md"]
                }
              ],
              "surfaces": [
                {
                  "kind": "chooser",
                  "path": "packages/README.md",
                  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                }
              ]
            }
            """;

        var parsed = ReleasePreparationDiffVerifier.TryParseWitness(json, out var witness, out var issue);

        Assert.True(parsed, issue);
        Assert.NotNull(witness);
        Assert.Equal("packages/package-index.yml", Assert.Single(witness.ChangedInputs).Path);
    }

    [Fact]
    public void WitnessParserRejectsDuplicatePropertiesAndUppercaseHashes()
    {
        var duplicateProperty = """
            {"schema":"forge-trust.appsurface.release-prep-witness/v1","schema":"forge-trust.appsurface.release-prep-witness/v1","baseRef":"a","baseTipCommit":"a","mergeBaseCommit":"b","headCommit":"c","verification":"verified","changedInputs":[],"surfaces":[]}
            """;
        var uppercaseHash = """
            {"schema":"forge-trust.appsurface.release-prep-witness/v1","baseRef":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseTipCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mergeBaseCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","headCommit":"cccccccccccccccccccccccccccccccccccccccc","verification":"verified","changedInputs":[],"surfaces":[{"kind":"chooser","path":"packages/README.md","sha256":"ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFAB"}]}
            """;

        Assert.False(ReleasePreparationDiffVerifier.TryParseWitness(duplicateProperty, out _, out var duplicateIssue));
        Assert.Contains("missing, duplicate", duplicateIssue, StringComparison.Ordinal);
        Assert.False(ReleasePreparationDiffVerifier.TryParseWitness(uppercaseHash, out _, out var hashIssue));
        Assert.Contains("SHA-256", hashIssue, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffReportEscapesTableBreakingDiffContent()
    {
        var result = new ReleasePreparationDiffResult(
            "origin/main",
            "base",
            "merge",
            "head",
            [new ReleasePreparationChange("M", "docs/a|b`c.md")],
            [ReleaseDiagnostic.Error("test|code", "problem", "line1\nline2", "use `repair`", "docs")],
            []);

        var report = ReleasePreparationDiffReportRenderer.Render(result);

        Assert.Contains("docs/a\\|b\\`c.md", report, StringComparison.Ordinal);
        Assert.Contains("test\\|code", report, StringComparison.Ordinal);
        Assert.Contains("line1\\nline2", report, StringComparison.Ordinal);
        Assert.DoesNotContain("line1\nline2", report, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffReportRendersEmptyCollectionsNullIdentitiesAndEveryEscapeSequence()
    {
        var result = new ReleasePreparationDiffResult(
            "",
            null,
            null,
            null,
            [],
            [],
            []);

        var emptyReport = ReleasePreparationDiffReportRenderer.Render(result);

        Assert.Contains("- Base ref: ``", emptyReport, StringComparison.Ordinal);
        Assert.Equal(2, emptyReport.Split("| — | — | — |", StringSplitOptions.None).Length - 1);
        Assert.Contains("unavailable", emptyReport, StringComparison.Ordinal);

        var escapedReport = ReleasePreparationDiffReportRenderer.Render(result with
        {
            BaseRef = "slash\\carriage\rcontrol\0",
            Changes = [new ReleasePreparationChange("M", "path")]
        });

        Assert.Contains("slash\\\\carriage\\rcontrol\\u0000", escapedReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPrepDiffWritesSuccessfulAndFailingReports()
    {
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        try
        {
            var validRunner = CreateRunnerForNoFetchDiff("M\0README.md\0");
            using var successOutput = new StringWriter();
            using var successError = new StringWriter();
            var successExitCode = await Program.RunAsync(
                ["verify-prep-diff", "--no-fetch", "--report", "reports/success.md"],
                successOutput,
                successError,
                repositoryRoot,
                commandRunner: validRunner);

            Assert.Equal(0, successExitCode);
            Assert.Empty(successError.ToString());
            Assert.Contains("Result: pass", successOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Result: pass", await File.ReadAllTextAsync(Path.Join(repositoryRoot, "reports", "success.md")), StringComparison.Ordinal);

            var failingRunner = CreateRunnerForNoFetchDiff("M\0CHANGELOG.md\0");
            using var failingOutput = new StringWriter();
            using var failingError = new StringWriter();
            var failingExitCode = await Program.RunAsync(
                ["verify-prep-diff", "--no-fetch", "--report", "reports/failure.md"],
                failingOutput,
                failingError,
                repositoryRoot,
                commandRunner: failingRunner);

            Assert.Equal(1, failingExitCode);
            Assert.Empty(failingError.ToString());
            Assert.Contains("Result: fail", failingOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Result: fail", await File.ReadAllTextAsync(Path.Join(repositoryRoot, "reports", "failure.md")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("main", "origin/main")]
    [InlineData("origin/release/0.1.0", "origin/release/0.1.0")]
    [InlineData("refs/heads/main", "origin/main")]
    [InlineData("refs/remotes/origin/release/0.1.0", "origin/release/0.1.0")]
    public void BaseRefNormalizationAcceptsSupportedOriginTrackingForms(string input, string expected)
    {
        var normalized = ReleasePreparationDiffVerifier.TryNormalizeBaseRef(input, out var actual, out var issue);

        Assert.True(normalized, issue);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("refs/tags/v0.1.0")]
    [InlineData("origin/../main")]
    [InlineData("refs/heads/")]
    public void BaseRefNormalizationRejectsUnsafeOrUnsupportedRefs(string input)
    {
        var normalized = ReleasePreparationDiffVerifier.TryNormalizeBaseRef(input, out _, out var issue);

        Assert.False(normalized);
        Assert.Contains("safe origin-tracking", issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPrepDiffRendersAStableDiagnosticWhenTheReportPathCannotBeCreated()
    {
        var baseCommit = new string('a', 40);
        var headCommit = new string('b', 40);
        var runner = new FakeCommandRunner();
        runner.Add($"git rev-parse --verify origin/main", new CommandResult(0, baseCommit + "\n", ""));
        runner.Add("git rev-parse HEAD", new CommandResult(0, headCommit + "\n", ""));
        runner.Add($"git merge-base --all {baseCommit} {headCommit}", new CommandResult(0, baseCommit + "\n", ""));
        runner.Add($"git diff --name-status -z --find-renames {baseCommit}..{headCommit}", new CommandResult(0, "", ""));
        var blockingDirectory = Path.Join(Path.GetTempPath(), $"appsurface-release-prep-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(blockingDirectory);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await Program.RunAsync(
                ["verify-prep-diff", "--no-fetch", "--report", blockingDirectory],
                output,
                error,
                Directory.GetCurrentDirectory(),
                commandRunner: runner);

            Assert.Equal(1, exitCode);
            Assert.Contains("Code: release-prep-report-io-failure", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(blockingDirectory);
        }
    }

    [Fact]
    public async Task VerifyPrepDiffAllowsNonReleasePullRequestsWithoutAReleaseManifest()
    {
        var baseCommit = new string('a', 40);
        var headCommit = new string('b', 40);
        var runner = new FakeCommandRunner();
        runner.Add($"git rev-parse --verify origin/main", new CommandResult(0, baseCommit + "\n", ""));
        runner.Add("git rev-parse HEAD", new CommandResult(0, headCommit + "\n", ""));
        runner.Add($"git merge-base --all {baseCommit} {headCommit}", new CommandResult(0, baseCommit + "\n", ""));
        runner.Add($"git diff --name-status -z --find-renames {baseCommit}..{headCommit}", new CommandResult(0, "M\0README.md\0", ""));

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: true,
            witnessPath: null,
            CancellationToken.None);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("dotnet ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyPrepDiffRejectsReleaseArtifactsWithoutAReleaseManifest()
    {
        var runner = CreateRunnerForNoFetchDiff("M\0CHANGELOG.md\0");

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: true,
            witnessPath: null,
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-prep-release-manifest-required");
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith("dotnet ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyPrepDiffAllowsTheNonReleaseUnreleasedEntryWorkflow()
    {
        var runner = CreateRunnerForNoFetchDiff("M\0releases/unreleased.md\0A\0releases/unreleased.entries/2026-08-11.md\0");

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: true,
            witnessPath: null,
            CancellationToken.None);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task VerifyPrepDiffAllowsTheInitialCurrentReleasePointerBootstrap()
    {
        var runner = CreateRunnerForNoFetchDiff("A\0releases/current.md\0A\0releases/current.md.yml\0");

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: true,
            witnessPath: null,
            CancellationToken.None);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task VerifyPrepDiffDoesNotClassifyReleaseDocumentationAsAnArtifactChange()
    {
        var runner = CreateRunnerForNoFetchDiff("M\0releases/release-authoring-checklist.md\0");

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: true,
            witnessPath: null,
            CancellationToken.None);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task VerifyPrepDiffReportsABaseFetchFailure()
    {
        var runner = new FakeCommandRunner();
        runner.Add("git fetch origin main:refs/remotes/origin/main", new CommandResult(1, string.Empty, "network unavailable"));

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: false,
            witnessPath: null,
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-prep-base-fetch-failed");
    }

    [Fact]
    public async Task VerifyPrepDiffRefreshesTheBaseRefBeforeClassifyingAnOrdinaryDiff()
    {
        var runner = CreateRunnerForNoFetchDiff("M\0README.md\0");
        runner.Add("git fetch origin main:refs/remotes/origin/main", new CommandResult(0, string.Empty, string.Empty));

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: false, witnessPath: null, CancellationToken.None);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains("git fetch origin main:refs/remotes/origin/main", runner.Calls);
    }

    [Fact]
    public async Task VerifyPrepDiffReportsAMalformedGitNameStatusStream()
    {
        var runner = CreateRunnerForNoFetchDiff("M\0README.md");

        var result = await new ReleasePreparationDiffVerifier(runner).VerifyAsync(
            Directory.GetCurrentDirectory(),
            "main",
            noFetch: true,
            witnessPath: null,
            CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-prep-unsupported-status");
    }

    [Fact]
    public void ReleaseArtifactValidationRequiresEveryExpectedArtifact()
    {
        var diagnostics = new List<ReleaseDiagnostic>();

        ReleasePreparationDiffVerifier.ValidateReleaseArtifactChanges(
            "1.2.3",
            [new ReleasePreparationChange("A", "releases/v1.2.3.release.json")],
            [],
            diagnostics);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-unexpected-path"
            && diagnostic.Problem == "A required release-preparation artifact is missing.");
    }

    [Fact]
    public void ReleaseArtifactValidationAcceptsTheCompleteExactArtifactSet()
    {
        var diagnostics = new List<ReleaseDiagnostic>();

        ReleasePreparationDiffVerifier.ValidateReleaseArtifactChanges(
            "1.2.3",
            [
                new ReleasePreparationChange("A", "releases/v1.2.3.md"),
                new ReleasePreparationChange("A", "releases/v1.2.3.md.yml"),
                new ReleasePreparationChange("A", "releases/v1.2.3.release.json"),
                new ReleasePreparationChange("A", "releases/v1.2.3.evidence.json"),
                new ReleasePreparationChange("M", "releases/current.md"),
                new ReleasePreparationChange("M", "CHANGELOG.md"),
                new ReleasePreparationChange("M", "releases/unreleased.md"),
                new ReleasePreparationChange("M", "releases/unreleased.md.yml"),
                new ReleasePreparationChange("D", "releases/unreleased.entries/2026-08-11.md")
            ],
            ["releases/unreleased.entries/2026-08-11.md"],
            diagnostics);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ReleaseArtifactValidationReportsInvalidStatusesMissingConsumedEntriesAndUnrelatedChanges()
    {
        var diagnostics = new List<ReleaseDiagnostic>();

        ReleasePreparationDiffVerifier.ValidateReleaseArtifactChanges(
            "1.2.3",
            [
                new ReleasePreparationChange("M", "releases/v1.2.3.md"),
                new ReleasePreparationChange("A", "packages/README.md"),
                new ReleasePreparationChange("M", "src/unrelated.cs")
            ],
            ["releases/unreleased.entries/2026-08-11.md"],
            diagnostics);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-unsupported-status"
            && diagnostic.Cause.Contains("releases/v1.2.3.md", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-unsupported-status"
            && diagnostic.Cause.Contains("packages/README.md", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-unexpected-path"
            && diagnostic.Cause.Contains("src/unrelated.cs", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-release-manifest-shape"
            && diagnostic.Cause.Contains("2026-08-11.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyPrepDiffReportsUnavailableGitIdentitiesAndAmbiguousHistory()
    {
        var missingBase = new FakeCommandRunner();
        missingBase.Add("git rev-parse --verify origin/main", new CommandResult(1, string.Empty, "missing base"));
        missingBase.Add("git rev-parse HEAD", new CommandResult(0, new string('b', 40), string.Empty));

        var missingBaseResult = await new ReleasePreparationDiffVerifier(missingBase).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);

        Assert.Contains(missingBaseResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-base-ref-unavailable");

        var ambiguousHistory = new FakeCommandRunner();
        var baseCommit = new string('a', 40);
        var headCommit = new string('b', 40);
        ambiguousHistory.Add("git rev-parse --verify origin/main", new CommandResult(0, baseCommit, string.Empty));
        ambiguousHistory.Add("git rev-parse HEAD", new CommandResult(0, headCommit, string.Empty));
        ambiguousHistory.Add($"git merge-base --all {baseCommit} {headCommit}", new CommandResult(0, $"{baseCommit}\n{headCommit}", string.Empty));

        var ambiguousResult = await new ReleasePreparationDiffVerifier(ambiguousHistory).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);

        Assert.Contains(ambiguousResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-merge-base-invalid");
    }

    [Fact]
    public async Task VerifyPrepDiffReportsInvalidBaseRefsMergeBaseFailuresAndUnavailableDiffs()
    {
        var invalidBaseRef = await new ReleasePreparationDiffVerifier(new FakeCommandRunner()).VerifyAsync(
            Directory.GetCurrentDirectory(), "refs/tags/v1.2.3", noFetch: true, witnessPath: null, CancellationToken.None);
        Assert.Contains(invalidBaseRef.Diagnostics, diagnostic => diagnostic.Code == "release-prep-base-ref-invalid");

        var baseCommit = new string('a', 40);
        var headCommit = new string('b', 40);
        var mergeBaseFailure = new FakeCommandRunner();
        mergeBaseFailure.Add("git rev-parse --verify origin/main", new CommandResult(0, baseCommit, string.Empty));
        mergeBaseFailure.Add("git rev-parse HEAD", new CommandResult(0, headCommit, string.Empty));
        mergeBaseFailure.Add($"git merge-base --all {baseCommit} {headCommit}", new CommandResult(1, "fallback output", "merge-base failed"));

        var mergeBaseResult = await new ReleasePreparationDiffVerifier(mergeBaseFailure).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);
        Assert.Contains(mergeBaseResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-merge-base-invalid"
            && diagnostic.Cause.Contains("merge-base failed", StringComparison.Ordinal));

        var unavailableDiff = new FakeCommandRunner();
        unavailableDiff.Add("git rev-parse --verify origin/main", new CommandResult(0, baseCommit, string.Empty));
        unavailableDiff.Add("git rev-parse HEAD", new CommandResult(0, headCommit, string.Empty));
        unavailableDiff.Add($"git merge-base --all {baseCommit} {headCommit}", new CommandResult(0, baseCommit, string.Empty));
        unavailableDiff.Add($"git diff --name-status -z --find-renames {baseCommit}..{headCommit}", new CommandResult(1, "fallback output", "diff failed"));

        var unavailableDiffResult = await new ReleasePreparationDiffVerifier(unavailableDiff).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);
        Assert.Contains(unavailableDiffResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-diff-unavailable"
            && diagnostic.Cause.Contains("diff failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyPrepDiffRejectsUnsafeDuplicateRenamedAndCopiedPaths()
    {
        var unsafeResult = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff("M\0../outside.md\0M\0README.md\0M\0README.md\0")).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);

        Assert.Contains(unsafeResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-unexpected-path"
            && diagnostic.Problem == "Release preparation contains an unsafe repository path.");
        Assert.Contains(unsafeResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-unsupported-status"
            && diagnostic.Problem == "Release preparation reports one path more than once.");

        var renameResult = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff("R100\0old.md\0new.md\0C100\0source.md\0copy.md\0")).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);

        Assert.Contains(renameResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-rename-forbidden");
        Assert.Contains(renameResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-unsupported-status"
            && diagnostic.Problem == "Release preparation may not copy files.");
    }

    [Fact]
    public async Task VerifyPrepDiffRejectsPermanentSidecarAndInvalidReleaseManifestShapes()
    {
        var sidecarResult = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff("M\0releases/current.md.yml\0")).VerifyAsync(
            Directory.GetCurrentDirectory(), "main", noFetch: true, witnessPath: null, CancellationToken.None);
        Assert.Contains(sidecarResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-permanent-sidecar-changed");

        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "releases"));
        try
        {
            var duplicateManifests = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff(
                "A\0releases/v1.2.3.release.json\0A\0releases/v1.2.4.release.json\0")).VerifyAsync(
                repositoryRoot, "main", noFetch: true, witnessPath: null, CancellationToken.None);
            Assert.Contains(duplicateManifests.Diagnostics, diagnostic => diagnostic.Code == "release-prep-release-manifest-shape");

            await File.WriteAllTextAsync(Path.Join(repositoryRoot, "releases", "v1.2.3.release.json"), "{}");
            var invalidManifest = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff("A\0releases/v1.2.3.release.json\0")).VerifyAsync(
                repositoryRoot, "main", noFetch: true, witnessPath: null, CancellationToken.None);
            Assert.Contains(invalidManifest.Diagnostics, diagnostic => diagnostic.Code == "release-prep-release-manifest-shape"
                && diagnostic.Problem == "The added release manifest is not a valid release-preparation manifest.");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyPrepDiffRejectsInvalidAndUnreadableProvidedWitnesses()
    {
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        var witnessPath = Path.Join(repositoryRoot, "witness.json");
        Directory.CreateDirectory(Path.Join(repositoryRoot, "releases"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "releases", "v1.2.3.release.json"), ValidReleaseManifestJson());
        await File.WriteAllTextAsync(witnessPath, "not JSON");
        try
        {
            var result = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff(CompleteReleasePreparationDiff("M\0packages/package-index.yml\0"))).VerifyAsync(
                repositoryRoot, "main", noFetch: true, witnessPath, CancellationToken.None);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-invalid"
                && diagnostic.Problem == "PackageIndex emitted an invalid release-preparation witness.");

            var unreadableResult = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff(CompleteReleasePreparationDiff("M\0packages/package-index.yml\0"))).VerifyAsync(
                repositoryRoot, "main", noFetch: true, Path.Join(repositoryRoot, "missing.json"), CancellationToken.None);

            Assert.Contains(unreadableResult.Diagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-invalid"
                && diagnostic.Problem == "PackageIndex did not provide a readable release-preparation witness.");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyPrepDiffValidatesAWellFormedProvidedWitness()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        var witnessPath = Path.Join(repositoryRoot, "witness.json");
        Directory.CreateDirectory(Path.Join(repositoryRoot, "releases"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "releases", "v1.2.3.release.json"), ValidReleaseManifestJson());
        await File.WriteAllTextAsync(
            witnessPath,
            JsonSerializer.Serialize(CreateManifestWitness(baseCommit, headCommit, [], []), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        try
        {
            var result = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff(CompleteReleasePreparationDiff("M\0packages/package-index.yml\0"))).VerifyAsync(
                repositoryRoot, "main", noFetch: true, witnessPath, CancellationToken.None);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-prep-package-surface-without-source"
                && diagnostic.Problem == "A changed package source did not produce a changed generated surface.");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyPrepDiffReportsAnUnavailableGeneratedWitnessAndCleansUpItsTemporaryPath()
    {
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "releases"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "releases", "v1.2.3.release.json"), ValidReleaseManifestJson());
        try
        {
            var result = await new ReleasePreparationDiffVerifier(CreateRunnerForNoFetchDiff(CompleteReleasePreparationDiff("M\0packages/package-index.yml\0"))).VerifyAsync(
                repositoryRoot, "main", noFetch: true, witnessPath: null, CancellationToken.None);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-invalid"
                && diagnostic.Problem == "PackageIndex could not produce a release-preparation witness.");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessValidationReportsIdentitySourceOutputAndDigestMismatches()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "packages"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "packages", "README.md"), "unexpected content");
        try
        {
            var verifier = new ReleasePreparationDiffVerifier(new FakeCommandRunner());
            var identityDiagnostics = new List<ReleaseDiagnostic>();
            await verifier.ValidateWitnessAsync(
                CreateManifestWitness(baseCommit, headCommit, [], []) with { HeadCommit = baseCommit },
                [], repositoryRoot, "origin/main", baseCommit, baseCommit, headCommit, identityDiagnostics, CancellationToken.None);
            Assert.Contains(identityDiagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-invalid");

            var outputDiagnostics = new List<ReleaseDiagnostic>();
            await verifier.ValidateWitnessAsync(
                CreateManifestWitness(baseCommit, headCommit, [], []),
                [new ReleasePreparationChange("M", "packages/README.md")], repositoryRoot, "origin/main", baseCommit, baseCommit, headCommit, outputDiagnostics, CancellationToken.None);
            Assert.Contains(outputDiagnostics, diagnostic => diagnostic.Code == "release-prep-package-surface-without-source");

            var digestDiagnostics = new List<ReleaseDiagnostic>();
            await verifier.ValidateWitnessAsync(
                CreateManifestWitness(baseCommit, headCommit,
                    [new ReleasePreparationWitnessSurfaceDocument("chooser", "packages/README.md", ComputeSha256("expected content"))],
                    ["packages/README.md"]),
                [
                    new ReleasePreparationChange("M", "packages/package-index.yml"),
                    new ReleasePreparationChange("M", "packages/README.md")
                ], repositoryRoot, "origin/main", baseCommit, baseCommit, headCommit, digestDiagnostics, CancellationToken.None);
            Assert.Contains(digestDiagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-mismatch"
                && diagnostic.Problem == "A generated package document does not match the witness digest.");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessValidationReportsMissingBaseAndUnauthorizedGeneratedSurfaces()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "packages"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "packages", "README.md"), "current chooser");
        try
        {
            var missingBaseDiagnostics = new List<ReleaseDiagnostic>();
            await new ReleasePreparationDiffVerifier(new FakeCommandRunner()).ValidateWitnessAsync(
                CreateManifestWitness(baseCommit, headCommit,
                    [new ReleasePreparationWitnessSurfaceDocument("chooser", "packages/README.md", ComputeSha256("new chooser"))],
                    ["packages/README.md"]),
                [new ReleasePreparationChange("M", "packages/package-index.yml")],
                repositoryRoot, "origin/main", baseCommit, baseCommit, headCommit, missingBaseDiagnostics, CancellationToken.None);
            Assert.Contains(missingBaseDiagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-mismatch"
                && diagnostic.Problem == "A generated package surface could not be compared with its merge-base version.");

            var unauthorizedDiagnostics = new List<ReleaseDiagnostic>();
            await new ReleasePreparationDiffVerifier(new FakeCommandRunner()).ValidateWitnessAsync(
                new ReleasePreparationWitnessDocument(
                    "forge-trust.appsurface.release-prep-witness/v1",
                    baseCommit,
                    baseCommit,
                    baseCommit,
                    headCommit,
                    "verified",
                    [new ReleasePreparationWitnessInputDocument("package-index-manifest", "packages/package-index.yml", [])],
                    [new ReleasePreparationWitnessSurfaceDocument("chooser", "packages/README.md", ComputeSha256("current chooser"))]),
                [
                    new ReleasePreparationChange("M", "packages/package-index.yml"),
                    new ReleasePreparationChange("M", "packages/README.md")
                ],
                repositoryRoot, "origin/main", baseCommit, baseCommit, headCommit, unauthorizedDiagnostics, CancellationToken.None);
            Assert.Contains(unauthorizedDiagnostics, diagnostic => diagnostic.Code == "release-prep-package-surface-without-source"
                && diagnostic.Problem == "A changed package documentation surface has no changed semantic source.");
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("before changed\n<!-- appsurface-release-guidance: begin -->\nnew body\n<!-- appsurface-release-guidance: end -->\nafter\n", "outside")]
    [InlineData("before\nnew body\nafter\n", "marker pair")]
    public async Task WitnessValidationRejectsInvalidManagedReadmeUpdates(string headContent, string expectedCause)
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string path = "src/Package/README.md";
        const string baseContent = "before\n<!-- appsurface-release-guidance: begin -->\nold body\n<!-- appsurface-release-guidance: end -->\nafter\n";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "src", "Package"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "src", "Package", "README.md"), headContent);
        try
        {
            var runner = new FakeCommandRunner();
            runner.Add($"git show {baseCommit}:{path}", new CommandResult(0, baseContent, string.Empty));
            var diagnostics = new List<ReleaseDiagnostic>();

            await new ReleasePreparationDiffVerifier(runner).ValidateWitnessAsync(
                new ReleasePreparationWitnessDocument(
                    "forge-trust.appsurface.release-prep-witness/v1",
                    baseCommit,
                    baseCommit,
                    baseCommit,
                    headCommit,
                    "verified",
                    [new ReleasePreparationWitnessInputDocument(
                        "release-guidance-template",
                        "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template",
                        [path])],
                    [new ReleasePreparationWitnessSurfaceDocument("managed-readme", path, ComputeSha256("new body\n"))]),
                [
                    new ReleasePreparationChange("M", "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template"),
                    new ReleasePreparationChange("M", path)
                ],
                repositoryRoot,
                "origin/main",
                baseCommit,
                baseCommit,
                headCommit,
                diagnostics,
                CancellationToken.None);

            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-mismatch"
                && diagnostic.Cause.Contains(expectedCause, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessValidationRejectsAManagedReadmeWhoseBodyDoesNotMatchItsWitnessHash()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string path = "src/Package/README.md";
        const string baseContent = "\nbefore\n<!-- appsurface-release-guidance: begin -->\nold body\n<!-- appsurface-release-guidance: end -->\nafter\n";
        const string headContent = "\nbefore\n<!-- appsurface-release-guidance: begin -->\ndifferent body\n<!-- appsurface-release-guidance: end -->\nafter\n";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "src", "Package"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "src", "Package", "README.md"), headContent);
        try
        {
            var runner = new FakeCommandRunner();
            runner.Add($"git show {baseCommit}:{path}", new CommandResult(0, baseContent, string.Empty));
            var diagnostics = new List<ReleaseDiagnostic>();

            await new ReleasePreparationDiffVerifier(runner).ValidateWitnessAsync(
                new ReleasePreparationWitnessDocument(
                    "forge-trust.appsurface.release-prep-witness/v1",
                    baseCommit,
                    baseCommit,
                    baseCommit,
                    headCommit,
                    "verified",
                    [new ReleasePreparationWitnessInputDocument(
                        "release-guidance-template",
                        "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template",
                        [path])],
                    [new ReleasePreparationWitnessSurfaceDocument("managed-readme", path, ComputeSha256("expected body\n"))]),
                [
                    new ReleasePreparationChange("M", "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template"),
                    new ReleasePreparationChange("M", path)
                ],
                repositoryRoot,
                "origin/main",
                baseCommit,
                baseCommit,
                headCommit,
                diagnostics,
                CancellationToken.None);

            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-package-witness-mismatch"
                && diagnostic.Cause.Contains("does not match the witness SHA-256", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("{", "Expected")]
    [InlineData("[]", "root must be an object")]
    [InlineData("{\"schema\":null,\"baseRef\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"baseTipCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mergeBaseCommit\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"headCommit\":\"cccccccccccccccccccccccccccccccccccccccc\",\"verification\":\"verified\",\"changedInputs\":[],\"surfaces\":[]}", "schema")]
    [InlineData("{\"schema\":\"forge-trust.appsurface.release-prep-witness/v1\",\"baseRef\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"baseTipCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mergeBaseCommit\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"headCommit\":\"cccccccccccccccccccccccccccccccccccccccc\",\"verification\":\"verified\",\"changedInputs\":{},\"surfaces\":[]}", "changedInputs")]
    [InlineData("{\"schema\":\"forge-trust.appsurface.release-prep-witness/v1\",\"baseRef\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"baseTipCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"mergeBaseCommit\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"headCommit\":\"cccccccccccccccccccccccccccccccccccccccc\",\"verification\":\"verified\",\"changedInputs\":[],\"surfaces\":[]}", "commit identities")]
    public void WitnessParserRejectsMalformedRootContracts(string json, string expectedIssue)
    {
        Assert.False(ReleasePreparationDiffVerifier.TryParseWitness(json, out _, out var issue));
        Assert.Contains(expectedIssue, issue, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[{\"kind\":\"package-index-manifest\",\"path\":\"packages/package-index.yml\",\"surfaces\":[\"packages/missing.md\"]}]", "[]", "generated surface")]
    [InlineData("[{\"kind\":\"package-index-manifest\",\"path\":\"packages/package-index.yml\",\"surfaces\":[]}]", "[{\"kind\":\"chooser\",\"path\":\"packages/readiness.md\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]", "surfaces contain")]
    [InlineData("[{\"kind\":\"invalid\",\"path\":\"packages/package-index.yml\",\"surfaces\":[]}]", "[]", "changedInputs contain")]
    public void WitnessParserRejectsInvalidInputAndSurfaceOrderingContracts(string changedInputs, string surfaces, string expectedIssue)
    {
        var json = $$"""
            {"schema":"forge-trust.appsurface.release-prep-witness/v1","baseRef":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseTipCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mergeBaseCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","headCommit":"cccccccccccccccccccccccccccccccccccccccc","verification":"verified","changedInputs":{{changedInputs}},"surfaces":{{surfaces}}}
            """;

        Assert.False(ReleasePreparationDiffVerifier.TryParseWitness(json, out _, out var issue));
        Assert.Contains(expectedIssue, issue, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[0]", "[]")]
    [InlineData("[]", "[0]")]
    public void WitnessParserRejectsNonObjectInputAndSurfaceEntries(string changedInputs, string surfaces)
    {
        var json = $$"""
            {"schema":"forge-trust.appsurface.release-prep-witness/v1","baseRef":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseTipCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mergeBaseCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","headCommit":"cccccccccccccccccccccccccccccccccccccccc","verification":"verified","changedInputs":{{changedInputs}},"surfaces":{{surfaces}}}
            """;

        Assert.False(ReleasePreparationDiffVerifier.TryParseWitness(json, out _, out _));
    }

    [Theory]
    [InlineData("[\"\"]", "Witness changed-input surfaces")]
    [InlineData("[1]", "Witness changed-input surfaces")]
    public void WitnessParserRejectsBlankAndNonStringChangedInputSurfacePaths(string surfaces, string expectedIssue)
    {
        var json = $$"""
            {"schema":"forge-trust.appsurface.release-prep-witness/v1","baseRef":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseTipCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mergeBaseCommit":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","headCommit":"cccccccccccccccccccccccccccccccccccccccc","verification":"verified","changedInputs":[{"kind":"package-index-manifest","path":"packages/package-index.yml","surfaces":{{surfaces}}}],"surfaces":[]}
            """;

        Assert.False(ReleasePreparationDiffVerifier.TryParseWitness(json, out _, out var issue));
        Assert.Contains(expectedIssue, issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WitnessValidationRequiresEveryGeneratedSurfaceWhoseDigestChanged()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "packages"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "packages", "README.md"), "new chooser");
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "packages", "readiness.md"), "old readiness");
        try
        {
            var runner = new FakeCommandRunner();
            runner.Add($"git show {baseCommit}:packages/README.md", new CommandResult(0, "old chooser", string.Empty));
            runner.Add($"git show {baseCommit}:packages/readiness.md", new CommandResult(0, "old readiness", string.Empty));
            var witness = new ReleasePreparationWitnessDocument(
                "forge-trust.appsurface.release-prep-witness/v1",
                baseCommit,
                baseCommit,
                baseCommit,
                headCommit,
                "verified",
                [new ReleasePreparationWitnessInputDocument(
                    "package-index-manifest",
                    "packages/package-index.yml",
                    ["packages/README.md", "packages/readiness.md"])],
                [
                    new ReleasePreparationWitnessSurfaceDocument("chooser", "packages/README.md", ComputeSha256("new chooser")),
                    new ReleasePreparationWitnessSurfaceDocument("readiness", "packages/readiness.md", ComputeSha256("new readiness"))
                ]);
            var diagnostics = new List<ReleaseDiagnostic>();

            await new ReleasePreparationDiffVerifier(runner).ValidateWitnessAsync(
                witness,
                [
                    new ReleasePreparationChange("M", "packages/package-index.yml"),
                    new ReleasePreparationChange("M", "packages/README.md")
                ],
                repositoryRoot,
                "origin/main",
                baseCommit,
                baseCommit,
                headCommit,
                diagnostics,
                CancellationToken.None);

            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-prep-package-surface-missing"
                && diagnostic.Cause.Contains("packages/readiness.md", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessValidationAcceptsEveryRegeneratedSurface()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "packages"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "packages", "README.md"), "new chooser");
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "packages", "readiness.md"), "new readiness");
        try
        {
            var runner = new FakeCommandRunner();
            runner.Add($"git show {baseCommit}:packages/README.md", new CommandResult(0, "old chooser", string.Empty));
            runner.Add($"git show {baseCommit}:packages/readiness.md", new CommandResult(0, "old readiness", string.Empty));
            var diagnostics = new List<ReleaseDiagnostic>();

            await new ReleasePreparationDiffVerifier(runner).ValidateWitnessAsync(
                CreateManifestWitness(baseCommit, headCommit,
                    [
                        new ReleasePreparationWitnessSurfaceDocument("chooser", "packages/README.md", ComputeSha256("new chooser")),
                        new ReleasePreparationWitnessSurfaceDocument("readiness", "packages/readiness.md", ComputeSha256("new readiness"))
                    ],
                    ["packages/README.md", "packages/readiness.md"]),
                [
                    new ReleasePreparationChange("M", "packages/package-index.yml"),
                    new ReleasePreparationChange("M", "packages/README.md"),
                    new ReleasePreparationChange("M", "packages/readiness.md")
                ],
                repositoryRoot,
                "origin/main",
                baseCommit,
                baseCommit,
                headCommit,
                diagnostics,
                CancellationToken.None);

            Assert.Empty(diagnostics);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WitnessValidationAcceptsAManagedReadmeWithOnlyItsGuidanceBodyChanged()
    {
        const string baseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string headCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string path = "src/Package/README.md";
        var baseContent = "before\r\n<!-- appsurface-release-guidance: begin -->\r\nold body\r\n<!-- appsurface-release-guidance: end -->\r\nafter\r\n";
        var headContent = "before\r\n<!-- appsurface-release-guidance: begin -->\r\nnew body\r\n<!-- appsurface-release-guidance: end -->\r\nafter\r\n";
        var repositoryRoot = Path.Join(Path.GetTempPath(), "ReleasePreparationDiffVerifierTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(repositoryRoot, "src", "Package"));
        await File.WriteAllTextAsync(Path.Join(repositoryRoot, "src", "Package", "README.md"), headContent);
        try
        {
            var runner = new FakeCommandRunner();
            runner.Add($"git show {baseCommit}:{path}", new CommandResult(0, baseContent, string.Empty));
            var diagnostics = new List<ReleaseDiagnostic>();

            await new ReleasePreparationDiffVerifier(runner).ValidateWitnessAsync(
                new ReleasePreparationWitnessDocument(
                    "forge-trust.appsurface.release-prep-witness/v1",
                    baseCommit,
                    baseCommit,
                    baseCommit,
                    headCommit,
                    "verified",
                    [new ReleasePreparationWitnessInputDocument(
                        "release-guidance-template",
                        "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template",
                        [path])],
                    [new ReleasePreparationWitnessSurfaceDocument("managed-readme", path, ComputeSha256("new body\r\n"))]),
                [
                    new ReleasePreparationChange("M", "tools/ForgeTrust.AppSurface.PackageIndex/release-guidance.template"),
                    new ReleasePreparationChange("M", path)
                ],
                repositoryRoot,
                "origin/main",
                baseCommit,
                baseCommit,
                headCommit,
                diagnostics,
                CancellationToken.None);

            Assert.Empty(diagnostics);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static FakeCommandRunner CreateRunnerForNoFetchDiff(string diff)
    {
        var baseCommit = new string('a', 40);
        var headCommit = new string('b', 40);
        var runner = new FakeCommandRunner();
        runner.Add($"git rev-parse --verify origin/main", new CommandResult(0, baseCommit + "\n", string.Empty));
        runner.Add("git rev-parse HEAD", new CommandResult(0, headCommit + "\n", string.Empty));
        runner.Add($"git merge-base --all {baseCommit} {headCommit}", new CommandResult(0, baseCommit + "\n", string.Empty));
        runner.Add($"git diff --name-status -z --find-renames {baseCommit}..{headCommit}", new CommandResult(0, diff, string.Empty));
        return runner;
    }

    private static string CompleteReleasePreparationDiff(string additionalChanges) =>
        "A\0releases/v1.2.3.md\0"
        + "A\0releases/v1.2.3.md.yml\0"
        + "A\0releases/v1.2.3.release.json\0"
        + "A\0releases/v1.2.3.evidence.json\0"
        + "M\0releases/current.md\0"
        + "M\0CHANGELOG.md\0"
        + "M\0releases/unreleased.md\0"
        + "M\0releases/unreleased.md.yml\0"
        + additionalChanges;

    private static string ValidReleaseManifestJson() =>
        """
        {"schema":"appsurface-release-manifest-v2","version":"1.2.3","tag":"v1.2.3","date":"2026-05-25","preparationBaseCommit":"abc123","releaseClassification":"stable","generatedFiles":[],"publishedPackageProjects":[],"coordinatedPackageReleaseNoteResolutions":[],"diagnostics":[],"warningIds":[],"consumedUnreleasedEntryPaths":[]}
        """;

    private static ReleasePreparationWitnessDocument CreateManifestWitness(
        string baseCommit,
        string headCommit,
        IReadOnlyList<ReleasePreparationWitnessSurfaceDocument> surfaces,
        IReadOnlyList<string> generatedPaths) =>
        new(
            "forge-trust.appsurface.release-prep-witness/v1",
            baseCommit,
            baseCommit,
            baseCommit,
            headCommit,
            "verified",
            [new ReleasePreparationWitnessInputDocument("package-index-manifest", "packages/package-index.yml", generatedPaths)],
            surfaces);

    private static string ComputeSha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
