using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

// protects: one-connection ownership, exact fixed-size frames, bounded reads/writes, and cancellation behavior.
// fails_when: a different owner is accepted, frame deadlines reset per partial read, or loss reconnects implicitly.
// seam: loopback TCP and an internal shortened deadline; no Aspire, Docker, or external daemon.
// no_claim: this coordination token is not authentication against host root or a trusted daemon.
public sealed class KeycloakOwnerHeartbeatTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task AcceptsAndAcknowledgesEachExactHeartbeatUntilEof()
    {
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, owner, TestTimeout, CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var frame = Frame(owner);
        await stream.WriteAsync(frame.AsMemory(0, 9));
        await stream.WriteAsync(frame.AsMemory(9));

        await using var heartbeat = await accepting;
        await heartbeat.AcknowledgeAsync(CancellationToken.None);
        Assert.Equal((byte)'R', await ReadAcknowledgementAsync(stream));

        var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);
        await stream.WriteAsync(frame);
        Assert.Equal((byte)'R', await ReadAcknowledgementAsync(stream));
        client.Dispose();
        await monitoring.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AcknowledgesValidHeartbeatOnlyAfterAllFragmentsArriveAndClosesOnEof()
    {
        var owner = Guid.NewGuid().ToString("N");
        var (heartbeat, client, _) = await ConnectOwnerAsync(owner, TestTimeout);
        await using (heartbeat)
        using (client)
        {
            var stream = client.GetStream();
            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(stream));

            var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);
            var acknowledgement = ReadAcknowledgementAsync(stream);
            var frame = Frame(owner);
            await stream.WriteAsync(frame.AsMemory(0, 3));
            await Task.Delay(40);
            Assert.False(acknowledgement.IsCompleted, "A partial heartbeat must not receive an acknowledgement.");
            Assert.False(monitoring.IsCompleted, "Monitoring must keep the owner connection while the frame is incomplete.");

            await stream.WriteAsync(frame.AsMemory(3, 5));
            await Task.Delay(40);
            Assert.False(acknowledgement.IsCompleted, "A heartbeat prefix must not receive an acknowledgement.");

            await stream.WriteAsync(frame.AsMemory(8));
            Assert.Equal((byte)'R', await acknowledgement.WaitAsync(TimeSpan.FromSeconds(1)));

            client.Client.Shutdown(SocketShutdown.Send);
            await monitoring.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task RejectsAFrameForAnotherOwner()
    {
        var expectedOwner = Guid.NewGuid().ToString("N");
        var differentOwner = Guid.NewGuid().ToString("N");
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, expectedOwner, TestTimeout, CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Frame(differentOwner));

        await Assert.ThrowsAsync<InvalidDataException>(() => accepting);
    }

    [Fact]
    public async Task RejectedOwnerFrameClosesAcceptedClientAndReleasesListener()
    {
        var expectedOwner = Guid.NewGuid().ToString("N");
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, expectedOwner, TestTimeout, CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Frame(Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<InvalidDataException>(() => accepting);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        using var replacement = new TcpListener(IPAddress.Any, port);
        replacement.Start();
    }

    [Fact]
    public async Task RejectsFirstFrameWhenOwnerClosesBeforeSendingACompleteFrame()
    {
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(
            port, Guid.NewGuid().ToString("N"), TestTimeout, CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.Dispose();

        await Assert.ThrowsAsync<InvalidDataException>(() => accepting);
    }

    [Fact]
    public async Task RejectsFirstFrameWhenPeerClosesAfterSendingOnlyAPrefix()
    {
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(
            port, Guid.NewGuid().ToString("N"), TestTimeout, CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Frame(Guid.NewGuid().ToString("N")).AsMemory(0, 7));
        client.Dispose();

        await Assert.ThrowsAsync<InvalidDataException>(() => accepting);
    }

    [Fact]
    public async Task FirstPartialFrameUsesBoundedDeadline()
    {
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailablePort();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(
            port, owner, TimeSpan.FromMilliseconds(120), CancellationToken.None, () => ready.TrySetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Frame(owner).AsMemory(0, 5));

        await Assert.ThrowsAsync<TimeoutException>(() => accepting.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task MalformedFirstFrameIsRejected()
    {
        var owner = Guid.NewGuid().ToString("N");
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, owner, TestTimeout, CancellationToken.None);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var malformed = Frame(owner);
        malformed[^1] = (byte)'X';
        await client.GetStream().WriteAsync(malformed);

        await Assert.ThrowsAsync<InvalidDataException>(() => accepting);
    }

    [Fact]
    public async Task ForeignHeartbeatAfterAcknowledgementEndsTheOriginalConnection()
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        await using (heartbeat)
        using (client)
        {
            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));
            var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);
            await client.GetStream().WriteAsync(Frame(Guid.NewGuid().ToString("N")));

            await monitoring.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task CancelledAcceptReleasesListenerWithoutTakingAnOwner()
    {
        var port = GetAvailablePort();
        using var cancellation = new CancellationTokenSource();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, Guid.NewGuid().ToString("N"), TestTimeout, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepting);

        using var replacement = new TcpListener(IPAddress.Any, port);
        replacement.Start();
    }

    [Fact]
    public async Task RequiresGuidNFormatAndPortAbove1024()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            KeycloakOwnerHeartbeat.AcceptAsync(12000, "not-a-guid", TestTimeout, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KeycloakOwnerHeartbeat.AcceptAsync(1024, Guid.NewGuid().ToString("N"), TestTimeout, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KeycloakOwnerHeartbeat.AcceptAsync(12000, Guid.NewGuid().ToString("N"), TimeSpan.Zero, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KeycloakOwnerHeartbeat.AcceptAsync(12000, Guid.NewGuid().ToString("N"), TimeSpan.FromDays(25), CancellationToken.None));
    }

    [Fact]
    public async Task InitialAcceptAndPartialFrameShareOneDeadlineFromListenStart()
    {
        var owner = Guid.NewGuid().ToString("N");
        var timeout = TimeSpan.FromMilliseconds(250);
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, owner, timeout, CancellationToken.None);

        await Task.Delay(150);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Frame(owner).AsMemory(0, 1));
        await Task.Delay(130);

        Assert.True(accepting.IsFaulted, "The first-frame deadline should include time spent waiting for the connection.");
        await Assert.ThrowsAsync<TimeoutException>(() => accepting);
    }

    [Fact]
    public async Task ExpiredInitialDeadlineStopsListenerAndDisposesPendingAccept()
    {
        var port = GetAvailablePort();
        var listenerReady = false;
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(
            port,
            Guid.NewGuid().ToString("N"),
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None,
            () =>
            {
                listenerReady = true;
                Thread.Sleep(60);
            });

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => accepting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(listenerReady);
        Assert.Equal("The Keycloak owner connection timed out.", failure.Message);
        using var replacement = new TcpListener(IPAddress.Any, port);
        replacement.Start(1);
        using var lateClient = new TcpClient(AddressFamily.InterNetwork);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await lateClient.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        using var accepted = await replacement.AcceptTcpClientAsync(deadline.Token);
        Assert.Equal(lateClient.Client.LocalEndPoint, accepted.Client.RemoteEndPoint);
    }

    [Fact]
    public async Task PartialHeartbeatTimesOutAndEndsMonitoring()
    {
        var owner = Guid.NewGuid().ToString("N");
        var (heartbeat, client, _) = await ConnectOwnerAsync(owner, TimeSpan.FromMilliseconds(600));
        await using (heartbeat)
        using (client)
        {
            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));

            var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);
            await client.GetStream().WriteAsync(Frame(owner).AsMemory(0, 4));
            await monitoring.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task PeerEofAfterAcknowledgementEndsMonitoring()
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        await using (heartbeat)
        using (client)
        {
            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));
            var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);
            client.Dispose();

            await monitoring.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task PeerResetAfterAcknowledgementEndsMonitoring()
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        await using (heartbeat)
        using (client)
        {
            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));
            var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);

            client.Client.LingerState = new LingerOption(true, 0);
            client.Dispose();

            await monitoring.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task CallerCancellationStopsMonitoringAndPropagates()
    {
        var owner = Guid.NewGuid().ToString("N");
        var (heartbeat, client, _) = await ConnectOwnerAsync(owner, TestTimeout);
        await using (heartbeat)
        using (client)
        {
            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => heartbeat.WaitForLossAsync(cancellation.Token));
        }
    }

    [Fact]
    public async Task CancellationDuringInitialAcknowledgementLeavesNoMonitoredOwner()
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        await using (heartbeat)
        using (client)
        {
            var cancellation = new CancellationToken(canceled: true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => heartbeat.AcknowledgeAsync(cancellation));
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.WaitForLossAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("timeout", "The Keycloak owner acknowledgement timed out.")]
    [InlineData("io", "The Keycloak owner acknowledgement failed.")]
    [InlineData("socket", "The Keycloak owner acknowledgement failed.")]
    [InlineData("disposed", "The Keycloak owner acknowledgement failed.")]
    public async Task FailedInitialAcknowledgementClosesOwnerAndReportsOnlyTheSafeDiagnostic(string kind, string expectedMessage)
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        await using (heartbeat)
        using (client)
        {
            using var operation = new CancellationTokenSource(TestTimeout);
            Exception injected = kind switch
            {
                "timeout" => new TimeoutException("synthetic transport details"),
                "io" => new IOException("synthetic transport details"),
                "socket" => new SocketException((int)SocketError.ConnectionReset),
                "disposed" => new ObjectDisposedException("synthetic transport details"),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };

            var failure = await Record.ExceptionAsync(() => heartbeat.AcknowledgeAsync(token =>
            {
                Assert.Equal(operation.Token, token);
                return Task.FromException(injected);
            }, operation.Token));

            Assert.NotNull(failure);
            Assert.Equal(kind == "timeout" ? typeof(TimeoutException) : typeof(IOException), failure.GetType());
            Assert.Equal(expectedMessage, failure.Message);
            Assert.Null(failure.InnerException);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], operation.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.AcknowledgeAsync(operation.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.WaitForLossAsync(operation.Token));
        }
    }

    [Fact]
    public async Task UnexpectedAcknowledgementFailurePropagatesToTheOwnerScope()
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        using (client)
        {
            var injected = new InvalidOperationException("fixture operation failure");
            await using (heartbeat)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.AcknowledgeAsync(
                    _ => Task.FromException(injected), CancellationToken.None));
                Assert.Same(injected, failure);
            }

            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TestTimeout));
        }
    }

    [Fact]
    public async Task AcceptedOwnerConnectionDoesNotReconnectOrAcceptASecondClient()
    {
        var owner = Guid.NewGuid().ToString("N");
        var (heartbeat, client, port) = await ConnectOwnerAsync(owner, TestTimeout);
        await using (heartbeat)
        using (client)
        {
            using var secondClient = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(
                () => secondClient.ConnectAsync(IPAddress.Loopback, port));

            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));
            client.Dispose();
            await heartbeat.WaitForLossAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnforcesAcknowledgementAndMonitoringStateTransitions()
    {
        var (heartbeat, client, _) = await ConnectOwnerAsync(Guid.NewGuid().ToString("N"), TestTimeout);
        await using (heartbeat)
        using (client)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.WaitForLossAsync(CancellationToken.None));

            await heartbeat.AcknowledgeAsync(CancellationToken.None);
            Assert.Equal((byte)'R', await ReadAcknowledgementAsync(client.GetStream()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.AcknowledgeAsync(CancellationToken.None));

            var monitoring = heartbeat.WaitForLossAsync(CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => heartbeat.WaitForLossAsync(CancellationToken.None));
            client.Dispose();
            await monitoring.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<(KeycloakOwnerHeartbeat Heartbeat, TcpClient Client, int Port)> ConnectOwnerAsync(
        string owner,
        TimeSpan timeout)
    {
        var port = GetAvailablePort();
        var accepting = KeycloakOwnerHeartbeat.AcceptAsync(port, owner, timeout, CancellationToken.None);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(Frame(owner));
            return (await accepting, client, port);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static byte[] Frame(string owner) => Encoding.ASCII.GetBytes(owner + "\n");

    private static async Task<byte> ReadAcknowledgementAsync(NetworkStream stream)
    {
        var acknowledgement = new byte[1];
        var readTask = stream.ReadAsync(acknowledgement.AsMemory()).AsTask();
        var count = await readTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, count);
        return acknowledgement[0];
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
