using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseCurrentPointerCoverageTests
{
    [Fact]
    public void CurrentPointerRejectsMarkerWithoutSuffix()
    {
        var content = ReleaseCurrentPointer.Build(SemVer.Parse("1.2.3"))
            .Replace(" -->", "", StringComparison.Ordinal);

        Assert.False(ReleaseCurrentPointer.TryParse(content, out _));
    }

    [Fact]
    public void CurrentPointerRejectsInvalidTag()
    {
        var content = ReleaseCurrentPointer.BuildNone()
            .Replace("none", "vnot-a-semver", StringComparison.Ordinal);

        Assert.False(ReleaseCurrentPointer.TryParse(content, out _));
    }

    [Fact]
    public async Task GateReportsUnexpectedTargetTagLookupExit()
    {
        var target = SemVer.Parse("1.0.0");
        var runner = new FakeCommandRunner();
        runner.Add(TagListCommand, new CommandResult(0, "", ""));
        runner.Add(TargetLookupCommand(target), new CommandResult(128, "", "rev-parse failed"));

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.BuildNone());

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "release-current-page-tag-discovery-failed"
                && diagnostic.Cause == "rev-parse failed");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "release-current-page-target-tag-exists");
    }

    [Fact]
    public async Task GateRejectsStaleNonePointerWhenReachableVersionedTagExists()
    {
        var target = SemVer.Parse("1.0.1");
        var tag = "v1.0.0";
        var runner = RunnerWithReachableTag(target, tag, "commit");

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.BuildNone());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-stale");
    }

    [Fact]
    public async Task GateRejectsStaleVersionedPointerWhenItDoesNotNameTheLatestTag()
    {
        var target = SemVer.Parse("1.0.2");
        var tag = "v1.0.1";
        var runner = RunnerWithReachableTag(target, tag, "commit");

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.Build(SemVer.Parse("1.0.0")));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-stale");
    }

    [Fact]
    public async Task GateReportsAnnotatedTagInspectionFailure()
    {
        var target = SemVer.Parse("1.0.1");
        var tag = "v1.0.0";
        var runner = new FakeCommandRunner();
        runner.Add(TagListCommand, new CommandResult(0, tag + "\n", ""));
        runner.Add(CatFileTypeCommand(tag), new CommandResult(128, "", "tag inspection failed"));
        runner.Add(TargetLookupCommand(target), MissingTarget);

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.BuildNone());

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "release-current-page-tag-discovery-failed"
                && diagnostic.Cause == "tag inspection failed");
    }

    [Fact]
    public async Task GateIgnoresLightweightTag()
    {
        var target = SemVer.Parse("1.0.1");
        var tag = "v1.0.0";
        var runner = new FakeCommandRunner();
        runner.Add(TagListCommand, new CommandResult(0, tag + "\n", ""));
        runner.Add(CatFileTypeCommand(tag), new CommandResult(0, "commit\n", ""));
        runner.Add(TargetLookupCommand(target), MissingTarget);

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.BuildNone());

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(128, "", "broken tag object")]
    [InlineData(0, " \n", "")]
    public async Task GateReportsUnreadablePeeledAnnotatedTag(int exitCode, string output, string error)
    {
        var target = SemVer.Parse("1.0.1");
        var tag = "v1.0.0";
        var runner = new FakeCommandRunner();
        runner.Add(TagListCommand, new CommandResult(0, tag + "\n", ""));
        runner.Add(CatFileTypeCommand(tag), new CommandResult(0, "tag\n", ""));
        runner.Add(PeelCommand(tag), new CommandResult(exitCode, output, error));
        runner.Add(TargetLookupCommand(target), MissingTarget);

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.BuildNone());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "release-current-page-tag-unreadable");
    }

    [Fact]
    public async Task GateReportsUnexpectedMergeBaseExit()
    {
        var target = SemVer.Parse("1.0.1");
        var tag = "v1.0.0";
        var runner = new FakeCommandRunner();
        runner.Add(TagListCommand, new CommandResult(0, tag + "\n", ""));
        runner.Add(CatFileTypeCommand(tag), new CommandResult(0, "tag\n", ""));
        runner.Add(PeelCommand(tag), new CommandResult(0, "commit\n", ""));
        runner.Add(MergeBaseCommand("commit"), new CommandResult(128, "", "merge-base failed"));
        runner.Add(TargetLookupCommand(target), MissingTarget);

        var diagnostics = await ValidateAsync(runner, target, ReleaseCurrentPointer.BuildNone());

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "release-current-page-tag-discovery-failed"
                && diagnostic.Cause == "merge-base failed");
    }

    private static readonly CommandResult MissingTarget = new(1, "", "");

    private const string TagListCommand = "git for-each-ref --format=%(refname:short) refs/tags/v*";

    private static readonly string RepositoryRoot = Path.Join(Path.GetTempPath(), "release-current-pointer-coverage");

    private static ReleaseCurrentPointerGate CreateGate(ICommandRunner runner) =>
        new(new ReleaseWorkspace(RepositoryRoot), runner);

    private static async Task<IReadOnlyList<ReleaseDiagnostic>> ValidateAsync(
        FakeCommandRunner runner,
        SemVer target,
        string pointer)
    {
        return await CreateGate(runner).ValidateAsync(target, pointer, "base", CancellationToken.None);
    }

    private static FakeCommandRunner RunnerWithReachableTag(SemVer target, string tag, string commit)
    {
        var runner = new FakeCommandRunner();
        runner.Add(TagListCommand, new CommandResult(0, tag + "\n", ""));
        AddReachableAnnotatedTag(runner, tag, commit);
        runner.Add(TargetLookupCommand(target), MissingTarget);
        return runner;
    }

    private static void AddReachableAnnotatedTag(FakeCommandRunner runner, string tag, string commit)
    {
        runner.Add(CatFileTypeCommand(tag), new CommandResult(0, "tag\n", ""));
        runner.Add(PeelCommand(tag), new CommandResult(0, commit + "\n", ""));
        runner.Add(MergeBaseCommand(commit), new CommandResult(0, "", ""));
    }

    private static string TargetLookupCommand(SemVer target) =>
        $"git rev-parse --verify --quiet refs/tags/{target.TagName}";

    private static string CatFileTypeCommand(string tag) =>
        $"git cat-file -t refs/tags/{tag}";

    private static string PeelCommand(string tag) =>
        $"git rev-parse refs/tags/{tag}^{{commit}}";

    private static string MergeBaseCommand(string commit) =>
        $"git merge-base --is-ancestor {commit} base";

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly Dictionary<string, CommandResult> _results = new(StringComparer.Ordinal);

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
