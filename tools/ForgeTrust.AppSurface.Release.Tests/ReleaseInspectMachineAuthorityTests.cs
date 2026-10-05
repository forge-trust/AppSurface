using System.Text.Json;
using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseInspectMachineAuthorityTests
{
    private const string Version = "0.1.0-preview.1";
    private const string Tag = "v0.1.0-preview.1";
    private const string TagObjectId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PeeledCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string PreparationBaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("repositoryRoot")]
    [InlineData("versionText")]
    [InlineData("tag")]
    [InlineData("baseRef")]
    public async Task InspectAsyncRejectsWhitespaceRequestValuesBeforeRunningGit(string parameterName)
    {
        var runner = new FakeCommandRunner();

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => InspectWithMissingValueAsync(parameterName, runner));

        Assert.Equal(parameterName, failure.ParamName);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("v0.1.0-preview.1", "release-version-leading-v")]
    [InlineData("01.0.0", "release-version-invalid")]
    public async Task InspectAsyncRejectsInvalidSemVerBeforeRunningGit(string versionText, string expectedCode)
    {
        var runner = new FakeCommandRunner();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ReleaseInspectMachineAuthority.InspectAsync(
            Path.GetTempPath(),
            versionText,
            Tag,
            "main",
            commandRunner: runner));

        Assert.Equal(expectedCode, failure.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task InspectAsyncRejectsMismatchedTagBeforeRunningGit()
    {
        var runner = new FakeCommandRunner();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ReleaseInspectMachineAuthority.InspectAsync(
            Path.GetTempPath(),
            Version,
            "v0.1.0-preview.2",
            "main",
            commandRunner: runner));

        Assert.Equal("release-tag-version-mismatch", failure.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task InspectAsyncRejectsAnObjectIdAsBaseRefBeforeRunningGit()
    {
        var runner = new FakeCommandRunner();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ReleaseInspectMachineAuthority.InspectAsync(
            Path.GetTempPath(),
            Version,
            Tag,
            PreparationBaseCommit,
            commandRunner: runner));

        Assert.Equal("release-base-ref-invalid", failure.Diagnostic.Code);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("origin/main")]
    [InlineData("refs/heads/main")]
    [InlineData("refs/remotes/origin/main")]
    public async Task InspectAsyncNormalizesBranchAliasesAndFailsClosedWhenReachabilityCannotBeProved(string baseRef)
    {
        var runner = CreateUnreachableTagRunner();

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ReleaseInspectMachineAuthority.InspectAsync(
            Path.GetTempPath(),
            Version,
            Tag,
            baseRef,
            commandRunner: runner));

        Assert.Equal("release-tag-unreachable-from-base-ref", failure.Diagnostic.Code);
        Assert.Contains(
            $"git merge-base --is-ancestor {PeeledCommit} origin/main",
            runner.Calls);
    }

    [Fact]
    public async Task InspectAsyncPropagatesCancellationToTheGitRunner()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new CancellationAwareCommandRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleaseInspectMachineAuthority.InspectAsync(
            Path.GetTempPath(),
            Version,
            Tag,
            "main",
            cancellation.Token,
            runner));

        Assert.Equal(cancellation.Token, runner.ReceivedCancellationToken);
        Assert.Equal($"git rev-parse --verify refs/tags/{Tag}", runner.Calls.Single());
    }

    [Fact]
    public async Task InspectAsyncReturnsTheSameMachineJsonAsTheCliForAValidatedTag()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"ReleaseInspectMachineAuthorityTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryRoot);

        try
        {
            await SeedRepositoryAsync(repositoryRoot);
            var prepare = await RunCliAsync(
                ["prepare", "--version", Version, "--date", "2026-05-25"],
                repositoryRoot,
                FakeCommandRunner.WithSourceCommit(PreparationBaseCommit));
            Assert.Equal(0, prepare.ExitCode);

            var cliRunner = await CreateSuccessfulV2InspectRunnerAsync(repositoryRoot);
            var cli = await RunCliAsync(
                ["inspect", "--version", Version, "--tag", Tag, "--base-ref", "refs/heads/main", "--machine-json"],
                repositoryRoot,
                cliRunner);
            Assert.Equal(0, cli.ExitCode);

            var authorityRunner = await CreateSuccessfulV2InspectRunnerAsync(repositoryRoot);
            var inProcess = await ReleaseInspectMachineAuthority.InspectAsync(
                repositoryRoot,
                Version,
                Tag,
                "refs/heads/main",
                commandRunner: authorityRunner);

            var cliJson = cli.Stdout.TrimEnd('\r', '\n');
            Assert.Equal(cliJson, inProcess.SerializeBounded());
            using var document = JsonDocument.Parse(cliJson);
            Assert.Equal("main", document.RootElement.GetProperty("baseRef").GetString());
            Assert.DoesNotContain(cliRunner.Calls, call => call.StartsWith("gh ", StringComparison.Ordinal));
            Assert.DoesNotContain(authorityRunner.Calls, call => call.StartsWith("gh ", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static Task<ReleaseInspectMachineResult> InspectWithMissingValueAsync(string parameterName, FakeCommandRunner runner)
    {
        var repositoryRoot = parameterName == "repositoryRoot" ? " " : Path.GetTempPath();
        var versionText = parameterName == "versionText" ? " " : Version;
        var tag = parameterName == "tag" ? " " : Tag;
        var baseRef = parameterName == "baseRef" ? " " : "main";
        return ReleaseInspectMachineAuthority.InspectAsync(repositoryRoot, versionText, tag, baseRef, commandRunner: runner);
    }

    private static FakeCommandRunner CreateUnreachableTagRunner()
    {
        var runner = new FakeCommandRunner();
        runner.Add($"git rev-parse --verify refs/tags/{Tag}", new CommandResult(0, TagObjectId + "\n", ""));
        runner.Add("git cat-file -t " + TagObjectId, new CommandResult(0, "tag\n", ""));
        runner.Add(
            "git cat-file -p " + TagObjectId,
            new CommandResult(0, CreateTagObjectWithoutBinding(), ""));
        runner.Add("git rev-parse " + TagObjectId + "^{commit}", new CommandResult(0, PeeledCommit + "\n", ""));
        runner.Add(
            $"git merge-base --is-ancestor {PeeledCommit} origin/main",
            new CommandResult(1, "", "commit is not reachable from origin/main"));
        return runner;
    }

    private static string CreateTagObjectWithoutBinding() =>
        $"object {PeeledCommit}\ntype commit\ntag {Tag}\ntagger Release Tests <release-tests@example.test> 1770000000 +0000\n\n";

    private static async Task SeedRepositoryAsync(string repositoryRoot)
    {
        await WriteFileAsync(repositoryRoot, ".github/workflows/nuget-prerelease-publish.yml", "name: NuGet Prerelease Publish\n");
        await WriteFileAsync(
            repositoryRoot,
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
            repositoryRoot,
            "releases/unreleased.md",
            """
            # Unreleased

            This is the living release note for the next coordinated AppSurface version.

            ## What is taking shape

            - The release story is almost ready.
            <!-- appsurface:unreleased-entries section="taking-shape" -->

            ## Included in the next coordinated version

            ### Release and docs surface

            - The release cockpit prepares release pull requests.
            <!-- appsurface:unreleased-entries section="included" -->

            ## Migration watch

            - No migration steps are required.
            <!-- appsurface:unreleased-entries section="migration-watch" -->
            """);
        await WriteFileAsync(
            repositoryRoot,
            "releases/unreleased.md.yml",
            """
            title: Unreleased
            summary: Living proof artifact.
            page_type: release-note
            nav_group: Releases
            order: 15
            """);
        await WriteFileAsync(
            repositoryRoot,
            "releases/current.md",
            "<!-- appsurface-current-coordinated-release: none -->\n# Current coordinated release\n\nNo coordinated AppSurface release has been tagged yet.\n");
        await WriteFileAsync(
            repositoryRoot,
            "releases/current.md.yml",
            "title: Current coordinated release\nsummary: Permanent pointer metadata.\n");
        await WriteFileAsync(repositoryRoot, "releases/templates/tagged-release-template.md", "# Release x.y.z\n");
        await WriteFileAsync(
            repositoryRoot,
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

    private static async Task<FakeCommandRunner> CreateSuccessfulV2InspectRunnerAsync(string repositoryRoot)
    {
        var sidecar = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "releases/v" + Version + ".md.yml"));
        var manifest = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "releases/v" + Version + ".release.json"));
        var evidenceJson = await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "releases/v" + Version + ".evidence.json"));
        var evidence = JsonSerializer.Deserialize<ReleaseEvidenceBundleV2>(evidenceJson, ReleaseJson.Options)!;
        var binding = new ReleaseTagBinding(
            Tag,
            ReleaseEvidence.ComputeSha256Hex(sidecar),
            ReleaseEvidence.ComputeSha256Hex(manifest),
            evidence.Subject.Sha256);
        var tagObject =
            $"object {PeeledCommit}\ntype commit\ntag {Tag}\ntagger Release Tests <release-tests@example.test> 1770000000 +0000\n\n{binding.Render()}";
        var runner = new FakeCommandRunner();
        runner.Add($"git rev-parse --verify refs/tags/{Tag}", new CommandResult(0, TagObjectId + "\n", ""));
        runner.Add("git cat-file -t " + TagObjectId, new CommandResult(0, "tag\n", ""));
        runner.Add("git cat-file -p " + TagObjectId, new CommandResult(0, tagObject, ""));
        runner.Add("git rev-parse " + TagObjectId + "^{commit}", new CommandResult(0, PeeledCommit + "\n", ""));
        runner.Add(
            $"git merge-base --is-ancestor {evidence.Commits.PreparationBaseCommit} {PeeledCommit}",
            new CommandResult(0, "", ""));
        runner.Add($"git merge-base --is-ancestor {PeeledCommit} origin/main", new CommandResult(0, "", ""));

        foreach (var path in new[]
                 {
                     $"releases/v{Version}.md",
                     $"releases/v{Version}.md.yml",
                     $"releases/v{Version}.release.json",
                     $"releases/v{Version}.evidence.json",
                     "releases/current.md",
                     "releases/current.md.yml",
                     "packages/package-index.yml"
                 })
        {
            var content = await File.ReadAllTextAsync(TestPathUtils.PathUnder(repositoryRoot, path));
            runner.Add($"git show {PeeledCommit}:{path}", new CommandResult(0, content, ""));
        }

        return runner;
    }

    private static async Task WriteFileAsync(string repositoryRoot, string relativePath, string content)
    {
        var path = TestPathUtils.PathUnder(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static async Task<CliResult> RunCliAsync(string[] args, string repositoryRoot, FakeCommandRunner runner)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunAsync(args, stdout, stderr, repositoryRoot, commandRunner: runner);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class CancellationAwareCommandRunner : ICommandRunner
    {
        internal List<string> Calls { get; } = [];

        internal CancellationToken? ReceivedCancellationToken { get; private set; }

        public Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            ReceivedCancellationToken = cancellationToken;
            Calls.Add(invocation.Executable + " " + string.Join(' ', invocation.Arguments));
            return Task.FromCanceled<CommandResult>(cancellationToken);
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
