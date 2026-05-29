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

        var scriptPath = Path.Join(_tempRoot, fileName);
        await File.WriteAllTextAsync(scriptPath, contents);
        const UnixFileMode executableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(scriptPath, executableMode);
        return scriptPath;
    }
}
