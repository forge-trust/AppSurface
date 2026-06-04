namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunnerSupportTests
{
    [Fact]
    public async Task ProgramMain_ShouldListGroups()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await Program.Main(["--list-groups"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("tools", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldCaptureOutputErrorAndExitCode()
    {
        var runner = new ProcessCommandRunner();
        var command = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/c", "echo out && echo err 1>&2 && exit /b 3" }
            : ["-c", "printf out && printf err >&2 && exit 3"];

        var result = await runner.RunAsync(command, arguments, Directory.GetCurrentDirectory(), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("out", result.Output, StringComparison.Ordinal);
        Assert.Contains("err", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldValidateRequiredInputs()
    {
        var runner = new ProcessCommandRunner();

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("", [], Directory.GetCurrentDirectory(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync("dotnet", null!, Directory.GetCurrentDirectory(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync("dotnet", [], "", CancellationToken.None));
    }

    [Fact]
    public void SystemClock_ShouldCreateStartedTimer()
    {
        var timer = new SystemClock().StartTimer();

        Assert.True(timer.ElapsedSeconds >= 0);
    }
}
