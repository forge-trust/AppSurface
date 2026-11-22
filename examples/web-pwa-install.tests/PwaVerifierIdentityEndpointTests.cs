using System.Net;
using ForgeTrust.AppSurface.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WebPwaInstallExample.Tests;

public sealed class PwaVerifierIdentityEndpointTests
{
    private const string IdentityPath = "/_appsurface/verify/instance";
    private const string InstanceToken = "A1B2C3D4E5F60718293A4B5C6D7E8F90123456789ABCDEF0";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task DevelopmentEndpoint_ReturnsInstanceOnlyToLoopback(string remoteAddressText)
    {
        var remoteAddress = IPAddress.Parse(remoteAddressText);
        await using var host = await CreateHostAsync(isDevelopment: true, InstanceToken, remoteAddress);

        using var response = await host.Client.GetAsync(IdentityPath);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(InstanceToken, body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData(null)]
    public async Task DevelopmentEndpoint_RejectsForeignOrMissingPeer(string? remoteAddressText)
    {
        var remoteAddress = remoteAddressText is null ? null : IPAddress.Parse(remoteAddressText);
        await using var host = await CreateHostAsync(isDevelopment: true, InstanceToken, remoteAddress);

        using var response = await host.Client.GetAsync(IdentityPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task DevelopmentEndpoint_IsNotMappedWithoutAnInstanceToken(string? instanceToken)
    {
        await using var host = await CreateHostAsync(
            isDevelopment: true,
            instanceToken,
            IPAddress.Loopback);

        using var response = await host.Client.GetAsync(IdentityPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Endpoint_IsNotMappedOutsideDevelopment()
    {
        await using var host = await CreateHostAsync(
            isDevelopment: false,
            InstanceToken,
            IPAddress.Loopback);

        using var response = await host.Client.GetAsync(IdentityPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<IdentityTestHost> CreateHostAsync(
        bool isDevelopment,
        string? instanceToken,
        IPAddress? remoteAddress)
    {
        var module = new WebPwaInstallModule();
        var environmentProvider = new TestEnvironmentProvider(isDevelopment, instanceToken);
        var context = new StartupContext([], module, EnvironmentProvider: environmentProvider);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentProvider.Environment,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new WebPushProofState(false));

        var app = builder.Build();
        app.Use(async (httpContext, next) =>
        {
            httpContext.Connection.RemoteIpAddress = remoteAddress;
            await next();
        });
        module.ConfigureEndpoints(context, app);

        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StartAsync(startupTimeout.Token);
            var client = app.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            return new IdentityTestHost(app, client);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private sealed class TestEnvironmentProvider(bool isDevelopment, string? instanceToken) : IEnvironmentProvider
    {
        public string Environment => isDevelopment ? Environments.Development : Environments.Production;

        public bool IsDevelopment => isDevelopment;

        public string? GetEnvironmentVariable(string name, string? defaultValue = null) =>
            string.Equals(name, "APPSURFACE_PWA_VERIFY_INSTANCE", StringComparison.Ordinal)
                ? instanceToken ?? defaultValue
                : defaultValue;

        public IReadOnlyDictionary<string, string> CaptureEnvironmentVariables() =>
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class IdentityTestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
