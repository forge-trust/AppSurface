using CliFx.Exceptions;
using CliFx.Infrastructure;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorWire.Cli.Tests;

public class ExportCommandTests
{
    [Fact]
    public void Mode_Should_Default_To_Cdn()
    {
        var command = CreateCommand("http://localhost:5001", null, null);

        Assert.Equal(ExportMode.Cdn, command.Mode);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Export_When_Url_Source_Is_Provided()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var logger = A.Fake<ILogger<ExportCommand>>();
            var engineLogger = A.Fake<ILogger<ExportEngine>>();
            var requestFactory = new ExportSourceRequestFactory();
            var processFactory = new NoopProcessFactory();
            var sourceResolver = new ExportSourceResolver(
                A.Fake<ILoggerFactory>(),
                processFactory,
                new TestHttpHelpers.Factory(TestHttpHelpers.UrlAwareHtmlRoot("http://localhost:5001")));
            var engine = new ExportEngine(
                engineLogger,
                new TestHttpHelpers.Factory(TestHttpHelpers.UrlAwareHtmlRoot("http://localhost:5001")));
            var command = new ExportCommand(logger, engine, requestFactory, sourceResolver)
            {
                OutputPath = tempDir,
                BaseUrl = "http://localhost:5001"
            };

            await command.ExecuteAsync(A.Fake<IConsole>());

            Assert.True(File.Exists(Path.Combine(tempDir, "index.html")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_Throw_When_No_Source_Is_Provided()
    {
        var command = CreateCommand(null, null, null);
        var ex = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(A.Fake<IConsole>()));
        Assert.Contains("exactly one source", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Throw_When_Multiple_Sources_Are_Provided()
    {
        var command = CreateCommand("http://localhost:5001", "/tmp/site.csproj", null);

        var ex = await Assert.ThrowsAsync<CommandException>(async () => await command.ExecuteAsync(A.Fake<IConsole>()));
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Choose one source", ex.Message, StringComparison.Ordinal);
        Assert.Contains("razorwire export --help", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WithToken_Should_Respect_Cancellation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var command = CreateCommand("http://localhost:5001", null, null, tempDir);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await command.ExecuteAsync(A.Fake<IConsole>(), cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_Should_Use_SeedRoutesPath()
    {
        var missingSeedFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "seeds.txt");
        var command = CreateCommand(
            "http://localhost:5001",
            null,
            null,
            seedRoutesPath: missingSeedFile);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await command.ExecuteAsync(A.Fake<IConsole>()));

        Assert.Equal(missingSeedFile, ex.FileName);
    }

    private static ExportCommand CreateCommand(
        string? url,
        string? project,
        string? dll,
        string outputPath = "dist",
        string? seedRoutesPath = null)
    {
        var logger = A.Fake<ILogger<ExportCommand>>();
        var engineLogger = A.Fake<ILogger<ExportEngine>>();
        var requestFactory = new ExportSourceRequestFactory();
        var processFactory = new NoopProcessFactory();
        var sourceResolver = new ExportSourceResolver(
            A.Fake<ILoggerFactory>(),
            processFactory,
            new TestHttpHelpers.Factory(TestHttpHelpers.UrlAwareHtmlRoot("http://localhost:5001")));
        var engine = new ExportEngine(
            engineLogger,
            new TestHttpHelpers.Factory(TestHttpHelpers.UrlAwareHtmlRoot("http://localhost:5001")));

        return new ExportCommand(logger, engine, requestFactory, sourceResolver)
        {
            OutputPath = outputPath,
            BaseUrl = url,
            ProjectPath = project,
            DllPath = dll,
            SeedRoutesPath = seedRoutesPath
        };
    }

    private sealed class NoopProcessFactory : ITargetAppProcessFactory
    {
        public ITargetAppProcess Create(ProcessLaunchSpec spec) => new NoopProcess();
    }

    /// <summary>
    /// URL-mode-only test helper process. Its <see cref="HasExited"/> intentionally always returns true.
    /// Do not reuse this for project/dll mode tests because readiness polling expects a running process.
    /// </summary>
    private sealed class NoopProcess : ITargetAppProcess
    {
        public event Action<string>? OutputLineReceived { add { } remove { } }
        public event Action<string>? ErrorLineReceived { add { } remove { } }
        public event Action? Exited { add { } remove { } }

        public bool HasExited => true;
        public void Start() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
