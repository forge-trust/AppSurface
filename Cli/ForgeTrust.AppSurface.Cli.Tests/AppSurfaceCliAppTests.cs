using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeTrust.RazorWire.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class AppSurfaceCliAppTests
{
    [Fact]
    public async Task AddPwaVerifierServices_Should_Disable_Automatic_Redirects_For_Pwa_Client()
    {
        var services = new ServiceCollection();

        AppSurfaceCliApp.AddPwaVerifierServices(services);
        services.AddTransient<PwaVerifier>();

        using var provider = services.BuildServiceProvider();
        using var redirectListener = new TcpListener(IPAddress.Loopback, port: 0);
        using var targetListener = new TcpListener(IPAddress.Loopback, port: 0);
        redirectListener.Start();
        targetListener.Start();
        var redirectPort = ((IPEndPoint)redirectListener.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
        var targetUrl = $"http://127.0.0.1:{targetPort}/target";
        var redirectTask = ServeRedirectAsync(redirectListener, targetUrl);
        var targetRequestTask = AcceptRequestWithinAsync(targetListener, TimeSpan.FromMilliseconds(250));

        var client = provider.GetRequiredService<IPwaVerificationHttpClient>();
        var response = await client.GetAsync(new Uri($"http://127.0.0.1:{redirectPort}/redirect"), 1024, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.False(response.IsSuccess);
        Assert.NotNull(provider.GetService<PwaVerifier>());
        Assert.False(await targetRequestTask);

        redirectListener.Stop();
        targetListener.Stop();
        await redirectTask;
    }

    [Fact]
    public async Task AddExportEngineServices_Should_Disable_Automatic_Redirects_For_ExportEngine_Client()
    {
        var services = new ServiceCollection();

        AppSurfaceCliApp.AddExportEngineServices(services);

        using var provider = services.BuildServiceProvider();
        using var redirectListener = new TcpListener(IPAddress.Loopback, port: 0);
        using var targetListener = new TcpListener(IPAddress.Loopback, port: 0);
        redirectListener.Start();
        targetListener.Start();
        var redirectPort = ((IPEndPoint)redirectListener.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
        var targetUrl = $"http://127.0.0.1:{targetPort}/target";
        var redirectTask = ServeRedirectAsync(redirectListener, targetUrl);
        var targetRequestTask = AcceptRequestWithinAsync(targetListener, TimeSpan.FromMilliseconds(250));

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ExportEngine");
        using var response = await client.GetAsync($"http://127.0.0.1:{redirectPort}/redirect");

        Assert.Equal(TimeSpan.FromSeconds(60), client.Timeout);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(targetUrl, response.Headers.Location?.OriginalString);
        Assert.False(await targetRequestTask);

        redirectListener.Stop();
        targetListener.Stop();
        await redirectTask;
    }

    [Fact]
    public void AddExportEngineServices_Should_Register_Export_Dependencies()
    {
        var services = new ServiceCollection();

        AppSurfaceCliApp.AddExportEngineServices(services);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<ExportEngine>());
        Assert.NotNull(provider.GetService<ExportSourceRequestFactory>());
        Assert.NotNull(provider.GetService<ExportSourceResolver>());
        Assert.NotNull(provider.GetService<ITargetAppProcessFactory>());
        Assert.NotNull(provider.GetService<IHttpClientFactory>());
    }

    private static async Task ServeRedirectAsync(TcpListener listener, string targetUrl)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync();
            await DrainRequestAsync(client);
            await WriteResponseAsync(
                client,
                $"HTTP/1.1 302 Found\r\nLocation: {targetUrl}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted
                                             or SocketError.Interrupted
                                             or SocketError.ConnectionReset)
        {
            System.Diagnostics.Trace.WriteLine($"Ignored expected test listener shutdown socket exception: {ex.SocketErrorCode}.");
        }
        catch (ObjectDisposedException ex)
        {
            System.Diagnostics.Trace.WriteLine($"Ignored expected test listener disposal: {ex.Message}");
        }
    }

    private static async Task<bool> AcceptRequestWithinAsync(TcpListener listener, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            await DrainRequestAsync(client);
            await WriteResponseAsync(
                client,
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted
                                             or SocketError.Interrupted
                                             or SocketError.ConnectionReset)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task DrainRequestAsync(TcpClient client)
    {
        var buffer = new byte[1024];
        var stream = client.GetStream();
        _ = await stream.ReadAsync(buffer);
    }

    private static async Task WriteResponseAsync(TcpClient client, string response)
    {
        var bytes = Encoding.ASCII.GetBytes(response);
        await client.GetStream().WriteAsync(bytes);
    }
}
