using System.Security.Cryptography;
using System.Text;
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
        var blockingFile = Path.GetTempFileName();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await Program.RunAsync(
                ["verify-prep-diff", "--no-fetch", "--report", Path.Combine(blockingFile, "report.md")],
                output,
                error,
                Directory.GetCurrentDirectory(),
                commandRunner: runner);

            Assert.Equal(1, exitCode);
            Assert.Contains("Code: release-prep-report-io-failure", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(blockingFile);
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
        var baseContent = "before\n<!-- appsurface-release-guidance: begin -->\nold body\n<!-- appsurface-release-guidance: end -->\nafter\n";
        var headContent = "before\n<!-- appsurface-release-guidance: begin -->\nnew body\n<!-- appsurface-release-guidance: end -->\nafter\n";
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
