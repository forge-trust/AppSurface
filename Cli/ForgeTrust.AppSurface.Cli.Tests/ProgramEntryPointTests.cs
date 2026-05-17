using System.Collections.Concurrent;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.RazorWire.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.Contains("docs export", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryPoint_Should_Print_Docs_Help_Without_Lifecycle_Noise()
    {
        var result = await InvokeEntryPointAsync(["docs", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Preview RazorDocs for a repository.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("docs export", result.AllText, StringComparison.Ordinal);
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
    public async Task EntryPoint_Should_Print_Docs_Export_Help_Without_Preview_Listener_Options()
    {
        var result = await InvokeEntryPointAsync(["docs", "export", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Export RazorDocs for a repository to static files.", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--output", result.AllText, StringComparison.Ordinal);
        Assert.Contains("dist/docs", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--mode", result.AllText, StringComparison.Ordinal);
        Assert.Contains("cdn", result.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--seeds", result.AllText, StringComparison.Ordinal);
        Assert.Contains("--strict", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--port", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("--urls", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", result.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Run Exited - Shutting down", result.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Default_Output_Environment_Mode_And_Derived_Seeds()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingRazorDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Equal(Path.GetFullPath("dist/docs"), runner.Args.Value.OutputPath);
        Assert.Equal(ExportMode.Cdn, runner.Args.Value.Mode);
        Assert.Null(runner.Args.Value.SeedRoutesPath);
        Assert.Equal(["/", "/docs"], runner.Args.Value.InitialSeedRoutes);
        Assert.Equal("http://127.0.0.1:0", runner.Args.Value.RequestedBaseUrl);
        Assert.Equal("Production", runner.Args.Value.HostArgs.EnvironmentName);
        Assert.Contains("--environment", runner.Args.Value.HostArgs.Args);
        Assert.Contains("Production", runner.Args.Value.HostArgs.Args);
        Assert.DoesNotContain("--urls", runner.Args.Value.HostArgs.Args);
        Assert.DoesNotContain("--port", runner.Args.Value.HostArgs.Args);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Args.Value.HostArgs.StartupTimeout);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Forward_Explicit_Export_Arguments()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-output-");
        var seedFile = System.IO.Path.Combine(repository.Path, "seeds.txt");
        await File.WriteAllLinesAsync(seedFile, ["/", "/reference/next"]);
        var runner = new CapturingRazorDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            [
                "docs", "export",
                "-r", repository.Path,
                "--output", output.Path,
                "--mode", "hybrid",
                "--seeds", seedFile,
                "--strict",
                "--route-root", "/reference",
                "--docs-root", "/reference/next",
                "--environment", "Development",
                "--startup-timeout-seconds", "2"
            ],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Equal(output.Path, runner.Args.Value.OutputPath);
        Assert.Equal(ExportMode.Hybrid, runner.Args.Value.Mode);
        Assert.Equal(seedFile, runner.Args.Value.SeedRoutesPath);
        Assert.Null(runner.Args.Value.InitialSeedRoutes);
        Assert.Equal("http://127.0.0.1:0", runner.Args.Value.RequestedBaseUrl);
        Assert.Equal("Development", runner.Args.Value.HostArgs.EnvironmentName);
        Assert.Contains("--RazorDocs:Source:RepositoryRoot", runner.Args.Value.HostArgs.Args);
        Assert.Contains(repository.Path, runner.Args.Value.HostArgs.Args);
        Assert.Contains("--RazorDocs:Harvest:FailOnFailure", runner.Args.Value.HostArgs.Args);
        Assert.Contains("true", runner.Args.Value.HostArgs.Args);
        Assert.Contains("--RazorDocs:Routing:RouteRootPath", runner.Args.Value.HostArgs.Args);
        Assert.Contains("/reference", runner.Args.Value.HostArgs.Args);
        Assert.Contains("--RazorDocs:Routing:DocsRootPath", runner.Args.Value.HostArgs.Args);
        Assert.Contains("/reference/next", runner.Args.Value.HostArgs.Args);
        Assert.Equal(TimeSpan.FromSeconds(2), runner.Args.Value.HostArgs.StartupTimeout);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Derive_Default_Seed_From_Custom_DocsRoot()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingRazorDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--route-root", "/foo/bar", "--docs-root", "/foo/bar/next"],
            options => RegisterRunner(options, runner));

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(runner.Args);
        Assert.Equal(["/", "/foo/bar/next"], runner.Args.Value.InitialSeedRoutes);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Blank_Output()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingRazorDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "--output", " "],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("The --output value must point to an export directory.", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Reject_Seeds_Short_Alias()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingRazorDocsExportRunner();

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path, "-s", "seeds.txt"],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-s", result.AllText, StringComparison.Ordinal);
        Assert.Null(runner.Args);
    }

    [Fact]
    public async Task DocsExportCommand_Should_Translate_Export_Validation_Failures()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        var runner = new CapturingRazorDocsExportRunner
        {
            Exception = new ExportValidationException(
                [new ExportDiagnostic("RWEXPORT004", "Managed URL could not be rewritten.", "/docs")])
        };

        var result = await InvokeProgramEntryPointAsync(
            ["docs", "export", "--repo", repository.Path],
            options => RegisterRunner(options, runner));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("RWEXPORT004", result.AllText, StringComparison.Ordinal);
        Assert.Contains("Managed URL could not be rewritten.", result.AllText, StringComparison.Ordinal);
        Assert.NotNull(runner.Args);
    }

    [Fact]
    public void RazorDocsInProcessExportRunner_Should_Resolve_Single_Bound_BaseUrl()
    {
        var result = RazorDocsInProcessExportRunner.ResolveBoundBaseUrl(["http://127.0.0.1:51234"]);

        Assert.Equal("http://127.0.0.1:51234", result);
    }

    [Fact]
    public void RazorDocsInProcessExportRunner_Should_Reject_Missing_Bound_BaseUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RazorDocsInProcessExportRunner.ResolveBoundBaseUrl(Array.Empty<string>()));

        Assert.Contains("did not publish a listening URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RazorDocsInProcessExportRunner_Should_Reject_Multiple_Bound_BaseUrls()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RazorDocsInProcessExportRunner.ResolveBoundBaseUrl(["http://127.0.0.1:1", "http://127.0.0.1:2"]));

        Assert.Contains("published 2 listening URLs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RazorDocsInProcessExportRunner_Should_Not_Translate_Exporter_Cancellation_To_Startup_Timeout()
    {
        using var repository = TempDirectory.Create("appsurface-docs-export-repo-");
        using var output = TempDirectory.Create("appsurface-docs-export-output-");
        var exporter = new CancelingStaticExporter();
        var runner = new RazorDocsInProcessExportRunner(
            NullLogger<RazorDocsInProcessExportRunner>.Instance,
            exporter);
        var hostArgs = new RazorDocsHostArgs(
            repository.Path,
            [
                "--RazorDocs:Source:RepositoryRoot",
                repository.Path,
                "--environment",
                "Production"
            ],
            TimeSpan.FromSeconds(30),
            "Production");
        var exportArgs = new RazorDocsExportArgs(
            hostArgs,
            output.Path,
            SeedRoutesPath: null,
            InitialSeedRoutes: ["/"],
            ExportMode.Cdn,
            "http://127.0.0.1:0");

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.ExportAsync(exportArgs, CancellationToken.None));

        Assert.Equal("Export canceled after startup.", ex.Message);
        Assert.NotNull(exporter.Context);
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

    private static void RegisterRunner(ConsoleOptions options, CapturingRazorDocsExportRunner runner)
    {
        options.CustomRegistrations.Add(services => services.AddSingleton<IRazorDocsExportRunner>(runner));
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

    private sealed class CapturingRazorDocsExportRunner : IRazorDocsExportRunner
    {
        public RazorDocsExportArgs? Args { get; private set; }

        public Exception? Exception { get; init; }

        public Task ExportAsync(RazorDocsExportArgs args, CancellationToken cancellationToken)
        {
            Args = args;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CancelingStaticExporter : IRazorWireStaticExporter
    {
        public ExportContext? Context { get; private set; }

        public Task ExportAsync(ExportContext context, CancellationToken cancellationToken)
        {
            Context = context;
            throw new OperationCanceledException("Export canceled after startup.");
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
