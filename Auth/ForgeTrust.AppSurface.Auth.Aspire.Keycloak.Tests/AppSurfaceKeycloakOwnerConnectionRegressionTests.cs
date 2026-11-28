using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests;

public sealed class AppSurfaceKeycloakOwnerConnectionRegressionTests
{
    [Theory]
    [InlineData(1025)]
    [InlineData(65535)]
    public async Task CancellationDuringRefusedConnectionRetriesDoesNotLatchFailure(int port)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var losses = 0;
        var attempts = 0;
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(
            port,
            Guid.NewGuid().ToString("N"),
            () => Interlocked.Increment(ref losses),
            stop.Token,
            (_, _, token) =>
            {
                if (++attempts == 1) return ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused));
                retryStarted.SetResult();
                return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token));
            });

        await retryStarted.Task.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(2, attempts);
        Assert.Equal(0, losses);
    }

    [Fact]
    public async Task InvalidHeartbeatAcknowledgementLatchesFailureWithoutReconnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owner = Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var losses = 0;
        var accepting = listener.AcceptTcpClientAsync(stop.Token).AsTask();
        var connecting = AppSurfaceKeycloakOwnerConnection.ConnectAsync(
            port, owner, () =>
            {
                Interlocked.Increment(ref losses);
                failed.TrySetResult();
            }, stop.Token);

        using var peer = await accepting;
        var stream = peer.GetStream();
        var initialFrame = new byte[33];
        await stream.ReadExactlyAsync(initialFrame, stop.Token);
        Assert.Equal(owner + "\n", Encoding.ASCII.GetString(initialFrame));
        await stream.WriteAsync("R"u8.ToArray(), stop.Token);
        using var connection = await connecting;

        var heartbeatFrame = new byte[33];
        await stream.ReadExactlyAsync(heartbeatFrame, stop.Token);
        Assert.Equal(owner + "\n", Encoding.ASCII.GetString(heartbeatFrame));
        await stream.WriteAsync("X"u8.ToArray(), stop.Token);

        await failed.Task.WaitAsync(stop.Token);
        await connection.ObserveStoppedAsync(stop.Token);

        Assert.Equal(1, losses);
        Assert.False(listener.Pending());
    }
}
