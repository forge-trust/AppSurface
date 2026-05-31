using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using ForgeTrust.RazorWire.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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
    public async Task AntiforgeryTokenEndpoint_ReturnsTokenPayloadWithNoStoreHeaders()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(Environments.Production);

        using var response = await fixture.Client.GetAsync("/_rw/antiforgery/token");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains(response.Headers.Pragma, pragma => string.Equals(pragma.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("__RequestVerificationToken", document.RootElement.GetProperty("formFieldName").GetString());
        Assert.Equal("RequestVerificationToken", document.RootElement.GetProperty("headerName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("requestToken").GetString()));
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

    [Fact]
    public async Task StreamEndpoint_InvalidChannel_ReturnsBadRequestBeforeAuthorizerOrHub()
    {
        var hub = new TrackingStreamHub();
        var authorizer = new CountingAllowAuthorizer();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.AddSingleton<IRazorWireChannelAuthorizer>(authorizer);
            });

        using var response = await fixture.Client.GetAsync("/_rw/streams/tenant%20secret");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Equal(0, hub.SubscribeCount);
        Assert.Contains(nameof(RazorWireStreamAdmissionRejectionReason.InvalidChannelName), body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_OverLimit_ReturnsTooManyRequestsBeforeSseOrSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveSubscriptions = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/public",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("text/event-stream", accepted.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotEqual("text/event-stream", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, hub.SubscribeCount);
    }

    [Fact]
    public async Task StreamEndpoint_EncodedAllowedChannel_DecodesBeforeSubscribe()
    {
        var hub = new TrackingStreamHub();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                });
            });

        using var response = await fixture.Client.GetAsync(
            "/_rw/streams/tenant%3Aorders",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tenant:orders", hub.SubscribedChannel);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_InDevelopmentWritesActionableSafeDiagnostic()
    {
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            services =>
            {
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            });

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other-secret-99");
        var body = await rejected.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains(nameof(RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels), body, StringComparison.Ordinal);
        Assert.Contains(nameof(RazorWireStreamOptions.MaxLiveChannels), body, StringComparison.Ordinal);
        Assert.Contains("ConfiguredLimit: 1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", body, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret-99", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_AdmissionRejection_LogStateOmitsRawChannelAndUserDetail()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Development,
            configureServices: services =>
            {
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveChannels = 1;
                });
            },
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

        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/tenant-secret-42",
            HttpCompletionOption.ResponseHeadersRead);
        using var rejected = await fixture.Client.GetAsync("/_rw/streams/other-secret-99");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var entry = Assert.Single(
            loggerProvider.Entries,
            log => log.EventId.Id == 13701 && log.EventId.Name == "StreamAdmissionRejected");

        var renderedState = string.Join(
            " ",
            entry.State.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Contains(nameof(RazorWireStreamAdmissionRejectionReason.TooManyLiveChannels), entry.Message, StringComparison.Ordinal);
        Assert.Contains("RejectionReason", renderedState, StringComparison.Ordinal);
        Assert.Contains("LiveChannels=1", renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-42", entry.Message + renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret-99", entry.Message + renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("user-123", entry.Message + renderedState, StringComparison.Ordinal);
        Assert.DoesNotContain("andrew@example.test", entry.Message + renderedState, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamEndpoint_SubscribeThrow_ReleasesAdmissionCapacity()
    {
        var hub = new TrackingStreamHub { ThrowOnSubscribe = true };
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveSubscriptions = 1;
                });
            });

        using var failed = await fixture.Client.GetAsync("/_rw/streams/public");
        hub.ThrowOnSubscribe = false;
        using var accepted = await fixture.Client.GetAsync(
            "/_rw/streams/public",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("text/event-stream", accepted.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StreamEndpoint_UnsubscribeThrow_ReleasesAdmissionCapacity()
    {
        var hub = new TrackingStreamHub { ThrowOnUnsubscribe = true };
        await using var fixture = await RazorWireEndpointFixture.StartAsync(
            Environments.Production,
            services =>
            {
                services.AddSingleton<IRazorWireStreamHub>(hub);
                services.Configure<RazorWireOptions>(options =>
                {
                    options.Streams.AuthorizationMode = RazorWireStreamAuthorizationMode.AllowAll;
                    options.Streams.MaxLiveSubscriptions = 1;
                });
            });

        using (var accepted = await fixture.Client.GetAsync(
                   "/_rw/streams/public",
                   HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await WaitUntilAsync(() => hub.UnsubscribeCount > 0);

        hub.ThrowOnUnsubscribe = false;
        using var next = await fixture.Client.GetAsync(
            "/_rw/streams/public",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
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
        private int _subscribeCount;
        private int _unsubscribeCount;

        public string? SubscribedChannel { get; private set; }

        public bool ThrowOnSubscribe { get; set; }

        public bool ThrowOnUnsubscribe { get; set; }

        public int SubscribeCount => Volatile.Read(ref _subscribeCount);

        public int UnsubscribeCount => Volatile.Read(ref _unsubscribeCount);

        public ValueTask PublishAsync(string channel, string message)
        {
            return ValueTask.CompletedTask;
        }

        public ChannelReader<string> Subscribe(string channel)
        {
            Interlocked.Increment(ref _subscribeCount);
            SubscribedChannel = channel;
            if (ThrowOnSubscribe)
            {
                throw new InvalidOperationException("Subscribe failed for test.");
            }

            return Channel.CreateUnbounded<string>().Reader;
        }

        public void Unsubscribe(string channel, ChannelReader<string> reader)
        {
            Interlocked.Increment(ref _unsubscribeCount);
            if (ThrowOnUnsubscribe)
            {
                throw new InvalidOperationException("Unsubscribe failed for test.");
            }
        }
    }

    private sealed class CountingAllowAuthorizer : IRazorWireChannelAuthorizer
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<bool> CanSubscribeAsync(HttpContext context, string channel)
        {
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(true);
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
            var stateValues = state as IEnumerable<KeyValuePair<string, object?>>;
            entries.Enqueue(
                new CapturedLogEntry(
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    stateValues?.ToArray() ?? []));
        }
    }

    private sealed record CapturedLogEntry(
        LogLevel LogLevel,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(predicate());
    }
}
