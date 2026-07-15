using System.Net;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs.Standalone;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ForgeTrust.RazorWire.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AppSurfaceDocsInProcessHostTests
{
    [Fact]
    public async Task StartAsync_UsesStandaloneHostInProcess()
    {
        var host = await AppSurfaceDocsInProcessHost.StartAsync("http://127.0.0.1:0");

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
        Assert.Equal("AppSurface Docs application host has stopped.", host.Diagnostics);

        await host.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_DisposesBuiltHost_WhenStartupFails()
    {
        var expected = new InvalidOperationException("startup failed");
        var host = new ThrowingHost(expected);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppSurfaceDocsInProcessHost.StartAsync(host));

        Assert.Same(expected, exception);
        Assert.True(host.IsDisposed);
    }

    [Fact]
    public void AppSurfaceDocsStandaloneHost_PreservesTwoParameterCreateBuilderOverload()
    {
        var environmentProvider = new TestEnvironmentProvider(Environments.Development);

        var builder = AppSurfaceDocsStandaloneHost.CreateBuilder(
            ["--urls", "http://127.0.0.1:0"],
            environmentProvider);

        using var host = builder.Build();

        Assert.Same(
            environmentProvider,
            host.Services.GetRequiredService<IEnvironmentProvider>());
    }

    [Fact]
    public void ResolveBoundBaseUrl_ReturnsOnlyPublishedAddress()
    {
        var baseUrl = AppSurfaceDocsInProcessHost.ResolveBoundBaseUrl(["http://127.0.0.1:5000"]);

        Assert.Equal("http://127.0.0.1:5000", baseUrl);
    }

    [Fact]
    public void ResolveBoundBaseUrl_PreservesIpv6Authority()
    {
        var baseUrl = AppSurfaceDocsInProcessHost.ResolveBoundBaseUrl(["http://[::1]:5000"]);

        Assert.Equal("http://[::1]:5000", baseUrl);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresPublishedAddress()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessHost.ResolveBoundBaseUrl([]));

        Assert.Equal(
            "AppSurface Docs application host did not publish a listening URL. No addresses were published.",
            exception.Message);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresPublishedAddress_WhenAddressesAreNull()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessHost.ResolveBoundBaseUrl(null));

        Assert.Equal(
            "AppSurface Docs application host did not publish a listening URL. No addresses were published.",
            exception.Message);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresSinglePublishedAddress()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessHost.ResolveBoundBaseUrl(["http://127.0.0.1:5000", "http://127.0.0.1:5001"]));

        Assert.Equal(
            "AppSurface Docs application host published 2 listening URLs; expected exactly one. Values: 'http://127.0.0.1:5000', 'http://127.0.0.1:5001'.",
            exception.Message);
    }

    [Fact]
    public void ResolveBoundBaseUrl_RequiresValidAbsoluteAddress()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppSurfaceDocsInProcessHost.ResolveBoundBaseUrl(["not-a-url"]));

        Assert.Equal(
            "AppSurface Docs application host did not publish a valid listening URL. Value: 'not-a-url'.",
            exception.Message);
    }

    private sealed class TestEnvironmentProvider(string environmentName) : IEnvironmentProvider
    {
        public string Environment { get; } = environmentName;

        public bool IsDevelopment { get; } = string.Equals(
            environmentName,
            Environments.Development,
            StringComparison.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) => defaultValue;
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
