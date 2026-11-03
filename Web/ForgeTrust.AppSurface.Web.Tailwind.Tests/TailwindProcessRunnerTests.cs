using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Web.Tailwind.Internal;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

public sealed class TailwindProcessRunnerTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(
        Path.GetTempPath(),
        Path.GetFileName($"{nameof(TailwindProcessRunnerTests)}_{Guid.NewGuid():N}"));

    public TailwindProcessRunnerTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("", (int)TailwindOutputLevel.Debug)]
    [InlineData(" \t", (int)TailwindOutputLevel.Debug)]
    [InlineData("≈ tailwindcss v4.1.18", (int)TailwindOutputLevel.Information)]
    [InlineData("Done in 34ms", (int)TailwindOutputLevel.Information)]
    [InlineData("\u001b[3m\u001b[1m\u001b[34m≈ tailwindcss v4.1.18\u001b[0m", (int)TailwindOutputLevel.Information)]
    [InlineData("\u001b[32mDone in \u001b[1m34ms\u001b[m", (int)TailwindOutputLevel.Information)]
    [InlineData("\u001b[38:2::34:56:78m≈ tailwindcss v4.1.18\u001b[0m", (int)TailwindOutputLevel.Information)]
    [InlineData("\u001b[33mWarning: unexpected input\u001b[0m", (int)TailwindOutputLevel.Error)]
    [InlineData("\u001b[31mError: boom\u001b[0m", (int)TailwindOutputLevel.Error)]
    [InlineData("unrecognized message", (int)TailwindOutputLevel.Error)]
    [InlineData("\u001b[31≈ tailwindcss v4.1.18", (int)TailwindOutputLevel.Error)]
    [InlineData("Done in 34ms\u001b[31", (int)TailwindOutputLevel.Error)]
    [InlineData("\u001b[2K≈ tailwindcss v4.1.18", (int)TailwindOutputLevel.Error)]
    [InlineData("Done in 34ms\u001b[2K", (int)TailwindOutputLevel.Error)]
    [InlineData("\u001b]0;title\u0007Done in 34ms", (int)TailwindOutputLevel.Error)]
    [InlineData("\u001b[0m \u001b[m", (int)TailwindOutputLevel.Debug)]
    public void Classify_IgnoresOnlyCompleteSgrStyling(string line, int expected)
    {
        Assert.Equal((TailwindOutputLevel)expected, TailwindStderrClassifier.Classify(line));
    }

    [Fact]
    public async Task ExecuteAsync_ClassifiesColoredStderrWithoutChangingCallbacksOrCapturedOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var scriptPath = await WriteUnixScriptAsync(
            "colored-tailwind",
            """
            #!/bin/sh
            printf '\033[3m\033[1m\033[34m≈ tailwindcss v4.1.18\033[0m\n\033[32mDone in 34ms\033[0m\n\033[33mWarning: unexpected input\033[0m\n\033[31mError: boom\033[0m\nDone in 34ms\033[2K\n' >&2
            exit 0
            """);
        string[] expectedLines =
        [
            "\u001b[3m\u001b[1m\u001b[34m≈ tailwindcss v4.1.18\u001b[0m",
            "\u001b[32mDone in 34ms\u001b[0m",
            "\u001b[33mWarning: unexpected input\u001b[0m",
            "\u001b[31mError: boom\u001b[0m",
            "Done in 34ms\u001b[2K",
        ];
        var stderrLines = new List<(string Line, TailwindOutputLevel Level)>();

        var result = await TailwindProcessRunner.ExecuteAsync(
            scriptPath,
            [],
            _tempRoot,
            null,
            (line, level) => stderrLines.Add((line, level)),
            captureLimit: 8192,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedLines, stderrLines.Select(static item => item.Line));
        Assert.Equal(
            [TailwindOutputLevel.Information, TailwindOutputLevel.Information, TailwindOutputLevel.Error, TailwindOutputLevel.Error, TailwindOutputLevel.Error],
            stderrLines.Select(static item => item.Level));
        Assert.Equal(string.Join('\n', expectedLines) + "\n", result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesBoundedOutputAndHandlesCarriageReturnLines()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var scriptPath = await WriteUnixScriptAsync(
            "line-endings-tailwind",
            """
            #!/bin/sh
            printf 'alpha\r\nbeta\rgamma'
            exit 0
            """);
        var stdoutLines = new List<string>();

        var result = await TailwindProcessRunner.ExecuteAsync(
            scriptPath,
            [],
            _tempRoot,
            stdoutLines.Add,
            null,
            captureLimit: 3,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["alpha", "beta", "gamma"], stdoutLines);
        Assert.Equal("mma", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsStartException_WhenExecutableCannotStart()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var scriptPath = Path.Join(_tempRoot, "not-executable-tailwind");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nexit 0\n");

        var exception = await Assert.ThrowsAsync<TailwindProcessStartException>(() =>
            TailwindProcessRunner.ExecuteAsync(
                scriptPath,
                [],
                _tempRoot,
                null,
                null,
                captureLimit: 8192,
                CancellationToken.None));

        Assert.Equal(scriptPath, exception.FileName);
        Assert.Contains(scriptPath, exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            TailwindProcessRunner.ExecuteAsync(
                "dotnet",
                ["--info"],
                _tempRoot,
                null,
                null,
                captureLimit: 8192,
                cancellationTokenSource.Token));
    }

    [Fact]
    public async Task ExecuteAsync_LeavesCapturedOutputEmpty_WhenCaptureLimitIsZero()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var scriptPath = await WriteUnixScriptAsync(
            "uncaptured-tailwind",
            """
            #!/bin/sh
            printf 'stdout'
            printf 'stderr' >&2
            exit 0
            """);

        var result = await TailwindProcessRunner.ExecuteAsync(
            scriptPath,
            [],
            _tempRoot,
            null,
            null,
            captureLimit: 0,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    private async Task<string> WriteUnixScriptAsync(string fileName, string contents)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("These tests write Unix executable scripts.");
        }

        if (Path.IsPathRooted(fileName) || Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException("Script file names must not include directory components.", nameof(fileName));
        }

        var scriptPath = TestPathUtils.PathUnder(_tempRoot, fileName);
        await File.WriteAllTextAsync(scriptPath, contents);
        const UnixFileMode executableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(scriptPath, executableMode);
        return scriptPath;
    }
}
