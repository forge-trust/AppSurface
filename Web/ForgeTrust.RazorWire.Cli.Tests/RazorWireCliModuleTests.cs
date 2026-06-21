using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.RazorWire.Cli.Tests;

public class RazorWireCliModuleTests
{
    [Fact]
    public void ConfigureServices_Should_Register_Expected_Services()
    {
        var module = new RazorWireCliModule();
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule());

        module.ConfigureServices(context, services);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<ExportEngine>());
        Assert.NotNull(provider.GetService<ExportSourceRequestFactory>());
        Assert.NotNull(provider.GetService<ExportSourceResolver>());
        Assert.NotNull(provider.GetService<ITargetAppProcessFactory>());
        Assert.NotNull(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public async Task ConfigureServices_Should_Disable_Automatic_Redirects_For_ExportEngine_Client()
    {
        var module = new RazorWireCliModule();
        var services = new ServiceCollection();
        var context = new StartupContext([], new TestHostModule());

        module.ConfigureServices(context, services);
        using var provider = services.BuildServiceProvider();
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeRedirectThenOkAsync(listener);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ExportEngine");
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/redirect");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal($"/target", response.Headers.Location?.OriginalString);

        listener.Stop();
        await serverTask;
    }

    [Fact]
    public void Noop_Host_Methods_Should_Not_Throw()
    {
        var module = new RazorWireCliModule();
        var context = new StartupContext([], new TestHostModule());
        var hostBuilder = Host.CreateDefaultBuilder([]);
        var depBuilder = new ModuleDependencyBuilder();

        module.ConfigureHostBeforeServices(context, hostBuilder);
        module.ConfigureHostAfterServices(context, hostBuilder);
        module.RegisterDependentModules(depBuilder);
    }

    private sealed class TestHostModule : IAppSurfaceHostModule
    {
        public void ConfigureServices(StartupContext context, IServiceCollection services) { }
        public void RegisterDependentModules(ModuleDependencyBuilder builder) { }
        public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
        public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
    }

    private static async Task ServeRedirectThenOkAsync(TcpListener listener)
    {
        try
        {
            using var redirectClient = await listener.AcceptTcpClientAsync();
            await DrainRequestAsync(redirectClient);
            await WriteResponseAsync(
                redirectClient,
                "HTTP/1.1 302 Found\r\nLocation: /target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                using var targetClient = await listener.AcceptTcpClientAsync(timeoutCts.Token);
                await DrainRequestAsync(targetClient);
                await WriteResponseAsync(
                    targetClient,
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
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
