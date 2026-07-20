using System.Diagnostics;
using System.Text.Json;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunWatchdogTests
{
    [Theory]
    [InlineData("1ms", 1)]
    [InlineData("30s", 30_000)]
    [InlineData("10m", 600_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("720h", 2_592_000_000)]
    public void DurationParser_ShouldAcceptExactGrammar(string value, long expectedMilliseconds)
    {
        var result = CoverageRunDurationParser.Parse(value, "--duration", allowZero: false);

        Assert.Equal(expectedMilliseconds, (long)result.TotalMilliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01s")]
    [InlineData("1S")]
    [InlineData("1.5s")]
    [InlineData("721h")]
    [InlineData("9223372036854776s")]
    [InlineData("999999999999999999999h")]
    public void DurationParser_ShouldRejectInvalidOrOutOfRangeValues(string value)
    {
        var exception = Assert.Throws<CommandException>(
            () => CoverageRunDurationParser.Parse(value, "--duration", allowZero: false));

        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DurationParser_ShouldAllowOnlyExactZeroWhenRequested()
    {
        Assert.Equal(TimeSpan.Zero, CoverageRunDurationParser.Parse("0", "--heartbeat-interval", allowZero: true));
        Assert.Throws<CommandException>(() => CoverageRunDurationParser.Parse("0ms", "--heartbeat-interval", allowZero: true));
    }

    [Fact]
    public async Task WarnMode_ShouldWriteIncidentWithoutCancellingRun()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project", "tests/Quiet.Tests/Quiet.Tests.csproj");

        var artifact = Path.Join(output.Path, "coverage-watchdog.json");
        await WaitForFileAsync(artifact);

        Assert.False(supervisor.CancellationToken.IsCancellationRequested);
        Assert.Contains("\"outcome\": \"warning\"", await File.ReadAllTextAsync(artifact), StringComparison.Ordinal);
        Assert.Contains("Coverage watchdog warning", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindOutputDirectory_ShouldSerializeWithBootstrapArtifactCommit()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        using var staged = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None,
            artifactStaged: () =>
            {
                staged.Set();
                release.Wait();
            });
        using var operation = supervisor.Start("project", "tests/Bootstrap.Tests/Bootstrap.Tests.csproj");
        Assert.True(staged.Wait(TimeSpan.FromSeconds(5)));

        var bind = Task.Run(() => supervisor.BindOutputDirectory(output.Path));
        await Task.Delay(25);
        Assert.False(bind.IsCompleted);
        release.Set();
        await bind.WaitAsync(TimeSpan.FromSeconds(5));

        var artifact = Path.Join(output.Path, "coverage-watchdog.json");
        await WaitForFileAsync(artifact);
        Assert.Contains("\"outcome\": \"warning\"", await File.ReadAllTextAsync(artifact), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindOutputDirectory_ShouldReportCanonicalPathAfterDelayedBootstrapPromotion()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        using var staged = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Fail,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None,
            artifactStaged: () =>
            {
                staged.Set();
                release.Wait();
            },
            artifactCommitTimeout: TimeSpan.FromMilliseconds(20));
        using var operation = supervisor.Start("project", "tests/Bootstrap.Tests/Bootstrap.Tests.csproj");
        Assert.True(staged.Wait(TimeSpan.FromSeconds(5)));

        supervisor.BindOutputDirectory(output.Path);
        release.Set();

        var artifact = Path.Join(output.Path, "coverage-watchdog.json");
        await WaitForFileAsync(artifact);
        var exception = Assert.Throws<CommandException>(supervisor.ThrowIfFailed);
        var error = console.ReadErrorString();

        Assert.Contains("coverage-watchdog.json", error, StringComparison.Ordinal);
        Assert.DoesNotContain("appsurface-coverage-watchdog", error, StringComparison.Ordinal);
        Assert.Contains("coverage-watchdog.json", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("appsurface-coverage-watchdog", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarnMode_ShouldRearmAfterObservedProgress()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(50),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project", "tests/Rearmed.Tests/Rearmed.Tests.csproj");
        var artifact = Path.Join(output.Path, "coverage-watchdog.json");

        await WaitForIncidentOrdinalAsync(artifact, 1);
        operation.ObserveBytes(1);
        await WaitForIncidentOrdinalAsync(artifact, 2);

        Assert.False(supervisor.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WarnMode_ShouldDrainLatestWarningQueuedBehindActiveArtifactWrite()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        using var firstStaged = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondQueued = new ManualResetEventSlim();
        var stagedOrdinal = 0;
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(20),
            console,
            TimeProvider.System,
            CancellationToken.None,
            artifactStaged: () =>
            {
                if (Interlocked.Increment(ref stagedOrdinal) == 1)
                {
                    firstStaged.Set();
                    releaseFirst.Wait();
                }
            },
            artifactWriteTimeout: TimeSpan.FromMilliseconds(20),
            artifactIncidentQueued: secondQueued.Set);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project", "tests/TwoWave.Tests/TwoWave.Tests.csproj");
        Assert.True(firstStaged.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            operation.ObserveBytes(1);
            Assert.True(secondQueued.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseFirst.Set();
        }

        var artifact = Path.Join(output.Path, "coverage-watchdog.json");
        await WaitForIncidentOrdinalAsync(artifact, 2);
        await WaitForErrorOccurrencesAsync(console, "Coverage watchdog warning", 2);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
        Assert.Equal(2, document.RootElement.GetProperty("incidentOrdinal").GetInt32());
        Assert.Equal(
            "tests/TwoWave.Tests/TwoWave.Tests.csproj",
            document.RootElement.GetProperty("primary").GetProperty("project").GetString());
        Assert.Equal(2, CountOccurrences(console.ReadErrorString(), "Coverage watchdog warning"));
    }

    [Fact]
    public async Task WarnMode_ShouldClassifySimultaneousStallsOnce()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var timeProvider = new FreezableTimeProvider();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100),
            console,
            timeProvider,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var first = supervisor.Start("project", "tests/First.Tests/First.Tests.csproj", order: 0);
        using var second = supervisor.Start("project", "tests/Second.Tests/Second.Tests.csproj", order: 1);
        using var third = supervisor.Start("project", "tests/Third.Tests/Third.Tests.csproj", order: 2);
        timeProvider.Release();

        var artifact = Path.Join(output.Path, "coverage-watchdog.json");
        await WaitForFileAsync(artifact);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));

        Assert.Equal("tests/First.Tests/First.Tests.csproj", document.RootElement.GetProperty("primary").GetProperty("project").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("concurrentlyStale").GetArrayLength());
    }

    [Fact]
    public async Task FailMode_ShouldCancelAndExposeStableExit124Diagnostic()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Fail,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project", "tests/Quiet.Tests/Quiet.Tests.csproj");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(2), supervisor.CancellationToken));
        await WaitForFileAsync(Path.Join(output.Path, "coverage-watchdog.json"));
        var exception = Assert.Throws<CommandException>(supervisor.ThrowIfFailed);

        Assert.Equal(124, exception.ExitCode);
        Assert.Contains("ASCOV121", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tests/Quiet.Tests/Quiet.Tests.csproj", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailMode_ShouldNotWaitForBlockingCancellationCallback()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        using var releaseCallback = new ManualResetEventSlim();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Fail,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var registration = supervisor.CancellationToken.Register(releaseCallback.Wait);
        using var operation = supervisor.Start("project", "tests/BlockedCallback.Tests/BlockedCallback.Tests.csproj");

        try
        {
            await WaitForFileAsync(Path.Join(output.Path, "coverage-watchdog.json"));
            var exception = Assert.Throws<CommandException>(supervisor.ThrowIfFailed);

            Assert.Equal(124, exception.ExitCode);
        }
        finally
        {
            releaseCallback.Set();
        }
    }

    [Fact]
    public async Task FailMode_ShouldKillProcessAttachedAfterCleanupStarts()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Fail,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project", "tests/LateProcess.Tests/LateProcess.Tests.csproj");
        var lease = operation.ReserveProcess();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(2), supervisor.CancellationToken));
        Assert.False(File.Exists(Path.Join(output.Path, "coverage-watchdog.json")));

        using var process = Process.Start(CreateLongRunningProcess())!;
        lease.Attach(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForFileAsync(Path.Join(output.Path, "coverage-watchdog.json"));
        lease.Complete();

        Assert.True(process.HasExited);
        var exception = Assert.Throws<CommandException>(supervisor.ThrowIfFailed);
        Assert.Contains("Cleanup: complete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailMode_ShouldWaitForUnattachedLeaseToComplete()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Fail,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project", "tests/NoProcess.Tests/NoProcess.Tests.csproj");
        var lease = operation.ReserveProcess();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(2), supervisor.CancellationToken));

        Assert.False(File.Exists(Path.Join(output.Path, "coverage-watchdog.json")));
        lease.Complete();
        await WaitForFileAsync(Path.Join(output.Path, "coverage-watchdog.json"));

        var exception = Assert.Throws<CommandException>(supervisor.ThrowIfFailed);
        Assert.Contains("Cleanup: complete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OffMode_ShouldSleepUntilStateChangesInsteadOfPolling()
    {
        using var console = new FakeInMemoryConsole();
        var timeProvider = new CountingTimeProvider();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Off,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            timeProvider,
            CancellationToken.None);
        using var operation = supervisor.Start("project", "tests/Quiet.Tests/Quiet.Tests.csproj");

        await Task.Delay(250);

        Assert.InRange(timeProvider.TimerCount, 1, 3);
    }

    [Fact]
    public async Task WarnMode_ShouldBoundArtifactAndReportRelativePath()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        var operations = Enumerable.Range(0, 40)
            .Select(index => supervisor.Start("project", $"tests/{index}-{new string('x', 10_000)}.csproj", log: $"logs/{new string('y', 10_000)}.log"))
            .ToArray();

        try
        {
            var artifact = Path.Join(output.Path, "coverage-watchdog.json");
            await WaitForFileAsync(artifact);

            Assert.InRange(new FileInfo(artifact).Length, 1, 64 * 1024);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
            Assert.InRange(document.RootElement.GetProperty("concurrentlyStale").GetArrayLength(), 0, 32);
            Assert.True(document.RootElement.GetProperty("concurrentlyStaleOmitted").GetInt32() >= 0);
            Assert.DoesNotContain($"artifact=\"{output.Path}", console.ReadErrorString(), StringComparison.Ordinal);
            Assert.Contains("coverage-watchdog.json", console.ReadErrorString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var operation in operations)
            {
                operation.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConsoleSink_ShouldBoundBlockedWritesAndKeepOnlyOneWriter()
    {
        using var output = new BlockingWriteStream();
        using var console = new FakeConsole(Stream.Null, output, Stream.Null);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromMilliseconds(20));

        await sink.WriteOutputAsync("first");
        await sink.WriteOutputAsync("second");

        Assert.Equal(1, output.WriteCount);
    }

    [Fact]
    public async Task ConsoleSink_ShouldRetainConcurrentWritesInOrder()
    {
        using var output = new GatedCaptureWriteStream();
        using var console = new FakeConsole(Stream.Null, output, Stream.Null);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromSeconds(1));

        var first = sink.WriteOutputAsync("first");
        await output.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var second = sink.WriteOutputAsync("second");
        output.ReleaseFirstWrite();

        await Task.WhenAll(first, second);

        Assert.Equal(2, output.WriteCount);
        var text = output.ReadString();
        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal),
            $"Expected ordered writes, but received: {text}");
    }

    [Fact]
    public async Task ConsoleSink_ShouldCoalescePeriodicWritesWhileWriterIsPending()
    {
        using var output = new GatedCaptureWriteStream();
        using var console = new FakeConsole(Stream.Null, output, Stream.Null);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromSeconds(1));

        var first = sink.WriteOutputAsync("heartbeat-1", coalesceIfWritePending: true);
        await output.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
        await sink.WriteOutputAsync("heartbeat-2", coalesceIfWritePending: true);
        await sink.WriteOutputAsync("heartbeat-3", coalesceIfWritePending: true);
        output.ReleaseFirstWrite();
        await first;

        Assert.Equal(1, output.WriteCount);
        var text = output.ReadString();
        Assert.Contains("heartbeat-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("heartbeat-2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("heartbeat-3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsoleSink_ShouldFallbackCriticalErrorWithoutLateDuplicate()
    {
        using var output = new GatedCaptureWriteStream();
        using var error = new GatedCaptureWriteStream();
        error.ReleaseFirstWrite();
        using var console = new FakeConsole(Stream.Null, output, error);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromMilliseconds(20));

        var blocked = sink.WriteOutputAsync("blocked-output");
        await output.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));

        await sink.WriteCriticalErrorAsync("critical-error").WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, error.WriteCount);
        Assert.Contains("critical-error", error.ReadString(), StringComparison.Ordinal);

        output.ReleaseFirstWrite();
        await blocked;
        await sink.WriteOutputAsync("queue-drained").WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, error.WriteCount);
        Assert.Equal(1, CountOccurrences(error.ReadString(), "critical-error"));
    }

    [Fact]
    public async Task ConsoleSink_ShouldIgnoreThrowingWriter()
    {
        using var output = new ThrowingWriteStream();
        using var console = new FakeConsole(Stream.Null, output, Stream.Null);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromMilliseconds(20));

        await sink.WriteOutputAsync("message");

        Assert.Equal(1, output.WriteCount);
    }

    [Fact]
    public async Task ConsoleSink_ShouldSupportErrorWithoutNewlineAndIgnoreWritesAfterDispose()
    {
        using var error = new MemoryStream();
        using var console = new FakeConsole(Stream.Null, Stream.Null, error);
        var sink = new CoverageRunConsoleSink(console, TimeSpan.FromSeconds(1));

        await sink.WriteErrorAsync("first", appendNewLine: false);
        sink.Dispose();
        await sink.WriteOutputAsync("ignored");
        await sink.WriteCriticalErrorAsync("also-ignored");

        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(error.ToArray()));
    }

    [Fact]
    public async Task ConsoleSink_ShouldPropagateCallerCancellationForBlockedCriticalWrite()
    {
        using var output = new GatedCaptureWriteStream();
        using var console = new FakeConsole(Stream.Null, output, Stream.Null);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var blocked = sink.WriteOutputAsync("blocked");
        await output.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
        var critical = sink.WriteCriticalErrorAsync("cancelled", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => critical);
        output.ReleaseFirstWrite();
        await blocked;
    }

    [Fact]
    public async Task ConsoleSink_ShouldBoundThrowingCriticalWriter()
    {
        using var error = new ThrowingWriteStream();
        using var console = new FakeConsole(Stream.Null, Stream.Null, error);
        using var sink = new CoverageRunConsoleSink(console, TimeSpan.FromMilliseconds(20));

        await sink.WriteCriticalErrorAsync("critical");

        Assert.Equal(1, error.WriteCount);
    }

    [Fact]
    public async Task ProcessLease_ShouldKillProcessAttachedAfterCompletion()
    {
        var lease = CoverageRunProcessLease.Detached();
        lease.Complete();
        using var process = Process.Start(CreateLongRunningProcess())!;

        lease.Attach(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ProcessLease_ShouldHandleAlreadyExitedAndDisposedProcesses()
    {
        var exitedLease = CoverageRunProcessLease.Detached();
        using (var exited = Process.Start(CreateCompletedProcess())!)
        {
            await exited.WaitForExitAsync();
            exitedLease.Attach(exited);
            await exitedLease.TerminateAsync();
        }

        var disposedLease = CoverageRunProcessLease.Detached();
        var disposed = Process.Start(CreateCompletedProcess())!;
        await disposed.WaitForExitAsync();
        disposedLease.Attach(disposed);
        disposed.Dispose();

        await disposedLease.TerminateAsync();
    }

    [Fact]
    public async Task Heartbeat_ShouldDescribeProgressTransitionsAndCompletion()
    {
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Off,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(1),
            console,
            TimeProvider.System,
            CancellationToken.None);
        using var operation = supervisor.Start("merge", state: "running");
        operation.ObserveBytes(12);
        operation.ObserveBytes(0);
        operation.Transition("finalizing");

        await WaitForOutputAsync(console, "finalizing=1");
        operation.Dispose();
        await WaitForOutputAsync(console, "complete=1");

        var text = console.ReadOutputString();
        Assert.Contains("operation=\"merge\"", text, StringComparison.Ordinal);
        Assert.Contains("output-bytes=12", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailMode_ShouldRejectNewWorkAndCommitsAfterTerminalOwnership()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Fail,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("diagnostics");
        await WaitForFileAsync(Path.Join(output.Path, "coverage-watchdog.json"));

        Assert.Throws<OperationCanceledException>(() => supervisor.Start("build"));
        Assert.Throws<OperationCanceledException>(() => operation.ReserveProcess());
        Assert.Throws<OperationCanceledException>(() => supervisor.Commit(() => { }));
        Assert.Throws<ArgumentNullException>(() => supervisor.Commit(null!));
    }

    [Fact]
    public async Task Commit_ShouldRunBeforeTerminalOwnershipAndCompletedOperationsIgnoreProgress()
    {
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Off,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            console,
            TimeProvider.System,
            CancellationToken.None);
        var committed = false;
        supervisor.Commit(() => committed = true);
        var operation = supervisor.Start("custom");
        operation.Dispose();
        operation.ObserveBytes(1);
        operation.Transition("ignored");
        operation.Dispose();

        Assert.True(committed);
        supervisor.ThrowIfFailed();
    }

    [Fact]
    public async Task WarnMode_ShouldReportOperationSubjectAndBoundCommandMetadata()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(20),
            console,
            TimeProvider.System,
            CancellationToken.None);
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start(
            "discovery",
            state: new string('s', 2_000),
            log: new string('l', 2_000),
            commandOptions: Enumerable.Range(0, 40).Select(index => $"--option-{index}-{new string('x', 300)}").ToArray());

        var artifact = Path.Join(output.Path, "coverage-watchdog.json");
        await WaitForFileAsync(artifact);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
        var primary = document.RootElement.GetProperty("primary");
        var options = primary.GetProperty("command").GetProperty("options");

        Assert.Equal(32, options.GetArrayLength());
        Assert.All(options.EnumerateArray(), item => Assert.True(item.GetString()!.Length <= 256));
        Assert.Equal(1024, primary.GetProperty("state").GetString()!.Length);
        Assert.Equal(1024, primary.GetProperty("log").GetString()!.Length);
        Assert.Contains("operation=discovery", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindOutputDirectory_ShouldReportPromotionFailure()
    {
        using var notDirectory = TestDirectory.Create();
        var occupiedPath = Path.Join(notDirectory.Path, "occupied");
        await File.WriteAllTextAsync(occupiedPath, "file");
        using var console = new FakeInMemoryConsole();
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None);
        using var operation = supervisor.Start("project");
        await WaitForErrorOccurrencesAsync(console, "Coverage watchdog warning", 1);

        supervisor.BindOutputDirectory(occupiedPath);
        await WaitForErrorOccurrencesAsync(console, "ASCOV122", 1);

        Assert.Contains("bootstrap artifact could not be promoted", console.ReadErrorString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarnMode_ShouldReportAndCleanStagingWhenCanonicalArtifactCannotBeReplaced()
    {
        using var output = TestDirectory.Create();
        using var console = new FakeInMemoryConsole();
        var destination = Path.Join(output.Path, "coverage-watchdog.json");
        await using var supervisor = new CoverageRunWatchdogSupervisor(
            CoverageRunWatchdogMode.Warn,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            console,
            TimeProvider.System,
            CancellationToken.None,
            artifactStaged: () => Directory.CreateDirectory(destination));
        supervisor.BindOutputDirectory(output.Path);
        using var operation = supervisor.Start("project");

        await WaitForErrorOccurrencesAsync(console, "ASCOV122", 1);

        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(output.Path, ".coverage-watchdog.json.*.tmp"));
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!File.Exists(path))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static async Task WaitForIncidentOrdinalAsync(string path, int ordinal)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, timeout.Token));
                if (document.RootElement.GetProperty("incidentOrdinal").GetInt32() >= ordinal)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or JsonException)
            {
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitForErrorOccurrencesAsync(FakeInMemoryConsole console, string value, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (CountOccurrences(console.ReadErrorString(), value) < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ProcessStartInfo CreateLongRunningProcess()
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"") { UseShellExecute = false };

    private static ProcessStartInfo CreateCompletedProcess()
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"") { UseShellExecute = false };

    private static async Task WaitForOutputAsync(FakeInMemoryConsole console, string value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!console.ReadOutputString().Contains(value, StringComparison.Ordinal))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TestDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"appsurface-watchdog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        public int TimerCount { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerCount++;
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }

    private sealed class FreezableTimeProvider : TimeProvider
    {
        private readonly long _frozenTimestamp = TimeProvider.System.GetTimestamp();
        private int _released;

        public override long GetTimestamp()
            => Volatile.Read(ref _released) == 0 ? _frozenTimestamp : TimeProvider.System.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => TimeProvider.System.CreateTimer(callback, state, dueTime, period);

        public void Release() => Volatile.Write(ref _released, 1);
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WriteCount { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCount++;
            _never.Task.GetAwaiter().GetResult();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return new ValueTask(_never.Task);
        }
    }

    private sealed class GatedCaptureWriteStream : Stream
    {
        private readonly MemoryStream _capture = new();
        private readonly TaskCompletionSource _firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new();
        private int _writeCount;

        public Task FirstWriteStarted => _firstWriteStarted.Task;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();

        public string ReadString()
        {
            lock (_sync)
            {
                return System.Text.Encoding.UTF8.GetString(_capture.ToArray());
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _writeCount);
            if (ordinal == 1)
            {
                _firstWriteStarted.TrySetResult();
                await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }

            lock (_sync)
            {
                _capture.Write(buffer.Span);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _releaseFirstWrite.TrySetResult();
                _capture.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public int WriteCount { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCount++;
            throw new IOException("console unavailable");
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.FromException(new IOException("console unavailable"));
        }
    }
}
