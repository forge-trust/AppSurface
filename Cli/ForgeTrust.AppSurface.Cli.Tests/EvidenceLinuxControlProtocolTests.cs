using System.Net.Sockets;
using System.Runtime.InteropServices;
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
