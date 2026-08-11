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
}
