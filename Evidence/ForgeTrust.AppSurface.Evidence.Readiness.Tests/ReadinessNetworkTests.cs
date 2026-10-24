using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Readiness;

namespace ForgeTrust.AppSurface.Evidence.Readiness.Tests;

public sealed class ReadinessNetworkTests
{
    [Fact]
    public async Task DefaultBclClient_FollowsRedirectAccordingToItsTransportPolicy()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start();
        destination.Start();
        var endpoint = Endpoint(source);
        var target = Endpoint(destination);
        var redirect = ServeHeadersAsync(source,
            $"HTTP/1.1 302 Found\r\nLocation: {target}final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", timeout.Token);
        var accepted = ServeHeadersAsync(destination,
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", timeout.Token);
        using var client = new HttpClient();

        await HttpReadinessProbe.WaitAsync(client, endpoint, "/healthz", TimeSpan.FromSeconds(10), cancellationToken: timeout.Token);

        Assert.StartsWith("GET /healthz HTTP/1.1", await redirect, StringComparison.Ordinal);
        Assert.StartsWith("GET /final HTTP/1.1", await accepted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealHttpTransport_AcceptsHeadersWithoutWaitingForDeclaredBody()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeIncompleteBodyAsync();
        using var client = new HttpClient();
        var readiness = HttpReadinessProbe.WaitAsync(client, Endpoint(listener), "/healthz", TimeSpan.FromSeconds(10), cancellationToken: timeout.Token);
        try
        {
            await headersSent.Task.WaitAsync(timeout.Token);
            await readiness.WaitAsync(timeout.Token);
            Assert.False(releaseBody.Task.IsCompleted);
        }
        finally
        {
            releaseBody.TrySetResult();
            await server;
        }

        async Task ServeIncompleteBodyAsync()
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = socket.GetStream();
            _ = await ReadHeadersAsync(stream, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 50000\r\nConnection: close\r\n\r\n"), timeout.Token);
            headersSent.SetResult();
            await releaseBody.Task.WaitAsync(timeout.Token);
        }
    }

    private static Uri Endpoint(TcpListener listener) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

    private static async Task<string> ServeHeadersAsync(TcpListener listener, string response, CancellationToken token)
    {
        using var socket = await listener.AcceptTcpClientAsync(token);
        using var stream = socket.GetStream();
        var request = await ReadHeadersAsync(stream, token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
        return request;
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new StringBuilder();
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            headers.AppendLine(line);
        }

        return headers.ToString();
    }
}
