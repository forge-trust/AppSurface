using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(EvidenceReuseSyntheticLocalContextCollection.Name)]
public sealed class EvidenceOwnedChildFailureCoverageTests
{
    [Fact]
    public async Task PublicImplicitEnvironmentOverloadsJoinWorkAndProcessWithoutInventingReceiptCapability()
    {
        using var environment = new AbsentChannelEnvironment();
        var calls = 0;
        await EvidenceOwnedChild.RunAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        var process = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        process.ArgumentList.Add("--version");

        Assert.Equal(0, await EvidenceOwnedChild.RunProcessAsync(process, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ImplicitUnenrolledWorkStillReportsCancellationAfterActualCallbackCompletion()
    {
        using var environment = new AbsentChannelEnvironment();
        using var caller = new CancellationTokenSource();
        var completed = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceOwnedChild.RunAsync(_ =>
        {
            completed = true;
            caller.Cancel();
            return Task.CompletedTask;
        }, caller.Token));

        Assert.True(completed);
    }

    [Fact]
    public async Task UnexpectedListenerFailureIsRethrownAfterJoiningAndRemainsObservableOnRepeatedDisposal()
    {
        var failure = new ListenerFailure();
        var session = EvidenceChildLifetimeSession.Start("fatal-listener", beforeListenerCreation: () => throw failure);

        Assert.False(session.CanAcceptChildren);
        Assert.False(session.IsUnknown);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Same(failure, await Assert.ThrowsAsync<ListenerFailure>(() => session.DisposeAsync().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<ListenerFailure>(() => session.DisposeAsync().AsTask()));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-2L)]
    [InlineData(4294967295L)]
    public async Task InvalidSettlementGraceDoesNotCloseAnOtherwiseAvailableChannel(long milliseconds)
    {
        await using var session = EvidenceChildLifetimeSession.Start("grace-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WaitForSettlementAsync(
            TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None));

        Assert.True(session.CanAcceptChildren);
        Assert.False(session.IsUnknown);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("user")]
    [InlineData("domain")]
    [InlineData("password")]
    public async Task AlternateProcessIdentityIsRejectedBeforeStartingOrEnrolling(string kind)
    {
        if (kind is "domain" or "password" && !OperatingSystem.IsWindows()) return;
        await using var session = EvidenceChildLifetimeSession.Start("identity-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo("must-not-start") { UseShellExecute = kind == "shell" };
        if (kind == "user") info.UserName = "another-user";
        if (OperatingSystem.IsWindows())
        {
            if (kind == "domain") info.Domain = "another-domain";
            if (kind == "password") info.Password = new System.Security.SecureString();
        }

        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceOwnedChild.RunProcessAsync(info, session.Environment, CancellationToken.None));

        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(session.IsUnknown);
    }

    [Fact]
    public async Task ProcessStartFailureCompletesTheAlreadyAcknowledgedEnrollment()
    {
        using var directory = TestDirectory.Create();
        await using var session = EvidenceChildLifetimeSession.Start("start-failure-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo(TestPathUtils.PathUnder(directory.Path, "absent-executable")) { UseShellExecute = false };

        await Assert.ThrowsAsync<Win32Exception>(() => EvidenceOwnedChild.RunProcessAsync(info, session.Environment, CancellationToken.None));

        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.False(session.IsUnknown);
        Assert.DoesNotContain(EvidenceChildLifetimeSession.NonceEnvironmentName, info.Environment.Keys);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("wrong-prefix")]
    [InlineData("invalid-id")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public async Task InvalidAcknowledgementNeverInvokesTheWork(string response)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "bad-ack-" + Guid.NewGuid().ToString("N")[..12];
        await using var server = Server(pipeName);
        var serverTask = Task.Run(async () =>
        {
            using (server)
            {
                await server.WaitForConnectionAsync(deadline.Token);
                Assert.StartsWith("BEGIN|", await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                var bytes = response switch
                {
                    "empty" => [],
                    "wrong-prefix" => Encoding.UTF8.GetBytes("NOT-ACK|" + Guid.NewGuid().ToString("N") + "\n"),
                    "invalid-id" => "ACK|invalid\n"u8.ToArray(),
                    "truncated" => "ACK|unfinished"u8.ToArray(),
                    _ => Enumerable.Repeat((byte)'x', EvidenceOwnedChild.MaximumFrameBytes).ToArray()
                };
                await server.WriteAsync(bytes, deadline.Token);
            }
        }, deadline.Token);
        var invoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceOwnedChild.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, Channel(pipeName), deadline.Token));
        await serverTask;

        Assert.False(invoked);
    }

    [Fact]
    public async Task WorkFailureIsRethrownOnlyAfterCompletionIsAcknowledged()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "work-failure-" + Guid.NewGuid().ToString("N")[..12];
        var childId = Guid.NewGuid().ToString("N");
        var server = Server(pipeName);
        var completionReceived = false;
        var serverTask = Task.Run(async () =>
        {
            await using (server)
            {
                await server.WaitForConnectionAsync(deadline.Token);
                Assert.StartsWith("BEGIN|", await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "ACK|" + childId, deadline.Token);
                Assert.Equal("COMPLETE|" + childId,
                    await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                completionReceived = true;
                await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "DONE|" + childId, deadline.Token);
            }
        }, deadline.Token);
        var workFailure = new ListenerFailure();
        var workInvoked = false;
        Task? childTask = null;
        try
        {
            childTask = EvidenceOwnedChild.RunAsync(_ =>
            {
                workInvoked = true;
                return Task.FromException(workFailure);
            }, Channel(pipeName), deadline.Token);

            var actual = await Assert.ThrowsAsync<ListenerFailure>(() => childTask.WaitAsync(deadline.Token));
            await serverTask.WaitAsync(deadline.Token);

            Assert.Same(workFailure, actual);
            Assert.True(workInvoked);
            Assert.True(completionReceived);
        }
        finally
        {
            deadline.Cancel();
            await server.DisposeAsync();
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (IOException) { }
            finally
            {
                if (childTask is not null)
                {
                    try { await childTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (ListenerFailure) { }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                    catch (InvalidOperationException) when (deadline.IsCancellationRequested) { }
                }
            }
        }
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("disconnect")]
    [InlineData("truncated")]
    public async Task WorkSuccessCannotHideALostCompletionAcknowledgement(string response)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "bad-done-" + Guid.NewGuid().ToString("N")[..12];
        var childId = Guid.NewGuid().ToString("N");
        await using var server = Server(pipeName);
        var serverTask = Task.Run(async () =>
        {
            using (server)
            {
                await server.WaitForConnectionAsync(deadline.Token);
                Assert.StartsWith("BEGIN|", await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "ACK|" + childId, deadline.Token);
                Assert.Equal("COMPLETE|" + childId, await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
                if (response == "wrong-id") await EvidenceOwnedChildProtocol.WriteFrameAsync(server, "DONE|" + Guid.NewGuid().ToString("N"), deadline.Token);
                if (response == "truncated") await server.WriteAsync("DONE|unfinished"u8.ToArray(), deadline.Token);
            }
        }, deadline.Token);
        var invoked = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceOwnedChild.RunAsync(_ =>
        {
            invoked++;
            return Task.CompletedTask;
        }, Channel(pipeName), deadline.Token));
        await serverTask;

        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task CancellationWhileAwaitingAcknowledgementCannotStartWork()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        var pipeName = "cancel-ack-" + Guid.NewGuid().ToString("N")[..10];
        await using var server = Server(pipeName);
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            Assert.StartsWith("BEGIN|", await EvidenceOwnedChildProtocol.ReadFrameAsync(server, deadline.Token));
            began.SetResult();
        }, deadline.Token);
        var invoked = false;
        var child = EvidenceOwnedChild.RunAsync(_ => { invoked = true; return Task.CompletedTask; }, Channel(pipeName), caller.Token);
        await began.Task.WaitAsync(deadline.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.WaitAsync(deadline.Token));
        await serverTask;
        Assert.False(invoked);
    }

    [Fact]
    public async Task CancellationKillsAndJoinsTheOwnedRootBeforeReportingSettlement()
    {
        using var directory = TestDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        Process? ownedRoot = null;
        Task<int>? child = null;
        var repository = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var probe = TestPathUtils.PathUnder(repository, "tests", "ForgeTrust.AppSurface.CoverageLeaseProbe", "bin",
            configuration, "net10.0", "ForgeTrust.AppSurface.CoverageLeaseProbe.dll");
        Assert.True(File.Exists(probe));
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(probe);
        info.ArgumentList.Add("settlement-heartbeat-child");
        info.ArgumentList.Add(directory.Path);
        var session = EvidenceChildLifetimeSession.Start("cancel-process-" + Guid.NewGuid().ToString("N"));
        try
        {
            child = EvidenceOwnedChild.RunProcessAsync(info, session.Environment, caller.Token);
            var pid = await HeartbeatPidAsync(TestPathUtils.PathUnder(directory.Path, "heartbeat.jsonl"), deadline.Token);
            ownedRoot = Process.GetProcessById(pid);
            Assert.False(child.IsCompleted);

            caller.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child.WaitAsync(deadline.Token));
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.False(session.IsUnknown);
            Assert.True(ownedRoot.HasExited);
            Assert.DoesNotContain(EvidenceChildLifetimeSession.NonceEnvironmentName, info.Environment.Keys);
        }
        finally
        {
            caller.Cancel();
            try
            {
                // This independent fixture shutdown also works if the API's cancellation/kill path regresses.
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(directory.Path, "stop-child.txt"),
                    session.ExecutionId + "|" + session.Environment[EvidenceChildLifetimeSession.NonceEnvironmentName]);
                if (ownedRoot is not null)
                {
                    try { await ownedRoot.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (TimeoutException)
                    {
                        if (!ownedRoot.HasExited) ownedRoot.Kill();
                        await ownedRoot.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                if (child is not null) await child.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (child?.IsCompleted == true) { }
            finally
            {
                ownedRoot?.Dispose();
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task HeartbeatPidCanReadACompleteRecordWhileTheAppendWriterRemainsOpen()
    {
        using var directory = TestDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var path = TestPathUtils.PathUnder(directory.Path, "heartbeat.jsonl");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine(JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId }));
        writer.Write("{incomplete");

        Assert.Equal(Environment.ProcessId, await HeartbeatPidAsync(path, deadline.Token));
    }

    private static async Task<int> HeartbeatPidAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var contents = await reader.ReadToEndAsync(cancellationToken);
                    var newline = contents.IndexOf('\n');
                    if (newline > 0)
                    {
                        using var json = JsonDocument.Parse(contents[..newline]);
                        return json.RootElement.GetProperty("ProcessId").GetInt32();
                    }
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            // gstack-timing-ok: a bounded readiness poll, with no elapsed-time assertion.
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static IReadOnlyDictionary<string, string> Channel(string pipeName) => new Dictionary<string, string>
    {
        [EvidenceChildLifetimeSession.PipeEnvironmentName] = pipeName,
        [EvidenceChildLifetimeSession.ExecutionEnvironmentName] = "fixture-execution",
        [EvidenceChildLifetimeSession.NonceEnvironmentName] = "fixture-nonce"
    };

    private sealed class ListenerFailure : Exception;

    private sealed class AbsentChannelEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original;
        internal AbsentChannelEnvironment()
        {
            _original = new[]
            {
                EvidenceChildLifetimeSession.PipeEnvironmentName,
                EvidenceChildLifetimeSession.ExecutionEnvironmentName,
                EvidenceChildLifetimeSession.NonceEnvironmentName
            }.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            foreach (var name in _original.Keys) Environment.SetEnvironmentVariable(name, null);
        }
        public void Dispose()
        {
            foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
