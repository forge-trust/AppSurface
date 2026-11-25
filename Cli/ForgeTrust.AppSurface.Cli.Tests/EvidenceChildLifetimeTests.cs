using System.Diagnostics;
using System.IO.Pipes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceChildLifetimeTests
{
    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment = new Dictionary<string, string>();

    [Fact]
    public async Task ListenerCreationFailureRejectsAdmissionWithoutInventingUnknownChildWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("listener-failure", beforeListenerCreation:
            () => throw new IOException("Controlled listener creation failure."));

        Assert.False(session.CanAcceptChildren);
        Assert.False(session.IsUnknown);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(session.IsSettled);
    }

    [Fact]
    public async Task DisposeAsyncWaitsForCloseEnrollmentStopSourceCancellation()
    {
        var session = EvidenceChildLifetimeSession.Start("close-dispose-race-" + Guid.NewGuid().ToString("N"),
            beforeListenerCreation: () => throw new IOException("Controlled listener creation failure."));
        var cancellationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeStopSourceCancellationForTests = () =>
        {
            cancellationStarted.TrySetResult();
            releaseCancellation.Task.GetAwaiter().GetResult();
        };
        var closeTask = Task.Run(session.CloseEnrollment);
        try
        {
            await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var disposeTask = Task.Run(() => session.DisposeAsync().AsTask());

            Assert.False(disposeTask.IsCompleted);
            releaseCancellation.TrySetResult();
            await closeTask.WaitAsync(TimeSpan.FromSeconds(5));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.IsSettled);
        }
        finally
        {
            releaseCancellation.TrySetResult();
            await closeTask.WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LaterListenerFailureStillJoinsKnownAdmittedWorkWithoutInventingAnEscape()
    {
        using var admitted = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedListener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var session = EvidenceChildLifetimeSession.Start("later-listener-failure", beforeListenerCreation: () =>
        {
            if (Interlocked.Increment(ref attempts) != 2) return;
            // gstack-timing-ok: bounds a controlled test barrier; elapsed performance is not asserted.
            if (!admitted.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The first owned child did not enroll.");
            failedListener.SetResult();
            throw new IOException("Controlled later listener creation failure.");
        });
        var child = RunAsync(async _ =>
        {
            admitted.Set();
            await release.Task;
        }, CancellationToken.None, session.Environment);
        try
        {
            await failedListener.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await session.WaitForSettlementAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
            release.SetResult();
            await child;
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.False(session.IsUnknown);
            Assert.False(session.CanAcceptChildren);
        }
        finally
        {
            release.TrySetResult();
            await child;
        }
    }

    [Fact]
    public async Task RunAsync_WithoutChannel_ShouldRunAndJoinNormally()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, CancellationToken.None, EmptyEnvironment);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.IsCompleted);
        release.SetResult();
        await run;
    }

    [Fact]
    public async Task PublicAmbientOverloads_WithoutChannel_ShouldRemainFreshOnly()
    {
        Assert.Null(System.Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.PipeEnvironmentName));
        Assert.Null(System.Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.ExecutionEnvironmentName));
        Assert.Null(System.Environment.GetEnvironmentVariable(EvidenceChildLifetimeSession.NonceEnvironmentName));

        var invoked = false;
        await EvidenceOwnedChild.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(invoked);
        var exitCode = await EvidenceOwnedChild.RunProcessAsync(new ProcessStartInfo("dotnet", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }, CancellationToken.None);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunProcessAsync_WithoutChannel_ShouldStartAndJoinNormally()
    {
        var exitCode = await RunProcessAsync(new ProcessStartInfo("dotnet", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }, CancellationToken.None, EmptyEnvironment);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunProcessAsync_ShouldNotRestoreVariablesRemovedFromCallerEnvironment()
    {
        const string sentinel = "APPSURFACE_EVIDENCE_CHILD_ENVIRONMENT_CLEAR_SENTINEL";
        using var parentSentinel = new EnvironmentVariableScope(sentinel, "set-in-parent-environment");
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var probeAssembly = TestPathUtils.PathUnder(repository, "tests", "ForgeTrust.AppSurface.CoverageLeaseProbe", "bin",
            configuration, "net10.0", "ForgeTrust.AppSurface.CoverageLeaseProbe.dll");
        var runtimeDirectory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtimeDirectory.Parent!.Parent!.Parent!.FullName;
        var dotnetHost = TestPathUtils.PathUnder(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(probeAssembly), $"The coverage lease probe was not built: {probeAssembly}");
        Assert.True(File.Exists(dotnetHost), $"The current .NET host was not found: {dotnetHost}");

        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(probeAssembly);
        startInfo.ArgumentList.Add("environment-clear");
        startInfo.Environment.Clear();

        var exitCode = await RunProcessAsync(startInfo, CancellationToken.None, EmptyEnvironment);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RunAsync_ShouldPreserveWorkFailureWhenCompletionChannelDisconnects()
    {
        var pipeName = "ecf-" + Guid.NewGuid().ToString("N")[..12];
        const string executionId = "failure-priority";
        const string nonce = "0123456789abcdef0123456789abcdef";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using (var reader = new StreamReader(server, leaveOpen: true))
            using (var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true })
            {
                Assert.Equal($"BEGIN|{executionId}|{nonce}", await reader.ReadLineAsync());
                await writer.WriteLineAsync("ACK|" + Guid.NewGuid().ToString("N"));
                Assert.StartsWith("COMPLETE|", await reader.ReadLineAsync());
            }
            // Closing without DONE models the server losing the completion acknowledgement.
            server.Dispose();
        });
        var channel = new Dictionary<string, string>
        {
            [EvidenceChildLifetimeSession.PipeEnvironmentName] = pipeName,
            [EvidenceChildLifetimeSession.ExecutionEnvironmentName] = executionId,
            [EvidenceChildLifetimeSession.NonceEnvironmentName] = nonce,
        };
        var workFailure = new ChildHandlerProbeException();

        var thrown = await Assert.ThrowsAsync<ChildHandlerProbeException>(() => RunAsync(
            _ => Task.FromException(workFailure), CancellationToken.None, channel));
        await serverTask;

        Assert.Same(workFailure, thrown);
    }

    [Fact]
    public async Task RunAsync_WithPartialChannel_ShouldRejectBeforeInvokingWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("partial-" + Guid.NewGuid().ToString("N"));
        var partial = session.Environment.Where(pair => pair.Key != EvidenceChildLifetimeSession.NonceEnvironmentName)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None, partial));
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(EvidenceChildLifetimeSession.PipeEnvironmentName)]
    [InlineData(EvidenceChildLifetimeSession.ExecutionEnvironmentName)]
    [InlineData(EvidenceChildLifetimeSession.NonceEnvironmentName)]
    public async Task RunAsync_WithPresentNullChannelValue_ShouldRejectBeforeInvokingWork(string key)
    {
        var environment = new Dictionary<string, string> { [key] = null! };
        var invoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None, environment));

        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_WithWrongNonce_ShouldRejectBeforeInvokingWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("nonce-" + Guid.NewGuid().ToString("N"));
        var invalid = session.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        invalid[EvidenceChildLifetimeSession.NonceEnvironmentName] = "badnonce";
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None, invalid));
        Assert.False(invoked);
        session.CloseEnrollment();
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WithUnreachableCompleteChannel_ShouldRejectBeforeInvokingWork()
    {
        var session = EvidenceChildLifetimeSession.Start("unreachable-" + Guid.NewGuid().ToString("N"));
        var values = session.Environment;
        await session.DisposeAsync();
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None, values));
        Assert.False(invoked);
    }

    [Fact]
    public async Task CloseEnrollment_BeforeBegin_ShouldRejectBeforeWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("closed-" + Guid.NewGuid().ToString("N"));
        session.CloseEnrollment();
        var invoked = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None, session.Environment));
        Assert.False(invoked);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task CloseEnrollment_AfterBegin_ShouldWaitForCompletion()
    {
        await using var session = EvidenceChildLifetimeSession.Start("join-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, CancellationToken.None, session.Environment);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            session.CloseEnrollment();
            Assert.False(await session.WaitForSettlementAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.False(session.IsSettled);
            release.SetResult();
            await child;
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.True(session.IsSettled);
        }
        finally
        {
            release.TrySetResult();
            await child;
        }
    }

    [Fact]
    public async Task Cancellation_ShouldRetainEnrollmentUntilIgnoredTokenTaskActuallyEnds()
    {
        await using var session = EvidenceChildLifetimeSession.Start("cancel-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, cancellation.Token, session.Environment);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            session.CloseEnrollment();
            cancellation.Cancel();
            Assert.False(child.IsCompleted);
            Assert.False(await session.WaitForSettlementAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            try { await child; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task DisconnectedAdmittedClient_ShouldRemainUnknownWhileControlledWorkLives()
    {
        await using var session = EvidenceChildLifetimeSession.Start("disconnect-" + Guid.NewGuid().ToString("N"));
        var variables = session.Environment;
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childWork = Task.CompletedTask;
        await using (var pipe = new NamedPipeClientStream(".", variables[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(3000);
            await EvidenceOwnedChildWire.WriteFrameAsync(pipe,
                $"BEGIN|{session.ExecutionId}|{variables[EvidenceChildLifetimeSession.NonceEnvironmentName]}", CancellationToken.None);
            var ack = await EvidenceOwnedChildWire.ReadFrameAsync(pipe, CancellationToken.None);
            Assert.StartsWith("ACK|", ack, StringComparison.Ordinal);
            childWork = Task.Run(async () =>
            {
                childStarted.SetResult();
                await childRelease.Task;
            });
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }

        session.CloseEnrollment();
        Assert.False(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(childWork.IsCompleted);
        Assert.True(session.IsUnknown);
        Assert.False(session.IsSettled);
        childRelease.SetResult();
        await childWork;
        Assert.True(session.IsUnknown);
        Assert.False(session.IsSettled);
    }

    [Fact]
    public async Task DisposeAsync_ShouldJoinAdmittedWorkWithoutDisconnectingIt()
    {
        var session = EvidenceChildLifetimeSession.Start("dispose-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, CancellationToken.None, session.Environment);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var dispose = session.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.False(child.IsCompleted);
        release.SetResult();
        await child;
        await dispose.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(session.IsSettled);
        Assert.False(session.IsUnknown);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCancelUnadmittedBeginHandshake()
    {
        var session = EvidenceChildLifetimeSession.Start("pending-" + Guid.NewGuid().ToString("N"));
        await using var pipe = new NamedPipeClientStream(".", session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000);

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(session.IsUnknown);
        Assert.True(session.IsSettled);
    }

    [Fact]
    public async Task DisposeAsync_ShouldRethrowOriginalUnexpectedHandlerFailureAfterJoin()
    {
        var session = EvidenceChildLifetimeSession.Start("fault-" + Guid.NewGuid().ToString("N"));
        var original = new ChildHandlerProbeException();
        var acknowledgementCount = 0;
        session.BeforeChildAcknowledgementForTests = () =>
        {
            if (Interlocked.Increment(ref acknowledgementCount) == 2) throw original;
        };
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeChild = RunAsync(async _ =>
        {
            activeStarted.SetResult();
            await release.Task;
        }, CancellationToken.None, session.Environment);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var rejectedWork = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            rejectedWork = true;
            return Task.CompletedTask;
        }, CancellationToken.None, session.Environment));
        Assert.False(rejectedWork);
        Assert.True(session.IsUnknown);

        var dispose = session.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        release.SetResult();
        await activeChild;
        var rethrown = await Assert.ThrowsAsync<ChildHandlerProbeException>(() => dispose);
        Assert.Same(original, rethrown);
    }

    [Fact]
    public async Task MoreThanMaximumChildren_ShouldBeRejectedBeforeWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("capacity-" + Guid.NewGuid().ToString("N"));
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var children = Enumerable.Range(0, EvidenceOwnedChild.MaximumChildren)
            .Select(_ => RunAsync(async _ =>
            {
                if (Interlocked.Increment(ref entered) == EvidenceOwnedChild.MaximumChildren) allEntered.TrySetResult();
                await release.Task;
            }, CancellationToken.None, session.Environment)).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var rejectedWork = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ =>
            {
                rejectedWork = true;
                return Task.CompletedTask;
            }, CancellationToken.None, session.Environment));
            Assert.False(rejectedWork);
            session.CloseEnrollment();
            release.SetResult();
            await Task.WhenAll(children);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(children); }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task RunProcessAsync_ShouldAcknowledgeOnlyAfterRootExit()
    {
        await using var session = EvidenceChildLifetimeSession.Start("process-" + Guid.NewGuid().ToString("N"));
        var startInfo = new ProcessStartInfo("dotnet", "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var exitCode = await RunProcessAsync(startInfo, CancellationToken.None, session.Environment);
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(EvidenceChildLifetimeSession.PipeEnvironmentName, startInfo.Environment.Keys);
        Assert.DoesNotContain(EvidenceChildLifetimeSession.ExecutionEnvironmentName, startInfo.Environment.Keys);
        Assert.DoesNotContain(EvidenceChildLifetimeSession.NonceEnvironmentName, startInfo.Environment.Keys);
        session.CloseEnrollment();
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
    }

    private static Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment)
        => EvidenceOwnedChild.RunAsync(work, environment, cancellationToken);

    private static Task<int> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment)
        => EvidenceOwnedChild.RunProcessAsync(startInfo, environment, cancellationToken);

    private sealed class ChildHandlerProbeException : Exception { }
}
