using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Threading.Channels;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Tests;

public class RazorWireEndpointAuthorizationTests
{
    [Fact]
    public async Task StreamEndpoint_DefaultConfiguration_ReturnsForbidden()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Production);

        using var response = await fixture.Client.GetAsync("/_rw/streams/public");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_DoesNotStartSseOrSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services => services.AddSingleton<IRazorWireStreamHub>(hub));

        using var response = await fixture.Client.GetAsync("/_rw/streams/sensitive-channel");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, hub.SubscribeCount);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_InDevelopmentWritesSafePlainTextDiagnostic()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Development);

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Streams deny subscriptions by default", body, StringComparison.Ordinal);
        Assert.Contains(nameof(RazorWireStreamAuthorizationMode.AllowAll), body, StringComparison.Ordinal);
        Assert.Contains(nameof(IRazorWireChannelAuthorizer), body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_InProductionWritesEmptyBody()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Production);

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task StreamEndpoint_DeniedSubscription_LogOmitsRawChannelAndUserDetail()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: null,
            configureLogging: logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(loggerProvider);
            },
            configureApp: app =>
            {
                app.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, "user-123"),
                                new Claim(ClaimTypes.Email, "andrew@example.test")
                            ],
                            "TestAuth"));

                    await next();
                });
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant-secret-42");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Assert.Single(
            loggerProvider.Entries,
            log => log.EventId.Id == 13700 && log.EventId.Name == "StreamSubscriptionDenied");
        Assert.Contains("RazorWire stream subscription denied", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ConfiguredAuthorizationMode", entry.Message, StringComparison.Ordinal);
        Assert.Contains("AuthorizerType", entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DenyAllRazorWireChannelAuthorizer), entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RazorWireStreamAuthorizationMode.DenyAll), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user-123", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("andrew@example.test", entry.Message, StringComparison.Ordinal);
    }

    private sealed class RazorWireEndpointFixture : IAsyncDisposable
    {
        private RazorWireEndpointFixture(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public HttpClient Client { get; }

        private WebApplication App { get; }

        public static async Task<RazorWireEndpointFixture> StartAsync(
            string environmentName,
            Action<IServiceCollection>? configureServices = null,
            Action<ILoggingBuilder>? configureLogging = null,
            Action<WebApplication>? configureApp = null)
        {
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = environmentName
                });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            configureLogging?.Invoke(builder.Logging);
            configureServices?.Invoke(builder.Services);
            builder.Services.AddControllersWithViews();
            builder.Services.AddRazorWire();

            var app = builder.Build();
            configureApp?.Invoke(app);
            app.MapRazorWire();
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>();
            var baseAddress = Assert.Single(addresses!.Addresses);
            var client = new HttpClient
            {
                BaseAddress = new Uri(baseAddress)
            };

            return new RazorWireEndpointFixture(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class TrackingStreamHub : IRazorWireStreamHub
    {
        public int SubscribeCount { get; private set; }

        public ValueTask PublishAsync(string channel, string message)
        {
            return ValueTask.CompletedTask;
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            SubscribeCount++;
            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

        public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(_entries);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new CapturedLogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record CapturedLogEntry(LogLevel LogLevel, EventId EventId, string Message);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
