using System.Net;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.Runnable.Web.RazorWire.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RazorDocsInProcessHostTests
{
    [Fact]
    public async Task StartAsync_UsesStandaloneHostInProcess()
    {
        var host = await RazorDocsInProcessHost.StartAsync("http://127.0.0.1:0");

        try
        {
            Assert.True(host.IsStarted);
            Assert.StartsWith("http://127.0.0.1:", host.BaseUrl, StringComparison.Ordinal);

            using var client = new HttpClient
            {
                BaseAddress = new Uri(host.BaseUrl)
            };

            using var response = await client.GetAsync("/docs");

            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Expected OK but received {response.StatusCode}.{Environment.NewLine}{body}");
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.False(host.IsStarted);
        Assert.Equal("RazorDocs standalone host has stopped.", host.Diagnostics);

        await host.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_DisposesBuiltHost_WhenStartupFails()
    {
        var expected = new InvalidOperationException("startup failed");
        var host = new ThrowingHost(expected);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RazorDocsInProcessHost.StartAsync(host));

        Assert.Same(expected, exception);
        Assert.True(host.IsDisposed);
    }

    [Fact]
    public void ResolveBoundBaseUrl_ReturnsOnlyPublishedAddress()
    {
        var baseUrl = RazorDocsInProcessHost.ResolveBoundBaseUrl(["http://127.0.0.1:5000"]);

        Assert.Equal("http://127.0.0.1:5000", baseUrl);
    }

    [Fact]
    public void ResolveBoundBaseUrl_PreservesIpv6Authority()
    {
        var baseUrl = RazorDocsInProcessHost.ResolveBoundBaseUrl(["http://[::1]:5000"]);

        Assert.Equal("http://[::1]:5000", baseUrl);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresPublishedAddress()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RazorDocsInProcessHost.ResolveBoundBaseUrl([]));

        Assert.Equal(
            "RazorDocs standalone host did not publish a listening URL. No addresses were published.",
            exception.Message);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresPublishedAddress_WhenAddressesAreNull()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RazorDocsInProcessHost.ResolveBoundBaseUrl(null));

        Assert.Equal(
            "RazorDocs standalone host did not publish a listening URL. No addresses were published.",
            exception.Message);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresSinglePublishedAddress()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RazorDocsInProcessHost.ResolveBoundBaseUrl(["http://127.0.0.1:5000", "http://127.0.0.1:5001"]));

        Assert.Equal(
            "RazorDocs standalone host published 2 listening URLs; expected exactly one. Values: 'http://127.0.0.1:5000', 'http://127.0.0.1:5001'.",
            exception.Message);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresValidAbsoluteAddress()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RazorDocsInProcessHost.ResolveBoundBaseUrl(["not-a-url"]));

        Assert.Equal(
            "RazorDocs standalone host did not publish a valid listening URL. Value: 'not-a-url'.",
            exception.Message);
    }

    private sealed class ThrowingHost : IHost
    {
        private readonly Exception _exception;

        public ThrowingHost(Exception exception)
        {
            _exception = exception;
        }

        public IServiceProvider Services => throw new InvalidOperationException("Services should not be accessed after startup fails.");

        public bool IsDisposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            throw _exception;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("StopAsync should not be called when startup fails.");
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
