using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceChildProtocolCoverageTests
{
    [Fact]
    public async Task BeginHandlerJoinsTimerCallbackBeforeDisposingItsCancellationSource()
    {
        var clock = new ControlledTimerProvider();
        var session = EvidenceChildLifetimeSession.Start("begin-timer-join-" + Guid.NewGuid().ToString("N"), clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ControlledTimer? timer = null;
        try
        {
            await using var pipe = new NamedPipeClientStream(".",
                session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, deadline.Token);
            timer = await clock.TimerCreated.Task.WaitAsync(deadline.Token);
            timer.StartBlockedCallback();
            await timer.CallbackStarted.Task.WaitAsync(deadline.Token);

            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe,
                $"BEGIN|{session.ExecutionId}|{session.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName]}", deadline.Token);
            var acknowledgementTask = EvidenceOwnedChildProtocol.ReadFrameAsync(pipe, deadline.Token);
            await timer.DisposeStarted.Task.WaitAsync(deadline.Token);

            Assert.False(acknowledgementTask.IsCompleted);

            timer.ReleaseCallback.TrySetResult();
            await timer.CallbackTask.WaitAsync(deadline.Token);
            var acknowledgement = await acknowledgementTask;
            Assert.StartsWith("ACK|", acknowledgement ?? string.Empty, StringComparison.Ordinal);
            var childId = acknowledgement!["ACK|".Length..];
            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe, $"COMPLETE|{childId}", deadline.Token);
            Assert.Equal($"DONE|{childId}", await EvidenceOwnedChildProtocol.ReadFrameAsync(pipe, deadline.Token));

            Assert.Equal(1, timer.DisposeAsyncCalls);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(5), deadline.Token));
        }
        finally
        {
            timer?.ReleaseCallback.TrySetResult();
            deadline.Cancel();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ListenerCreationIoFailureDisablesEnrollmentWithoutInventingUnknownWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("listener-io-failure-" + Guid.NewGuid().ToString("N"),
            beforeListenerCreation: () => throw new IOException("controlled listener setup failure"));

        Assert.False(session.CanAcceptChildren);
        Assert.False(session.IsUnknown);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(session.IsSettled);
    }

    [Fact]
    public async Task UnexpectedListenerCreationFailureIsRethrownAfterListenerJoin()
    {
        var expected = new ChildListenerProbeException();
        var session = EvidenceChildLifetimeSession.Start("listener-unexpected-" + Guid.NewGuid().ToString("N"),
            beforeListenerCreation: () => throw expected);
        try
        {
            Assert.False(session.CanAcceptChildren);
            Assert.False(session.IsUnknown);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));

            var thrown = await Assert.ThrowsAsync<ChildListenerProbeException>(() =>
                session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(expected, thrown);
            Assert.True(session.IsSettled);
        }
        finally
        {
            try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ChildListenerProbeException) { }
        }
    }

    [Fact]
    public async Task EnrollmentClosedDuringNextListenerCreationDisposesUnpublishedListener()
    {
        var secondCreationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EvidenceChildLifetimeSession? session = null;
        var creationCalls = 0;
        session = EvidenceChildLifetimeSession.Start("close-before-listener-" + Guid.NewGuid().ToString("N"),
            beforeListenerCreation: () =>
            {
                if (Interlocked.Increment(ref creationCalls) == 2)
                {
                    session!.CloseEnrollment();
                    secondCreationReached.TrySetResult();
                }
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(3000, timeout.Token);
            await secondCreationReached.Task.WaitAsync(timeout.Token);

            await session.DisposeAsync().AsTask().WaitAsync(timeout.Token);

            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);
            Assert.False(session.CanAcceptChildren);
            Assert.Equal(2, Volatile.Read(ref creationCalls));
        }
        finally
        {
            session.CloseEnrollment();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConnectionCompletedAfterEnrollmentClosedIsDisposedWithoutPendingAdmission()
    {
        var connectionAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWaitCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitCalls = 0;
        var session = EvidenceChildLifetimeSession.Start("close-after-listener-" + Guid.NewGuid().ToString("N"),
            waitForConnectionForTests: async (pipe, token) =>
            {
                if (Interlocked.Increment(ref waitCalls) == 1)
                {
                    await pipe.WaitForConnectionAsync(token);
                    connectionAccepted.TrySetResult();
                    await releaseWaitCallback.Task.ConfigureAwait(false);
                    return;
                }
                await pipe.WaitForConnectionAsync(token);
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(3000, timeout.Token);
            await connectionAccepted.Task.WaitAsync(timeout.Token);

            session.CloseEnrollment();
            releaseWaitCallback.TrySetResult();
            await session.DisposeAsync().AsTask().WaitAsync(timeout.Token);

            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);
            Assert.False(session.CanAcceptChildren);
            Assert.Equal(1, Volatile.Read(ref waitCalls));
        }
        finally
        {
            session.CloseEnrollment();
            releaseWaitCallback.TrySetResult();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ListenerCreationCancellationObservedDuringDisposeIsJoinedAndSuppressed()
    {
        var cancellationRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EvidenceChildLifetimeSession? session = null;
        var creationCalls = 0;
        session = EvidenceChildLifetimeSession.Start("dispose-listener-cancel-" + Guid.NewGuid().ToString("N"),
            beforeListenerCreation: () =>
            {
                if (Interlocked.Increment(ref creationCalls) == 2)
                {
                    session!.CloseEnrollment();
                    cancellationRaised.TrySetResult();
                    throw new OperationCanceledException("controlled listener setup cancellation");
                }
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(3000, timeout.Token);
            await cancellationRaised.Task.WaitAsync(timeout.Token);

            await session.DisposeAsync().AsTask().WaitAsync(timeout.Token);

            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);
            Assert.False(session.CanAcceptChildren);
            Assert.Equal(2, Volatile.Read(ref creationCalls));
        }
        finally
        {
            session.CloseEnrollment();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task InvalidExecutionIdIsRejectedBeforeChildWorkRuns()
    {
        var session = EvidenceChildLifetimeSession.Start("protocol-" + Guid.NewGuid().ToString("N"));
        await using (session)
        {
            var environment = new Dictionary<string, string>(session.Environment, StringComparer.Ordinal)
            {
                [EvidenceChildLifetimeSession.ExecutionEnvironmentName] = "invalid id",
            };
            var invoked = false;

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceOwnedChild.RunAsync(_ =>
            {
                invoked = true;
                return Task.CompletedTask;
            }, environment, CancellationToken.None));

            Assert.Contains("valid current-user Evidence invocation channel", rejected.Message, StringComparison.Ordinal);
            Assert.False(invoked);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.False(session.IsUnknown);
        }
    }

    [Fact]
    public async Task AuthenticationMismatchIsRejectedWithoutAdmittingChildWork()
    {
        await using var session = EvidenceChildLifetimeSession.Start("auth-" + Guid.NewGuid().ToString("N"));
        var environment = new Dictionary<string, string>(session.Environment, StringComparer.Ordinal)
        {
            [EvidenceChildLifetimeSession.NonceEnvironmentName] = (session.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName][0] == '0' ? "1" : "0")
                + session.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName][1..],
        };
        var invoked = false;

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceOwnedChild.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, environment, CancellationToken.None));

        Assert.Contains("rejected", rejected.Message, StringComparison.Ordinal);
        Assert.False(invoked);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(session.IsUnknown);
    }

    [Fact]
    public async Task RunProcessRejectsShellExecutionBeforeOpeningEnrollmentChannel()
    {
        await using var session = EvidenceChildLifetimeSession.Start("shell-child-" + Guid.NewGuid().ToString("N"));
        var startInfo = new ProcessStartInfo("unused-command") { UseShellExecute = true };

        var rejected = await Assert.ThrowsAsync<ArgumentException>(() => EvidenceOwnedChild.RunProcessAsync(
            startInfo, session.Environment, CancellationToken.None));

        Assert.Contains("UseShellExecute=false", rejected.Message, StringComparison.Ordinal);
        Assert.True(session.CanAcceptChildren);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(session.IsUnknown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunProcessClosesOnlyConfiguredStandardInputAndCompletesEnrollment(bool redirectInput)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "process-input-" + Guid.NewGuid().ToString("N")[..12];
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var childId = Guid.NewGuid().ToString("N");
        var serverTask = Task.Run(async () =>
        {
            await using (server)
            {
                await server.WaitForConnectionAsync(deadline.Token);
                Assert.StartsWith("BEGIN|", await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "ACK|" + childId, deadline.Token);
                Assert.Equal("COMPLETE|" + childId,
                    await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "DONE|" + childId, deadline.Token);
            }
        }, deadline.Token);
        Task<int>? processTask = null;
        try
        {
            var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sh")
            {
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            if (redirectInput)
            {
                if (OperatingSystem.IsWindows())
                {
                    startInfo.ArgumentList.Add("/d");
                    startInfo.ArgumentList.Add("/c");
                    startInfo.ArgumentList.Add("more >nul");
                }
                else
                {
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add("cat >/dev/null");
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("exit 0");
            }
            else
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("exit 0");
            }
            processTask = EvidenceOwnedChild.RunProcessAsync(startInfo, Channel(pipeName), deadline.Token);

            Assert.Equal(0, await processTask.WaitAsync(deadline.Token));
            await serverTask.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            finally
            {
                if (processTask is not null)
                {
                    try { await processTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (InvalidOperationException) { }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                }
                await server.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(false, "did not acknowledge cooperative child completion")]
    [InlineData(true, "channel was lost before cooperative child completion")]
    public async Task RunProcessReportsCompletionChannelDisconnectAfterJoiningTheRootProcess(
        bool truncatedReply, string diagnostic)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "process-done-drop-" + Guid.NewGuid().ToString("N")[..10];
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var childId = Guid.NewGuid().ToString("N");
        var serverTask = Task.Run(async () =>
        {
            await using (server)
            {
                await server.WaitForConnectionAsync(deadline.Token);
                Assert.StartsWith("BEGIN|", await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "ACK|" + childId, deadline.Token);
                Assert.Equal("COMPLETE|" + childId,
                    await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                if (truncatedReply) await server.WriteAsync("DONE|unfinished"u8.ToArray(), deadline.Token);
                // Empty EOF refuses acknowledgement; a partial frame also proves transport-loss reporting.
            }
        }, deadline.Token);
        Task<int>? processTask = null;
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            processTask = EvidenceOwnedChild.RunProcessAsync(startInfo, Channel(pipeName), deadline.Token);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => processTask.WaitAsync(deadline.Token));
            Assert.Contains(diagnostic, failure.Message, StringComparison.Ordinal);
            await serverTask.WaitAsync(deadline.Token);
            Assert.True(processTask.IsFaulted);
        }
        finally
        {
            deadline.Cancel();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            finally
            {
                if (processTask is not null)
                {
                    try { await processTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (InvalidOperationException) { }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                }
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task OversizedBeginFrameIsDiscardedWithoutLeavingAnActiveEnrollment()
    {
        await using var session = EvidenceChildLifetimeSession.Start("frame-" + Guid.NewGuid().ToString("N"));
        await using (var pipe = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(3000);
            var oversized = new byte[EvidenceOwnedChild.MaximumFrameBytes + 1];
            Array.Fill(oversized, (byte)'x');
            try { await pipe.WriteAsync(oversized); }
            catch (IOException) { /* The server may close the rejected oversized frame before the client flushes it. */ }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = new byte[1];
            try { Assert.Equal(0, await pipe.ReadAsync(response, timeout.Token)); }
            catch (IOException error) when (error.InnerException is SocketException
            { SocketErrorCode: SocketError.ConnectionReset })
            {
                // Unix can reset the unadmitted pipe when its oversized frame is rejected.
            }
        }

        var admitted = false;
        using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await EvidenceOwnedChild.RunAsync(_ =>
        {
            admitted = true;
            return Task.CompletedTask;
        }, session.Environment, childTimeout.Token);
        Assert.True(admitted);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(session.IsUnknown);
    }

    [Fact]
    public async Task DisconnectAfterAcknowledgementBecomesStickyUnknownState()
    {
        await using var session = EvidenceChildLifetimeSession.Start("disconnect-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var pipe = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(3000, deadline.Token);
            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe,
                $"BEGIN|{session.ExecutionId}|{session.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName]}", deadline.Token);
            var acknowledgement = await EvidenceOwnedChildProtocol.ReadFrameAsync(pipe, deadline.Token);
            Assert.StartsWith("ACK|", acknowledgement ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.False(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(session.IsUnknown);
        Assert.False(session.IsSettled);
        Assert.False(session.CanAcceptChildren);
    }

    [Fact]
    public async Task MismatchedCompletionFrameMakesAdmittedChildLifetimeUnknown()
    {
        await using var session = EvidenceChildLifetimeSession.Start("wrong-complete-" + Guid.NewGuid().ToString("N"));
        await using (var pipe = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(3000, timeout.Token);
            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe,
                $"BEGIN|{session.ExecutionId}|{session.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName]}", timeout.Token);
            var acknowledgement = await EvidenceOwnedChildProtocol.ReadFrameAsync(pipe, timeout.Token);
            Assert.StartsWith("ACK|", acknowledgement ?? string.Empty, StringComparison.Ordinal);
            var childId = acknowledgement!["ACK|".Length..];
            Assert.Matches("^[0-9a-f]{32}$", childId);
            var mismatchedChildId = (childId[0] == '0' ? '1' : '0') + childId[1..];

            await EvidenceOwnedChildProtocol.WriteFrameAsync(pipe, $"COMPLETE|{mismatchedChildId}", timeout.Token);
        }

        Assert.False(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(session.IsUnknown);
        Assert.False(session.IsSettled);
        Assert.False(session.CanAcceptChildren);
    }

    [Fact]
    public async Task TruncatedBeginFrameSettlesAsKnownUnadmittedConnection()
    {
        await using var session = EvidenceChildLifetimeSession.Start("truncated-begin-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var pipe = new NamedPipeClientStream(".",
            session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(3000, timeout.Token);
            await pipe.WriteAsync("BEGIN|truncated"u8.ToArray(), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }

        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(session.IsSettled);
        Assert.False(session.IsUnknown);
        Assert.False(session.CanAcceptChildren);
    }

    [Fact]
    public async Task ClosingEnrollmentDisposesAConnectedClientBeforeBeginWithoutUnknownWork()
    {
        var session = EvidenceChildLifetimeSession.Start("pending-close-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(".",
                session.Environment[EvidenceChildLifetimeSession.PipeEnvironmentName], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, timeout.Token);

            session.CloseEnrollment();

            var response = new byte[1];
            var peerClosed = false;
            try { peerClosed = await pipe.ReadAsync(response, timeout.Token) == 0; }
            catch (IOException) { peerClosed = true; }
            Assert.True(peerClosed, "Closing enrollment must close the connected, not-yet-admitted client pipe.");
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);
            Assert.False(session.CanAcceptChildren);
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ListenerRetriesOnceAfterNativeWaitIOExceptionAndJoinsTheRealChild()
    {
        var waitCalls = 0;
        var session = EvidenceChildLifetimeSession.Start("listener-retry-" + Guid.NewGuid().ToString("N"),
            waitForConnectionForTests: (pipe, token) => Interlocked.Increment(ref waitCalls) == 1
                ? Task.FromException(new IOException("synthetic first wait failure"))
                : pipe.WaitForConnectionAsync(token));
        using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task? childTask = null;
        var childInvoked = false;
        try
        {
            childTask = EvidenceOwnedChild.RunAsync(_ =>
            {
                childInvoked = true;
                return Task.CompletedTask;
            }, session.Environment, childTimeout.Token);
            await childTask.WaitAsync(TimeSpan.FromSeconds(7));

            Assert.True(waitCalls >= 2);
            Assert.True(childInvoked);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);
        }
        finally
        {
            await JoinActualChildAndDisposeSessionAsync(childTask, childTimeout, session);
        }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    public async Task ListenerFailureDuringShutdownDoesNotUnsettleAdmittedChild(string failureKind)
    {
        var secondWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitCalls = 0;
        var session = EvidenceChildLifetimeSession.Start("listener-shutdown-" + Guid.NewGuid().ToString("N"),
            waitForConnectionForTests: (pipe, token) => Interlocked.Increment(ref waitCalls) == 1
                ? pipe.WaitForConnectionAsync(token)
                : WaitForReleaseThenFailAsync(failureKind));
        session.BeforeChildAcknowledgementForTests = () => childAdmitted.TrySetResult();
        using var childTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task? childTask = null;
        try
        {
            childTask = EvidenceOwnedChild.RunAsync(_ => Task.CompletedTask, session.Environment, childTimeout.Token);
            await childAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await secondWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

            session.CloseEnrollment();
            releaseSecondWait.TrySetResult();

            await childTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            Assert.True(waitCalls >= 2);
            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);
        }
        finally
        {
            releaseSecondWait.TrySetResult();
            await JoinActualChildAndDisposeSessionAsync(childTask, childTimeout, session);
        }

        async Task WaitForReleaseThenFailAsync(string kind)
        {
            secondWaitStarted.TrySetResult();
            await releaseSecondWait.Task.ConfigureAwait(false);
            switch (kind)
            {
                case "io": throw new IOException("synthetic listener wait failure after enrollment closed");
                case "disposed": throw new ObjectDisposedException("synthetic listener wait");
                case "cancelled": throw new OperationCanceledException("synthetic listener wait cancellation");
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }

    [Fact]
    public async Task UnexpectedListenerWaitFailureIsRethrownOnlyAfterAcceptTaskJoins()
    {
        var expected = new ChildListenerProbeException();
        var session = EvidenceChildLifetimeSession.Start("listener-wait-unexpected-" + Guid.NewGuid().ToString("N"),
            waitForConnectionForTests: (_, _) => Task.FromException(expected));
        try
        {
            Assert.False(session.CanAcceptChildren);
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.True(session.IsSettled);
            Assert.False(session.IsUnknown);

            var thrown = await Assert.ThrowsAsync<ChildListenerProbeException>(() =>
                session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(expected, thrown);
        }
        finally
        {
            try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ChildListenerProbeException) { }
        }
    }

    [Theory]
    [InlineData("empty-eof")]
    [InlineData("truncated")]
    public async Task ProtocolReaderDistinguishesCleanEofFromInvalidFrames(string frameKind)
    {
        var bytes = frameKind switch
        {
            "empty-eof" => [],
            _ => "BEGIN|partial"u8.ToArray()
        };
        using var stream = new MemoryStream(bytes, writable: false);

        if (frameKind == "empty-eof")
        {
            Assert.Null(await EvidenceOwnedChildProtocol.ReadFrameAsync(stream, CancellationToken.None));
            return;
        }

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            EvidenceOwnedChildProtocol.ReadFrameAsync(stream, CancellationToken.None));
        Assert.Contains("Truncated", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedProtocolWriteFailsBeforeEmittingAnyFrameBytes()
    {
        using var stream = new MemoryStream();

        var failure = await Assert.ThrowsAsync<IOException>(() => EvidenceOwnedChildProtocol.WriteFrameAsync(
            stream, new string('x', EvidenceOwnedChild.MaximumFrameBytes), CancellationToken.None));

        Assert.Contains("exceeds its limit", failure.Message, StringComparison.Ordinal);
        Assert.Empty(stream.ToArray());
    }

    [Theory]
    [InlineData("io", false)]
    [InlineData("disposed", false)]
    [InlineData("cancelled", false)]
    [InlineData("unexpected", true)]
    public async Task BestEffortRejectionWriteSuppressesOnlyTransportFailures(string failureKind, bool propagates)
    {
        Exception expected = failureKind switch
        {
            "io" => new IOException("controlled write failure"),
            "disposed" => new ObjectDisposedException("controlled pipe"),
            "cancelled" => new OperationCanceledException("controlled write cancellation"),
            _ => new InvalidOperationException("unexpected stream failure")
        };
        using var stream = new ThrowingWriteStream(expected);

        if (propagates)
        {
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EvidenceOwnedChildProtocol.TryWriteFrameAsync(stream, "REJECT|invalid", CancellationToken.None));
            Assert.Same(expected, actual);
        }
        else
        {
            await EvidenceOwnedChildProtocol.TryWriteFrameAsync(stream, "REJECT|invalid", CancellationToken.None);
        }
    }

    private static async Task JoinActualChildAndDisposeSessionAsync(Task? childTask,
        CancellationTokenSource childTimeout, EvidenceChildLifetimeSession session)
    {
        childTimeout.Cancel();
        try
        {
            if (childTask is not null) await childTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (childTimeout.IsCancellationRequested) { }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static IReadOnlyDictionary<string, string> Channel(string pipeName) => new Dictionary<string, string>
    {
        [EvidenceChildLifetimeSession.PipeEnvironmentName] = pipeName,
        [EvidenceChildLifetimeSession.ExecutionEnvironmentName] = "protocol-process-fixture",
        [EvidenceChildLifetimeSession.NonceEnvironmentName] = "protocol-process-nonce"
    };

    private sealed class ChildListenerProbeException : Exception { }

    private sealed class ControlledTimerProvider : TimeProvider
    {
        internal TaskCompletionSource<ControlledTimer> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state);
            TimerCreated.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
    {
        private Task? _callbackTask;
        private int _disposeAsyncCalls;

        internal TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeAsyncCalls => Volatile.Read(ref _disposeAsyncCalls);
        internal Task CallbackTask => Volatile.Read(ref _callbackTask) ?? Task.CompletedTask;

        internal void StartBlockedCallback()
        {
            var task = Task.Run(() =>
            {
                CallbackStarted.TrySetResult();
                ReleaseCallback.Task.GetAwaiter().GetResult();
                callback(state);
            });
            if (Interlocked.CompareExchange(ref _callbackTask, task, null) is not null)
                throw new InvalidOperationException("The controlled timer callback was already started.");
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeAsyncCalls);
            DisposeStarted.TrySetResult();
            return new ValueTask(Volatile.Read(ref _callbackTask) ?? Task.CompletedTask);
        }
    }

    private sealed class ThrowingWriteStream(Exception failure) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw failure;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(failure);
    }
}
