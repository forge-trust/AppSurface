using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FakeItEasy;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Web.Tailwind;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Tailwind.Tests;

public class TailwindWatchServiceTests : IDisposable
{
    private static readonly string TestContentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tailwind-watch-tests"));
    private readonly TailwindCliManager _cliManager;
    private readonly IOptions<TailwindOptions> _options;
    private readonly ILogger<TailwindWatchService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly TailwindOptions _tailwindOptions;

    public TailwindWatchServiceTests()
    {
        _cliManager = A.Fake<TailwindCliManager>(x => x.WithArgumentsForConstructor([A.Fake<ILogger<TailwindCliManager>>()]));
        _tailwindOptions = new TailwindOptions
        {
            Enabled = true,
            InputPath = "input.css",
            OutputPath = "output.css"
        };
        _options = Options.Create(_tailwindOptions);
        _logger = A.Fake<ILogger<TailwindWatchService>>();
        _environment = A.Fake<IHostEnvironment>();

        A.CallTo(() => _environment.EnvironmentName).Returns(Environments.Development);
        A.CallTo(() => _environment.ContentRootPath).Returns(TestContentRoot);
        A.CallTo(() => _cliManager.GetTailwindPath()).Returns("/path/to/tailwind");
    }

    public void Dispose()
    {
        TailwindCliManager.IsOSPlatformOverride = null;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEarly_IfNotDevelopment()
    {
        // Arrange
        A.CallTo(() => _environment.EnvironmentName).Returns(Environments.Production);
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        // Act
        await service.ExecuteAsyncPublic(CancellationToken.None);

        // Assert
        Assert.False(service.ProcessExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEarly_IfDisabled()
    {
        // Arrange
        _tailwindOptions.Enabled = false;
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        // Act
        await service.ExecuteAsyncPublic(CancellationToken.None);

        // Assert
        Assert.False(service.ProcessExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_StartsProcess_WithCorrectArgs()
    {
        // Arrange
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);
        service.ResultToReturn = new CommandResult(0, "", "");

        // Act
        await service.ExecuteAsyncPublic(CancellationToken.None);

        // Assert
        Assert.True(service.ProcessExecuted);
        Assert.Equal("/path/to/tailwind", service.ExecutedFileName);
        Assert.NotNull(service.ExecutedArgs);
        Assert.Contains("-i", service.ExecutedArgs);
        Assert.Contains("input.css", service.ExecutedArgs);
        Assert.Contains("-o", service.ExecutedArgs);
        Assert.Contains("output.css", service.ExecutedArgs);
        Assert.Contains("--watch", service.ExecutedArgs);
    }

    [Fact]
    public async Task ExecuteAsync_LogsError_OnNonZeroExitCode()
    {
        // Arrange
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);
        service.ResultToReturn = new CommandResult(1, "", "error");

        // Act
        await service.ExecuteAsyncPublic(CancellationToken.None);

        // Assert
        Assert.True(service.ProcessExecuted);
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log"
                && call.Arguments.Count > 0
                && Equals(call.Arguments[0], LogLevel.Error))
            .MustHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_LogsError_WhenInputPathIsInvalid(string? inputPath)
    {
        _tailwindOptions.InputPath = inputPath!;
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.False(service.ProcessExecuted);
        AssertErrorLogged();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_LogsError_WhenOutputPathIsInvalid(string? outputPath)
    {
        _tailwindOptions.OutputPath = outputPath!;
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.False(service.ProcessExecuted);
        AssertErrorLogged();
    }

    [Fact]
    public async Task ExecuteAsync_LogsError_WhenInputAndOutputResolveToSameFile()
    {
        _tailwindOptions.InputPath = Path.Combine("styles", "..", "app.css");
        _tailwindOptions.OutputPath = "app.css";
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.False(service.ProcessExecuted);
        AssertErrorLogged();
        A.CallTo(() => _cliManager.GetTailwindPath()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_LogsError_WhenInputAndOutputDifferOnlyByCase_OnCaseInsensitiveHosts()
    {
        _tailwindOptions.InputPath = "Styles/APP.CSS";
        _tailwindOptions.OutputPath = "styles/app.css";
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment)
        {
            HostPathsAreCaseInsensitiveOverride = true
        };

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.False(service.ProcessExecuted);
        AssertErrorLogged();
        A.CallTo(() => _cliManager.GetTailwindPath()).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_StartsWatchProcess_WhenInputAndOutputDifferOnlyByCase_OnCaseSensitiveHosts()
    {
        _tailwindOptions.InputPath = "Styles/APP.CSS";
        _tailwindOptions.OutputPath = "styles/app.css";
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment)
        {
            HostPathsAreCaseInsensitiveOverride = false
        };

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.True(service.ProcessExecuted);
        AssertErrorNotLogged();
        A.CallTo(() => _cliManager.GetTailwindPath()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotLogError_WhenWatchProcessIsCanceled()
    {
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment)
        {
            ExceptionToThrow = new OperationCanceledException()
        };

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.True(service.ProcessExecuted);
        AssertErrorNotLogged();
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotLogError_ForNonZeroExitCode_WhenCancellationRequested()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment)
        {
            ResultToReturn = new CommandResult(1, string.Empty, "error")
        };

        await service.ExecuteAsyncPublic(cancellationTokenSource.Token);

        Assert.True(service.ProcessExecuted);
        AssertErrorNotLogged();
    }

    [Fact]
    public async Task ExecuteAsync_LogsError_WhenTailwindPathResolutionThrows()
    {
        A.CallTo(() => _cliManager.GetTailwindPath()).Throws(new InvalidOperationException("boom"));
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.False(service.ProcessExecuted);
        AssertErrorLogged();
    }

    [Fact]
    public async Task ExecuteAsync_LogsWarning_WhenTailwindCliIsMissing()
    {
        A.CallTo(() => _cliManager.GetTailwindPath()).Throws(new FileNotFoundException("missing tailwind"));
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.False(service.ProcessExecuted);
        AssertErrorNotLogged();
        AssertWarningLogged();
    }

    [Fact]
    public async Task ExecuteAsync_LogsWarning_WhenResolvedTailwindExecutableIsMissing()
    {
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment)
        {
            ExceptionToThrow = new FileNotFoundException("missing executable")
        };

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.True(service.ProcessExecuted);
        AssertErrorNotLogged();
        AssertWarningLogged();
    }

    [Fact]
    public async Task ExecuteAsync_UsesCmdLauncher_ForWindowsCmdShim()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;
        A.CallTo(() => _cliManager.GetTailwindPath()).Returns(@"C:\tools\tailwindcss.cmd");
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.True(service.ProcessExecuted);
        Assert.Equal("cmd.exe", service.ExecutedFileName);
        Assert.Equal(
            ["/d", "/c", @"C:\tools\tailwindcss.cmd", "-i", "input.css", "-o", "output.css", "--watch"],
            service.ExecutedArgs);
    }

    [Fact]
    public async Task ExecuteAsync_UsesPowerShellLauncher_ForWindowsPowerShellShim()
    {
        TailwindCliManager.IsOSPlatformOverride = platform => platform == OSPlatform.Windows;
        A.CallTo(() => _cliManager.GetTailwindPath()).Returns(@"C:\tools\tailwindcss.ps1");
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment);

        await service.ExecuteAsyncPublic(CancellationToken.None);

        Assert.True(service.ProcessExecuted);
        Assert.Equal("powershell.exe", service.ExecutedFileName);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\tools\tailwindcss.ps1", "-i", "input.css", "-o", "output.css", "--watch"],
            service.ExecutedArgs);
    }

    [Theory]
    [InlineData(true, StringComparison.OrdinalIgnoreCase)]
    [InlineData(false, StringComparison.Ordinal)]
    public void GetPathComparison_UsesHostCaseSensitivity(bool hostPathsAreCaseInsensitive, StringComparison expectedComparison)
    {
        var service = new TestTailwindWatchService(_cliManager, _options, _logger, _environment)
        {
            HostPathsAreCaseInsensitiveOverride = hostPathsAreCaseInsensitive
        };

        var result = service.GetPathComparison();

        Assert.Equal(expectedComparison, result);
    }

    [Fact]
    public async Task ExecuteTailwindProcessAsync_ReturnsCommandResult()
    {
        var service = new TailwindWatchService(_cliManager, _options, _logger, _environment);
        var (fileName, args) = CreateShellCommand(
            "(echo watch-out) & (echo watch-err 1>&2) & exit /b 2",
            "printf 'watch-out\\n'; printf 'watch-err\\n' >&2; exit 2");

        var result = await service.ExecuteTailwindProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("watch-out", result.Stdout);
        Assert.Contains("watch-err", result.Stderr);
    }

    [Fact]
    public async Task ExecuteTailwindProcessAsync_DowngradesBenignTailwindStderrOutput()
    {
        var logger = new ListLogger<TailwindWatchService>();
        var service = new TailwindWatchService(_cliManager, _options, logger, _environment);
        const string banner = "\u2248 tailwindcss v4.1.18";
        var (fileName, args) = CreateShellCommand(
            $"(echo {banner} 1>&2) & (echo. 1>&2) & (echo Done in 34ms 1>&2) & (echo Error: boom 1>&2) & exit /b 1",
            $"printf '{banner}\\n' >&2; printf '\\n' >&2; printf 'Done in 34ms\\n' >&2; printf 'Error: boom\\n' >&2; exit 1");

        var result = await service.ExecuteTailwindProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(logger.Messages, entry => entry.LogLevel == LogLevel.Information && entry.Message.Contains("tailwindcss v4.1.18", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, entry => entry.LogLevel == LogLevel.Information && entry.Message.Contains("Done in 34ms", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, entry => entry.LogLevel == LogLevel.Debug);
        Assert.Contains(logger.Messages, entry => entry.LogLevel == LogLevel.Error && entry.Message.Contains("Error: boom", StringComparison.Ordinal));
    }

    private void AssertErrorLogged()
    {
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log"
                && call.Arguments.Count > 0
                && Equals(call.Arguments[0], LogLevel.Error))
            .MustHaveHappened();
    }

    private void AssertErrorNotLogged()
    {
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log"
                && call.Arguments.Count > 0
                && Equals(call.Arguments[0], LogLevel.Error))
            .MustNotHaveHappened();
    }

    private void AssertWarningLogged()
    {
        A.CallTo(_logger)
            .Where(call => call.Method.Name == "Log"
                && call.Arguments.Count > 0
                && Equals(call.Arguments[0], LogLevel.Warning))
            .MustHaveHappened();
    }

    private static (string FileName, IReadOnlyList<string> Args) CreateShellCommand(string windowsScript, string unixScript)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd", ["/c", windowsScript]);
        }

        return ("/bin/sh", ["-c", unixScript]);
    }

    private class TestTailwindWatchService : TailwindWatchService
    {
        public bool ProcessExecuted { get; private set; }
        public string? ExecutedFileName { get; private set; }
        public IReadOnlyList<string>? ExecutedArgs { get; private set; }
        public CommandResult ResultToReturn { get; set; } = new CommandResult(0, "", "");
        public Exception? ExceptionToThrow { get; set; }
        public bool? HostPathsAreCaseInsensitiveOverride { get; set; }

        public TestTailwindWatchService(
            TailwindCliManager cliManager,
            IOptions<TailwindOptions> options,
            ILogger<TailwindWatchService> logger,
            IHostEnvironment environment)
            : base(cliManager, options, logger, environment)
        {
        }

        public Task ExecuteAsyncPublic(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        internal override Task<CommandResult> ExecuteTailwindProcessAsync(
            string fileName,
            IReadOnlyList<string> args,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            ProcessExecuted = true;
            ExecutedFileName = fileName;
            ExecutedArgs = args;

            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(ResultToReturn);
        }

        internal override bool HostPathsAreCaseInsensitive()
        {
            return HostPathsAreCaseInsensitiveOverride ?? base.HostPathsAreCaseInsensitive();
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message);
}
