using System.Collections.Concurrent;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public class ProgramEntryPointTests
{
    [Fact]
    public async Task EntryPoint_Should_Print_Root_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Initializing Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application is shutting down", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hosting environment", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Export_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["export", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Export a RazorWire site to a static directory.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--seeds", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--mode", result.AllText, StringComparison.Ordinal);
        Assert.Contains("cdn", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--routes", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Initializing Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application is shutting down", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hosting environment", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Invalid_Option_Error_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["export", "--definitely-invalid"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--definitely-invalid", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Initializing Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application is shutting down", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hosting environment", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Reject_Routes_Option()
    {
        var result = await InvokeEntryPointAsync(["export", "--routes", "seeds.txt", "--url", "http://localhost:5001"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--routes", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Unrecognized option", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryPoint_Should_Reject_Invalid_Mode()
    {
        var result = await InvokeEntryPointAsync(["export", "--mode", "s3", "--url", "http://localhost:5001"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("s3", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Initializing Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Accept_Hybrid_Mode()
    {
        var missingSeedFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "missing-file.txt");

        var result = await InvokeEntryPointAsync(
            ["export", "--mode", "hybrid", "--seeds", missingSeedFile, "--url", "http://localhost:5001"],
            options =>
            {
                options.CustomRegistrations.Add(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(
                        new TestHttpHelpers.Factory(TestHttpHelpers.UrlAwareHtmlRoot("http://localhost:5001")));
                });
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(missingSeedFile, result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid value", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unrecognized option", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryPoint_Should_Accept_Seeds_Option()
    {
        var missingSeedFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "missing-file.txt");

        var result = await InvokeEntryPointAsync(
            ["export", "--seeds", missingSeedFile, "--url", "http://localhost:5001"],
            options =>
            {
                options.CustomRegistrations.Add(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(
                        new TestHttpHelpers.Factory(TestHttpHelpers.UrlAwareHtmlRoot("http://localhost:5001")));
                });
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(missingSeedFile, result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrecognized option", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Missing_Source_Validation_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["export"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exactly one source", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("razorwire export --help", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--project ./MyApp.csproj", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Initializing Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping Critical Service", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application is shutting down", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hosting environment", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Main_Should_Delegate_To_RazorWireCliApp()
    {
        var overrideApplied = false;

        var result = await InvokeEntryPointAsync(
            ["--help"],
            _ => { overrideApplied = true; });

        Assert.True(overrideApplied);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryPoint_Main_Should_Capture_Raw_SystemConsole_Output()
    {
        const string rawStdout = "raw stdout marker";
        const string rawStderr = "raw stderr marker";

        var result = await InvokeEntryPointAsync(
            ["--help"],
            _ =>
            {
                global::System.Console.Out.Write(rawStdout);
                global::System.Console.Error.Write(rawStderr);
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(rawStdout, result.AllText, StringComparison.Ordinal);
        Assert.Contains(rawStderr, result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramEntryPoint_RunAsync_WithExplicitConfigureOptions_WorksWithoutTestOverride()
    {
        var explicitConfigureApplied = false;

        var result = await InvokeProgramEntryPointAsync(
            ["--help"],
            options => { explicitConfigureApplied = true; });

        Assert.True(explicitConfigureApplied);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProgramEntryPoint_RunAsync_WithExplicitConfigureOptions_AndTestOverride_AppliesBoth()
    {
        var explicitConfigureApplied = false;
        var overrideApplied = false;
        using var overrideScope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(
            options =>
            {
                overrideApplied = true;
            });

        var result = await InvokeProgramEntryPointAsync(
            ["--help"],
            options => { explicitConfigureApplied = true; });

        Assert.True(explicitConfigureApplied);
        Assert.True(overrideApplied);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PushConfigureOptionsOverrideForTests_Should_Throw_When_ConfigureOptions_IsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ProgramEntryPoint.PushConfigureOptionsOverrideForTests(null!));
    }

    private static async Task<CapturedCliRun> InvokeEntryPointAsync(
        string[] args,
        Action<ConsoleOptions>? configureOptions = null)
    {
        var console = new FakeInMemoryConsole();
        var loggerProvider = new InMemoryLoggerProvider();
        var entryPoint = typeof(ExportCommand).Assembly.EntryPoint;
        Assert.NotNull(entryPoint);
        var originalExitCode = Environment.ExitCode;
        var originalStdout = global::System.Console.Out;
        var originalStderr = global::System.Console.Error;
        using var rawStdoutWriter = new StringWriter();
        using var rawStderrWriter = new StringWriter();
        using var overrideScope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(
            options =>
            {
                AddCaptureServices(options, console, loggerProvider);
                configureOptions?.Invoke(options);
            });

        try
        {
            Environment.ExitCode = 0;
            global::System.Console.SetOut(rawStdoutWriter);
            global::System.Console.SetError(rawStderrWriter);
            var invocation = entryPoint!.Invoke(null, [args]);
            if (invocation is Task task)
            {
                await task;
            }

            return new CapturedCliRun(
                rawStdoutWriter.ToString(),
                rawStderrWriter.ToString(),
                console.ReadOutputString(),
                console.ReadErrorString(),
                loggerProvider.GetMessages(),
                Environment.ExitCode);
        }
        finally
        {
            global::System.Console.SetOut(originalStdout);
            global::System.Console.SetError(originalStderr);
            Environment.ExitCode = originalExitCode;
        }
    }

    private static async Task<CapturedCliRun> InvokeProgramEntryPointAsync(
        string[] args,
        Action<ConsoleOptions>? configureOptions = null)
    {
        var console = new FakeInMemoryConsole();
        var loggerProvider = new InMemoryLoggerProvider();
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await ProgramEntryPoint.RunAsync(
                args,
                options =>
                {
                    AddCaptureServices(options, console, loggerProvider);
                    configureOptions?.Invoke(options);
                });

            return new CapturedCliRun(
                string.Empty,
                string.Empty,
                console.ReadOutputString(),
                console.ReadErrorString(),
                loggerProvider.GetMessages(),
                Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    private static void AddCaptureServices(
        ConsoleOptions options,
        FakeInMemoryConsole console,
        InMemoryLoggerProvider loggerProvider)
    {
        options.CustomRegistrations.Add(services =>
        {
            services.AddSingleton<IConsole>(console);
            services.AddSingleton<ILoggerProvider>(loggerProvider);
        });
    }

    private sealed record CapturedCliRun(
        string RawStdout,
        string RawStderr,
        string Stdout,
        string Stderr,
        IReadOnlyList<string> LogMessages,
        int ExitCode)
    {
        public string AllText =>
            string.Join(
                Environment.NewLine,
                new[]
                {
                    RawStdout,
                    RawStderr,
                    Stdout,
                    Stderr,
                    string.Join(Environment.NewLine, LogMessages)
                });
    }

    private sealed class InMemoryLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public ILogger CreateLogger(string categoryName) => new InMemoryLogger(_messages);

        public void Dispose()
        {
        }

        public IReadOnlyList<string> GetMessages() => _messages.ToArray();

        private sealed class InMemoryLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
