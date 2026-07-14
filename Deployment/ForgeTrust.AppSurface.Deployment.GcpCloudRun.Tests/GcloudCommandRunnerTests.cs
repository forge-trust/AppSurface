using ForgeTrust.AppSurface.Deployment.GcpCloudRun;

public sealed class GcloudCommandRunnerTests
{
    [Fact]
    public void DefaultConstructorSelectsPlatformLauncher()
    {
        var runner = new GcloudCommandRunner();

        Assert.Equal(OperatingSystem.IsWindows() ? "gcloud.cmd" : "gcloud", runner.Command);
        Assert.Equal(OperatingSystem.IsWindows(), runner.RequiresWindowsCommandInterpreter);
    }

    [Fact]
    public void ConstructorRejectsBlankExecutable()
    {
        Assert.Throws<ArgumentException>(() => new GcloudCommandRunner(" "));
    }

    [Fact]
    public async Task RunAsyncRejectsInvalidArgumentsBeforeLaunch()
    {
        var runner = new GcloudCommandRunner(ProcessPath());
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, TimeSpan.FromSeconds(1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync([], TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync([null!], TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task RunAsyncCapturesOutputAndExitCodeWithoutShellInterpolation()
    {
        var runner = new GcloudCommandRunner(ProcessPath());
        var result = await runner.RunAsync(Command("printf 'safe-output'; printf 'safe-error' >&2; exit 7"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("safe-output", result.StandardOutput);
        Assert.Equal("safe-error", result.StandardError);
    }

    [Fact]
    public async Task RunAsyncClassifiesMissingExecutable()
    {
        var runner = new GcloudCommandRunner(Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-gcloud"));
        var error = await Assert.ThrowsAsync<GcloudCommandException>(() => runner.RunAsync([], TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("ASDEPLOY165", error.Code);
    }

    [Fact]
    public async Task RunAsyncClassifiesTimeoutAndKillsProcess()
    {
        var runner = new GcloudCommandRunner(ProcessPath());
        var error = await Assert.ThrowsAsync<GcloudCommandException>(() => runner.RunAsync(Command(LongSleep()), TimeSpan.FromMilliseconds(20), CancellationToken.None));
        Assert.Equal("ASDEPLOY166", error.Code);
    }

    [Fact]
    public async Task RunAsyncPreservesCallerCancellationAndKillsProcess()
    {
        var runner = new GcloudCommandRunner(ProcessPath());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Command(LongSleep()), TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Fact]
    public void BuildWindowsCommandAcceptsProviderTokensAndRejectsShellSyntax()
    {
        Assert.Equal(
            "gcloud.cmd run jobs describe migration --project skoolit-staging --format=json --quiet",
            GcloudCommandRunner.BuildWindowsCommand(
                "gcloud.cmd",
                ["run", "jobs", "describe", "migration", "--project", "skoolit-staging", "--format=json", "--quiet"]));

        Assert.Throws<ArgumentException>(() => GcloudCommandRunner.BuildWindowsCommand("gcloud.cmd", ["migration&whoami"]));
        Assert.Throws<ArgumentException>(() => GcloudCommandRunner.BuildWindowsCommand("gcloud.cmd", ["two words"]));
        Assert.Throws<ArgumentException>(() => GcloudCommandRunner.BuildWindowsCommand("gcloud.cmd", [null!]));
        Assert.Throws<ArgumentNullException>(() => GcloudCommandRunner.BuildWindowsCommand("gcloud.cmd", null!));
        Assert.Throws<ArgumentException>(() => GcloudCommandRunner.BuildWindowsCommand(" ", []));
    }

    [Fact]
    public void CreateStartInfoBuildsBoundedWindowsCommandInterpreterDispatch()
    {
        var runner = new GcloudCommandRunner("gcloud.cmd", requiresWindowsCommandInterpreter: true);

        var startInfo = runner.CreateStartInfo(["run", "jobs", "describe", "migration", "--format=json"]);

        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", startInfo.FileName);
        Assert.Equal(["/d", "/c", "gcloud.cmd run jobs describe migration --format=json"], startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
    }

    private static string ProcessPath() => OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
        : "/bin/sh";

    private static string[] Command(string command) => OperatingSystem.IsWindows()
        ? ["/d", "/s", "/c", command]
        : ["-c", command];

    private static string LongSleep() => OperatingSystem.IsWindows()
        ? "ping 127.0.0.1 -n 6 > nul"
        : "sleep 5";
}
