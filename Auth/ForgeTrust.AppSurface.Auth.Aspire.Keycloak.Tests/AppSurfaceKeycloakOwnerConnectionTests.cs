using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakOwnerConnectionTests
{
    [Theory]
    [InlineData(1023, "0123456789abcdef0123456789abcdef")]
    [InlineData(65536, "0123456789abcdef0123456789abcdef")]
    [InlineData(1024, "not-a-guid")]
    public async Task InvalidOwnerEndpointOrIdentityIsRejectedBeforeConnecting(int port, string owner)
    {
        var losses = 0;
        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            AppSurfaceKeycloakOwnerConnection.ConnectAsync(port, owner, () => losses++, default));

        Assert.Equal("Invalid owner connection.", failure.Message);
        Assert.Equal(0, losses);
    }

    [Fact]
    public async Task OwnerRetriesARefusedConnectionAndWaitsForTheFrameAcknowledgement()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string owner = "0123456789AbCdEf0123456789aBcDeF";
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var losses = 0;
        var attempts = 0;

        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(
            port,
            owner,
            () => Interlocked.Increment(ref losses),
            stop.Token,
            (client, endpoint, token) => ++attempts == 1
                ? ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused))
                : client.ConnectAsync(IPAddress.Loopback, endpoint, token));
        using var peer = await listener.AcceptTcpClientAsync(stop.Token);
        var stream = peer.GetStream();
        var frame = new byte[33];
        await stream.ReadExactlyAsync(frame, stop.Token);
        Assert.Equal(owner + "\n", Encoding.ASCII.GetString(frame));
        Assert.False(connecting.IsCompleted);
        await stream.WriteAsync("R"u8.ToArray(), stop.Token);
        using var connection = await connecting;

        await connection.ObserveStoppedAsync(stop.Token);

        Assert.Equal(2, attempts);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], stop.Token));
        Assert.Equal(0, losses);
    }

    [Fact]
    public async Task IncompleteFirstAcknowledgementPropagatesCancellationWithoutFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var losses = 0;
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(
            port, Guid.NewGuid().ToString("N"), () => Interlocked.Increment(ref losses), stop.Token);
        using var peer = await accepting;
        await peer.GetStream().ReadExactlyAsync(new byte[33], stop.Token);

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(0, losses);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PeerEofBeforeFirstAcknowledgementFailsWithoutFailureCallback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var losses = 0;
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(
            port, Guid.NewGuid().ToString("N"), () => Interlocked.Increment(ref losses), stop.Token);
        using var peer = await accepting;
        await peer.GetStream().ReadExactlyAsync(new byte[33], stop.Token);
        peer.Dispose();

        await Assert.ThrowsAsync<EndOfStreamException>(() => connecting);
        Assert.Equal(0, losses);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task OwnerCancellationClosesConnectionWithoutFailureOrReconnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owner = Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var losses = 0;
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(port, owner, () => Interlocked.Increment(ref losses), stop.Token);
        using var peer = await accepting;
        var frame = new byte[33];
        await peer.GetStream().ReadExactlyAsync(frame, stop.Token);
        Assert.Equal(owner + "\n", Encoding.ASCII.GetString(frame));
        await peer.GetStream().WriteAsync("R"u8.ToArray(), stop.Token);
        using var connection = await connecting;

        stop.Cancel();
        await connection.ObserveStoppedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], observe.Token));
        Assert.Equal(0, losses);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task InvalidFirstAcknowledgementFailsWithoutReconnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(port, Guid.NewGuid().ToString("N"), () => { }, stop.Token);
        using var peer = await accepting;
        await peer.GetStream().ReadExactlyAsync(new byte[33], stop.Token);
        await peer.GetStream().WriteAsync("X"u8.ToArray(), stop.Token);

        await Assert.ThrowsAsync<IOException>(async () => await connecting);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task PeerLossLatchesFailureAndEndsHeartbeat()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(port, Guid.NewGuid().ToString("N"), () => failed.TrySetResult(), stop.Token);
        using var peer = await accepting;
        await peer.GetStream().ReadExactlyAsync(new byte[33], stop.Token);
        await peer.GetStream().WriteAsync("R"u8.ToArray(), stop.Token);
        using var connection = await connecting;
        peer.Dispose();

        await failed.Task.WaitAsync(stop.Token);
        connection.Dispose();
        await connection.ObserveStoppedAsync(stop.Token);
        Assert.False(listener.Pending());
    }
}
