using System.Collections.Concurrent;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Console.Tests;

[Collection(CommandServiceStateCollection.Name)]
public class ConsoleOutputContractTests
{
    [Fact]
    public async Task RunAsync_DefaultOutputMode_EmitsLifecycleLogsAndProgress()
    {
        var capture = await RunWithCapturedOutputAsync(["logged-progress"]);

        Assert.Equal(0, capture.ExitCode);
        Assert.Contains("Visible progress", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Initializing Critical Service", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Stopping Critical Service", capture.AllText, StringComparison.Ordinal);
        Assert.Contains("Run Exited - Shutting down", capture.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CommandFirstOutputMode_SuppressesLifecycleLogsButKeepsProgress()
    {
        var capture = await RunWithCapturedOutputAsync(
            ["logged-progress"],
            options => { options.OutputMode = ConsoleOutputMode.CommandFirst; });

        Assert.Equal(0, capture.ExitCode);
        Assert.Contains("Visible progress", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Initializing Critical Service", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping Critical Service", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application is shutting down", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hosting environment", capture.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CommandFirstOutputMode_KeepsWarningsVisible()
    {
        var capture = await RunWithCapturedOutputAsync(
            ["warn-progress"],
            options => { options.OutputMode = ConsoleOutputMode.CommandFirst; });

        Assert.Equal(0, capture.ExitCode);
        Assert.Contains("Visible warning", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Initializing Critical Service", capture.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping Critical Service", capture.AllText, StringComparison.Ordinal);
    }

    private static async Task<CapturedRun> RunWithCapturedOutputAsync(
        string[] args,
        Action<ConsoleOptions>? configureOptions = null)
    {
        var console = new FakeInMemoryConsole();
        var loggerProvider = new InMemoryLoggerProvider();
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await ConsoleApp<LoggingModule>.RunAsync(
                args,
                options =>
                {
                    options.CustomRegistrations.Add(services =>
                    {
                        services.AddSingleton<IConsole>(console);
                        services.AddSingleton<ILoggerProvider>(loggerProvider);
                    });

                    configureOptions?.Invoke(options);
                });

            return new CapturedRun(
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

    private sealed record CapturedRun(
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

    [Command("logged-progress", Description = "Logs command-owned progress for console output contract tests.")]
    public class LoggedProgressCommand(ILogger<LoggedProgressCommand> logger) : ICommand
    {
        public ValueTask ExecuteAsync(IConsole console)
        {
            logger.LogInformation("Visible progress");
            return ValueTask.CompletedTask;
        }
    }

    [Command("warn-progress", Description = "Logs a warning for console output contract tests.")]
    public class WarnProgressCommand(ILogger<WarnProgressCommand> logger) : ICommand
    {
        public ValueTask ExecuteAsync(IConsole console)
        {
            logger.LogWarning("Visible warning");
            return ValueTask.CompletedTask;
        }
    }

    public class LoggingModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services)
        {
            // ConsoleStartup scans the whole test assembly for ICommand implementations.
            // Register the shared tracker so unrelated test commands remain constructible.
            services.AddSingleton<ExecutionTracker>();
        }

        public void RegisterDependentModules(ModuleDependencyBuilder builder)
        {
        }

        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder)
        {
        }

        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder)
        {
        }
    }
}
