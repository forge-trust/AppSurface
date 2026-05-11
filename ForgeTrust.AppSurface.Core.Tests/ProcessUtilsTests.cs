using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core.Tests;

public class ProcessUtilsTests
{
    [Fact]
    public async Task ExecuteProcessAsync_CapturesOutput_WhenStreaming()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo stdout) & (echo stderr 1>&2) & exit /b 3",
            "printf 'stdout\\n'; printf 'stderr\\n' >&2; exit 3");

        var result = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            logger,
            CancellationToken.None,
            streamOutput: true);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("stdout", result.Stdout);
        Assert.Contains("stderr", result.Stderr);
        Assert.Contains(logger.Messages, entry => entry.Message.Contains(fileName, StringComparison.Ordinal) && entry.Message.Contains("stdout", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, entry => entry.Message.Contains(fileName, StringComparison.Ordinal) && entry.Message.Contains("stderr", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteProcessAsync_CapturesOutput_WhenNotStreaming()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo steady) & exit /b 0",
            "printf 'steady\\n'");

        var result = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            logger,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("steady", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
    }

    [Fact]
    public async Task ExecuteProcessAsync_ThrowsOperationCanceledException_WhenCanceled()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "ping -n 30 127.0.0.1 >NUL",
            "sleep 30");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                args,
                Directory.GetCurrentDirectory(),
                logger,
                cts.Token));
    }

    [Fact]
    public async Task ExecuteProcessAsync_ThrowsOperationCanceledException_WhenCanceled_WithStreaming()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo streamed) & ping -n 30 127.0.0.1 >NUL",
            "printf 'streamed\\n'; sleep 30");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                args,
                Directory.GetCurrentDirectory(),
                logger,
                cts.Token,
                streamOutput: true));
    }

    [Fact]
    public async Task ExecuteProcessAsync_ThrowsOperationCanceledException_WhenStreamingCanceledAfterOutputStarts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logger = new CancelingLogger(cts, fileNameFilter: null);
        var (fileName, args) = CreateShellCommand(
            "for /L %i in (1,1,5000) do @echo line%i",
            "i=1; while [ \"$i\" -le 5000 ]; do printf 'line%04d\\n' \"$i\"; i=$((i+1)); done");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                args,
                Directory.GetCurrentDirectory(),
                logger,
                cts.Token,
                streamOutput: true));

        Assert.True(logger.WasCanceled, "Expected the logger to cancel after streaming output started.");
    }

    [Fact]
    public async Task StreamToLoggerAsync_ThrowsAndSkipsIncompleteLine_WhenCanceledDuringDrain()
    {
        using var cts = new CancellationTokenSource();
        var logger = new ListLogger();
        var reader = new ScriptedTextReader(
            ReadChunk("done\npartial"),
            CancelOnRead(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessUtils.StreamToLoggerAsync(
                reader,
                logger,
                LogLevel.Information,
                "test-process",
                cts.Token));

        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Information
                && entry.Message.Contains("test-process", StringComparison.Ordinal)
                && entry.Message.Contains("done", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            entry => entry.Message.Contains("partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamToLoggerAsync_PreservesCarriageReturns_AndLogsWithoutFileNamePrefix()
    {
        var logger = new ListLogger();
        var reader = new ScriptedTextReader(ReadChunk("alpha\r\nbeta\r"));

        var output = await ProcessUtils.StreamToLoggerAsync(
            reader,
            logger,
            LogLevel.Information,
            string.Empty,
            CancellationToken.None);

        Assert.Equal("alpha\r\nbeta\r", output);
        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Information && entry.Message == "alpha");
        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Information && entry.Message == "beta");
        Assert.DoesNotContain(
            logger.Messages,
            entry => entry.Message.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamToLoggerAsync_DoesNotLogExtraTrailingLine_WhenInputEndsWithNewline()
    {
        var logger = new ListLogger();
        var reader = new ScriptedTextReader(ReadChunk("alpha\n"));

        var output = await ProcessUtils.StreamToLoggerAsync(
            reader,
            logger,
            LogLevel.Information,
            "test-process",
            CancellationToken.None);

        Assert.Equal("alpha\n", output);
        Assert.Single(logger.Messages, entry => entry.Message.Contains("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamToLoggerAsync_LogsFinalLine_WhenInputEndsWithoutNewline()
    {
        var logger = new ListLogger();
        var reader = new ScriptedTextReader(ReadChunk("alpha"));

        var output = await ProcessUtils.StreamToLoggerAsync(
            reader,
            logger,
            LogLevel.Information,
            "test-process",
            CancellationToken.None);

        Assert.Equal("alpha", output);
        Assert.Single(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Information
                && entry.Message.Contains("test-process", StringComparison.Ordinal)
                && entry.Message.Contains("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteProcessAsync_UsesConfiguredStderrLogLevelSelector_WhenStreaming()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo remapped 1>&2) & exit /b 0",
            "printf 'remapped\\n' >&2");

        var result = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            logger,
            CancellationToken.None,
            streamOutput: true,
            stderrLogLevelSelector: _ => LogLevel.Warning);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("remapped", result.Stderr);
        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Warning
                && entry.Message.Contains(fileName, StringComparison.Ordinal)
                && entry.Message.Contains("remapped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteProcessAsync_PropagatesExceptionFromStderrLogLevelSelector_WhenStreaming()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo boom 1>&2) & exit /b 0",
            "printf 'boom\\n' >&2");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                args,
                Directory.GetCurrentDirectory(),
                logger,
                CancellationToken.None,
                streamOutput: true,
                stderrLogLevelSelector: _ => throw new InvalidOperationException("selector failure")));

        Assert.Equal("selector failure", exception.Message);
    }

    [Fact]
    public async Task ExecuteProcessAsync_CapturesMultipleStreamedLines_InOrder()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo first) & (echo second) & exit /b 0",
            "printf 'first\\nsecond\\n'");

        var result = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            logger,
            CancellationToken.None,
            streamOutput: true);

        Assert.Equal(GetPlatformMultilineOutput("first", "second"), result.Stdout);
        Assert.Contains(logger.Messages, entry => entry.Message.Contains("first", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, entry => entry.Message.Contains("second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteProcessAsync_PreservesExactOutput_WhenStreamingAndNonStreaming()
    {
        var streamingLogger = new ListLogger();
        var nonStreamingLogger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo first) & (echo second) & exit /b 0",
            "printf 'first\\nsecond\\n'");

        var streamingResult = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            streamingLogger,
            CancellationToken.None,
            streamOutput: true);

        var nonStreamingResult = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            nonStreamingLogger,
            CancellationToken.None);

        Assert.Equal(GetPlatformMultilineOutput("first", "second"), streamingResult.Stdout);
        Assert.Equal(streamingResult.Stdout, nonStreamingResult.Stdout);
    }

    [Fact]
    public async Task ExecuteProcessAsync_AllowsEmptyArgumentArray()
    {
        var logger = new ListLogger();
        var result = await ProcessUtils.ExecuteProcessAsync(
            "dotnet",
            [],
            Directory.GetCurrentDirectory(),
            logger,
            CancellationToken.None);

        Assert.True(
            !string.IsNullOrWhiteSpace(result.Stdout) || !string.IsNullOrWhiteSpace(result.Stderr),
            "Expected dotnet without arguments to produce console output.");
    }

    [Fact]
    public async Task ExecuteProcessAsync_PropagatesStreamingFailures_WhenLoggerThrows()
    {
        var logger = new ThrowingLogger(LogLevel.Information);
        var (fileName, args) = CreateShellCommand(
            "(echo stdout) & exit /b 0",
            "printf 'stdout\\n'");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                args,
                Directory.GetCurrentDirectory(),
                logger,
                CancellationToken.None,
                streamOutput: true));
    }

    [Fact]
    public async Task ExecuteProcessAsync_PropagatesStreamingFailures_BeforeProcessExit()
    {
        var logger = new ThrowingLogger(LogLevel.Information);
        var (fileName, args) = CreateShellCommand(
            "(echo stdout) & ping -n 6 127.0.0.1 >NUL & exit /b 0",
            "printf 'stdout\\n'; sleep 5");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                args,
                Directory.GetCurrentDirectory(),
                logger,
                cts.Token,
                streamOutput: true));
    }

    [Fact]
    public async Task ExecuteProcessAsync_LogsAndThrows_WhenProcessCannotStart()
    {
        var logger = new ListLogger();
        var fileName = $"no-such-executable-{Guid.NewGuid():N}";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                [],
                Directory.GetCurrentDirectory(),
                logger,
                CancellationToken.None));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Error
                && entry.Message.Contains(fileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteProcessAsync_LogsAndThrows_WhenProcessStartReturnsFalse()
    {
        var logger = new ListLogger();
        const string fileName = "synthetic-process";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessUtils.ExecuteProcessAsync(
                fileName,
                [],
                Directory.GetCurrentDirectory(),
                logger,
                CancellationToken.None,
                streamOutput: false,
                stderrLogLevelSelector: null,
                hooks: new ProcessUtils.ProcessExecutionHooks
                {
                    StartProcessOverride = _ => false
                }));

        Assert.Equal($"Failed to start process: {fileName}", exception.Message);
        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Error
                && entry.Message.Contains(fileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteProcessAsync_LogsDebug_WhenCleanupKillThrows()
    {
        var logger = new ListLogger();
        var (fileName, args) = CreateShellCommand(
            "(echo steady) & exit /b 0",
            "printf 'steady\\n'");

        var result = await ProcessUtils.ExecuteProcessAsync(
            fileName,
            args,
            Directory.GetCurrentDirectory(),
            logger,
            CancellationToken.None,
            streamOutput: false,
            stderrLogLevelSelector: null,
            hooks: new ProcessUtils.ProcessExecutionHooks
            {
                HasExitedOverride = _ => false,
                KillProcessOverride = _ => throw new InvalidOperationException("kill failure")
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            logger.Messages,
            entry => entry.LogLevel == LogLevel.Debug
                && entry.Exception?.Message == "kill failure");
    }

    private static (string FileName, IReadOnlyList<string> Args) CreateShellCommand(string windowsScript, string unixScript)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd", ["/c", windowsScript]);
        }

        return ("/bin/sh", ["-c", unixScript]);
    }

    private static string GetPlatformMultilineOutput(params string[] lines)
    {
        var newline = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r\n" : "\n";
        return string.Join(newline, lines) + newline;
    }

    private sealed class ListLogger : ILogger
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
            Messages.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);

    private sealed class ScriptedTextReader(params Func<Memory<char>, CancellationToken, ValueTask<int>>[] steps) : TextReader
    {
        private readonly Queue<Func<Memory<char>, CancellationToken, ValueTask<int>>> _steps = new(steps);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_steps.Count == 0)
            {
                return 0;
            }

            return await _steps.Dequeue()(buffer, cancellationToken);
        }
    }

    private sealed class CancelingLogger(CancellationTokenSource cts, string? fileNameFilter) : ILogger
    {
        public bool WasCanceled { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (WasCanceled)
            {
                return;
            }

            var message = formatter(state, exception);
            if (fileNameFilter == null || message.Contains(fileNameFilter, StringComparison.Ordinal))
            {
                WasCanceled = true;
                cts.Cancel();
            }
        }
    }

    private static Func<Memory<char>, CancellationToken, ValueTask<int>> ReadChunk(string value)
    {
        return (buffer, _) =>
        {
            value.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(value.Length);
        };
    }

    private static Func<Memory<char>, CancellationToken, ValueTask<int>> CancelOnRead(CancellationTokenSource cts)
    {
        return (_, cancellationToken) =>
        {
            cts.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        };
    }

    private sealed class ThrowingLogger(LogLevel logLevelToThrow) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == logLevelToThrow)
            {
                throw new InvalidOperationException("Logger failure");
            }
        }
    }
}
