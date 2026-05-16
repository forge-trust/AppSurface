using System.Collections.Concurrent;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class ProgramEntryPointTests
{
    [Fact]
    public async Task EntryPoint_Should_Print_Root_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Docs_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["docs", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Preview RazorDocs for a repository.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--repo", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--strict", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--startup-timeout-seconds", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsCommand_Should_Forward_RazorDocs_Host_Arguments()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            [
                "docs",
                "--repo", repository.Path,
                "--urls", "http://127.0.0.1:5189",
                "--strict",
                "--route-root", "/reference",
                "--docs-root", "/reference/next",
                "--environment", "Development"
            ],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Contains("--RazorDocs:Source:RepositoryRoot", runner.Args);
        Assert.Contains(repository.Path, runner.Args);
        Assert.Contains("--urls", runner.Args);
        Assert.Contains("http://127.0.0.1:5189", runner.Args);
        Assert.Contains("--RazorDocs:Harvest:FailOnFailure", runner.Args);
        Assert.Contains("true", runner.Args);
        Assert.Contains("--RazorDocs:Routing:RouteRootPath", runner.Args);
        Assert.Contains("/reference", runner.Args);
        Assert.Contains("--RazorDocs:Routing:DocsRootPath", runner.Args);
        Assert.Contains("/reference/next", runner.Args);
        Assert.Contains("--environment", runner.Args);
        Assert.Contains("Development", runner.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.StartupTimeout);
    }

    [Fact]
    public async Task DocsPreviewAlias_Should_Forward_RazorDocs_Host_Arguments()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "preview", "--repo", repository.Path, "--port", "5189"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Contains("--RazorDocs:Source:RepositoryRoot", runner.Args);
        Assert.Contains(repository.Path, runner.Args);
        Assert.Contains("--port", runner.Args);
        Assert.Contains("5189", runner.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.StartupTimeout);
    }

    [Fact]
    public async Task DocsCommand_Should_Default_ToDevelopmentEnvironment_ForLocalPreview()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var environmentIndex = Array.IndexOf(runner.Args, "--environment");
        Assert.InRange(environmentIndex, 0, runner.Args.Length - 2);
        Assert.Equal("Development", runner.Args[environmentIndex + 1]);
        Assert.DoesNotContain("--urls", runner.Args);
        Assert.DoesNotContain("--port", runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Preserve_Explicit_Environment()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--environment", "Production"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        var environmentIndex = Array.IndexOf(runner.Args, "--environment");
        Assert.InRange(environmentIndex, 0, runner.Args.Length - 2);
        Assert.Equal("Production", runner.Args[environmentIndex + 1]);
    }

    [Fact]
    public async Task DocsCommand_Should_Allow_Disabling_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--startup-timeout-seconds", "0"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Null(runner.StartupTimeout);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_Blank_RepositoryRoot()
    {
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", " "],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --repo value must point to a repository directory.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task DocsCommand_Should_Reject_Invalid_Startup_Timeout(string timeout)
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--startup-timeout-seconds", timeout],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The --startup-timeout-seconds value must be a finite number greater than or equal to 0.",
            result.AllText,
            StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_Oversized_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--startup-timeout-seconds", "1E+300"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The --startup-timeout-seconds value must be less than or equal to",
            result.AllText,
            StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    public async Task DocsCommand_Should_Reject_Invalid_Port(string port)
    {
        using var repository = TempDirectory.Create("appsurface-docs-repo-");
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", repository.Path, "--port", port],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --port value must be between 1 and 65535.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsCommand_Should_Reject_Missing_RepositoryRoot()
    {
        var missingRepository = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var runner = new CapturingRazorDocsHostRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "--repo", missingRepository],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(missingRepository, result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public void PushConfigureOptionsOverrideForTests_Should_Throw_When_ConfigureOptions_IsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ProgramEntryPoint.PushConfigureOptionsOverrideForTests(null!));
    }

    [Fact]
    public async Task ProgramEntryPoint_Should_Apply_Direct_And_Test_ConfigureOptions_InOrder()
    {
        var calls = new List<string>();
        using var overrideScope = ProgramEntryPoint.PushConfigureOptionsOverrideForTests(
            _ => calls.Add("override"));

        await ProgramEntryPoint.RunAsync(
            ["--help"],
            _ => calls.Add("direct"));

        Assert.Equal(["direct", "override"], calls);
    }

    [Fact]
    public void AppSurfaceCliModule_NoOpHooks_DoNotThrow()
    {
        var module = new AppSurfaceCliModule();
        var context = new StartupContext([], module);
        var services = new ServiceCollection();
        var builder = Host.CreateDefaultBuilder();
        var dependencies = new ModuleDependencyBuilder();

        module.ConfigureServices(context, services);
        module.ConfigureHostBeforeServices(context, builder);
        module.ConfigureHostAfterServices(context, builder);
        module.RegisterDependentModules(dependencies);
    }

    private static async Task<CapturedCliRun> InvokeEntryPointAsync(
        string[] args,
        Action<ConsoleOptions>? configureOptions = null)
    {
        var console = new FakeInMemoryConsole();
        var loggerProvider = new InMemoryLoggerProvider();
        var originalExitCode = Environment.ExitCode;
        var originalStdout = System.Console.Out;
        var originalStderr = System.Console.Error;
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
            System.Console.SetOut(rawStdoutWriter);
            System.Console.SetError(rawStderrWriter);
            await ProgramEntryPoint.RunAsync(
                args,
                options =>
                {
                    AddCaptureServices(options, console, loggerProvider);
                    configureOptions?.Invoke(options);
                });

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
            System.Console.SetOut(originalStdout);
            System.Console.SetError(originalStderr);
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

    private static void RegisterRunner(ConsoleOptions options, CapturingRazorDocsHostRunner runner)
    {
        options.CustomRegistrations.Add(services => services.AddSingleton<IRazorDocsHostRunner>(runner));
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

    private sealed class CapturingRazorDocsHostRunner : IRazorDocsHostRunner
    {
        public string[]? Args { get; private set; }

        public TimeSpan? StartupTimeout { get; private set; }

        public Task RunAsync(string[] args, TimeSpan? startupTimeout, CancellationToken cancellationToken)
        {
            Args = args;
            StartupTimeout = startupTimeout;
            return Task.CompletedTask;
        }
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

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create(string prefix)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
