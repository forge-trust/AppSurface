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

        var capturesCompleted = 0;
        var afterRunner = new EvidenceCommandRunner(stream => new EofObservingStream(stream,
            () => Interlocked.Increment(ref capturesCompleted)));
        var after = CreateShellRequest("true", "[Console]::Write('done')") with
        {
            CheckBudget = _ => Volatile.Read(ref capturesCompleted) == 2
                ? ValueTask.FromException(new InvalidOperationException("synthetic"))
                : ValueTask.CompletedTask,
        };
        var afterException = await Assert.ThrowsAsync<EvidenceCommandException>(() => afterRunner.RunAsync(after));
        Assert.Equal(2, capturesCompleted);
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
            var pid = 0;
            var runner = new EvidenceCommandRunner(_ =>
            {
                var readiness = Stopwatch.StartNew();
                while (readiness.Elapsed < TimeSpan.FromSeconds(2))
                {
                    if (File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath), out pid))
                    {
                        throw new InvalidOperationException("adapter failed");
                    }
                    Thread.Sleep(10);
                }
                throw new InvalidOperationException("child readiness failed");
            });
            var request = new EvidenceCommandRequest("sh", ["-c", $"echo $$ > '{pidPath}'; exec sleep 30"], parent.FullName, TimeSpan.FromSeconds(5));

            var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(request));
            Assert.Equal("capture_failed", exception.Code);
            Assert.True(pid > 0, "The child must report its process identity before capture fails.");
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

    [Fact]
    public async Task RunAsync_ShouldClassifyMissingExecutableWithoutExposingTheInput()
    {
        const string missing = "evidence-private-missing-command-4ab10b9b";
        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => new EvidenceCommandRunner().RunAsync(
            new EvidenceCommandRequest(missing, ["private-argument"], Environment.CurrentDirectory, TimeSpan.FromSeconds(10))));

        Assert.Equal("start_failed", exception.Code);
        Assert.DoesNotContain(missing, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-argument", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldRejectAnUnsupportedDeadlineBeforeAnyCommandCanStart()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new EvidenceCommandRunner().RunAsync(
            new EvidenceCommandRequest("evidence-must-not-start", [], Environment.CurrentDirectory, TimeSpan.MaxValue)));
    }

    [Fact]
    public async Task RunAsync_CancellationDuringAdmissionRemainsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new EvidenceCommandRequest("evidence-must-not-start", [], Environment.CurrentDirectory, TimeSpan.FromSeconds(1))
        {
            CheckBudget = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            },
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EvidenceCommandRunner().RunAsync(request, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringDrainStopsBothCaptureTasks()
    {
        var started = 0;
        var stopped = 0;
        var capturesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var runner = new EvidenceCommandRunner(_ =>
        {
            if (Interlocked.Increment(ref started) == 2) capturesStarted.TrySetResult();
            return new HoldingCaptureStream(() => Interlocked.Increment(ref stopped));
        });
        var running = runner.RunAsync(CreateShellRequest("printf done", "[Console]::Write('done')"), cancellation.Token);
        await capturesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(2, stopped);
    }

    [Fact]
    public async Task RunAsync_CancellationAtTheFinalBudgetCheckCannotPublishSuccess()
    {
        var capturesCompleted = 0;
        using var cancellation = new CancellationTokenSource();
        var runner = new EvidenceCommandRunner(stream => new EofObservingStream(stream,
            () => Interlocked.Increment(ref capturesCompleted)));
        var request = CreateShellRequest("printf done", "[Console]::Write('done')") with
        {
            CheckBudget = token =>
            {
                if (Volatile.Read(ref capturesCompleted) == 2)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return ValueTask.CompletedTask;
            },
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(request, cancellation.Token));

        Assert.Equal(2, capturesCompleted);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ObservesCaptureCloseFailuresDuringTimeoutCleanup(bool disposed)
    {
        var capturesStopped = 0;
        var runner = new EvidenceCommandRunner(_ => new CloseFailingCaptureStream(disposed,
            () => Interlocked.Increment(ref capturesStopped)));
        var request = CreateShellRequest("sleep 30", "Start-Sleep -Seconds 30") with
        {
            Timeout = TimeSpan.FromMilliseconds(100),
        };

        var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(request));

        Assert.Equal("timeout", exception.Code);
        Assert.Equal(2, capturesStopped);
    }

    [Fact]
    public async Task RunAsync_NoncooperativeCaptureCannotPassCleanupAndCanBeReleasedByItsOwner()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        var runner = new EvidenceCommandRunner(_ => new NoncooperativeCaptureStream(release.Task, () =>
        {
            if (Interlocked.Increment(ref completed) == 2) stopped.TrySetResult();
        }));
        try
        {
            var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => runner.RunAsync(
                CreateShellRequest("sleep 30", "Start-Sleep -Seconds 30") with { Timeout = TimeSpan.FromMilliseconds(100) }));

            Assert.Equal("cleanup_failed", exception.Code);
            Assert.Contains("capture", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(stopped.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_StopsForegroundDescendantsOnTimeoutOrCallerCancellation(bool cancel)
    {
        var parent = Directory.CreateTempSubdirectory("evidence-command-descendant-").FullName;
        var pidPath = Path.Join(parent, "child.pid");
        using var cancellation = new CancellationTokenSource();
        Task<EvidenceCommandResult>? running = null;
        try
        {
            // An absolute marker avoids PowerShell's separate provider location;
            // -NoNewWindow keeps the child in the foreground process tree under test.
            var request = CreateShellRequest("sleep 30 & child=$!; printf '%s' \"$child\" > child.pid; wait \"$child\"",
                "$ErrorActionPreference = 'Stop'; $child = Start-Process powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30' -NoNewWindow -PassThru; [IO.File]::WriteAllText($env:EVIDENCE_CHILD_PID_PATH, [string]$child.Id); $child.WaitForExit()") with
            {
                WorkingDirectory = parent,
                Timeout = OperatingSystem.IsWindows() ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2),
                Environment = new Dictionary<string, string> { ["EVIDENCE_CHILD_PID_PATH"] = pidPath },
            };
            running = new EvidenceCommandRunner().RunAsync(request, cancellation.Token);
            if (cancel)
            {
                var readiness = Stopwatch.StartNew();
                while ((!File.Exists(pidPath) || !int.TryParse(await File.ReadAllTextAsync(pidPath), out _))
                    && readiness.Elapsed < request.Timeout)
                    await Task.Delay(10);
                Assert.True(File.Exists(pidPath), "The foreground child must start before cancellation.");
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<EvidenceCommandException>(() => running);
                Assert.Equal("timeout", exception.Code);
            }
            Assert.True(File.Exists(pidPath), "The foreground descendant must be observed before its parent stops.");
            var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
            var cleanup = Stopwatch.StartNew();
            while (IsRunning(pid) && cleanup.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            Assert.False(IsRunning(pid), "The owned foreground descendant must not survive command cleanup.");
        }
        finally
        {
            // Readiness can fail before the caller-cancellation assertion. Stop and
            // observe the command before removing its working directory in that case.
            cancellation.Cancel();
            if (running is not null)
            {
                try { await running; }
                catch (OperationCanceledException) { }
                catch (EvidenceCommandException) { }
            }
            Directory.Delete(parent, recursive: true);
        }

        static bool IsRunning(int pid)
        {
            try { using var child = Process.GetProcessById(pid); return !child.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

    private sealed class EofObservingStream(Stream inner, Action completed) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                await Task.Delay(100, cancellationToken);
                completed();
            }
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HoldingCaptureStream(Action stopped) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            finally { stopped(); }
        }
    }

    private sealed class CloseFailingCaptureStream(bool disposed, Action stopped) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                if (disposed) throw new ObjectDisposedException("synthetic capture");
                throw new IOException("synthetic capture close");
            }
            finally { stopped(); }
            return 0;
        }
    }

    private sealed class NoncooperativeCaptureStream(Task release, Action stopped) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await release; return 0; }
            finally { stopped(); }
        }
    }

    private static EvidencePlan CreatePlan(EvidenceProducerDeclaration producer) =>
        new("1", "test", "policy", "diff", new EvidenceProfile("test", EvidenceProfileScope.Targeted, [], [producer], []), [], [], "plan");
}
