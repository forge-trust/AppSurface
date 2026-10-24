using System.Diagnostics;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidenceCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_UsesArgvWorkingDirectoryAndExplicitEnvironment()
    {
        var runner = new EvidenceCommandRunner();
        var request = CreateShellRequest("printf '%s' \"$EVIDENCE_TEST_VALUE\"", "[Console]::Write($env:EVIDENCE_TEST_VALUE)") with
        {
            Environment = new Dictionary<string, string> { ["EVIDENCE_TEST_VALUE"] = "argv-safe-value" },
        };

        var result = await runner.RunAsync(request);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("argv-safe-value", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task RunAsync_EnforcesOneCombinedOutputByteBudgetWhileDrainingBothStreams()
    {
        var runner = new EvidenceCommandRunner();
        var result = await runner.RunAsync(CreateShellRequest("printf '1234567890'; printf 'abcdefghij' >&2", "[Console]::Out.Write('1234567890'); [Console]::Error.Write('abcdefghij')") with
        {
            MaximumOutputBytes = 12,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput) + System.Text.Encoding.UTF8.GetByteCount(result.StandardError) <= 12);
        Assert.True(result.OutputTruncated);
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsOwnedProcessTreeAndDoesNotExposeInputs()
    {
        var runner = new EvidenceCommandRunner();
        var request = CreateShellRequest("sleep 30; printf '%s' secret-argument", "Start-Sleep -Seconds 30; [Console]::Write('secret-argument')") with
        {
            Timeout = TimeSpan.FromMilliseconds(100),
        };

        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(request));

        Assert.Equal("timeout", exception.Code);
        Assert.DoesNotContain("secret-argument", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(request.FileName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RethrowsCallerCancellationAfterCleanup()
    {
        var runner = new EvidenceCommandRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            CreateShellRequest("sleep 30", "Start-Sleep -Seconds 30"), cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_ConvertsWorkspaceBudgetFailureToStableCode()
    {
        var runner = new EvidenceCommandRunner();
        var request = CreateShellRequest("sleep 30", "Start-Sleep -Seconds 30") with
        {
            CheckBudget = static _ => ValueTask.FromException(new EvidenceWorkspaceBudgetException("budget", 1, 0, 1, 0)),
        };

        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(request));

        Assert.Equal("budget_exceeded", exception.Code);
    }

    [Fact]
    public async Task EvidenceCommandProducer_EvaluatesOnlySuccessfulDeclaredAssertions()
    {
        var evaluated = false;
        var producer = new EvidenceCommandProducer(
            "native-command",
            new EvidenceCommandRunner(),
            CreateShellRequest("printf passed", "[Console]::Write('passed')"),
            (result, _, _) =>
            {
                evaluated = result.StandardOutput == "passed";
                return ValueTask.FromResult<IReadOnlyList<string>>(["native/assertion@1"]);
            });
        var declaration = new EvidenceProducerDeclaration("native-command", "command", "1.0", [], ["native/assertion@1"], [], 10);
        var context = new EvidenceProducerContext(CreatePlan(declaration), declaration, TimeProvider.System);

        var result = await producer.ProduceAsync(context, CancellationToken.None);

        Assert.True(evaluated);
        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(["native/assertion@1"], result.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task RunAsync_RejectsBudgetBeforeLaunchingTheCommand()
    {
        var request = new EvidenceCommandRequest("nonexistent-evidence-executable", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1))
        {
            CheckBudget = _ => ValueTask.FromException(new EvidenceWorkspaceBudgetException("budget", 2, 1, 100, 0)),
        };
        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(request));
        Assert.Equal("budget_exceeded", exception.Code);
    }

    [Fact]
    public async Task RunAsync_StopsWhenTheBudgetIsExhaustedDuringExecution()
    {
        var checks = 0;
        var request = CreateShellRequest("sleep 30", "Start-Sleep -Seconds 30") with
        {
            CheckBudget = _ => ++checks > 1
                ? ValueTask.FromException(new EvidenceWorkspaceBudgetException("budget", 2, 1, 100, 0)) : ValueTask.CompletedTask,
        };
        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(request));
        Assert.Equal("budget_exceeded", exception.Code);
        Assert.True(checks > 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Producer_RefusesUndeclaredOrTruncatedAssertions(bool truncate)
    {
        var request = CreateShellRequest("printf 'complete-output'", "[Console]::Write('complete-output')") with { MaximumOutputBytes = truncate ? 3 : 1024 };
        var declaration = new EvidenceProducerDeclaration("native-command", "command", "1.0", [], ["declared@1"], [], 10);
        var producer = new EvidenceCommandProducer("native-command", new EvidenceCommandRunner(), request,
            (_, _, _) => ValueTask.FromResult<IReadOnlyList<string>>(["undeclared@1"]));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(CreatePlan(declaration), declaration, TimeProvider.System), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task RunAsync_RejectsAnOverrunObservedAfterTheChildExits()
    {
        var parent = Directory.CreateTempSubdirectory("evidence-command-overrun-");
        try
        {
            await using var workspace = EvidenceWorkspace.Create(parent.FullName, new EvidenceWorkspaceOptions(128, 0));
            var checks = 0;
            var request = CreateShellRequest("printf '" + new string('x', 256) + "' > payload", "[IO.File]::WriteAllText('payload', '" + new string('x', 256) + "')") with
            {
                WorkingDirectory = workspace.RootPath,
                CheckBudget = _ => { Interlocked.Increment(ref checks); workspace.CheckBudget(); return ValueTask.CompletedTask; },
            };
            var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(request));
            Assert.Equal("budget_exceeded", exception.Code);
            Assert.True(checks >= 2);
        }
        finally { parent.Delete(recursive: true); }
    }

    private static EvidenceCommandRequest CreateShellRequest(string command, string windowsCommand) =>
        OperatingSystem.IsWindows()
            ? new EvidenceCommandRequest("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", windowsCommand], Environment.CurrentDirectory, TimeSpan.FromSeconds(10))
            : new EvidenceCommandRequest("sh", ["-c", command], Environment.CurrentDirectory, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task RunAsync_ReportsCaptureFailureOnlyAfterBothCapturesHaveStopped()
    {
        var reads = 0;
        var completed = 0;
        var runner = new EvidenceCommandRunner(stream => new FailingCaptureStream(stream,
            Interlocked.Increment(ref reads) == 1, () => Interlocked.Increment(ref completed)));

        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(
            CreateShellRequest("printf output; printf error >&2", "[Console]::Out.Write('output'); [Console]::Error.Write('error')")));

        Assert.Equal("capture_failed", exception.Code);
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task RunAsync_RejectsInvalidRequestBoundsBeforeStarting()
    {
        var runner = new EvidenceCommandRunner();
        var cases = new Func<Task>[]
        {
            () => runner.RunAsync(null!),
            () => runner.RunAsync(new EvidenceCommandRequest(" ", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1))),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", null!, Environment.CurrentDirectory, TimeSpan.FromSeconds(1))),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], "relative", TimeSpan.FromSeconds(1))),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], Environment.CurrentDirectory, TimeSpan.Zero)),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], Environment.CurrentDirectory, Timeout.InfiniteTimeSpan)),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1)) { MaximumOutputBytes = 0 }),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1)) { MaximumOutputBytes = 256L * 1024 * 1024 + 1 }),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], Path.Join(Environment.CurrentDirectory, "missing-evidence-directory"), TimeSpan.FromSeconds(1))),
            () => runner.RunAsync(new EvidenceCommandRequest("sh", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1))
            {
                Environment = new Dictionary<string, string> { ["BAD=NAME"] = "value" },
            }),
        };

        await Assert.ThrowsAnyAsync<Exception>(cases[0]);
        await Assert.ThrowsAsync<ArgumentException>(cases[1]);
        await Assert.ThrowsAsync<ArgumentException>(cases[2]);
        await Assert.ThrowsAsync<ArgumentException>(cases[3]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(cases[4]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(cases[5]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(cases[6]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(cases[7]);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(cases[8]);
        await Assert.ThrowsAsync<ArgumentException>(cases[9]);
    }

    [Fact]
    public async Task RunAsync_ConvertsGenericBudgetFailuresBeforeDuringAndAfterExecution()
    {
        var before = CreateShellRequest("true", "[Console]::Write('unused')") with
        {
            CheckBudget = static _ => ValueTask.FromException(new InvalidOperationException("synthetic")),
        };
        var beforeException = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(before));
        Assert.Equal("budget_check_failed", beforeException.Code);

        var checks = 0;
        var during = CreateShellRequest("sleep 30", "Start-Sleep -Seconds 30") with
        {
            CheckBudget = _ => Interlocked.Increment(ref checks) > 1
                ? ValueTask.FromException(new InvalidOperationException("synthetic"))
                : ValueTask.CompletedTask,
        };
        var duringException = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(during));
        Assert.Equal("budget_check_failed", duringException.Code);

        var after = CreateShellRequest("true", "[Console]::Write('done')") with
        {
            CheckBudget = static _ => ValueTask.FromException(new InvalidOperationException("synthetic")),
        };
        var afterException = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(after));
        Assert.Equal("budget_check_failed", afterException.Code);
    }

    [Fact]
    public async Task RunAsync_SynchronousCaptureAdapterFailureTerminatesStartedChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Directory.CreateTempSubdirectory("evidence-command-adapter-");
        var pidPath = Path.Join(parent.FullName, "child.pid");
        try
        {
            var runner = new EvidenceCommandRunner(_ => throw new InvalidOperationException("adapter failed"));
            var request = new EvidenceCommandRequest("sh", ["-c", $"echo $$ > '{pidPath}'; exec sleep 30"], parent.FullName, TimeSpan.FromSeconds(5));

            var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(request));
            Assert.Equal("capture_failed", exception.Code);
            for (var attempt = 0; attempt < 20 && !File.Exists(pidPath); attempt++)
            {
                await Task.Delay(25);
            }

            Assert.True(File.Exists(pidPath));
            var pid = int.Parse(File.ReadAllText(pidPath));
            try
            {
                Assert.True(Process.GetProcessById(pid).HasExited);
            }
            catch (ArgumentException)
            {
                // The process already exited and disappeared from the process table.
            }
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    private sealed class FailingCaptureStream(Stream inner, bool fail, Action stopped) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fail) { stopped(); throw new IOException("synthetic capture failure"); }
            var count = await inner.ReadAsync(buffer, cancellationToken);
            if (count == 0) stopped();
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RunAsync_RefusesToCompleteWhenOutputNeverClosesAndStopsCaptureTasks()
    {
        var stopped = 0;
        var runner = new EvidenceCommandRunner(_ => new HoldingCaptureStream(() => Interlocked.Increment(ref stopped)));

        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(
            CreateShellRequest("printf done", "[Console]::Write('done')")));

        Assert.Equal("drain_timeout", exception.Code);
        Assert.Equal(2, stopped);
    }

    private sealed class HoldingCaptureStream(Action stopped) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            finally { stopped(); }
        }
    }

    private static EvidencePlan CreatePlan(EvidenceProducerDeclaration producer) =>
        new("1", "test", "policy", "diff", new EvidenceProfile("test", EvidenceProfileScope.Targeted, [], [producer], []), [], [], "plan");
}
