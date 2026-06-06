using System.Collections;
using System.Collections.Specialized;

namespace ForgeTrust.AppSurface.CoverageRunner.Tests;

public sealed class CoverageRunnerSupportTests
{
    [Fact]
    public void Program_ShouldCreateCaseInsensitiveEnvironmentSnapshot()
    {
        var variables = new Hashtable { ["build_configuration"] = "Release" };

        var environment = Program.CreateEnvironmentSnapshot(variables);

        Assert.Equal("Release", environment["BUILD_CONFIGURATION"]);
    }

    [Fact]
    public void Program_ShouldTolerateDuplicateEnvironmentKeysWithDifferentCasing()
    {
        var variables = new ListDictionary
        {
            { "TEST_GROUP", "all" },
            { "test_group", "tools" },
        };

        var environment = Program.CreateEnvironmentSnapshot(variables);

        Assert.Single(environment);
        Assert.Equal("tools", environment["TEST_GROUP"]);
    }

    [Fact]
    public async Task ProgramMain_ShouldListGroups()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Program.RunAsync(["--list-groups"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("tools", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ProgramMain_ShouldUseConsoleWriters()
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
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains($"out{Environment.NewLine}err", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("", "err", "err")]
    [InlineData("out", "", "out")]
    [InlineData("out\n", "err", "out\nerr")]
    public void ProcessCommandRunner_ShouldJoinBufferedOutputWithReadableBoundaries(
        string standardOutput,
        string standardError,
        string expected)
    {
        Assert.Equal(expected, ProcessCommandRunner.JoinOutput(standardOutput, standardError));
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldStreamOutputToFile()
    {
        var runner = new ProcessCommandRunner();
        var command = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/c", "echo out && echo err 1>&2 && exit /b 3" }
            : ["-c", "printf out && printf err >&2 && exit 3"];
        var logFile = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var result = await runner.RunAsync(command, arguments, Directory.GetCurrentDirectory(), CancellationToken.None, outputFile: logFile);

            Assert.Equal(3, result.ExitCode);
            Assert.Empty(result.Output);
            var log = await File.ReadAllTextAsync(logFile);
            Assert.Contains("out", log, StringComparison.Ordinal);
            Assert.Contains("err", log, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(logFile);
        }
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldReturnFailureWhenProcessCannotStart()
    {
        var runner = new ProcessCommandRunner();

        var result = await runner.RunAsync(
            "forge-trust-appsurface-missing-command",
            [],
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Failed to start command", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldWriteLaunchFailureToOutputFile()
    {
        var runner = new ProcessCommandRunner();
        var logFile = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var result = await runner.RunAsync(
                "forge-trust-appsurface-missing-command",
                [],
                Directory.GetCurrentDirectory(),
                CancellationToken.None,
                outputFile: logFile);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Failed to start command", await File.ReadAllTextAsync(logFile), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(logFile);
        }
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldReturnFailureWhenLaunchFailureLogCannotBeWritten()
    {
        var runner = new ProcessCommandRunner();
        var existingFile = Path.GetTempFileName();

        try
        {
            var result = await runner.RunAsync(
                "forge-trust-appsurface-missing-command",
                [],
                Directory.GetCurrentDirectory(),
                CancellationToken.None,
                outputFile: Path.Join(existingFile, "dotnet-test.log"));

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Failed to start command", result.Output, StringComparison.Ordinal);
            Assert.Contains("Failed to write command log", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(existingFile);
        }
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldPreserveCancellationWhenWritingLaunchFailureLog()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessCommandRunner.TryAppendFailureLogAsync(
            Path.Join(Path.GetTempPath(), Path.GetRandomFileName()),
            "failed",
            cancellation.Token));
    }

    [Fact]
    public async Task ProcessCommandRunner_ShouldPreserveCancellation()
    {
        var runner = new ProcessCommandRunner();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(
            OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
            OperatingSystem.IsWindows() ? ["/c", "timeout /t 1"] : ["-c", "sleep 1"],
            Directory.GetCurrentDirectory(),
            cancellation.Token));
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
