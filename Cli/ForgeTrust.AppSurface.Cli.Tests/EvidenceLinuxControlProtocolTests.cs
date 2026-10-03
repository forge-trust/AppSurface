using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceLinuxControlProtocolTests
{
    [Fact]
    public async Task ConnectAsync_rejects_invalid_control_paths_without_disclosing_them()
    {
        var paths = new[] { "relative-control-protocol-secret.sock", "/" + new string('a', 101) };
        foreach (var socketPath in paths)
        {
            var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(
                () => EvidenceLinuxWorkerSupervisor.ConnectAsync(socketPath, CancellationToken.None));

            Assert.Equal("ASEVD402", exception.Code);
            Assert.DoesNotContain(socketPath, exception.Message);
            Assert.Contains(OperatingSystem.IsLinux()
                ? "control channel is invalid"
                : "unavailable on this platform", exception.Message);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task ConnectAsync_RejectsMissingChannelBeforeOpeningASocket(string? socketPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedAsync(socketPath!);
            return;
        }

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            EvidenceLinuxWorkerSupervisor.ConnectAsync(socketPath!, CancellationToken.None));
        Assert.Equal("socketPath", exception.ParamName);
    }

    [Fact]
    public async Task ConnectAsync_CancelledAttemptCannotAuthenticateAndNonRootPeerReceivesNoReadyRequest()
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedAsync("/tmp/evidence-control-untrusted.sock");
            return;
        }

        // SO_PEERCRED authenticates the actual UID. This fixture creates no root peer
        // and must run in the same non-root worker as the protected consumer tests.
        Assert.True(GetUid() != 0, "The untrusted-peer fixture requires a non-root Linux test host.");
        var directory = TestPathUtils.PathUnder(Path.GetTempPath(), "ehcp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var socketPath = TestPathUtils.PathUnder(directory, "untrusted.sock");
            Assert.InRange(socketPath.Length, 1, 100);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EvidenceLinuxWorkerSupervisor.ConnectAsync(socketPath, cancelled.Token));
            Assert.False(listener.Poll(0, SelectMode.SelectRead));

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var observation = CountRequestBytesAsync(listener, deadline.Token);
            var rejected = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
                EvidenceLinuxWorkerSupervisor.ConnectAsync(socketPath, deadline.Token));

            Assert.Equal("ASEVD402", rejected.Code);
            Assert.Contains("A root-owned independent supervisor is required.", rejected.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(socketPath, rejected.Message, StringComparison.Ordinal);
            Assert.Equal(0, await observation);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public Task CollectArtifactsAsync_RejectsNegativeLengthBeforeRequestingArtifactBytes() =>
        AssertArtifactRejectionAsync("protocol-negative-length", "declarations exceed", "ready", "artifacts", "stop", "wait");

    [Fact]
    public Task CollectArtifactsAsync_RejectsChunkExceedingDeclaredLengthWithoutReturningArtifacts() =>
        AssertArtifactRejectionAsync("protocol-declared-length", "declared size", "ready", "artifacts", "artifact", "stop", "wait");

    [Fact]
    public Task CollectArtifactsAsync_RejectsEncodedChunkLimitBeforeDecodingWithoutReturningArtifacts() =>
        AssertArtifactRejectionAsync("protocol-encoded-limit", "chunk exceeds", "ready", "artifacts", "artifact", "stop", "wait");

    [Fact]
    public async Task WaitForOwnedExitAsync_RejectsFalseAcknowledgementWithoutCompletingWorker()
    {
        var fixture = await RequireRootProtocolFixtureAsync("protocol-owned-exit-false");
        if (fixture is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var supervisor = await EvidenceLinuxWorkerSupervisor.ConnectAsync(fixture.Socket, deadline.Token);
        try
        {
            AssertAuthenticatedTestHost(supervisor, fixture);
        }
        finally
        {
            // Close admission even if an identity assertion fails; the root owner still joins physical work.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await supervisor.RequestStopAsync(cleanup.Token);
        }
        // A false wait must never become a terminal exit acknowledgement.
        var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await supervisor.WaitForOwnedExitAsync(deadline.Token));
        Assert.Equal("ASEVD410", exception.Code);
        Assert.Contains("exit was not confirmed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "ready", "stop", "wait" }, ReadProtocolOperations(fixture));
    }

    private static async Task AssertArtifactRejectionAsync(string scenario, string message, params string[] operations)
    {
        var fixture = await RequireRootProtocolFixtureAsync(scenario);
        if (fixture is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var supervisor = await EvidenceLinuxWorkerSupervisor.ConnectAsync(fixture.Socket, deadline.Token);
        try
        {
            AssertAuthenticatedTestHost(supervisor, fixture);
            IReadOnlyList<EvidenceRestrictedArtifact>? artifacts = null;
            var exception = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            {
                artifacts = await supervisor.CollectArtifactsAsync("coverage-protocol", deadline.Token);
            });
            Assert.Equal("ASEVD420", exception.Code);
            Assert.Contains(message, exception.Message, StringComparison.Ordinal);
            Assert.Null(artifacts);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await supervisor.RequestStopAsync(cleanup.Token);
            await supervisor.WaitForOwnedExitAsync(cleanup.Token);
        }
        Assert.Equal(operations, ReadProtocolOperations(fixture));
    }

    private static async Task<RootProtocolFixture?> RequireRootProtocolFixtureAsync(string scenario)
    {
        if (!OperatingSystem.IsLinux())
        {
            await AssertUnsupportedAsync("/tmp/evidence-root-protocol.sock");
            return null;
        }
        var directory = Environment.GetEnvironmentVariable("EVIDENCEHOST_TEST_BROKER_SOCKET");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Linux protocol controls require the actual root broker fixture.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(
            TestPathUtils.PathUnder(directory!, scenario + ".json"), deadline.Token));
        var root = metadata.RootElement;
        return new RootProtocolFixture(root.GetProperty("socket").GetString()!,
            root.GetProperty("operationsFile").GetString()!, root.GetProperty("peerFile").GetString()!,
            root.GetProperty("workerUid").GetUInt32(), root.GetProperty("workerGid").GetUInt32(),
            root.GetProperty("subjectUid").GetUInt32(), root.GetProperty("subjectGid").GetUInt32());
    }

    private static void AssertAuthenticatedTestHost(EvidenceLinuxWorkerSupervisor supervisor, RootProtocolFixture fixture)
    {
        Assert.Equal(Environment.ProcessId, supervisor.Descriptor.WorkerPid);
        var workerUid = GetUid();
        var workerGid = GetGid();
        Assert.NotEqual(0u, workerUid);
        Assert.NotEqual(0u, workerGid);
        Assert.Equal(fixture.WorkerUid, workerUid);
        Assert.Equal(fixture.WorkerGid, workerGid);
        Assert.Equal(fixture.WorkerUid, supervisor.Descriptor.WorkerUid);
        Assert.Equal(fixture.WorkerGid, supervisor.Descriptor.WorkerGid);
        Assert.NotEqual(0u, fixture.SubjectUid);
        Assert.NotEqual(0u, fixture.SubjectGid);
        Assert.NotEqual(fixture.WorkerUid, fixture.SubjectUid);
        Assert.NotEqual(fixture.WorkerGid, fixture.SubjectGid);
        Assert.Equal(fixture.SubjectUid, supervisor.Descriptor.SubjectUid);
        Assert.Equal(fixture.SubjectGid, supervisor.Descriptor.SubjectGid);
        Assert.True(supervisor.IsArmed);
        using var peer = JsonDocument.Parse(File.ReadAllBytes(fixture.PeerFile));
        Assert.Equal(Environment.ProcessId, peer.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(workerUid, peer.RootElement.GetProperty("uid").GetUInt32());
        Assert.Equal(workerGid, peer.RootElement.GetProperty("gid").GetUInt32());
    }

    private static string[] ReadProtocolOperations(RootProtocolFixture fixture) => File.ReadAllLines(fixture.OperationsFile)
        .Select(static line =>
        {
            using var record = JsonDocument.Parse(line);
            return record.RootElement.GetProperty("op").GetString()!;
        }).ToArray();

    private sealed record RootProtocolFixture(string Socket, string OperationsFile, string PeerFile,
        uint WorkerUid, uint WorkerGid, uint SubjectUid, uint SubjectGid);

    [DllImport("libc", EntryPoint = "getgid")]
    private static extern uint GetGid();

    private static async Task<int> CountRequestBytesAsync(Socket listener, CancellationToken cancellationToken)
    {
        using var peer = await listener.AcceptAsync(cancellationToken);
        // A single received byte would prove a ready request escaped authentication.
        // EOF is the supported neighbor: connection succeeded, authority was rejected,
        // and the client closed before transmitting any operation or descriptor input.
        return await peer.ReceiveAsync(new byte[1], SocketFlags.None, cancellationToken);
    }

    private static async Task AssertUnsupportedAsync(string socketPath)
    {
        var unsupported = await Assert.ThrowsAsync<EvidenceAdmissionException>(() =>
            EvidenceLinuxWorkerSupervisor.ConnectAsync(socketPath, CancellationToken.None));
        Assert.Equal("ASEVD402", unsupported.Code);
        Assert.Contains("Linux worker supervision is unavailable on this platform.", unsupported.Message, StringComparison.Ordinal);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
